using System.Collections.Generic;

namespace AiAssistant.Core.Models
{
    public class EditorContextData
    {
        public string FilePath { get; set; } = "";
        public string FileContent { get; set; } = "";
        public string SelectedText { get; set; } = "";
        public int CursorPosition { get; set; }
        public int CursorLine { get; set; }
        public int VersionNumber { get; set; }
        
        // Roslyn / Build Data
        public string TargetFramework { get; set; } = "";
        public List<string> NuGetPackages { get; set; } = new();
        public List<string> ActiveDiagnostics { get; set; } = new();
        
        // Symbols
        public string SymbolName { get; set; } = "";
        public string SymbolDefinition { get; set; } = "";
        public string ContainingType { get; set; } = "";
        public List<string> SymbolReferences { get; set; } = new();
    }
}
