using System.Threading;
using System.Threading.Tasks;

namespace AiAssistant.Core.Services;

public interface IEmbeddingProvider
{
    /// <summary>
    /// The output dimension count of the model (e.g., 384 for BGE-small).
    /// </summary>
    int Dimensions { get; }

    /// <summary>
    /// Generates an embedding for the given text.
    /// </summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Generates an embedding for a user query.
    /// Some models (like BGE) require a specific prefix for search queries.
    /// </summary>
    Task<float[]> EmbedQueryAsync(string query, CancellationToken ct = default);
}
