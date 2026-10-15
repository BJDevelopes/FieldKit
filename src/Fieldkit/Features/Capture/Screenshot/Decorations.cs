// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace Fieldkit.Features.Capture;

/// <summary>Background presets (upstream BackdropID), gradient stops top-left → bottom-right.</summary>
public enum BackdropPreset { None, Ocean, Sunset, Forest, Candy, Graphite }

public enum BackdropKind { None, Preset, Solid, Gradient, Image, Wallpaper }

/// <summary>The background behind a capture: padding, fill, corner rounding, shadow and blur.</summary>
public sealed record BackdropStyle
{
    public BackdropKind Kind { get; init; } = BackdropKind.None;
    public BackdropPreset Preset { get; init; } = BackdropPreset.Ocean;
    /// <summary>One colour (solid) or two (gradient) as "#RRGGBB".</summary>
    public string[] Colors { get; init; } = ["#3378F5", "#73D4FA"];
    public string? ImagePath { get; init; }
    /// <summary>Margin slider 0…1.</summary>
    public double Padding { get; init; } = 0.5;
    /// <summary>Corner slider 0…1.</summary>
    public double CornerRadius { get; init; } = 0.15;
    /// <summary>Image backgrounds: blur slider 0…1.</summary>
    public double Blur { get; init; }
    public bool Shadow { get; init; } = true;

    public bool IsVisible => Kind != BackdropKind.None;

    public static BackdropStyle Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new BackdropStyle();
        try { return JsonSerializer.Deserialize<BackdropStyle>(json, DecorationJson.Options) ?? new BackdropStyle(); }
        catch { return new BackdropStyle(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, DecorationJson.Options);
}

public enum WatermarkPosition { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }

/// <summary>Text (and optional image) stamped over a capture.</summary>
public sealed record WatermarkStyle
{
    public bool Enabled { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? ImagePath { get; init; }
    public WatermarkPosition Position { get; init; } = WatermarkPosition.BottomRight;
    /// <summary>Size slider 0…1 (fraction of the short side).</summary>
    public double Size { get; init; } = 0.35;
    public double Opacity { get; init; } = 0.6;
    /// <summary>Degrees, −45…45.</summary>
    public double Rotation { get; init; }
    public AnnotationColor Color { get; init; } = AnnotationColor.White;

    public bool IsVisible => Enabled && (!string.IsNullOrWhiteSpace(Text) || !string.IsNullOrWhiteSpace(ImagePath));

    public static WatermarkStyle Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new WatermarkStyle();
        try { return JsonSerializer.Deserialize<WatermarkStyle>(json, DecorationJson.Options) ?? new WatermarkStyle(); }
        catch { return new WatermarkStyle(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, DecorationJson.Options);
}

internal static class DecorationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Pure layout maths for backgrounds and watermarks.</summary>
public static class DecorationMath
{
    public static (byte R, byte G, byte B)[] PresetStops(BackdropPreset preset) => preset switch
    {
        BackdropPreset.Ocean => [(51, 120, 245), (115, 212, 250)],
        BackdropPreset.Sunset => [(252, 92, 107), (255, 191, 89)],
        BackdropPreset.Forest => [(18, 143, 110), (158, 222, 128)],
        BackdropPreset.Candy => [(168, 82, 242), (252, 143, 166)],
        BackdropPreset.Graphite => [(59, 64, 79), (140, 153, 179)],
        _ => [],
    };

    /// <summary>
    /// Padding around the image: even a zero margin keeps a small frame so the background shows,
    /// and a floor keeps a visible margin on small captures (upstream backdropPadding).
    /// </summary>
    public static double Padding(double imageWidth, double imageHeight, double factor)
    {
        var side = Math.Min(imageWidth, imageHeight);
        var proportional = side * (0.035 + 0.14 * Math.Clamp(factor, 0, 1));
        return Math.Round(Math.Max(24, proportional));
    }

    /// <summary>Corner rounding of the capture card; 1 is a fifth of the short side.</summary>
    public static double CornerRadius(double imageWidth, double imageHeight, double factor) =>
        Math.Round(Math.Clamp(factor, 0, 1) * Math.Min(imageWidth, imageHeight) * 0.2);

    public static double BlurRadius(double width, double height, double factor) =>
        Math.Clamp(factor, 0, 1) * Math.Min(width, height) * 0.035;

    /// <summary>Output size of a capture placed on a background.</summary>
    public static Size Canvas(double imageWidth, double imageHeight, BackdropStyle style)
    {
        if (!style.IsVisible) return new Size(imageWidth, imageHeight);
        var pad = Padding(imageWidth, imageHeight, style.Padding);
        return new Size(imageWidth + pad * 2, imageHeight + pad * 2);
    }

    /// <summary>Top-left of a watermark of <paramref name="mark"/> size inside <paramref name="canvas"/>.</summary>
    public static Point WatermarkOrigin(Size canvas, Size mark, WatermarkPosition position, double margin)
    {
        var left = margin;
        var right = canvas.Width - mark.Width - margin;
        var centerX = (canvas.Width - mark.Width) / 2;
        var top = margin;
        var bottom = canvas.Height - mark.Height - margin;
        var centerY = (canvas.Height - mark.Height) / 2;
        return position switch
        {
            WatermarkPosition.TopLeft => new Point(left, top),
            WatermarkPosition.Top => new Point(centerX, top),
            WatermarkPosition.TopRight => new Point(right, top),
            WatermarkPosition.Left => new Point(left, centerY),
            WatermarkPosition.Center => new Point(centerX, centerY),
            WatermarkPosition.Right => new Point(right, centerY),
            WatermarkPosition.BottomLeft => new Point(left, bottom),
            WatermarkPosition.Bottom => new Point(centerX, bottom),
            _ => new Point(right, bottom),
        };
    }

    /// <summary>Watermark text height in pixels for the size slider.</summary>
    public static double WatermarkTextSize(Size canvas, double sizeFactor) =>
        Math.Max(10, Math.Min(canvas.Width, canvas.Height) * (0.025 + 0.12 * Math.Clamp(sizeFactor, 0, 1)));

    /// <summary>Margin between a watermark and the edge.</summary>
    public static double WatermarkMargin(Size canvas) => Math.Round(Math.Max(8, Math.Min(canvas.Width, canvas.Height) * 0.03));
}
