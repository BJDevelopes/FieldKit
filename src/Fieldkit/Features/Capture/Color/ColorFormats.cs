// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Fieldkit.Features.Capture;

/// <summary>The formats the color picker copies (upstream ColorCopyFormat; SwiftUI becomes WPF).</summary>
public enum ColorFormat { Hex, Rgb, Hsl, Wpf }

/// <summary>Pure color-string formatting shared by the picker, the loupe readout and tests.</summary>
public static class ColorFormats
{
    public static readonly IReadOnlyList<(string Value, string Label)> Options =
    [
        ("hex", "HEX"),
        ("rgb", "RGB"),
        ("hsl", "HSL"),
        ("wpf", "WPF"),
    ];

    public static ColorFormat Parse(string? raw) => raw?.ToLowerInvariant() switch
    {
        "rgb" => ColorFormat.Rgb,
        "hsl" => ColorFormat.Hsl,
        "wpf" => ColorFormat.Wpf,
        _ => ColorFormat.Hex,
    };

    /// <summary>
    /// Formats 8-bit sRGB components. <paramref name="bareHex"/> drops the leading # (only HEX);
    /// a non-null <paramref name="alpha"/> (0…1) writes the alpha form.
    /// </summary>
    public static string Format(byte r, byte g, byte b, ColorFormat format, bool bareHex = false, double? alpha = null)
    {
        var a = alpha is { } av ? Math.Clamp(av, 0, 1) : (double?)null;
        var alphaText = a is { } at ? (Math.Round(at * 1000) / 1000).ToString("0.###", CultureInfo.InvariantCulture) : null;
        switch (format)
        {
            case ColorFormat.Rgb:
                var rgb = $"{r}, {g}, {b}";
                return alphaText is null ? $"rgb({rgb})" : $"rgba({rgb}, {alphaText})";
            case ColorFormat.Hsl:
                var (h, s, l) = ToHsl(r / 255.0, g / 255.0, b / 255.0);
                var hsl = string.Create(CultureInfo.InvariantCulture,
                    $"{(int)Math.Round(h, MidpointRounding.AwayFromZero) % 360}, {(int)Math.Round(s * 100, MidpointRounding.AwayFromZero)}%, {(int)Math.Round(l * 100, MidpointRounding.AwayFromZero)}%");
                return alphaText is null ? $"hsl({hsl})" : $"hsla({hsl}, {alphaText})";
            case ColorFormat.Wpf:
                // Source code, not prose: always invariant digits.
                return a is { } wa
                    ? $"Color.FromArgb({(int)Math.Round(wa * 255)}, {r}, {g}, {b})"
                    : $"Color.FromRgb({r}, {g}, {b})";
            default:
                var hex = $"{(bareHex ? "" : "#")}{r:X2}{g:X2}{b:X2}";
                return a is { } ha ? hex + ((int)Math.Round(ha * 255)).ToString("X2") : hex;
        }
    }

    /// <summary>HSL from sRGB components in 0…1. Hue in degrees [0, 360).</summary>
    public static (double Hue, double Saturation, double Lightness) ToHsl(double red, double green, double blue)
    {
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var delta = max - min;
        var lightness = (max + min) / 2;
        if (delta < 0.000001) return (0, 0, lightness);
        var saturation = delta / (1 - Math.Abs(2 * lightness - 1));
        double hue;
        if (max == red) hue = (green - blue) / delta % 6;
        else if (max == green) hue = (blue - red) / delta + 2;
        else hue = (red - green) / delta + 4;
        hue *= 60;
        if (hue < 0) hue += 360;
        return (hue, Math.Clamp(saturation, 0, 1), lightness);
    }

    /// <summary>Parses "#RRGGBB" / "RRGGBB" back to components (recent colours list).</summary>
    public static bool TryParseHex(string? text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().TrimStart('#');
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        r = (byte)(v >> 16);
        g = (byte)(v >> 8);
        b = (byte)v;
        return true;
    }

    /// <summary>Adds a colour to the front of the recent list (no duplicates, capped).</summary>
    public static List<string> PushRecent(IEnumerable<string> recent, string hex, int limit = 12)
    {
        var key = hex.TrimStart('#').ToUpperInvariant();
        var list = new List<string> { "#" + key };
        list.AddRange(recent.Where(c => !string.Equals(c.TrimStart('#'), key, StringComparison.OrdinalIgnoreCase)));
        return list.Take(limit).ToList();
    }
}
