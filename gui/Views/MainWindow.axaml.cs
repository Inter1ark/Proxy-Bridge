using Avalonia.Controls;
using ProxyBridge.GUI.ViewModels;

namespace ProxyBridge.GUI.Views;

public partial class MainWindow : Window
{
    private bool _forceClose = false;

    public MainWindow()
    {
        InitializeComponent();
        
        this.Opened += (s, e) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.SetMainWindow(this);
            }
        };
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            if (vm.MinimizeToTray && !_forceClose)
            {
                e.Cancel = true;
                this.Hide();
                return;
            }

            vm.Cleanup();
        }
        base.OnClosing(e);
    }
}
