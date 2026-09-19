using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Text;

namespace AiAssistant.Engine.Models
{
    public class EditSessionState
    {
        public int VersionNumber { get; }
        public string FilePath { get; }
        public DateTime Timestamp { get; }
        public IReadOnlyList<Diagnostic> PreExistingDiagnostics { get; }

        public EditSessionState(
            int versionNumber,
            string filePath,
            IReadOnlyList<Diagnostic> preExistingDiagnostics)
        {
            VersionNumber = versionNumber;
            FilePath = filePath;
            Timestamp = DateTime.UtcNow;
            PreExistingDiagnostics = preExistingDiagnostics;
        }
    }
}
