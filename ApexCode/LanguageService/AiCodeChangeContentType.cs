using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Utilities;

#pragma warning disable CS0649 // MEF will set these fields at runtime

namespace ApexCode.LanguageService;

internal static class AiCodeChangeContentType
{
    public const string ContentTypeName = "AiCodeChange";

    [Export]
    [Name(ContentTypeName)]
    [BaseDefinition("text")]
    internal static ContentTypeDefinition? AiCodeChangeContentTypeDefinition;

    [Export]
    [FileExtension(".ai_code_change")]
    [ContentType(ContentTypeName)]
    internal static FileExtensionToContentTypeDefinition? AiCodeChangeFileExtensionDefinition;
}

