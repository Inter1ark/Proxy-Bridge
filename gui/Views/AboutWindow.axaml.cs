using Avalonia.Controls;
using System.Diagnostics;
using Avalonia.Input;

namespace ProxyBridge.GUI.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    private void OnWebsiteClick(object? sender, PointerPressedEventArgs e)
    {
        OpenUrl("https://www.proxybridge.org");
    }

    private void OnSupportClick(object? sender, PointerPressedEventArgs e)
    {
        OpenUrl("mailto:support@proxybridge.org");
    }

    private void OnTelegramClick(object? sender, PointerPressedEventArgs e)
    {
        OpenUrl("https://t.me/inter1ark");
    }

    private void OnGitHubClick(object? sender, PointerPressedEventArgs e)
    {
        OpenUrl("https://github.com/Inter1ark/Proxy-Bridge");
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Silently ignore when the browser or mail client cannot be opened
        }
    }
}
