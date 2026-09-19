using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AiAssistant.UI.Controls;

public static class FocusHelper
{
    public static readonly DependencyProperty IsFocusedProperty =
        DependencyProperty.RegisterAttached(
            "IsFocused", typeof(bool), typeof(FocusHelper),
            new UIPropertyMetadata(false, OnIsFocusedPropertyChanged));

    public static bool GetIsFocused(DependencyObject obj)
    {
        return (bool)obj.GetValue(IsFocusedProperty);
    }

    public static void SetIsFocused(DependencyObject obj, bool value)
    {
        obj.SetValue(IsFocusedProperty, value);
    }

    private static void OnIsFocusedPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var uie = (UIElement)d;
        if ((bool)e.NewValue)
        {
            uie.Dispatcher.BeginInvoke(new Action(() =>
            {
                uie.Focus();
                if (uie is TextBox textBox)
                {
                    textBox.CaretIndex = textBox.Text.Length;
                }
            }), DispatcherPriority.Loaded);
        }
    }
}

public partial class PlanReviewPanel : UserControl
{
    public PlanReviewPanel()
    {
        InitializeComponent();
    }
}
