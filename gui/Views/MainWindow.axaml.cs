using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using ProxyBridge.GUI.ViewModels;

namespace ProxyBridge.GUI.Views;

public partial class MainWindow : Window
{
    private bool _forceClose = false;

    public MainWindow()
    {
        InitializeComponent();
        ApplyCustomChrome(this, Root);

        this.Opened += (s, e) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.SetMainWindow(this);
            }
        };
    }

    /// <summary>
    /// Windows / Linux: our own title bar drawn into the client area (no system caption, OS resize
    /// borders kept). macOS keeps the native title bar. When maximised, the off-screen margin of the
    /// extended frame is applied as padding so nothing is cut at the screen edges.
    /// </summary>
    public static void ApplyCustomChrome(Window w, Decorator root)
    {
        if (OperatingSystem.IsMacOS()) return;
        w.ExtendClientAreaToDecorationsHint = true;
        w.ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        w.ExtendClientAreaTitleBarHeightHint = -1;

        void Update()
        {
            var max = w.WindowState == WindowState.Maximized;
            root.Padding = max ? MaximizedMargin(w) : default;
            if (root is Border b)
                b.BorderThickness = max ? new Thickness(0) : new Thickness(1);
        }

        w.PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty || e.Property == OffScreenMarginProperty
                || e.Property == ClientSizeProperty || e.Property == BoundsProperty)
                Update();
        };
        w.PositionChanged += (_, _) => { if (w.WindowState == WindowState.Maximized) Update(); };
        w.ScalingChanged += (_, _) => Update();
        Update();
    }

    /// <summary>
    /// Part of a maximised window that lies outside the monitor's work area (Windows grows a
    /// maximised window by its resize border, about 8 px at 100% and 10 px at 125%). Avalonia's
    /// OffScreenMargin is not always reported for a frameless window, so the margin is also measured
    /// from the window and screen rectangles; the larger of the two wins on each side.
    /// </summary>
    private static Thickness MaximizedMargin(Window w)
    {
        var reported = w.OffScreenMargin;
        double l = 0, t = 0, r = 0, btm = 0;
        try
        {
            var screen = w.Screens.ScreenFromWindow(w);
            if (screen != null)
            {
                var scale = w.RenderScaling > 0 ? w.RenderScaling : screen.Scaling;
                var wa = screen.WorkingArea;
                var pos = w.Position;
                var width = w.ClientSize.Width * scale;
                var height = w.ClientSize.Height * scale;
                l = (wa.X - pos.X) / scale;
                t = (wa.Y - pos.Y) / scale;
                r = (pos.X + width - (wa.X + wa.Width)) / scale;
                btm = (pos.Y + height - (wa.Y + wa.Height)) / scale;
            }
        }
        catch
        {
            // fall back to the reported margin
        }

        static double Clamp(double measured, double rep) => Math.Clamp(Math.Max(measured, rep), 0, 32);
        return new Thickness(Clamp(l, reported.Left), Clamp(t, reported.Top), Clamp(r, reported.Right), Clamp(btm, reported.Bottom));
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
