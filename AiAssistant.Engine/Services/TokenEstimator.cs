using System;
using System.IO;
using System.Threading.Tasks;

namespace AiAssistant.Engine.Services;

/// <summary>
/// Estimates token counts for files using various strategies.
/// </summary>
public class TokenEstimator
{
    private readonly TokenEstimationStrategy _strategy;

    public TokenEstimator(TokenEstimationStrategy strategy = TokenEstimationStrategy.CharacterCount)
    {
        _strategy = strategy;
    }

    /// <summary>
    /// Estimates token count for a file.
    /// </summary>
    public async Task<int> EstimateAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return 0;

        try
        {
            return _strategy switch
            {
                TokenEstimationStrategy.LineCount => await EstimateByLineCountAsync(filePath),
                TokenEstimationStrategy.CharacterCount => await EstimateByCharacterCountAsync(filePath),
                TokenEstimationStrategy.ActualTokenize => await EstimateByActualTokenizationAsync(filePath),
                _ => await EstimateByCharacterCountAsync(filePath)
            };
        }
        catch
        {
            // Fallback to file size / 4
            var fileInfo = new FileInfo(filePath);
            return (int)(fileInfo.Length / 4);
        }
    }

    /// <summary>
    /// Estimates based on line count (fast but rough).
    /// Assumes ~4 tokens per line on average.
    /// </summary>
    private async Task<int> EstimateByLineCountAsync(string filePath)
    {
        int lineCount = 0;
        await foreach (var line in ReadLinesAsync(filePath))
        {
            lineCount++;
        }
        return lineCount * 4;
    }

    /// <summary>
    /// Estimates based on character count (balanced).
    /// Assumes ~4 characters per token on average.
    /// </summary>
    private async Task<int> EstimateByCharacterCountAsync(string filePath)
    {
        var content = await Task.Run(() => File.ReadAllText(filePath));
        return content.Length / 4;
    }

    /// <summary>
    /// Estimates using actual tokenization (accurate but slow).
    /// TODO: Integrate with tiktoken or SharpToken library.
    /// </summary>
    private async Task<int> EstimateByActualTokenizationAsync(string filePath)
    {
        // For now, fallback to character count
        // TODO: Implement actual tokenization using tiktoken/SharpToken
        return await EstimateByCharacterCountAsync(filePath);
    }

    /// <summary>
    /// Estimates token count for a string content.
    /// </summary>
    public int EstimateFromContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return 0;

        return _strategy switch
        {
            TokenEstimationStrategy.LineCount => EstimateByLineCountFromContent(content),
            TokenEstimationStrategy.CharacterCount => content.Length / 4,
            TokenEstimationStrategy.ActualTokenize => content.Length / 4, // TODO: Actual tokenization
            _ => content.Length / 4
        };
    }

    private int EstimateByLineCountFromContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return 0;

        int lineCount = 1;
        foreach (char c in content)
        {
            if (c == '\n')
                lineCount++;
        }
        return lineCount * 4;
    }

    private async System.Collections.Generic.IAsyncEnumerable<string> ReadLinesAsync(string filePath)
    {
        using var reader = new StreamReader(filePath);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            yield return line;
        }
    }
}
