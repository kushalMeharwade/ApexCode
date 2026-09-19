using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AiAssistant.UI.ViewModels;

public class HistoryGroupViewModel : INotifyPropertyChanged
{
    private string _header = "";
    private bool _isExpanded = true;

    public string Header
    {
        get => _header;
        set { _header = value; OnPropertyChanged(); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public ObservableCollection<HistoryItemViewModel> Items { get; set; } = new();

    public HistoryGroupViewModel(string header)
    {
        _header = header;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = "")
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
