using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ProxyBridge.GUI.Services;

namespace ProxyBridge.GUI.Controls;

/// <summary>
/// Minimise / maximise-restore / close buttons for windows with a custom title bar.
/// Hidden on macOS, where the native title bar is kept.
/// </summary>
public class WindowButtons : StackPanel
{
    private static readonly Geometry MinGeo = Geometry.Parse("M0 0.5 L10 0.5");
    private static readonly Geometry MaxGeo = Geometry.Parse("M0.5 0.5 L9.5 0.5 L9.5 9.5 L0.5 9.5 Z");
    private static readonly Geometry RestoreGeo = Geometry.Parse("M2.5 2.5 L2.5 0.5 L9.5 0.5 L9.5 7.5 L7.5 7.5 M0.5 2.5 L7.5 2.5 L7.5 9.5 L0.5 9.5 Z");
    private static readonly Geometry CloseGeo = Geometry.Parse("M0 0 L10 10 M10 0 L0 10");

    public static readonly StyledProperty<bool> ShowMinimizeProperty =
        AvaloniaProperty.Register<WindowButtons, bool>(nameof(ShowMinimize), true);

    public static readonly StyledProperty<bool> ShowMaximizeProperty =
        AvaloniaProperty.Register<WindowButtons, bool>(nameof(ShowMaximize), true);

    private readonly Button _min;
    private readonly Button _max;
    private readonly Button _close;
    private readonly Path _maxPath;
    private Window? _window;

    public WindowButtons()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Stretch;
        IsVisible = !OperatingSystem.IsMacOS();

        _min = Make(MinGeo, "min", out _);
        _max = Make(MaxGeo, "max", out _maxPath);
        _close = Make(CloseGeo, "close", out _);
        _close.Classes.Add("close");

        _min.Click += (_, _) => { if (_window != null) _window.WindowState = WindowState.Minimized; };
        _max.Click += (_, _) => ToggleMaximize(_window);
        _close.Click += (_, _) => _window?.Close();

        Children.Add(_min);
        Children.Add(_max);
        Children.Add(_close);
    }

    public bool ShowMinimize
    {
        get => GetValue(ShowMinimizeProperty);
        set => SetValue(ShowMinimizeProperty, value);
    }

    public bool ShowMaximize
    {
        get => GetValue(ShowMaximizeProperty);
        set => SetValue(ShowMaximizeProperty, value);
    }

    public static void ToggleMaximize(Window? w)
    {
        if (w == null || !w.CanResize) return;
        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowMinimizeProperty) _min.IsVisible = ShowMinimize;
        if (change.Property == ShowMaximizeProperty) _max.IsVisible = ShowMaximize;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window != null)
        {
            _window.PropertyChanged += OnWindowPropertyChanged;
            UpdateMaxGlyph();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window != null) _window.PropertyChanged -= OnWindowPropertyChanged;
        _window = null;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty) UpdateMaxGlyph();
    }

    private void UpdateMaxGlyph()
    {
        _maxPath.Data = _window?.WindowState == WindowState.Maximized ? RestoreGeo : MaxGeo;
    }

    private static Button Make(Geometry geo, string id, out Path path)
    {
        path = new Path
        {
            Data = geo,
            StrokeThickness = 1,
            Width = 10,
            Height = 10,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        path.Classes.Add("glyph");
        var b = new Button { Content = path, Name = "WinBtn_" + id, Focusable = false };
        b.Classes.Add("winbtn");
        return b;
    }
}

/// <summary>Converters shared by the views.</summary>
public static class Conv
{
    /// <summary>Upper-cases a string in the current UI culture (Avalonia has no text-transform).</summary>
    public static readonly IValueConverter Upper =
        new FuncValueConverter<string?, string>(s => (s ?? "").ToUpper(I18n.Instance.Culture));
}

/// <summary>
/// Helper for custom title bars: pressing the attached element drags the window, a double click
/// toggles maximise. Use <c>c:TitleDrag.IsEnabled="True"</c> on the title bar background.
/// </summary>
public static class TitleDrag
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(TitleDrag));

    static TitleDrag()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            if (e.NewValue is true)
                c.PointerPressed += OnPressed;
            else
                c.PointerPressed -= OnPressed;
        });
    }

    public static bool GetIsEnabled(Control c) => c.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control c, bool v) => c.SetValue(IsEnabledProperty, v);

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c || e.Handled) return;
        if (!e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) return;
        if (TopLevel.GetTopLevel(c) is not Window w || OperatingSystem.IsMacOS()) return;
        if (e.ClickCount >= 2)
        {
            // BeginMoveDrag runs a modal loop, so the second click arrives here instead of DoubleTapped
            WindowButtons.ToggleMaximize(w);
            e.Handled = true;
            return;
        }
        w.BeginMoveDrag(e);
    }
}
