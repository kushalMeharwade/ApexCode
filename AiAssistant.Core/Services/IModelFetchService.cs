using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiAssistant.Core.Services;

public interface IModelFetchService
{
    Task<IReadOnlyList<ModelInfo>> FetchModelsAsync(string providerType, string apiKey, string? apiEndpoint = null, string? modelFetchEndpoint = null, CancellationToken cancellationToken = default);
}

public record ModelInfo(
    string ModelId,
    string DisplayName,
    int ContextWindowTokens,
    decimal? InputPricePerMToken,
    decimal? OutputPricePerMToken,
    bool IsFree
);
