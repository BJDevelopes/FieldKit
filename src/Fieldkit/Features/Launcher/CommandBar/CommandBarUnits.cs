// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// Unit conversion for the Command Bar: "100 km to mi", "20c to f", "5 gb to mb" (port of upstream
/// CommandBarUnits). Strict: a number, a known unit, a word meaning "to" and a unit of the SAME family.
/// No currencies: the bar never goes to the network.
/// </summary>
public static class CommandBarUnits
{
    public sealed record Result(string Formatted, double Value);

    /// <summary>Words that mean "convert into" (parser vocabulary, already folded).</summary>
    public static readonly HashSet<string> ConversionWords =
    [
        "to", "in", "into", "as", "em", "para", "pra", "en", "a", "nach", "zu", "à", "su", "->", ">", "→",
    ];

    private enum Family { Temperature, Length, Mass, Data, Duration, Volume }

    /// <param name="Factor">Multiplier to the family's base unit (metre, kilogram, byte, second, litre).</param>
    /// <param name="Offset">Added after the factor (temperature: to kelvin).</param>
    private sealed record Unit(Family Family, string Symbol, double Factor, double Offset = 0)
    {
        public double ToBase(double v) => v * Factor + Offset;
        public double FromBase(double v) => (v - Offset) / Factor;
    }

    private static readonly Dictionary<string, Unit> Lexicon = Build();

    private static Dictionary<string, Unit> Build()
    {
        var map = new Dictionary<string, Unit>(StringComparer.Ordinal);
        void Add(string[] names, Unit unit)
        {
            foreach (var n in names) map[n] = unit;
        }
        // Temperature: absolute readings, never increments (20 °C is 68 °F).
        Add(["c", "celsius", "°c", "centigrade"], new Unit(Family.Temperature, "°C", 1, 273.15));
        Add(["f", "fahrenheit", "°f"], new Unit(Family.Temperature, "°F", 5.0 / 9.0, 273.15 - 32 * 5.0 / 9.0));
        Add(["k", "kelvin"], new Unit(Family.Temperature, "K", 1));
        // Length
        Add(["mm", "millimeter", "millimeters", "milimetro", "milimetros"], new Unit(Family.Length, "mm", 0.001));
        Add(["cm", "centimeter", "centimeters", "centimetro", "centimetros"], new Unit(Family.Length, "cm", 0.01));
        Add(["m", "meter", "meters", "metro", "metros", "metre", "metres"], new Unit(Family.Length, "m", 1));
        Add(["km", "kilometer", "kilometers", "quilometro", "quilometros"], new Unit(Family.Length, "km", 1000));
        Add(["in", "inch", "inches", "polegada", "polegadas", "\""], new Unit(Family.Length, "in", 0.0254));
        Add(["ft", "foot", "feet", "pe", "pes"], new Unit(Family.Length, "ft", 0.3048));
        Add(["yd", "yard", "yards", "jarda", "jardas"], new Unit(Family.Length, "yd", 0.9144));
        Add(["mi", "mile", "miles", "milha", "milhas"], new Unit(Family.Length, "mi", 1609.344));
        // Mass
        Add(["mg", "milligram", "milligrams"], new Unit(Family.Mass, "mg", 1e-6));
        Add(["g", "gram", "grams", "grama", "gramas"], new Unit(Family.Mass, "g", 0.001));
        Add(["kg", "kilogram", "kilograms", "quilo", "quilos"], new Unit(Family.Mass, "kg", 1));
        Add(["t", "ton", "tons", "tonne", "tonelada", "toneladas"], new Unit(Family.Mass, "t", 1000));
        Add(["oz", "ounce", "ounces", "onca", "oncas"], new Unit(Family.Mass, "oz", 0.028349523125));
        Add(["lb", "lbs", "pound", "pounds", "libra", "libras"], new Unit(Family.Mass, "lb", 0.45359237));
        // Data: decimal and binary stay separate units; settling which was meant is the point.
        Add(["b", "byte", "bytes"], new Unit(Family.Data, "B", 1));
        Add(["kb", "kilobyte", "kilobytes"], new Unit(Family.Data, "kB", 1e3));
        Add(["mb", "megabyte", "megabytes"], new Unit(Family.Data, "MB", 1e6));
        Add(["gb", "gigabyte", "gigabytes"], new Unit(Family.Data, "GB", 1e9));
        Add(["tb", "terabyte", "terabytes"], new Unit(Family.Data, "TB", 1e12));
        Add(["kib", "kibibyte", "kibibytes"], new Unit(Family.Data, "KiB", 1024));
        Add(["mib", "mebibyte", "mebibytes"], new Unit(Family.Data, "MiB", 1024.0 * 1024));
        Add(["gib", "gibibyte", "gibibytes"], new Unit(Family.Data, "GiB", 1024.0 * 1024 * 1024));
        Add(["tib", "tebibyte", "tebibytes"], new Unit(Family.Data, "TiB", 1024.0 * 1024 * 1024 * 1024));
        Add(["bit", "bits"], new Unit(Family.Data, "bit", 0.125));
        // Duration
        Add(["ms", "millisecond", "milliseconds"], new Unit(Family.Duration, "ms", 0.001));
        Add(["s", "sec", "secs", "second", "seconds", "segundo", "segundos"], new Unit(Family.Duration, "sec", 1));
        Add(["min", "mins", "minute", "minutes", "minuto", "minutos"], new Unit(Family.Duration, "min", 60));
        Add(["h", "hr", "hrs", "hour", "hours", "hora", "horas"], new Unit(Family.Duration, "hr", 3600));
        // Volume
        Add(["ml", "milliliter", "milliliters", "mililitro", "mililitros"], new Unit(Family.Volume, "mL", 0.001));
        Add(["l", "liter", "liters", "litre", "litres", "litro", "litros"], new Unit(Family.Volume, "L", 1));
        Add(["gal", "gallon", "gallons", "galao", "galoes"], new Unit(Family.Volume, "gal", 3.785411784));
        Add(["qt", "quart", "quarts"], new Unit(Family.Volume, "qt", 0.946352946));
        Add(["floz", "fluidounce", "fluidounces"], new Unit(Family.Volume, "fl oz", 0.0295735295625));
        Add(["cup", "cups", "xicara", "xicaras"], new Unit(Family.Volume, "cup", 0.24));
        return map;
    }

    /// <summary>The conversion for <paramref name="input"/>, or null when it is not one.</summary>
    public static Result? Convert(string input, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var tokens = Tokenize(input);
        if (tokens.Count is < 3 or > 8) return null;
        // Each candidate keyword from the right: in "5 in to cm" the last one is the verb.
        for (var position = tokens.Count - 2; position >= 1; position--)
        {
            if (!ConversionWords.Contains(tokens[position])) continue;
            var left = tokens.Take(position).ToList();
            var right = tokens.Skip(position + 1).ToList();
            if (right.Count != 1 || !Lexicon.TryGetValue(right[0], out var target)) continue;
            if (left.Count != 2 || !Lexicon.TryGetValue(left[1], out var source)) continue;
            var value = Number(left[0], culture);
            if (value is null || source.Family != target.Family) continue;
            var converted = target.FromBase(source.ToBase(value.Value));
            if (!double.IsFinite(converted)) continue;
            // Floating noise from the round trip through the base unit is not a result.
            converted = Math.Round(converted, 10);
            var formatted = FeetAndInches(converted, target, culture) ?? Format(converted, target, culture);
            return new Result(formatted, converted);
        }
        return null;
    }

    /// <summary>Folded tokens, pulling "100km" apart into a number and a unit.</summary>
    public static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        foreach (var raw in CommandBarSearch.Normalize(input).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var first = raw[0];
            if (!(char.IsDigit(first) || first is '-' or '.' or ','))
            {
                tokens.Add(raw);
                continue;
            }
            var digits = new string(raw.TakeWhile(c => char.IsDigit(c) || c is '.' or ',' or '-').ToArray());
            tokens.Add(digits);
            if (digits.Length < raw.Length) tokens.Add(raw[digits.Length..]);
        }
        return tokens;
    }

    /// <summary>Like the calculator, accepts the alternate decimal separator unless it forms grouped thousands.</summary>
    private static double? Number(string token, CultureInfo culture)
    {
        var decimalSeparator = culture.NumberFormat.NumberDecimalSeparator;
        var groupingSeparator = culture.NumberFormat.NumberGroupSeparator;
        var groupsWithPunctuation = groupingSeparator is "." or "," && groupingSeparator != decimalSeparator;
        var alternate = groupsWithPunctuation ? groupingSeparator : decimalSeparator == "," ? "." : ",";
        var normalized = token;
        var hasDecimal = token.Contains(decimalSeparator, StringComparison.Ordinal);
        var hasAlternate = token.Contains(alternate, StringComparison.Ordinal);
        if (hasDecimal && hasAlternate)
        {
            normalized = token.LastIndexOf(alternate, StringComparison.Ordinal) > token.LastIndexOf(decimalSeparator, StringComparison.Ordinal)
                ? token.Replace(decimalSeparator, string.Empty).Replace(alternate, ".")
                : token.Replace(alternate, string.Empty).Replace(decimalSeparator, ".");
        }
        else if (hasAlternate)
        {
            var unsigned = token.StartsWith('-') ? token[1..] : token;
            var groups = unsigned.Split(alternate);
            var isGrouping = groups.Length >= 2 && groups[0].Length is >= 1 and <= 3 && !groups[0].StartsWith('0')
                             && groups.Skip(1).All(g => g.Length == 3);
            normalized = token.Replace(alternate, isGrouping ? string.Empty : ".");
        }
        else if (hasDecimal)
        {
            normalized = token.Replace(decimalSeparator, ".");
        }
        if (normalized.Length == 0 || normalized.Count(c => c == '.') > 1 || !normalized.All(c => char.IsDigit(c) || c is '.' or '-')) return null;
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Mixed units once there is a whole foot to show ("5 ft 10 in").</summary>
    private static string? FeetAndInches(double value, Unit unit, CultureInfo culture)
    {
        if (unit.Symbol != "ft" || value < 1) return null;
        var wholeFeet = Math.Floor(value);
        var rawInches = (value - wholeFeet) * 12;
        var scale = Math.Pow(10, FractionDigits(rawInches));
        var inches = Math.Round(rawInches * scale, MidpointRounding.AwayFromZero) / scale;
        if (inches >= 12)
        {
            wholeFeet += 1;
            inches = 0;
        }
        var feet = Format(wholeFeet, unit, culture);
        if (inches == 0) return feet;
        return feet + " " + Format(inches, Lexicon["in"], culture);
    }

    private static string Format(double value, Unit unit, CultureInfo culture)
    {
        var digits = FractionDigits(value);
        var number = Math.Round(value, digits, MidpointRounding.AwayFromZero).ToString("#,0." + new string('#', Math.Max(digits, 0)), culture);
        if (number.EndsWith(culture.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal)) number = number[..^culture.NumberFormat.NumberDecimalSeparator.Length];
        if (number == "-0") number = "0";
        return unit.Family == Family.Temperature && unit.Symbol != "K" ? number + unit.Symbol : number + " " + unit.Symbol;
    }

    /// <summary>Small numbers keep their decimals, big ones lose the noise.</summary>
    private static int FractionDigits(double value)
    {
        var magnitude = Math.Abs(value);
        if (magnitude == 0) return 0;
        if (magnitude < 1) return 4;
        if (magnitude < 100) return 2;
        return 1;
    }
}
