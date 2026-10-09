using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using ProxyBridge.GUI.ViewModels;

namespace ProxyBridge.GUI.Views.Pages;

/// <summary>HOME: rules table, connection panel and the app picker overlay.</summary>
public partial class HomePage : UserControl
{
    private MainWindowViewModel? _vm;

    public HomePage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as MainWindowViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
        };
        AddHandler(KeyDownEvent, OnKeyDown, handledEventsToo: false);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsPickerOpen) && _vm?.IsPickerOpen == true)
        {
            // focus the search box once the overlay is laid out
            Dispatcher.UIThread.Post(() => PickerSearch.Focus(), DispatcherPriority.Input);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm?.IsPickerOpen == true)
        {
            _vm.PickerCancelCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm?.PickerCancelCommand.Execute(null);
        e.Handled = true;
    }
}
