using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Core.Services;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApexCode.Services;

public class BgeEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    private Tokenizer _tokenizer;
    private InferenceSession _session;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);

    // BGE-small uses 384 dimensions
    public int Dimensions => 384;

    // BGE instruction prefix for queries
    private const string QueryPrefix = "Represent this sentence for searching relevant passages: ";

    private readonly string _modelDirectory;

    public BgeEmbeddingProvider(string modelDirectory)
    {
        _modelDirectory = modelDirectory;

        try
        {
            InitializeSession();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApexCode] Failed to load ONNX model initially: {ex.Message}");
        }
    }

    private void InitializeSession()
    {
        if (_session != null) return;
        
        var options = new SessionOptions
        {
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        var modelPath = Path.Combine(_modelDirectory, "model_quantized.onnx");
        if (File.Exists(modelPath))
        {
            _session = new InferenceSession(modelPath, options);
        }
    }

    private async Task EnsureTokenizerInitializedAsync(CancellationToken ct)
    {
        if (_tokenizer != null) return;
        
        await _inferenceLock.WaitAsync(ct);
        try
        {
            if (_tokenizer == null)
            {
                var tokenizerPath = Path.Combine(_modelDirectory, "tokenizer.json");
                if (!File.Exists(tokenizerPath))
                {
                    throw new FileNotFoundException($"BGE tokenizer not found at: {tokenizerPath}");
                }

                var json = File.ReadAllText(tokenizerPath);
                var doc = JsonNode.Parse(json);
                var vocabObj = doc["model"]["vocab"].AsObject();
                var vocabList = new string[vocabObj.Count];
                foreach (var kv in vocabObj)
                {
                    vocabList[(int)kv.Value] = kv.Key;
                }
                var vocabText = string.Join("\n", vocabList);
                var vocabStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(vocabText));
                
                _tokenizer = BertTokenizer.Create(vocabStream, null);
            }
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        return await GenerateEmbeddingAsync(text, ct);
    }

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct = default)
    {
        return await GenerateEmbeddingAsync(QueryPrefix + query, ct);
    }

    private async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct)
    {
        if (_session == null)
        {
            InitializeSession();
            if (_session == null)
            {
                throw new InvalidOperationException("Semantic Search embedding model is not installed. Please download the models first.");
            }
        }

        await EnsureTokenizerInitializedAsync(ct);
        
        var resultIds = _tokenizer.EncodeToIds(text);
        
        // Truncate to 512
        var tokens = resultIds.Take(512).Select(i => (long)i).ToArray();
        // BGE requires [CLS] (101) at the start and [SEP] (102) at the end
        if (tokens.Length == 0 || tokens[0] != 101)
        {
            var withSpecial = new long[Math.Min(512, tokens.Length + 2)];
            withSpecial[0] = 101;
            Array.Copy(tokens, 0, withSpecial, 1, Math.Min(tokens.Length, 510));
            withSpecial[withSpecial.Length - 1] = 102;
            tokens = withSpecial;
        }

        var attentionMask = Enumerable.Repeat(1L, tokens.Length).ToArray();
        var tokenTypeIds = Enumerable.Repeat(0L, tokens.Length).ToArray();

        var inputIdsTensor = new DenseTensor<long>(tokens.ToArray(), new[] { 1, tokens.Length });
        var attentionMaskTensor = new DenseTensor<long>(attentionMask.ToArray(), new[] { 1, tokens.Length });
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds.ToArray(), new[] { 1, tokens.Length });

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
        };

        await _inferenceLock.WaitAsync(ct);
        try
        {
            using var runResult = _session.Run(inputs);
            var lastHiddenState = runResult.First(r => r.Name == "last_hidden_state").AsTensor<float>();

            return MeanPoolAndNormalize(lastHiddenState, attentionMask);
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private float[] MeanPoolAndNormalize(Tensor<float> lastHiddenState, long[] attentionMask)
    {
        var seqLength = attentionMask.Length;
        var vector = new float[Dimensions];

        // Mean pooling
        for (int i = 0; i < seqLength; i++)
        {
            if (attentionMask[i] == 0) continue;

            for (int j = 0; j < Dimensions; j++)
            {
                vector[j] += lastHiddenState[0, i, j];
            }
        }

        var validTokens = attentionMask.Count(m => m == 1);
        var sumSquares = 0f;

        for (int j = 0; j < Dimensions; j++)
        {
            vector[j] /= validTokens;
            sumSquares += vector[j] * vector[j];
        }

        // L2 normalization
        var magnitude = (float)Math.Sqrt(sumSquares);
        if (magnitude > 0)
        {
            for (int j = 0; j < Dimensions; j++)
            {
                vector[j] /= magnitude;
            }
        }

        return vector;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _inferenceLock?.Dispose();
    }
}

