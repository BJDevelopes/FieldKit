// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// The Command Bar's inline calculator (port of upstream CommandBarMath). Deliberately strict:
/// input must be entirely mathematical, so commands and searches are never answered as sums.
/// Supports + - * / ^ %, "x" and "×", brackets (missing closers are supplied), implicit products
/// (2pi, 3(4)), "20% of 50", constants pi/π/e and sqrt abs sin cos tan asin acos atan ln log log10
/// exp floor ceil round (radians).
/// </summary>
public static class CommandBarMath
{
    private static readonly HashSet<string> OfWords = ["of", "de", "da", "do", "von", "di", "del", "dal"];

    /// <param name="Formatted">The value written the way this PC writes numbers.</param>
    /// <param name="ClosingBrackets">Brackets supplied virtually for an unfinished expression.</param>
    public sealed record Result(string Formatted, double Value, string ClosingBrackets);

    public static Result? Evaluate(string input, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var parsed = Parse(input, culture.NumberFormat.NumberDecimalSeparator, culture.NumberFormat.NumberGroupSeparator);
        if (parsed is null) return null;
        var value = SignificantRounded(parsed.Value.Value);
        if (!double.IsFinite(value)) return null;
        var formatted = Format(value, culture);
        return formatted is null ? null : new Result(formatted, value, parsed.Value.Closings);
    }

    /// <summary>An ungrouped number to put an answer back into the field (Tab); negatives in brackets.</summary>
    public static string ReusableExpression(Result result, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var number = result.Value == 0 ? 0 : result.Value;
        var ascii = number.ToString("R", CultureInfo.InvariantCulture);
        if (ascii.EndsWith(".0", StringComparison.Ordinal)) ascii = ascii[..^2];
        var separator = string.IsNullOrEmpty(culture.NumberFormat.NumberDecimalSeparator) ? "." : culture.NumberFormat.NumberDecimalSeparator;
        var localized = ascii.Replace(".", separator);
        return number < 0 ? $"({localized})" : localized;
    }

    /// <summary>Rounds away floating point noise and writes the number with this PC's separators.
    /// Very large or tiny magnitudes use scientific notation ("1.5e12").</summary>
    public static string? Format(double value, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var rounded = SignificantRounded(value);
        if (!double.IsFinite(rounded)) return null;
        if (rounded == 0) return "0";
        var magnitude = Math.Abs(rounded);
        if (magnitude >= 1e12 || magnitude < 1e-6)
        {
            var exponent = (int)Math.Floor(Math.Log10(magnitude));
            var mantissa = rounded / Math.Pow(10, exponent);
            if (Math.Abs(mantissa) >= 10)
            {
                mantissa /= 10;
                exponent++;
            }
            else if (Math.Abs(mantissa) < 1)
            {
                mantissa *= 10;
                exponent--;
            }
            mantissa = Math.Round(mantissa, 7);
            return mantissa.ToString("0.#######", culture) + "e" + exponent.ToString(CultureInfo.InvariantCulture);
        }
        return rounded.ToString("#,0.########", culture);
    }

    /// <summary>True for "2026-07-27", "27/07/2026" and "10:30": digit groups held together by one
    /// kind of separator with nothing around them.</summary>
    public static bool LooksLikeDateOrTime(string input)
    {
        foreach (var separator in new[] { '-', '/', ':' })
        {
            var parts = input.Split(separator);
            if (parts.Length < 2 || parts.Length > 3) continue;
            var ok = parts.All(part =>
            {
                var digits = separator == ':' ? part.Trim() : part;
                return digits.Length > 0 && digits.Length <= 4 && digits.All(char.IsDigit);
            });
            if (!ok) continue;
            if (separator == ':') return true;
            if (parts.Length == 3) return true;
        }
        return false;
    }

    private static double SignificantRounded(double value)
    {
        if (value == 0 || !double.IsFinite(value)) return value;
        var magnitude = Math.Floor(Math.Log10(Math.Abs(value)));
        var factor = Math.Pow(10.0, 10.0 - magnitude - 1);
        if (!double.IsFinite(factor) || factor == 0) return value;
        var scaled = Math.Round(value * factor, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(scaled)) return value;
        return scaled / factor;
    }

    // ---------------------------------------------------------------- tokens

    private enum Kind { Number, Constant, Function, Plus, Minus, Times, Divide, Power, Percent, OfWord, Left, Right }

    private readonly record struct Token(Kind Kind, double Value = 0, string Function = "", char Bracket = '(')
    {
        public bool IsExplicitOperation => Kind is Kind.Plus or Kind.Minus or Kind.Times or Kind.Divide or Kind.Power or Kind.OfWord;
        public bool EndsImplicitProduct => Kind is Kind.Number or Kind.Constant or Kind.Right;
        public bool StartsImplicitProduct => Kind is Kind.Constant or Kind.Function or Kind.Left;
    }

    private static readonly HashSet<string> Functions =
        ["sqrt", "abs", "sin", "cos", "tan", "asin", "acos", "atan", "ln", "log", "exp", "floor", "ceil", "round"];

    private static double? Apply(string function, double value)
    {
        double result;
        switch (function)
        {
            case "sqrt":
                if (value < 0) return null;
                result = Math.Sqrt(value);
                break;
            case "abs": result = Math.Abs(value); break;
            case "sin": result = Math.Sin(value); break;
            case "cos": result = Math.Cos(value); break;
            case "tan": result = Math.Tan(value); break;
            case "asin":
                if (value is < -1 or > 1) return null;
                result = Math.Asin(value);
                break;
            case "acos":
                if (value is < -1 or > 1) return null;
                result = Math.Acos(value);
                break;
            case "atan": result = Math.Atan(value); break;
            case "ln":
                if (value <= 0) return null;
                result = Math.Log(value);
                break;
            case "log":
                if (value <= 0) return null;
                result = Math.Log10(value);
                break;
            case "exp": result = Math.Exp(value); break;
            case "floor": result = Math.Floor(value); break;
            case "ceil": result = Math.Ceiling(value); break;
            case "round": result = Math.Round(value, MidpointRounding.AwayFromZero); break;
            default: return null;
        }
        return double.IsFinite(result) ? result : null;
    }

    /// <summary>How this PC writes numbers, plus the one other separator people type.</summary>
    private readonly struct Separators
    {
        public readonly char Decimal;
        public readonly char Alternate;
        public readonly char? GroupingOnly;

        public Separators(string decimalSeparator, string groupingSeparator)
        {
            Decimal = decimalSeparator.Length > 0 ? decimalSeparator[0] : '.';
            var grouping = groupingSeparator.Length > 0 ? groupingSeparator[0] : ',';
            var punctuation = grouping is '.' or ',';
            if (punctuation && grouping != Decimal)
            {
                Alternate = grouping;
                GroupingOnly = null;
            }
            else
            {
                Alternate = Decimal == ',' ? '.' : ',';
                GroupingOnly = grouping == Decimal ? null : grouping;
            }
        }

        public bool IsPartOfNumber(char c) => char.IsDigit(c) || c == Decimal || c == Alternate || (GroupingOnly is { } g && c == g);
    }

    private static (double Value, string Closings)? Parse(string input, string decimalSeparator, string groupingSeparator)
    {
        var expression = WithoutTrailingEquals(input);
        if (expression is null || expression.Length > 120 || expression.Length == 0 || LooksLikeDateOrTime(expression)) return null;
        var separators = new Separators(decimalSeparator, groupingSeparator);
        var tokens = Tokenize(expression, separators);
        if (tokens is null || !IsCalculation(tokens)) return null;
        var closings = VirtualClosings(tokens);
        if (closings is null) return null;
        foreach (var bracket in closings) tokens.Add(new Token(Kind.Right, Bracket: bracket));
        var parser = new Parser(tokens);
        var operand = parser.ParseExpression();
        if (operand is null || !parser.IsAtEnd || !double.IsFinite(operand.Value.Value)) return null;
        return (operand.Value.Value, new string(closings.Select(b => b == '(' ? ')' : ']').ToArray()));
    }

    private static string? WithoutTrailingEquals(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0) return null;
        var equals = trimmed.IndexOf('=');
        if (equals < 0) return trimmed;
        if (equals != trimmed.Length - 1) return null;
        var expression = trimmed[..equals].Trim();
        return expression.Length == 0 ? null : expression;
    }

    private static bool IsCalculation(List<Token> tokens) =>
        tokens.Any(t => t.IsExplicitOperation)
        || tokens.Any(t => t.Kind == Kind.Function)
        || tokens.Zip(tokens.Skip(1)).Any(p => p.First.EndsImplicitProduct && p.Second.StartsImplicitProduct);

    private static List<char>? VirtualClosings(List<Token> tokens)
    {
        var stack = new List<char>();
        foreach (var t in tokens)
        {
            if (t.Kind == Kind.Left) stack.Add(t.Bracket);
            else if (t.Kind == Kind.Right)
            {
                if (stack.Count == 0 || stack[^1] != t.Bracket) return null;
                stack.RemoveAt(stack.Count - 1);
            }
        }
        stack.Reverse();
        return stack;
    }

    private static List<Token>? Tokenize(string input, Separators separators)
    {
        var tokens = new List<Token>();
        var chars = input.ToCharArray();
        var index = 0;

        string ReadWord()
        {
            var sb = new StringBuilder();
            while (index < chars.Length && char.IsLetter(chars[index]) && chars[index] != 'π')
            {
                sb.Append(chars[index]);
                index++;
            }
            var word = sb.ToString().ToLowerInvariant();
            if (word == "log")
            {
                while (index < chars.Length && char.IsDigit(chars[index]))
                {
                    word += chars[index];
                    index++;
                }
            }
            return word;
        }

        while (index < chars.Length)
        {
            var c = chars[index];
            if (char.IsWhiteSpace(c))
            {
                index++;
                continue;
            }
            if (separators.IsPartOfNumber(c))
            {
                var number = ReadNumber(chars, ref index, separators);
                if (number is null) return null;
                tokens.Add(new Token(Kind.Number, number.Value));
                continue;
            }
            if (c == 'π')
            {
                tokens.Add(new Token(Kind.Constant, Math.PI));
                index++;
                continue;
            }
            if (char.IsLetter(c))
            {
                var word = ReadWord();
                if (word == "x") tokens.Add(new Token(Kind.Times));
                else if (word == "pi") tokens.Add(new Token(Kind.Constant, Math.PI));
                else if (word == "e") tokens.Add(new Token(Kind.Constant, Math.E));
                else if (word == "log10") tokens.Add(new Token(Kind.Function, Function: "log"));
                else if (Functions.Contains(word)) tokens.Add(new Token(Kind.Function, Function: word));
                else if (OfWords.Contains(word)) tokens.Add(new Token(Kind.OfWord));
                else return null;
                continue;
            }
            index++;
            switch (c)
            {
                case '+': tokens.Add(new Token(Kind.Plus)); break;
                case '-' or '−': tokens.Add(new Token(Kind.Minus)); break;
                case '*' or '×': tokens.Add(new Token(Kind.Times)); break;
                case '/' or '÷': tokens.Add(new Token(Kind.Divide)); break;
                case '^': tokens.Add(new Token(Kind.Power)); break;
                case '%': tokens.Add(new Token(Kind.Percent)); break;
                case '(': tokens.Add(new Token(Kind.Left, Bracket: '(')); break;
                case '[': tokens.Add(new Token(Kind.Left, Bracket: '[')); break;
                case ')': tokens.Add(new Token(Kind.Right, Bracket: '(')); break;
                case ']': tokens.Add(new Token(Kind.Right, Bracket: '[')); break;
                default: return null;
            }
        }
        return tokens.Count == 0 ? null : tokens;
    }

    /// <summary>Reads one number, deciding what each separator means: when both appear the last is the
    /// decimal point; the alternate alone is grouping only in groups of exactly three digits.</summary>
    private static double? ReadNumber(char[] chars, ref int index, Separators separators)
    {
        var written = new StringBuilder();
        while (index < chars.Length)
        {
            var c = chars[index];
            if (!separators.IsPartOfNumber(c)) break;
            if (separators.GroupingOnly is { } g && c == g && !(index + 1 < chars.Length && char.IsDigit(chars[index + 1]))) break;
            written.Append(c);
            index++;
        }
        if (written.Length == 0) return null;
        var raw = written.ToString();
        if (separators.GroupingOnly is { } groupingOnly && raw.Contains(groupingOnly))
        {
            var integerPart = new string(raw.TakeWhile(ch => ch != separators.Decimal && ch != separators.Alternate).ToArray());
            if (!LooksLikeGrouping(integerPart, groupingOnly) || raw[integerPart.Length..].Contains(groupingOnly)) return null;
            raw = raw.Replace(groupingOnly.ToString(), string.Empty);
        }

        var exponent = string.Empty;
        if (index < chars.Length && chars[index] is 'e' or 'E')
        {
            var e = index + 1;
            if (e < chars.Length && chars[e] is '+' or '-') e++;
            var digitsStart = e;
            while (e < chars.Length && char.IsDigit(chars[e])) e++;
            if (e > digitsStart)
            {
                exponent = new string(chars, index, e - index);
                index = e;
            }
        }

        var dec = separators.Decimal;
        var alt = separators.Alternate;
        var hasDecimal = raw.Contains(dec);
        var hasAlternate = dec != alt && raw.Contains(alt);
        var normalized = raw;
        if (hasDecimal && hasAlternate)
        {
            normalized = raw.LastIndexOf(alt) > raw.LastIndexOf(dec)
                ? raw.Replace(dec.ToString(), string.Empty).Replace(alt, '.')
                : raw.Replace(alt.ToString(), string.Empty).Replace(dec, '.');
        }
        else if (hasAlternate)
        {
            normalized = LooksLikeGrouping(raw, alt) ? raw.Replace(alt.ToString(), string.Empty) : raw.Replace(alt, '.');
        }
        else if (hasDecimal)
        {
            normalized = raw.Replace(dec, '.');
        }
        if (normalized.Count(ch => ch == '.') > 1) return null;
        if (!double.TryParse(normalized + exponent, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) return null;
        return value;
    }

    private static bool LooksLikeGrouping(string raw, char separator)
    {
        var parts = raw.Split(separator);
        if (parts.Length < 2) return false;
        var first = parts[0];
        if (first.Length == 0 || first.Length > 3 || first[0] == '0') return false;
        return parts.Skip(1).All(p => p.Length == 3);
    }

    // ---------------------------------------------------------------- parser

    private readonly record struct Operand(double Value, double? PercentRaw);

    private sealed class Parser(List<Token> tokens)
    {
        private int _index;
        private int _depth;

        public bool IsAtEnd => _index >= tokens.Count;

        private Token? Peek() => _index < tokens.Count ? tokens[_index] : null;

        private void Advance() => _index++;

        public Operand? ParseExpression()
        {
            if (_depth >= 32) return null;
            var left = ParseTerm();
            if (left is null) return null;
            while (Peek() is { Kind: Kind.Plus or Kind.Minus } token)
            {
                Advance();
                var right = ParseTerm();
                if (right is null) return null;
                var delta = right.Value.PercentRaw is { } pct ? left.Value.Value * pct / 100 : right.Value.Value;
                var value = token.Kind == Kind.Plus ? left.Value.Value + delta : left.Value.Value - delta;
                if (!double.IsFinite(value)) return null;
                left = new Operand(value, null);
            }
            return left;
        }

        private Operand? ParseTerm()
        {
            var left = ParseFactor();
            if (left is null) return null;
            while (Peek() is { } token)
            {
                var implicitProduct = token.StartsImplicitProduct;
                if (!implicitProduct && token.Kind is not (Kind.Times or Kind.Divide or Kind.OfWord)) break;
                if (!implicitProduct) Advance();
                if (token.Kind == Kind.OfWord && left.Value.PercentRaw is null) return null;
                var right = ParseFactor();
                if (right is null) return null;
                double value;
                if (token.Kind == Kind.Divide)
                {
                    if (right.Value.Value == 0) return null;
                    value = left.Value.Value / right.Value.Value;
                }
                else
                {
                    value = left.Value.Value * right.Value.Value;
                }
                if (!double.IsFinite(value)) return null;
                left = new Operand(value, null);
            }
            return left;
        }

        /// <summary>A sign applies to the whole power, so -2^2 is -4.</summary>
        private Operand? ParseFactor()
        {
            if (Peek() is { Kind: Kind.Minus or Kind.Plus } token)
            {
                Advance();
                var operand = ParseFactor();
                if (operand is null) return null;
                return token.Kind == Kind.Minus ? new Operand(-operand.Value.Value, -operand.Value.PercentRaw) : operand;
            }
            return ParsePower();
        }

        /// <summary>Powers group to the right: 2^3^2 is 2^(3^2).</summary>
        private Operand? ParsePower()
        {
            var b = ParsePrimary();
            if (b is null) return null;
            if (Peek() is not { Kind: Kind.Power }) return b;
            Advance();
            var exponent = ParseFactor();
            if (exponent is null) return null;
            var value = Math.Pow(b.Value.Value, exponent.Value.Value);
            return double.IsFinite(value) ? new Operand(value, null) : null;
        }

        private Operand? ParsePrimary()
        {
            if (Peek() is not { } token) return null;
            Operand b;
            switch (token.Kind)
            {
                case Kind.Number or Kind.Constant:
                    Advance();
                    b = new Operand(token.Value, null);
                    break;
                case Kind.Left:
                {
                    var value = ParseBracketed();
                    if (value is null) return null;
                    b = new Operand(value.Value, null);
                    break;
                }
                case Kind.Function:
                {
                    Advance();
                    var argument = ParseBracketed();
                    if (argument is null) return null;
                    var value = Apply(token.Function, argument.Value);
                    if (value is null) return null;
                    b = new Operand(value.Value, null);
                    break;
                }
                default:
                    return null;
            }
            if (Peek() is { Kind: Kind.Percent })
            {
                Advance();
                return new Operand(b.Value / 100, b.Value);
            }
            return b;
        }

        private double? ParseBracketed()
        {
            if (Peek() is not { Kind: Kind.Left } open) return null;
            Advance();
            _depth++;
            try
            {
                if (_depth >= 32) return null;
                var inner = ParseExpression();
                if (inner is null || Peek() is not { Kind: Kind.Right } close || close.Bracket != open.Bracket) return null;
                Advance();
                return inner.Value.Value;
            }
            finally
            {
                _depth--;
            }
        }
    }
}
