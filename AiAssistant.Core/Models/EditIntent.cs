using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AiAssistant.Core.Models
{
    public class EditIntent
    {
        [JsonPropertyName("edits")]
        public List<EditOperation> Edits { get; set; } = new List<EditOperation>();

        [JsonPropertyName("explanation")]
        public string? Explanation { get; set; }
    }

    public class EditOperation
    {
        [JsonPropertyName("file_path")]
        public string? FilePath { get; set; }

        [JsonPropertyName("search")]
        public string? Search { get; set; }

        [JsonPropertyName("replace")]
        public string? Replace { get; set; }
    }
}
