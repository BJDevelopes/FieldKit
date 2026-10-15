// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Fieldkit.Features.Launcher.CommandBar;

namespace Fieldkit.Tests.Launcher.CommandBar;

public class CommandBarUnitsTests
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    private static string? Convert(string input) => CommandBarUnits.Convert(input, Us)?.Formatted;

    [Theory]
    [InlineData("100 km to mi", "62.14 mi")]
    [InlineData("100km to mi", "62.14 mi")]
    [InlineData("20c to f", "68°F")]
    [InlineData("68 f in c", "20°C")]
    [InlineData("100 c to f", "212°F")]
    [InlineData("5 gb to mb", "5,000 MB")]
    [InlineData("1 gib to mib", "1,024 MiB")]
    [InlineData("8 bits to bytes", "1 B")]
    [InlineData("2 hours to min", "120 min")]
    [InlineData("1 gal to l", "3.79 L")]
    [InlineData("1 kg to lb", "2.2 lb")]
    [InlineData("180 cm to ft", "5 ft 10.87 in")]
    [InlineData("5 in to cm", "12.7 cm")]
    [InlineData("0.5 m to cm", "50 cm")]
    [InlineData("1 m to km", "0.001 km")]
    [InlineData("3 ft to m", "0.9144 m")]
    public void Converts(string input, string expected) => Assert.Equal(expected, Convert(input));

    [Theory]
    [InlineData("100 km to kg")]
    [InlineData("km to mi")]
    [InlineData("100 km")]
    [InlineData("hello to world")]
    [InlineData("100 xyz to mi")]
    [InlineData("100 km to mi please now")]
    public void RefusesWhatIsNotAConversion(string input) => Assert.Null(Convert(input));

    [Fact]
    public void SplitsNumberFromUnit() => Assert.Equal(["100", "km", "to", "mi"], CommandBarUnits.Tokenize("100KM to MI"));

    [Fact]
    public void ReadsGroupedThousands() => Assert.Equal("1.86 mi", Convert("3,000 m to mi"));
}

public class CommandBarDatesTests
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly DateTime Now = new(2026, 10, 14, 15, 0, 0);

    [Fact]
    public void CountsForwardAndBack()
    {
        Assert.Equal("November 4, 2026", CommandBarDates.Evaluate("in 3 weeks", Now, Us)?.Formatted);
        Assert.Equal("Wednesday", CommandBarDates.Evaluate("in 3 weeks", Now, Us)?.Detail);
        Assert.Equal("October 11, 2026", CommandBarDates.Evaluate("3 days ago", Now, Us)?.Formatted);
        Assert.Equal("October 24, 2026", CommandBarDates.Evaluate("today + 10 days", Now, Us)?.Formatted);
        Assert.Equal("October 4, 2026", CommandBarDates.Evaluate("today - 10 days", Now, Us)?.Formatted);
        Assert.Equal("October 14, 2027", CommandBarDates.Evaluate("in 1 year", Now, Us)?.Formatted);
    }

    [Fact]
    public void NeedsADirection()
    {
        Assert.Null(CommandBarDates.Evaluate("3 days", Now, Us));
        Assert.Null(CommandBarDates.Evaluate("days", Now, Us));
    }

    [Fact]
    public void CountsDaysUntilADate()
    {
        var result = CommandBarDates.Evaluate("days until 12/25", Now, Us);
        Assert.NotNull(result);
        Assert.Equal("72 days", result.Formatted);
        Assert.Equal("December 25, 2026", result.Detail);
        // A day already gone this year means next year's.
        Assert.Equal("362 days", CommandBarDates.Evaluate("days until 10/11", Now, Us)?.Formatted);
        // Day-first culture reads 25/12.
        Assert.Equal("72 days", CommandBarDates.Evaluate("dias ate 25/12", Now, Br)?.Formatted);
        Assert.Equal("1 day", CommandBarDates.Evaluate("days until 10/15", Now, Us)?.Formatted);
    }

    [Fact]
    public void AnswersTimeElsewhere()
    {
        var result = CommandBarDates.Evaluate("time in tokyo", new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc), Us);
        Assert.NotNull(result);
        Assert.Equal("9:00 PM", result.Formatted);
        Assert.StartsWith("Tokyo · ", result.Detail);
        Assert.NotNull(CommandBarDates.Evaluate("new york time", Now, Us));
        Assert.NotNull(CommandBarDates.Evaluate("hora em londres", Now, Us));
        Assert.Null(CommandBarDates.Evaluate("time in atlantis", Now, Us));
    }

    [Fact]
    public void KnowsWhichComesFirst()
    {
        Assert.False(CommandBarDates.DayComesFirst(Us));
        Assert.True(CommandBarDates.DayComesFirst(Br));
    }
}

public class CommandBarColorsTests
{
    [Theory]
    [InlineData("#ff0000 to rgb", "rgb(255, 0, 0)")]
    [InlineData("#f00 to hex", "#FF0000")]
    [InlineData("rgb(0, 128, 255) to hex", "#0080FF")]
    [InlineData("#00ff00 to hsl", "hsl(120, 100%, 50%)")]
    [InlineData("hsl(240, 100%, 50%) to hex", "#0000FF")]
    [InlineData("#ff000080 to rgb", "rgba(255, 0, 0, 0.5)")]
    [InlineData("#a2b3b4 to wpf", "#FFA2B3B4")]
    public void Converts(string input, string expected) => Assert.Equal(expected, CommandBarColors.Convert(input)?.Formatted);

    [Theory]
    [InlineData("#abc", true)]
    [InlineData("#abcd12", true)]
    [InlineData("rgba(1,2,3,0.5)", true)]
    [InlineData("#abcde", false)]
    [InlineData("#1234567", false)]
    [InlineData("rgb(300, 0, 0)", false)]
    [InlineData("hello", false)]
    public void ParsesColours(string input, bool valid) => Assert.Equal(valid, CommandBarColors.Parse(input) is not null);

    [Fact]
    public void RefusesUnknownTargets() => Assert.Null(CommandBarColors.Convert("#fff to cmyk"));
}
