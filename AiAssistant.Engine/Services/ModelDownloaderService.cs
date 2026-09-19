using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AiAssistant.Engine.Services;

public class ModelDownloaderService
{
    private readonly HttpClient _httpClient;
    
    // Hardcoded HuggingFace URLs for the BGE Small v1.5 model and tokenizer
    private const string ModelUrl = "https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/main/onnx/model_quantized.onnx";
    private const string TokenizerUrl = "https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/main/tokenizer.json";

    public ModelDownloaderService()
    {
        _httpClient = new HttpClient();
    }

    public async Task DownloadModelsAsync(string outputDirectory, IProgress<double> progress, CancellationToken ct = default)
    {
        if (!Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var modelPath = Path.Combine(outputDirectory, "model_quantized.onnx");
        var tokenizerPath = Path.Combine(outputDirectory, "tokenizer.json");

        // We download tokenizer first since it's small, then the larger ONNX model
        await DownloadFileAsync(TokenizerUrl, tokenizerPath, null, ct);
        await DownloadFileAsync(ModelUrl, modelPath, progress, ct);
    }

    private async Task DownloadFileAsync(string url, string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        var canReportProgress = totalBytes != -1 && progress != null;

        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
        
        var totalRead = 0L;
        var buffer = new byte[8192];
        var isMoreToRead = true;

        do
        {
            var read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct);
            if (read == 0)
            {
                isMoreToRead = false;
            }
            else
            {
                await fileStream.WriteAsync(buffer, 0, read, ct);

                totalRead += read;

                if (canReportProgress)
                {
                    var percentage = (double)totalRead / totalBytes * 100;
                    progress?.Report(percentage);
                }
            }
        } while (isMoreToRead);
    }
}
