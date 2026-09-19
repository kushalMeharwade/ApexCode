using System.Collections.Generic;

namespace AiAssistant.Llm.Models
{
    public class PromptContext
    {
        public string TargetFramework { get; set; } = string.Empty;
        public List<string> NuGetPackages { get; set; } = new List<string>();
        
        public string ActiveFilePath { get; set; } = string.Empty;
        public string ActiveFileContent { get; set; } = string.Empty;
        
        public List<ContextFile> ContextFiles { get; set; } = new List<ContextFile>();
        
        public string SelectedText { get; set; } = string.Empty;
        public int CursorLine { get; set; }
        public int CursorPosition { get; set; }
        
        public List<string> ExistingErrors { get; set; } = new List<string>();
        
        public string UserRequest { get; set; } = string.Empty;
    }

    public class ContextFile
    {
        public string FilePath { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }
}
