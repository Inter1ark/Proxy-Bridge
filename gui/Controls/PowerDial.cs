using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ProxyBridge.GUI.Controls;

public enum PowerState { Disconnected, Connecting, Connected, Error }

/// <summary>
/// The large connect button of the Overview tab: a blue disc with a power glyph, a ring and an outer
/// dial of short tick marks. It is a Button (Command = connect / disconnect) without a template;
/// everything is drawn in <see cref="Render"/>. While connecting a bright sweep runs around the dial.
/// </summary>
public class PowerDial : Button, ICustomHitTest
{
    public static readonly StyledProperty<PowerState> StateProperty =
        AvaloniaProperty.Register<PowerDial, PowerState>(nameof(State));

    private const int Ticks = 90;

    private static readonly Color Cyan = Color.Parse("#19D3FF");
    private static readonly Color Accent = Color.Parse("#2F5BFF");
    private static readonly Color AccentHi = Color.Parse("#5A82FF");
    private static readonly Color DimDisc = Color.Parse("#1D3696");
    private static readonly Color DimDiscEdge = Color.Parse("#172C78");
    private static readonly Color Grey = Color.Parse("#34405E");
    private static readonly Color TickGrey = Color.Parse("#3A4766");
    private static readonly Color Danger = Color.Parse("#EF4444");
    private static readonly Color Line = Color.Parse("#1E2A44");

    private DispatcherTimer? _timer;
    private double _phase;

    static PowerDial()
    {
        AffectsRender<PowerDial>(StateProperty, IsPointerOverProperty, IsPressedProperty, IsEnabledProperty);
        CursorProperty.OverrideDefaultValue<PowerDial>(new Cursor(StandardCursorType.Hand));
        FocusableProperty.OverrideDefaultValue<PowerDial>(true);
    }

    public PowerState State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StateProperty) UpdateTimer();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
    }

    private void UpdateTimer()
    {
        var run = State == PowerState.Connecting && (this.GetVisualRoot() != null);
        if (run)
        {
            _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
            {
                _phase = (_phase + 0.085) % (Math.PI * 2);
                InvalidateVisual();
            });
            _timer.Start();
        }
        else
        {
            _timer?.Stop();
        }
    }

    public bool HitTest(Point point)
    {
        var s = Math.Min(Bounds.Width, Bounds.Height);
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var dx = point.X - c.X;
        var dy = point.Y - c.Y;
        return Math.Sqrt(dx * dx + dy * dy) <= s / 2;
    }

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    public override void Render(DrawingContext ctx)
    {
        var s = Math.Min(Bounds.Width, Bounds.Height);
        if (s < 40) return;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var R = s / 2 - 2;
        var state = State;
        var hover = IsPointerOver && IsEnabled;
        var pressed = IsPressed;

        // faint outer circle
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Line), 1), c, R, R);

        // tick dial
        var tickW = Math.Max(1, s / 260.0);
        for (var i = 0; i < Ticks; i++)
        {
            var a = i * Math.PI * 2 / Ticks - Math.PI / 2;
            var major = i % 5 == 0;
            var r1 = R * (major ? 0.835 : 0.865);
            var r2 = R * 0.935;
            Color col;
            switch (state)
            {
                case PowerState.Connected:
                    col = WithAlpha(Cyan, major ? 0.85 : 0.5);
                    break;
                case PowerState.Error:
                    col = WithAlpha(Danger, major ? 0.6 : 0.35);
                    break;
                case PowerState.Connecting:
                {
                    var d = Math.Abs(Math.IEEERemainder(a + Math.PI / 2 - _phase, Math.PI * 2));
                    var t = Math.Max(0, 1 - d / 1.3);
                    col = Mix(WithAlpha(TickGrey, major ? 0.9 : 0.6), Cyan, t * t);
                    break;
                }
                default:
                    col = WithAlpha(TickGrey, major ? 0.95 : 0.6);
                    break;
            }
            var cos = Math.Cos(a);
            var sin = Math.Sin(a);
            ctx.DrawLine(new Pen(new SolidColorBrush(col), major ? tickW * 1.5 : tickW),
                new Point(c.X + cos * r1, c.Y + sin * r1),
                new Point(c.X + cos * r2, c.Y + sin * r2));
        }

        // ring
        var ringR = R * 0.735;
        var ringW = Math.Max(2.5, s * 0.014);
        switch (state)
        {
            case PowerState.Connected:
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Cyan, 0.07), ringW * 7), c, ringR, ringR);
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Cyan, 0.16), ringW * 3.2), c, ringR, ringR);
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Cyan), ringW), c, ringR, ringR);
                break;
            case PowerState.Error:
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Danger, 0.14), ringW * 3.2), c, ringR, ringR);
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Danger), ringW), c, ringR, ringR);
                break;
            case PowerState.Connecting:
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Cyan, 0.35), ringW), c, ringR, ringR);
                DrawArc(ctx, c, ringR, _phase - Math.PI / 2 - 0.7, 1.4, new Pen(new SolidColorBrush(Cyan), ringW, lineCap: PenLineCap.Round));
                break;
            default:
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Grey), ringW), c, ringR, ringR);
                break;
        }

        // disc
        var discR = R * 0.655;
        var on = state is PowerState.Connected or PowerState.Connecting;
        var inner = on ? AccentHi : Mix(DimDisc, Accent, 0.25);
        var outer = on ? Accent : DimDiscEdge;
        if (hover)
        {
            inner = Mix(inner, Colors.White, 0.10);
            outer = Mix(outer, AccentHi, 0.35);
        }
        if (pressed)
        {
            inner = Mix(inner, Colors.Black, 0.12);
            outer = Mix(outer, Colors.Black, 0.12);
        }
        var disc = new RadialGradientBrush
        {
            Center = new RelativePoint(0.42, 0.36, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.42, 0.36, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops = { new GradientStop(inner, 0), new GradientStop(outer, 1) }
        };
        ctx.DrawEllipse(disc, null, c, discR, discR);

        // power glyph
        var gr = R * 0.205;
        var gw = Math.Max(2.5, R * 0.05);
        var glyph = new Pen(new SolidColorBrush(Colors.White, on || hover ? 1.0 : 0.82), gw, lineCap: PenLineCap.Round);
        DrawArc(ctx, c, gr, -Math.PI / 2 + 0.72, Math.PI * 2 - 1.44, glyph);
        ctx.DrawLine(glyph, new Point(c.X, c.Y - gr * 1.28), new Point(c.X, c.Y - gr * 0.18));
    }

    private static void DrawArc(DrawingContext ctx, Point c, double r, double start, double sweep, IPen pen)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            var p0 = new Point(c.X + Math.Cos(start) * r, c.Y + Math.Sin(start) * r);
            var end = start + sweep;
            var p1 = new Point(c.X + Math.Cos(end) * r, c.Y + Math.Sin(end) * r);
            g.BeginFigure(p0, false);
            g.ArcTo(p1, new Size(r, r), 0, sweep > Math.PI, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }
}
