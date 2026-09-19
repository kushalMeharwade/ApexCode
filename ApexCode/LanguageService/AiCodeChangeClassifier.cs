using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace ApexCode.LanguageService;

[Export(typeof(IClassifierProvider))]
[ContentType(AiCodeChangeContentType.ContentTypeName)]
internal class AiCodeChangeClassifierProvider : IClassifierProvider
{
    [Import]
    internal IClassificationTypeRegistryService ClassificationRegistry = null!;

    public IClassifier GetClassifier(ITextBuffer textBuffer)
    {
        return textBuffer.Properties.GetOrCreateSingletonProperty(() => new AiCodeChangeClassifier(ClassificationRegistry));
    }
}

internal class AiCodeChangeClassifier : IClassifier
{
    private readonly IClassificationType _keywordType;
    private readonly IClassificationType _variableType;
    private readonly IClassificationType _commentType;

    public AiCodeChangeClassifier(IClassificationTypeRegistryService registry)
    {
        _keywordType = registry.GetClassificationType("keyword");
        _variableType = registry.GetClassificationType("symbol definition");
        _commentType = registry.GetClassificationType("comment");
    }

#pragma warning disable 67
    public event EventHandler<ClassificationChangedEventArgs>? ClassificationChanged;
#pragma warning restore 67

    public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
    {
        var spans = new List<ClassificationSpan>();
        string text = span.GetText();

        // Highlight block headers like [System] or [User]
        var blockHeaderMatches = Regex.Matches(text, @"\[[A-Za-z]+\]");
        foreach (Match match in blockHeaderMatches)
        {
            spans.Add(new ClassificationSpan(new SnapshotSpan(span.Snapshot, span.Start + match.Index, match.Length), _keywordType));
        }

        // Highlight {{variables}}
        var varMatches = Regex.Matches(text, @"\{\{[^\}]+\}\}");
        foreach (Match match in varMatches)
        {
            spans.Add(new ClassificationSpan(new SnapshotSpan(span.Snapshot, span.Start + match.Index, match.Length), _variableType));
        }

        // Highlight comments starting with //
        int commentIndex = text.IndexOf("//");
        if (commentIndex >= 0)
        {
            spans.Add(new ClassificationSpan(new SnapshotSpan(span.Snapshot, span.Start + commentIndex, text.Length - commentIndex), _commentType));
        }

        return spans;
    }
}

