using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AiAssistant.Core.Services;
using AiAssistant.Storage.Models;
using AiAssistant.Storage.Repositories;
using Microsoft.Extensions.Hosting;

namespace AiAssistant.Engine.Services;

public class IndexingRequest
{
    public string FilePath { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool IsFullScan { get; set; }
}

public class IndexingService : BackgroundService
{
    private readonly Channel<IndexingRequest> _queue;
    private readonly ISemanticSearchIndexer _indexer;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IEmbeddingSearchRepository _repository;
    private readonly ISettingsService _settingsService;
    private readonly Dictionary<string, DateTime> _lastSaveTimes = new();

    public event EventHandler<int>? IndexingStarted;
    public event EventHandler<(string FileName, int Remaining)>? FileIndexed;
    public event EventHandler<(int ChunksIndexed, TimeSpan Duration)>? IndexingCompleted;
    public event EventHandler<string>? IndexingLog;

    private int _remainingFiles;
    private int _totalChunksIndexed;
    private DateTime _scanStartTime;
    private bool _isScanning;

    public IndexingService(
        ISemanticSearchIndexer indexer,
        IEmbeddingProvider embeddingProvider,
        IEmbeddingSearchRepository repository,
        ISettingsService settingsService)
    {
        _indexer = indexer;
        _embeddingProvider = embeddingProvider;
        _repository = repository;
        _settingsService = settingsService;

        _queue = Channel.CreateBounded<IndexingRequest>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public void EnqueueFile(string filePath, string reason, bool isFullScan)
    {
        if (!_settingsService.EmbeddingIndexingEnabled) return;

        // Debounce saves
        if (reason == "FileSaved")
        {
            if (_lastSaveTimes.TryGetValue(filePath, out var lastSave))
            {
                if ((DateTime.UtcNow - lastSave).TotalSeconds < 2) return;
            }
            _lastSaveTimes[filePath] = DateTime.UtcNow;
        }

        _queue.Writer.TryWrite(new IndexingRequest
        {
            FilePath = filePath,
            Reason = reason,
            IsFullScan = isFullScan
        });
    }

    public void StartFullScan(IEnumerable<string> filePaths)
    {
        if (!_settingsService.EmbeddingIndexingEnabled) return;

        var paths = filePaths.ToList();
        _remainingFiles = paths.Count;
        _totalChunksIndexed = 0;
        _scanStartTime = DateTime.UtcNow;
        _isScanning = true;

        IndexingStarted?.Invoke(this, paths.Count);

        foreach (var path in paths)
        {
            EnqueueFile(path, "SolutionOpened", true);
        }
    }

    public void StopFullScan()
    {
        if (_isScanning)
        {
            _remainingFiles = 0;
            _isScanning = false;
            IndexingCompleted?.Invoke(this, (_totalChunksIndexed, DateTime.UtcNow - _scanStartTime));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await _queue.Reader.WaitToReadAsync(stoppingToken))
        {
            while (_queue.Reader.TryRead(out var request))
            {
                if (!_settingsService.EmbeddingIndexingEnabled)
                {
                    _remainingFiles = 0;
                    _isScanning = false;
                    continue; // Skip if indexing is disabled
                }

                try
                {
                    await ProcessFileAsync(request, stoppingToken);
                }
                catch (Exception ex)
                {
                    var msg = $"Error for {Path.GetFileName(request.FilePath)}: {ex.Message}";
                    System.Diagnostics.Debug.WriteLine($"[ApexCode] {msg}");
                    IndexingLog?.Invoke(this, msg);
                }

                if (_isScanning)
                {
                    _remainingFiles--;
                    FileIndexed?.Invoke(this, (Path.GetFileName(request.FilePath), _remainingFiles));

                    if (_remainingFiles <= 0)
                    {
                        _isScanning = false;
                        IndexingCompleted?.Invoke(this, (_totalChunksIndexed, DateTime.UtcNow - _scanStartTime));
                    }
                }
            }
        }
    }

    private async Task ProcessFileAsync(IndexingRequest request, CancellationToken ct)
    {
        if (!File.Exists(request.FilePath))
        {
            await _repository.DeleteChunksForFileAsync(request.FilePath, ct);
            return;
        }

        var fileInfo = new FileInfo(request.FilePath);
        if (fileInfo.Length > 10 * 1024 * 1024) return; // Skip files > 10MB

        var lastIndexed = await _repository.GetLastIndexedTimestampAsync(request.FilePath, ct);
        var fileModifiedTime = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeMilliseconds();

        // If doing a full scan and file hasn't changed, skip
        if (request.IsFullScan && lastIndexed.HasValue && lastIndexed.Value >= fileModifiedTime)
        {
            IndexingLog?.Invoke(this, $"Skipped {fileInfo.Name} (Unchanged)");
            return;
        }

        // Delete stale chunks
        await _repository.DeleteChunksForFileAsync(request.FilePath, ct);

        // Chunk file
        var content = File.ReadAllText(request.FilePath);
        var chunks = (await _indexer.ChunkFileAsync(request.FilePath, content, ct)).ToList();

        if (chunks.Count == 0) return;

        // Embed
        var embeddings = new List<float[]>();
        foreach (var chunk in chunks)
        {
            var emb = await _embeddingProvider.EmbedAsync(chunk.ChunkText, ct);
            embeddings.Add(emb);
        }

        // Insert
        await _repository.InsertChunksAsync(chunks, embeddings, ct);

        IndexingLog?.Invoke(this, $"Embedded {chunks.Count} chunks for {fileInfo.Name}");
        _totalChunksIndexed += chunks.Count;
    }
}
