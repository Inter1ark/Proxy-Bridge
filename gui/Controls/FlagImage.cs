using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ProxyBridge.GUI.Controls;

/// <summary>
/// Country flag from the bundled Assets/Flags/&lt;cc&gt;.png (4:3), or a neutral globe icon when
/// the code is empty or unknown. Windows cannot draw emoji flags, so images are used everywhere.
/// Default size 20x15; set Width/Height to change it.
/// </summary>
public class FlagImage : Control
{
    public static readonly StyledProperty<string?> CountryCodeProperty =
        AvaloniaProperty.Register<FlagImage, string?>(nameof(CountryCode));

    private static readonly Geometry Globe = Geometry.Parse(
        "M12 2 A10 10 0 1 0 12 22 A10 10 0 1 0 12 2 Z M2 12 L22 12 M12 2 C15 5 16.5 8.5 16.5 12 C16.5 15.5 15 19 12 22 C9 19 7.5 15.5 7.5 12 C7.5 8.5 9 5 12 2 Z");
    private static readonly IPen GlobePen = new Pen(new SolidColorBrush(Color.Parse("#6F7B99")), 1.6);
    private static readonly IPen FramePen = new Pen(new SolidColorBrush(Color.Parse("#33FFFFFF")), 1);

    static FlagImage()
    {
        AffectsRender<FlagImage>(CountryCodeProperty);
        WidthProperty.OverrideDefaultValue<FlagImage>(20);
        HeightProperty.OverrideDefaultValue<FlagImage>(15);
    }

    public string? CountryCode
    {
        get => GetValue(CountryCodeProperty);
        set => SetValue(CountryCodeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var bmp = Flags.Get(CountryCode);
        if (bmp != null)
        {
            // keep 4:3 inside the box
            var w = Math.Min(rect.Width, rect.Height * 4 / 3);
            var h = w * 3 / 4;
            var r = new Rect((rect.Width - w) / 2, (rect.Height - h) / 2, w, h);
            using (context.PushClip(new RoundedRect(r, 2)))
                context.DrawImage(bmp, new Rect(bmp.Size), r);
            context.DrawRectangle(null, FramePen, r.Deflate(0.5), 2, 2);
            return;
        }

        var s = Math.Min(rect.Width, rect.Height);
        var scale = s / 24.0;
        var ox = (rect.Width - s) / 2;
        var oy = (rect.Height - s) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(ox, oy)))
            context.DrawGeometry(null, new Pen(GlobePen.Brush, 1.6 / Math.Max(scale, 0.01) * 0.7), Globe);
    }
}

/// <summary>Flag bitmaps by ISO 3166-1 alpha-2 code, loaded once from the app resources.</summary>
public static class Flags
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? Get(string? countryCode)
    {
        var cc = (countryCode ?? "").Trim().ToLowerInvariant();
        if (cc.Length != 2 || !char.IsLetter(cc[0]) || !char.IsLetter(cc[1])) return null;
        if (Cache.TryGetValue(cc, out var cached)) return cached;
        Bitmap? bmp = null;
        try
        {
            var uri = new Uri($"avares://ProxyBridge/Assets/Flags/{cc}.png");
            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                bmp = new Bitmap(stream);
            }
        }
        catch
        {
            bmp = null;
        }
        Cache[cc] = bmp;
        return bmp;
    }

    /// <summary>XAML converter: country code to flag bitmap (null when unknown).</summary>
    public static readonly IValueConverter ToBitmap =
        new FuncValueConverter<string?, Bitmap?>(Get);
}
