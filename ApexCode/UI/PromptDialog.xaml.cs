using System.Windows;
using System.Windows.Input;

namespace ApexCode.UI
{
    public partial class PromptDialog : Window
    {
        public string PromptText { get; private set; }

        public PromptDialog(string activeFileName, int cursorLine, string selectedText)
        {
            InitializeComponent();
            
            var contextString = $"File: {activeFileName} | Line: {cursorLine}";
            if (!string.IsNullOrWhiteSpace(selectedText))
            {
                var truncatedSelection = selectedText.Length > 50 ? selectedText.Substring(0, 47) + "..." : selectedText;
                contextString += $" | Selection: {truncatedSelection}";
            }
            ContextLabel.Text = contextString;
            InputTextBox.Focus();
        }

        private void Execute_Click(object sender, RoutedEventArgs e)
        {
            Submit();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    // Allow Shift+Enter for new line
                    return;
                }
                
                e.Handled = true;
                Submit();
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }

        private void Submit()
        {
            var text = InputTextBox.Text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                ErrorLabel.Visibility = Visibility.Visible;
                return;
            }

            PromptText = text;
            DialogResult = true;
            Close();
        }
    }
}
