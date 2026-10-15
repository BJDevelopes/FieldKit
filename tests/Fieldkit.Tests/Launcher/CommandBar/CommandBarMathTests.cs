// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Fieldkit.Features.Launcher.CommandBar;

namespace Fieldkit.Tests.Launcher.CommandBar;

public class CommandBarMathTests
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private static string? Eval(string input, CultureInfo? culture = null) => CommandBarMath.Evaluate(input, culture ?? Us)?.Formatted;

    [Theory]
    [InlineData("2+2*3", "8")]
    [InlineData("(2+2)*3", "12")]
    [InlineData("0.1+0.2", "0.3")]
    [InlineData("10/4", "2.5")]
    [InlineData("2^3^2", "512")]
    [InlineData("-2^2", "-4")]
    [InlineData("2x3", "6")]
    [InlineData("3 × 4", "12")]
    [InlineData("8 ÷ 2", "4")]
    [InlineData("1000*1000", "1,000,000")]
    [InlineData("2pi", "6.28318531")]
    [InlineData("sqrt(16)", "4")]
    [InlineData("log10(1000)", "3")]
    [InlineData("ln(e)", "1")]
    [InlineData("abs(-3)", "3")]
    [InlineData("round(2.5)", "3")]
    [InlineData("20% of 50", "10")]
    [InlineData("100 + 10%", "110")]
    [InlineData("100 - 10%", "90")]
    [InlineData("5 * 3 =", "15")]
    [InlineData("1e3 + 1", "1,001")]
    [InlineData("3(4)", "12")]
    public void EvaluatesExpressions(string input, string expected) => Assert.Equal(expected, Eval(input));

    [Theory]
    [InlineData("42")]
    [InlineData("hello")]
    [InlineData("2026-07-27")]
    [InlineData("27/07/2026")]
    [InlineData("10:30")]
    [InlineData("1/0")]
    [InlineData("sqrt(-1)")]
    [InlineData("2 = 2")]
    [InlineData("x = 3")]
    [InlineData("10 of 5")]
    [InlineData("(2+3]")]
    [InlineData("")]
    public void RefusesWhatIsNotASum(string input) => Assert.Null(Eval(input));

    [Fact]
    public void SuppliesMissingClosingBrackets()
    {
        var result = CommandBarMath.Evaluate("(2+3*(4", Us);
        Assert.NotNull(result);
        Assert.Equal("14", result.Formatted);
        Assert.Equal("))", result.ClosingBrackets);
    }

    [Fact]
    public void SubtractionIsNotADate() => Assert.Equal("50", Eval("100 - 20 - 30"));

    [Fact]
    public void ReadsLocalSeparators()
    {
        Assert.Equal("2,5", Eval("1,5+1", De));
        Assert.Equal("1.001", Eval("1.000+1", De));
        Assert.Equal("1,001", Eval("1,000+1", Us));
        Assert.Equal("1.5", Eval("1,5*1", Us)); // "1,5" is not thousands, so it is a decimal
    }

    [Fact]
    public void UsesScientificNotationForExtremes()
    {
        Assert.Equal("1e12", Eval("10^12"));
        Assert.Equal("1e-7", Eval("1/10^7"));
        Assert.Equal("1.5e15", Eval("1.5*10^15"));
    }

    [Fact]
    public void ReusableExpressionIsUngrouped()
    {
        var result = CommandBarMath.Evaluate("1000*1000", Us)!;
        Assert.Equal("1000000", CommandBarMath.ReusableExpression(result, Us));
        var negative = CommandBarMath.Evaluate("2-5", Us)!;
        Assert.Equal("(-3)", CommandBarMath.ReusableExpression(negative, Us));
        var dec = CommandBarMath.Evaluate("1/4", De)!;
        Assert.Equal("0,25", CommandBarMath.ReusableExpression(dec, De));
    }

    [Theory]
    [InlineData("2026-07-27", true)]
    [InlineData("10:30", true)]
    [InlineData("27/07/2026", true)]
    [InlineData("100 - 20 - 30", false)]
    [InlineData("10/2", false)]
    public void RecognizesDatesAndTimes(string input, bool expected) => Assert.Equal(expected, CommandBarMath.LooksLikeDateOrTime(input));
}
