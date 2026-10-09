using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ProxyBridge.GUI.ViewModels;

namespace ProxyBridge.GUI.Views.Pages;

public partial class ProxiesPage : UserControl
{
    public ProxiesPage()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ProxyItem item } || Vm == null) return;
        if (e.Key == Key.Enter)
        {
            Vm.CommitRenameCommand.Execute(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Vm.CancelRenameCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ProxyItem item } && item.IsEditing)
            Vm?.CommitRenameCommand.Execute(item);
    }

    private void OnRenamePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && e.NewValue is true && sender is TextBox tb)
        {
            Dispatcher.UIThread.Post(() =>
            {
                tb.Focus();
                tb.SelectAll();
            }, DispatcherPriority.Input);
        }
    }
}
