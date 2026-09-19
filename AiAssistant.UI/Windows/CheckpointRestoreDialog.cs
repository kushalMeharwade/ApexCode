using System;
using System.Windows;
using System.Windows.Controls;

namespace AiAssistant.UI.Windows;

internal static class CheckpointRestoreDialog
{
    public static bool Confirm(Window? owner, string restoreType)
    {
        string action;
        string message;
        switch (restoreType)
        {
            case "taskAndWorkspace":
                action = "Revert chat and files";
                message = "Return the conversation and checkpoint-covered files to before this request. " +
                    "This request and later messages will be removed. Later file changes will be undone, including your own edits to covered files. " +
                    "Files created since then will be removed. The request will return to the message box for editing.";
                break;
            case "task":
                action = "Revert chat";
                message = "Remove this request and all later messages from the conversation. " +
                    "Your files will stay unchanged. The request will return to the message box for editing.";
                break;
            case "workspace":
                action = "Restore files";
                message = "Restore checkpoint-covered files to their saved state before this request. " +
                    "Later changes, including your own edits to covered files, will be undone. Files created since then will be removed, " +
                    "and changed or deleted files will be restored. Your conversation will stay unchanged.";
                break;
            default:
                throw new ArgumentException("Unknown checkpoint action.", nameof(restoreType));
        }

        var dialog = new Window
        {
            Title = restoreType == "workspace" ? "Restore files to this checkpoint?" : action + "?",
            Width = 490, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        if (owner != null) dialog.Owner = owner;
        Helpers.ThemeManager.Initialize();
        dialog.Resources.MergedDictionaries.Add(Helpers.ThemeManager.GetThemeDictionaryContainer());
        dialog.SetResourceReference(Control.BackgroundProperty, "PanelBackgroundBrush");
        dialog.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        var body = new StackPanel { Margin = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, Padding = new Thickness(14, 7, 14, 7), MinWidth = 80 };
        var confirm = new Button { Content = action, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        confirm.Click += (_, __) => dialog.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        body.Children.Add(buttons);
        dialog.Content = body;
        return dialog.ShowDialog() == true;
    }
}
