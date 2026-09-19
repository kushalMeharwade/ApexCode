using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace AiAssistant.UI.Controls;

/// <summary>
/// A themed, in-panel modal overlay used to replace Win32 <see cref="MessageBox"/> and
/// VisualBasic InputBox usage. Renders as a scrim + centered card matching the
/// DiffViewerOverlay pattern already used in ChatPanel, so it follows the extension's
/// VS-theme-aware brushes instead of the OS chrome.
///
/// This control must be hosted in the parent XAML with Visibility="Collapsed" and
/// Panel.ZIndex high enough to sit above other content (see DiffViewerOverlay usage
/// in ChatPanel.xaml for the established pattern).
/// </summary>
public partial class ConfirmationOverlay : UserControl
{
    private TaskCompletionSource<bool>? _tcs;

    public ConfirmationOverlay()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Shows an OK/Cancel (or Yes/No) confirmation card and awaits the user's choice.
    /// Returns true if the user confirmed, false if cancelled or dismissed via Escape.
    /// </summary>
    public Task<bool> ShowConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel", bool isDestructive = false)
    {
        return ShowInternalAsync(title, message, confirmText, cancelText, showCancel: true, isDestructive: isDestructive);
    }

    /// <summary>
    /// Shows an informational, OK-only notice card and awaits dismissal.
    /// </summary>
    public Task<bool> ShowInfoAsync(string title, string message, string confirmText = "OK")
    {
        return ShowInternalAsync(title, message, confirmText, cancelText: null, showCancel: false, isDestructive: false);
    }

    private Task<bool> ShowInternalAsync(string title, string message, string confirmText, string? cancelText, bool showCancel, bool isDestructive)
    {
        // If a previous prompt is still pending (shouldn't normally happen since the
        // overlay blocks input), resolve it as cancelled before starting a new one.
        _tcs?.TrySetResult(false);

        _tcs = new TaskCompletionSource<bool>();

        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText ?? "Cancel";
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;

        // For destructive actions, default focus to Cancel so a reflexive Enter press
        // doesn't confirm a delete. IsDefault stays on ConfirmButton for non-destructive
        // flows so pressing Enter confirms quickly.
        ConfirmButton.IsDefault = !isDestructive;
        CancelButton.IsDefault = isDestructive && showCancel;

        Visibility = Visibility.Visible;

        return _tcs.Task;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        Complete(true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Complete(false);
    }

    private void Complete(bool result)
    {
        Visibility = Visibility.Collapsed;
        var tcs = _tcs;
        _tcs = null;
        tcs?.TrySetResult(result);
    }

    private void ConfirmationOverlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Visibility == Visibility.Visible)
        {
            e.Handled = true;
            Complete(false);
        }
    }

    private void ConfirmationOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Visibility != Visibility.Visible) return;

        // Move keyboard focus into the card so Escape/Enter work immediately and the
        // control underneath the overlay can't be typed into while it's blocked.
        _ = Dispatcher.InvokeAsync(() =>
        {
            var target = ConfirmButton.IsDefault ? ConfirmButton : (Button)CancelButton;
            target.Focus();
        }, DispatcherPriority.Input);
    }
}
