using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Document;
using System.Linq;

namespace AiAssistant.UI.Helpers;

public class ThemeAwareColorizer : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        // Only adjust colors if we are in Dark Theme
        if (!ThemeManager.IsDarkTheme)
            return;

        int lineStartOffset = line.Offset;
        string text = CurrentContext.Document.GetText(line);
        if (string.IsNullOrEmpty(text))
            return;

        ChangeLinePart(
            lineStartOffset,
            lineStartOffset + line.Length,
            element =>
            {
                // Remove hardcoded light backgrounds so the dark theme shines through
                if (element.TextRunProperties.BackgroundBrush is SolidColorBrush bgBrush)
                {
                    if (bgBrush.Color.R > 200 && bgBrush.Color.G > 200 && bgBrush.Color.B > 200)
                    {
                        element.TextRunProperties.SetBackgroundBrush(null);
                    }
                }

                // Adjust foreground for readability in dark mode
                if (element.TextRunProperties.ForegroundBrush is SolidColorBrush fgBrush)
                {
                    var newBrush = AdjustForDarkTheme(fgBrush);
                    if (newBrush != fgBrush)
                    {
                        element.TextRunProperties.SetForegroundBrush(newBrush);
                    }
                }
            });
    }

    private SolidColorBrush AdjustForDarkTheme(SolidColorBrush original)
    {
        var color = original.Color;
        
        // Simple YIQ luminance calculation
        double yiq = (color.R * 299 + color.G * 587 + color.B * 114) / 1000.0;
        
        // If the color is already bright enough, keep it
        if (yiq >= 100)
            return original;

        // Black / Very Dark Gray -> Light Gray
        if (color.R < 50 && color.G < 50 && color.B < 50)
            return new SolidColorBrush(Color.FromRgb(220, 220, 220));

        // Blue / Dark Blue -> Light Cyan/Blue (VS Code dark theme style)
        if (color.B > color.R && color.B > color.G)
            return new SolidColorBrush(Color.FromRgb(86, 156, 214)); 

        // Red / Dark Red / Brown -> Light Orange/Pink
        if (color.R > color.B && color.R > color.G)
            return new SolidColorBrush(Color.FromRgb(206, 145, 120)); 

        // Green / Dark Green -> Light Green
        if (color.G > color.R && color.G > color.B)
            return new SolidColorBrush(Color.FromRgb(181, 206, 168)); 

        // Fallback: Just lighten the color directly
        byte Lighten(byte b) => (byte)System.Math.Min(255, b + 100);
        return new SolidColorBrush(Color.FromRgb(Lighten(color.R), Lighten(color.G), Lighten(color.B)));
    }
}
