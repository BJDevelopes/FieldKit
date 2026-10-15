// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>An sRGB colour with alpha, channels 0..1.</summary>
public readonly record struct ColorValue(double Red, double Green, double Blue, double Alpha = 1)
{
    public const int MaxLength = 64;

    public byte R8 => (byte)Math.Round(Math.Clamp(Red, 0, 1) * 255);
    public byte G8 => (byte)Math.Round(Math.Clamp(Green, 0, 1) * 255);
    public byte B8 => (byte)Math.Round(Math.Clamp(Blue, 0, 1) * 255);
    public byte A8 => (byte)Math.Round(Math.Clamp(Alpha, 0, 1) * 255);
}

/// <summary>Formats a colour can be copied as.</summary>
public enum ColorCopyFormat { Hex, Rgb, Hsl, Wpf }

/// <summary>
/// Colours typed into the bar (port of upstream CommandBarColors/ColorValue): "#a2b3b4" previews,
/// "#a2b3b4 to rgb" converts. Accepts #rgb, #rgba, #rrggbb, #rrggbbaa, rgb()/rgba() and hsl()/hsla().
/// </summary>
public static partial class CommandBarColors
{
    public sealed record Result(ColorValue Color, string Formatted);

    private static readonly Dictionary<string, (ColorCopyFormat Format, bool AlwaysAlpha)> Targets = new(StringComparer.Ordinal)
    {
        ["hex"] = (ColorCopyFormat.Hex, false),
        ["rgb"] = (ColorCopyFormat.Rgb, false),
        ["rgba"] = (ColorCopyFormat.Rgb, true),
        ["hsl"] = (ColorCopyFormat.Hsl, false),
        ["hsla"] = (ColorCopyFormat.Hsl, true),
        ["wpf"] = (ColorCopyFormat.Wpf, false),
        ["xaml"] = (ColorCopyFormat.Wpf, false),
    };

    /// <summary>The conversion for <paramref name="input"/> ("#fff to rgb"), or null.</summary>
    public static Result? Convert(string input)
    {
        if (input.Length > ColorValue.MaxLength + 16) return null;
        var tokens = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3) return null;
        if (!CommandBarUnits.ConversionWords.Contains(tokens[^2].ToLowerInvariant())) return null;
        if (!Targets.TryGetValue(tokens[^1].ToLowerInvariant(), out var target)) return null;
        var color = Parse(string.Join(' ', tokens[..^2]));
        if (color is null) return null;
        double? alpha = target.AlwaysAlpha || color.Value.Alpha < 1 ? color.Value.Alpha : null;
        return new Result(color.Value, Format(color.Value, alpha, target.Format));
    }

    /// <summary>A colour typed on its own, or null.</summary>
    public static ColorValue? Parse(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Length > ColorValue.MaxLength) return null;
        var hex = HexRegex().Match(t);
        if (hex.Success) return FromHex(hex.Groups[1].Value);
        var fn = FunctionRegex().Match(t);
        if (!fn.Success) return null;
        var name = fn.Groups[1].Value.ToLowerInvariant();
        var parts = fn.Groups[2].Value.Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 3 or > 4) return null;
        double alpha = 1;
        if (parts.Length == 4)
        {
            var a = Channel(parts[3], 1);
            if (a is null) return null;
            alpha = a.Value;
        }
        if (name.StartsWith("rgb", StringComparison.Ordinal))
        {
            var r = Channel(parts[0], 255);
            var g = Channel(parts[1], 255);
            var b = Channel(parts[2], 255);
            return r is null || g is null || b is null ? null : new ColorValue(r.Value, g.Value, b.Value, alpha);
        }
        var h = Number(parts[0].Replace("deg", string.Empty, StringComparison.OrdinalIgnoreCase));
        var s = Channel(parts[1], 100);
        var l = Channel(parts[2], 100);
        if (h is null || s is null || l is null) return null;
        var (rr, gg, bb) = HslToRgb(h.Value, s.Value, l.Value);
        return new ColorValue(rr, gg, bb, alpha);
    }

    private static ColorValue? FromHex(string digits)
    {
        if (digits.Length is 3 or 4) digits = string.Concat(digits.Select(c => new string(c, 2)));
        if (digits.Length is not (6 or 8)) return null;
        var v = System.Convert.ToUInt32(digits, 16);
        if (digits.Length == 6) v = (v << 8) | 0xFF;
        return new ColorValue(((v >> 24) & 0xFF) / 255.0, ((v >> 16) & 0xFF) / 255.0, ((v >> 8) & 0xFF) / 255.0, (v & 0xFF) / 255.0);
    }

    private static double? Number(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    /// <summary>A channel as 0..1: "50%" is a percentage, a bare number is out of <paramref name="scale"/>.</summary>
    private static double? Channel(string s, double scale)
    {
        if (s.EndsWith('%'))
        {
            var pct = Number(s[..^1]);
            return pct is >= 0 and <= 100 ? pct / 100 : null;
        }
        var v = Number(s);
        return v is null || v < 0 || v > scale ? null : v / scale;
    }

    private static (double, double, double) HslToRgb(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360;
        if (s == 0) return (l, l, l);
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        double Hue(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        return (Hue(h + 1.0 / 3), Hue(h), Hue(h - 1.0 / 3));
    }

    public static string Format(ColorValue c, double? alpha, ColorCopyFormat format)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (format)
        {
            case ColorCopyFormat.Hex:
                return alpha is null ? $"#{c.R8:X2}{c.G8:X2}{c.B8:X2}" : $"#{c.R8:X2}{c.G8:X2}{c.B8:X2}{(byte)Math.Round(alpha.Value * 255):X2}";
            case ColorCopyFormat.Rgb:
                return alpha is null
                    ? $"rgb({c.R8}, {c.G8}, {c.B8})"
                    : string.Create(inv, $"rgba({c.R8}, {c.G8}, {c.B8}, {Math.Round(alpha.Value, 2)})");
            case ColorCopyFormat.Hsl:
            {
                var (h, s, l) = RgbToHsl(c.Red, c.Green, c.Blue);
                var core = string.Create(inv, $"{Math.Round(h)}, {Math.Round(s * 100)}%, {Math.Round(l * 100)}%");
                return alpha is null ? $"hsl({core})" : string.Create(inv, $"hsla({core}, {Math.Round(alpha.Value, 2)})");
            }
            default:
                return $"#{(byte)Math.Round((alpha ?? 1) * 255):X2}{c.R8:X2}{c.G8:X2}{c.B8:X2}";
        }
    }

    private static (double H, double S, double L) RgbToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h * 60, s, l);
    }

    [GeneratedRegex(@"^#([0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"^(rgba?|hsla?)\s*\(\s*([^)]*)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionRegex();
}
