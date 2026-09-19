using System.Windows;
using ICSharpCode.AvalonEdit;
using System.Linq;

namespace AiAssistant.UI.Helpers;

public static class AvalonEditHelper
{
    public static readonly DependencyProperty BoundTextProperty =
        DependencyProperty.RegisterAttached("BoundText", typeof(string), typeof(AvalonEditHelper), new PropertyMetadata(string.Empty, OnBoundTextChanged));

    public static string GetBoundText(DependencyObject obj)
    {
        return (string)obj.GetValue(BoundTextProperty);
    }

    public static void SetBoundText(DependencyObject obj, string value)
    {
        obj.SetValue(BoundTextProperty, value);
    }

    private static void OnBoundTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextEditor editor)
        {
            if (editor.Text != (string)e.NewValue)
            {
                editor.Text = (string)e.NewValue;
            }
        }
    }

    public static readonly DependencyProperty LanguageProperty =
        DependencyProperty.RegisterAttached("Language", typeof(string), typeof(AvalonEditHelper), new PropertyMetadata(string.Empty, OnLanguageChanged));

    public static string GetLanguage(DependencyObject obj)
    {
        return (string)obj.GetValue(LanguageProperty);
    }

    public static void SetLanguage(DependencyObject obj, string value)
    {
        obj.SetValue(LanguageProperty, value);
    }

    private static void OnLanguageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextEditor editor)
        {
            string lang = (e.NewValue as string)?.ToLowerInvariant() ?? "";
            
            // Map common markdown language tags to AvalonEdit definitions
            string definitionName = lang switch
            {
                "csharp" or "cs" => "C#",
                "javascript" or "js" => "JavaScript",
                "typescript" or "ts" => "JavaScript", // AvalonEdit doesn't have TS out of the box, fallback to JS
                "xml" or "html" or "xaml" => "XML",
                "python" or "py" => "Python",
                "sql" => "TSQL",
                "cpp" or "c++" or "c" => "C++",
                "java" => "Java",
                "php" => "PHP",
                "css" => "CSS",
                _ => null
            };

            if (definitionName != null)
            {
                editor.SyntaxHighlighting = ICSharpCode.AvalonEdit.Highlighting.HighlightingManager.Instance.GetDefinition(definitionName);
            }
            else
            {
                editor.SyntaxHighlighting = null;
            }
            
            // Add theme-aware colorizer to adapt light-themed highlights to dark mode
            var transformers = editor.TextArea.TextView.LineTransformers;
            if (!transformers.OfType<ThemeAwareColorizer>().Any())
            {
                transformers.Add(new ThemeAwareColorizer());
            }
        }
    }
}
