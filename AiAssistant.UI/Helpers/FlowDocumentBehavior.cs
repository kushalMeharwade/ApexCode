using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace AiAssistant.UI.Helpers
{
    public static class FlowDocumentBehavior
    {
        public static readonly DependencyProperty DocumentProperty =
            DependencyProperty.RegisterAttached(
                "Document",
                typeof(FlowDocument),
                typeof(FlowDocumentBehavior),
                new PropertyMetadata(null, OnDocumentChanged));

        public static void SetDocument(DependencyObject element, FlowDocument value)
        {
            element.SetValue(DocumentProperty, value);
        }

        public static FlowDocument GetDocument(DependencyObject element)
        {
            return (FlowDocument)element.GetValue(DocumentProperty);
        }

        private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FlowDocumentScrollViewer viewer)
            {
                ApplyDocument(viewer, e.NewValue as FlowDocument);

                // FIX (scroll breaks markdown): VirtualizingStackPanel Recycling mode reuses
                // the same FlowDocumentScrollViewer instance when items scroll back into view.
                // The viewer goes through an Unloaded→Loaded cycle during the transition. The
                // zombie-parent check in TextElement.Document.get fires during Unloaded and nulls
                // out _document; the binding re-read on re-entry then receives null and shows raw
                // text. Subscribing to Loaded here means every time the viewer comes back into the
                // visual tree (including after recycling) we re-read the attached DP value and
                // re-apply it, which calls RefreshDocument() and regenerates the FlowDocument.
                // We unsubscribe first to guarantee exactly one subscription per viewer instance.
                viewer.Loaded -= OnViewerLoaded;
                viewer.Loaded += OnViewerLoaded;
            }
        }

        private static void OnViewerLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is FlowDocumentScrollViewer viewer)
            {
                // Re-read the document currently stored in the attached DP.
                // If the bound TextElement.Document returned null during the recycling
                // transition (zombie-parent check), the DP still holds the last non-null
                // value set by the binding, so this is a no-op. But if the DataContext
                // changed to a new TextElement whose Document is null, we still need the
                // trigger evaluation to pick up the correct template — calling ApplyDocument
                // with null is safe and clears any stale viewer state.
                var doc = GetDocument(viewer);
                ApplyDocument(viewer, doc);

                // If the viewer has a DataContext that is a TextElement with text but no
                // document yet (e.g. the zombie check just evicted it), ask it to rebuild.
                if (doc == null && viewer.DataContext is AiAssistant.UI.ViewModels.TextElement el
                    && !el.IsCode && el.HasVisibleContent)
                {
                    el.RefreshDocument();
                }
            }
        }

        private static void ApplyDocument(FlowDocumentScrollViewer viewer, FlowDocument? newDoc)
        {
            // Always clear first for a clean state (handles stale document from prior binding).
            viewer.Document = null;

            if (newDoc != null)
            {
                // FlowDocument can only belong to one visual parent at a time.
                // When the visual tree is torn down (tab hidden) and rebuilt (tab shown),
                // the old FlowDocumentScrollViewer may still be the logical parent of the
                // document even though it is no longer in the visual tree. We must detach it
                // unconditionally — regardless of whether the old parent is the same element —
                // to avoid WPF throwing "Element already has a logical parent" exceptions that
                // silently swallow the document and leave the viewer blank after a tab switch.
                var oldParent = System.Windows.LogicalTreeHelper.GetParent(newDoc);
                if (oldParent is FlowDocumentScrollViewer oldViewer && !ReferenceEquals(oldViewer, viewer))
                {
                    // Detach from the old (possibly destroyed) viewer.
                    try { oldViewer.Document = null; }
                    catch { /* old viewer may be in an invalid state; ignore */ }
                }
            }

            viewer.Document = newDoc;
        }
    }
}
