using System;

namespace AiAssistant.UI.Converters;

public static class PersonaLogoResolver
{
    public static Uri Resolve(string? logoResourceKey)
    {
        var slug = string.IsNullOrWhiteSpace(logoResourceKey) ? "generic" : logoResourceKey;
        // fallback to provider generic icon if persona doesn't have one since we only added specific ones
        var resourcePath = (slug == "generic" || slug == "general")
            ? "pack://application:,,,/AiAssistant.UI;component/Resources/ProviderLogos/generic.svg"
            : $"pack://application:,,,/AiAssistant.UI;component/Resources/PersonaLogos/{slug}.svg";
        return new Uri(resourcePath);
    }
}
