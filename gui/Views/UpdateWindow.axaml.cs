using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ProxyBridge.GUI.ViewModels;

namespace ProxyBridge.GUI.Views;

/// <summary>Update window bound to <see cref="UpdateViewModel.Shared"/> through <see cref="UpdatePresenter"/>.</summary>
public partial class UpdateWindow : Window
{
    private readonly UpdatePresenter _presenter;

    public UpdateWindow()
    {
        InitializeComponent();
        MainWindow.ApplyCustomChrome(this, Root);
        _presenter = new UpdatePresenter(UpdateViewModel.Shared);
        DataContext = _presenter;
        UpdateViewModel.Shared.RequestClose += OnRequestClose;
    }

    private void OnRequestClose(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(Close);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        UpdateViewModel.Shared.RequestClose -= OnRequestClose;
        _presenter.Dispose();
        base.OnClosed(e);
    }
}
