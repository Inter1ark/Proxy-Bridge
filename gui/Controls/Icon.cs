using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ProxyBridge.GUI.Controls;

/// <summary>
/// Stroke icon drawn from a 24x24 path geometry (Lucide-style). The stroke color follows the
/// inherited Foreground, so an icon inside a button takes the button's text color.
/// </summary>
public class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    /// <summary>Stroke width in 24-unit icon space (scaled with the icon size).</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 1.9);

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, ForegroundProperty, StrokeThicknessProperty);
        WidthProperty.OverrideDefaultValue<Icon>(20);
        HeightProperty.OverrideDefaultValue<Icon>(20);
        IsHitTestVisibleProperty.OverrideDefaultValue<Icon>(false);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var geometry = Data;
        var brush = Foreground;
        if (geometry == null || brush == null) return;

        var size = Bounds.Size;
        var scale = System.Math.Min(size.Width, size.Height) / 24.0;
        if (scale <= 0) return;
        var ox = (size.Width - 24 * scale) / 2;
        var oy = (size.Height - 24 * scale) / 2;

        var pen = new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(ox, oy)))
        {
            context.DrawGeometry(null, pen, geometry);
        }
    }
}

/// <summary>The ProxyBridge brand mark (white and blue peaks, 48x32 viewBox).</summary>
public class BrandMark : Control
{
    private static readonly Geometry White = Geometry.Parse("M0 32 L15 9 L27 24 L21 32 Z");
    private static readonly Geometry Blue = Geometry.Parse("M13 32 L30 1 L48 32 Z");
    private static readonly IBrush WhiteBrush = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BlueBrush = new SolidColorBrush(Color.Parse("#2D5BFF"));

    static BrandMark()
    {
        WidthProperty.OverrideDefaultValue<BrandMark>(30);
        HeightProperty.OverrideDefaultValue<BrandMark>(20);
        IsHitTestVisibleProperty.OverrideDefaultValue<BrandMark>(false);
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        var scale = System.Math.Min(size.Width / 48.0, size.Height / 32.0);
        if (scale <= 0) return;
        var ox = (size.Width - 48 * scale) / 2;
        var oy = (size.Height - 32 * scale) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(ox, oy)))
        {
            context.DrawGeometry(WhiteBrush, null, White);
            context.DrawGeometry(BlueBrush, null, Blue);
        }
    }
}
