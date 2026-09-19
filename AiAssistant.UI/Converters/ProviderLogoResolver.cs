using System;

namespace AiAssistant.UI.Converters;

public static class ProviderLogoResolver
{
    public static Uri Resolve(string? logoResourceKey)
    {
        var slug = string.IsNullOrWhiteSpace(logoResourceKey) ? "generic" : logoResourceKey;
        return new Uri($"pack://application:,,,/AiAssistant.UI;component/Resources/ProviderLogos/{slug}.svg");
    }
}
