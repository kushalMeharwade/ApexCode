namespace AiAssistant.Storage.Models;

public class EmbeddingChunk
{
    public string Id { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string ChunkText { get; set; } = "";
    public string ChunkType { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? SymbolName { get; set; }
    public long LastIndexed { get; set; }
}

public class EmbeddingSearchResult
{
    public EmbeddingChunk Chunk { get; set; } = new();
    public float Distance { get; set; }
}
