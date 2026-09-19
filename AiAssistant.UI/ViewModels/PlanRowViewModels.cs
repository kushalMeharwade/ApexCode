using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace AiAssistant.UI.ViewModels;

public class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class PlanSectionRowViewModel : ViewModelBase
{
    private string _text;
    private string _comment;
    private bool _isCommentVisible;
    private bool _isSent;

    private string _indexText;

    public string IndexText
    {
        get => _indexText;
        set { _indexText = value; OnPropertyChanged(); }
    }

    public string Text
    {
        get => _text;
        set { _text = value; OnPropertyChanged(); }
    }

    public string Comment
    {
        get => _comment;
        set { _comment = value; OnPropertyChanged(); }
    }

    public bool IsCommentVisible
    {
        get => _isCommentVisible;
        set { _isCommentVisible = value; OnPropertyChanged(); }
    }

    public bool IsSent
    {
        get => _isSent;
        set { _isSent = value; OnPropertyChanged(); }
    }
}

public class PlanFileRowViewModel : PlanSectionRowViewModel
{
    private string _action;
    private string _path;
    private string _description;
    private Brush _actionBadgeColor;

    public string Action
    {
        get => _action;
        set { _action = value; OnPropertyChanged(); }
    }

    public string Path
    {
        get => _path;
        set { _path = value; OnPropertyChanged(); }
    }

    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged(); }
    }

    public Brush ActionBadgeColor
    {
        get => _actionBadgeColor;
        set { _actionBadgeColor = value; OnPropertyChanged(); }
    }
}
