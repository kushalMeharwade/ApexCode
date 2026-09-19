using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AiAssistant.Core.Services;

namespace AiAssistant.UI.Controls;

public partial class ModelPickerControl : UserControl
{
    private ICollectionView? _modelsView;
    private IReadOnlyList<ModelInfo> _allModels = new List<ModelInfo>();

    public event EventHandler? RetryClicked;
    public event EventHandler? SelectedModelChanged;

    public static readonly DependencyProperty SelectedModelProperty = DependencyProperty.Register(
        nameof(SelectedModel), typeof(ModelInfo), typeof(ModelPickerControl), new PropertyMetadata(null, OnSelectedModelChanged));

    public ModelInfo? SelectedModel
    {
        get => (ModelInfo?)GetValue(SelectedModelProperty);
        set => SetValue(SelectedModelProperty, value);
    }

    private static void OnSelectedModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ModelPickerControl control)
        {
            control.SelectedModelChanged?.Invoke(control, EventArgs.Empty);
        }
    }

    public ModelPickerControl()
    {
        InitializeComponent();
    }

    public bool HasModels() => _allModels.Count > 0;

    public void SetModels(IReadOnlyList<ModelInfo> models)
    {
        _allModels = models;
        _modelsView = CollectionViewSource.GetDefaultView(_allModels);
        _modelsView.Filter = FilterModel;
        
        ModelList.ItemsSource = _modelsView;
        
        StatusBorder.Visibility = Visibility.Collapsed;
        ModelList.Visibility = Visibility.Visible;
        
        ApplySort();

        // Try to keep selection if it exists in the new list
        if (SelectedModel != null)
        {
            var match = _allModels.FirstOrDefault(m => m.ModelId == SelectedModel.ModelId);
            if (match != null)
                SelectedModel = match;
            else if (_allModels.Count > 0)
                SelectedModel = _allModels[0];
        }
        else if (_allModels.Count > 0)
        {
            SelectedModel = _allModels[0];
        }

        // Close the dropdown after models are loaded
        DropdownToggle.IsChecked = false;
    }

    public void ShowLoading()
    {
        DropdownToggle.IsChecked = false;
        ModelList.Visibility = Visibility.Collapsed;
        StatusBorder.Visibility = Visibility.Visible;
        StatusText.Text = "Loading models...";
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        RetryButton.Visibility = Visibility.Collapsed;
    }

    public void ShowError(string message)
    {
        DropdownToggle.IsChecked = false;
        ModelList.Visibility = Visibility.Collapsed;
        StatusBorder.Visibility = Visibility.Visible;
        StatusText.Text = message;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("ErrorRedBrush");
        RetryButton.Visibility = Visibility.Visible;
    }

    private bool FilterModel(object obj)
    {
        if (obj is not ModelInfo model) return false;

        if (FreeOnlyCheck.IsChecked == true && !model.IsFree)
            return false;

        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
            return true;

        return model.DisplayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
               model.ModelId.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void ApplySort()
    {
        if (_modelsView == null) return;
        
        _modelsView.SortDescriptions.Clear();
        
        if (SortCombo.SelectedItem is ComboBoxItem item && item.Tag?.ToString() == "Price")
        {
            _modelsView.SortDescriptions.Add(new SortDescription(nameof(ModelInfo.InputPricePerMToken), ListSortDirection.Ascending));
            _modelsView.SortDescriptions.Add(new SortDescription(nameof(ModelInfo.DisplayName), ListSortDirection.Ascending));
        }
        else
        {
            _modelsView.SortDescriptions.Add(new SortDescription(nameof(ModelInfo.DisplayName), ListSortDirection.Ascending));
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _modelsView?.Refresh();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _modelsView?.Refresh();
        ApplySort();
    }

    private void DropdownToggle_Click(object sender, RoutedEventArgs e)
    {
        if (DropdownToggle.IsChecked == true)
        {
            SearchBox.Text = "";
            SearchBox.Focus();
        }
    }

    private void ModelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelList.SelectedItem is ModelInfo model)
        {
            SelectedModel = model;
            DropdownToggle.IsChecked = false;
        }
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        RetryClicked?.Invoke(this, EventArgs.Empty);
    }
}
