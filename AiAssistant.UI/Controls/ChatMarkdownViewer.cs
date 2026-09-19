using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using Wpf.Ui.Markdown.Controls;
using Wpf.Ui.Markdown.Renderers;
using Wpf.Ui.Markdown.Renderers.Wpf;
using Wpf.Ui.Markdown.Renderers.Wpf.Extensions;
using Wpf.Ui.Markdown.Renderers.Wpf.Inlines;

namespace AiAssistant.UI.Controls;

/// <summary>Owns each message's document and batches streaming updates on the UI thread.</summary>
public sealed class ChatMarkdownViewer : MarkdownViewer
{
    private DispatcherTimer? _renderTimer;

    public static readonly DependencyProperty IsStreamingProperty = DependencyProperty.Register(
        nameof(IsStreaming), typeof(bool), typeof(ChatMarkdownViewer),
        new PropertyMetadata(false, (d, _) => ((ChatMarkdownViewer)d).RefreshDocument()));

    public bool IsStreaming
    {
        get => (bool)GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    public ChatMarkdownViewer()
    {
        Loaded += (_, _) => RenderNow();
        Unloaded += (_, _) => _renderTimer?.Stop();
    }

    protected override void RefreshDocument()
    {
        if (!IsLoaded) return;
        if (!IsStreaming || Document == null)
        {
            RenderNow();
            return;
        }

        if (_renderTimer == null)
        {
            _renderTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _renderTimer.Tick += (_, _) => RenderNow();
        }
        // Do not restart: a continuous stream must still render at regular intervals.
        _renderTimer.Start();
    }

    private void RenderNow()
    {
        _renderTimer?.Stop();
        try
        {
            Document = Wpf.Ui.Markdown.Markdown.ToFlowDocument(
                Markdown ?? string.Empty, Pipeline ?? DefaultPipeline, new ChatRenderer());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatMarkdownViewer] {ex.Message}");
            // Preserve content even if an incomplete image/link cannot be rendered.
            var fallback = new FlowDocument(new Paragraph(new Run(Markdown ?? string.Empty)));
            fallback.SetResourceReference(FrameworkContentElement.StyleProperty,
                Wpf.Ui.Markdown.Styles.DocumentStyleKey);
            Document = fallback;
        }
    }

    private sealed class ChatRenderer : WpfRenderer
    {
        protected override void LoadRenderers()
        {
            // The package's default implementation attaches every renderer to a static
            // theme event without detaching it. Use its renderers without that lifetime
            // coupling; our dynamic resources provide the host's current theme instead.
            ObjectRenderers.Add(new CodeBlockRenderer());
            ObjectRenderers.Add(new ListRenderer());
            ObjectRenderers.Add(new HeadingRenderer());
            ObjectRenderers.Add(new ParagraphRenderer());
            ObjectRenderers.Add(new QuoteBlockRenderer());
            ObjectRenderers.Add(new ThematicBreakRenderer());
            ObjectRenderers.Add(new AutolinkInlineRenderer());
            ObjectRenderers.Add(new SelectableCodeInlineRenderer());
            ObjectRenderers.Add(new DelimiterInlineRenderer());
            ObjectRenderers.Add(new EmphasisInlineRenderer());
            ObjectRenderers.Add(new HtmlEntityInlineRenderer());
            ObjectRenderers.Add(new LineBreakInlineRenderer());
            ObjectRenderers.Add(new LinkInlineRenderer());
            ObjectRenderers.Add(new LiteralInlineRenderer());
            ObjectRenderers.Add(new TableRenderer());
            ObjectRenderers.Add(new TaskListRenderer());
        }
    }

    private sealed class SelectableCodeInlineRenderer : WpfObjectRenderer<Markdig.Syntax.Inlines.CodeInline>
    {
        protected override void Write(WpfRenderer renderer, Markdig.Syntax.Inlines.CodeInline code)
        {
            // The package wraps inline code in a nested UI control. A normal Run
            // keeps inline code selectable as part of the surrounding sentence.
            var run = new Run(code.Content);
            run.SetResourceReference(FrameworkContentElement.StyleProperty,
                Wpf.Ui.Markdown.Styles.CodeStyleKey);
            renderer.WriteInline(run);
        }
    }
}
