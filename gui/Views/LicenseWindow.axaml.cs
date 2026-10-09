using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using ProxyBridge.GUI.ViewModels;

namespace ProxyBridge.GUI.Views;

public partial class LicenseWindow : Window
{
    private bool _activated;

    public LicenseWindow()
    {
        InitializeComponent();
        MainWindow.ApplyCustomChrome(this, Root);

        Opened += async (s, e) =>
        {
            if (DataContext is LicenseViewModel vm)
            {
                vm.Activated += OnActivated;
                KeyBox.Focus();
                await vm.OnOpenedAsync();
            }
        };
    }

    private void OnActivated()
    {
        if (_activated) return;
        _activated = true;

        if (Avalonia.Application.Current is App app)
        {
            app.ShowMainWindow();
        }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (DataContext is LicenseViewModel vm)
        {
            vm.Activated -= OnActivated;
            vm.Detach();
        }

        // Closing the activation window without a license exits the application.
        if (!_activated && Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
