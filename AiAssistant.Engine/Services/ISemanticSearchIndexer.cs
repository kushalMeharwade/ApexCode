using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiAssistant.Storage.Models;

namespace AiAssistant.Engine.Services;

public interface ISemanticSearchIndexer
{
    Task<IEnumerable<EmbeddingChunk>> ChunkFileAsync(string filePath, string fileContent, CancellationToken ct = default);
}
