using System;

namespace AiAssistant.Storage.Models;

public record UICustomizationPreferences
{
    public int AppFontSizeOffset { get; set; } = 0;
    public int MessageFontSizeOffset { get; set; } = 0;
    public int InputFontSizeOffset { get; set; } = 0;
    public int CodeFontSizeOffset { get; set; } = 0;
    public string AppFontFamily { get; set; } = string.Empty;
    public string CodeFontFamily { get; set; } = string.Empty;
}
