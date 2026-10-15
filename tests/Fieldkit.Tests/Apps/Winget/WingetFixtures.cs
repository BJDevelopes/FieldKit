// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Fieldkit.Tests.Apps.Winget;

/// <summary>
/// Hand-built winget outputs for cases a single capture cannot show: pinned "explicit targeting"
/// tables and footers, spinner/VT noise, truncated cells, localized headers, East Asian wide
/// characters and an install transcript with progress bars. Layout follows winget's tables:
/// each column is as wide as its longest value plus one space, the rule spans every column.
/// </summary>
public static class WingetFixtures
{
    /// <summary>Pads cells to the given widths (in terminal cells); the last cell is not padded.</summary>
    public static string Line(int[] widths, params string[] cells)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cells.Length; i++)
        {
            sb.Append(cells[i]);
            if (i < cells.Length - 1) sb.Append(' ', Math.Max(1, widths[i] - Cells(cells[i])));
        }
        return sb.ToString();
    }

    public static string Rule(int[] widths) => new('-', widths.Sum());

    /// <summary>Independent width count for the fixtures: Hiragana, Katakana and CJK ideographs take two cells.</summary>
    private static int Cells(string s) => System.Text.RegularExpressions.Regex.Replace(s, "\u001b\\[[0-9;]*m", string.Empty)
        .Sum(c => c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿' ? 2 : 1);

    private static string Join(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    private static readonly int[] UpgradeWidths = [20, 20, 8, 10, 6];

    /// <summary>upgrade with a pinned package in the "explicit targeting" table and both footer hints.</summary>
    public static readonly string UpgradeWithExplicit = Join(
        Line(UpgradeWidths, "Name", "Id", "Version", "Available", "Source"),
        Rule(UpgradeWidths),
        Line(UpgradeWidths, "Git", "Git.Git", "2.53.0", "2.55.0.5", "winget"),
        Line(UpgradeWidths, "Spotify Music", "9NCBCSZSJRSB", "1.2.69", "1.2.70", "msstore"),
        Line(UpgradeWidths, "Discord", "Discord.Discord", "Unknown", "1.0.9205", "winget"),
        "3 upgrades available.",
        "",
        "The following packages have an upgrade available, but require explicit targeting for upgrade:",
        Line(UpgradeWidths, "Name", "Id", "Version", "Available", "Source"),
        Rule(UpgradeWidths),
        Line(UpgradeWidths, "Python 3.12.4", "Python.Python.3.12", "3.12.4", "3.12.10", "winget"),
        "2 package(s) have version numbers that cannot be determined. Use --include-unknown to see all results.",
        "1 package(s) have pins that prevent upgrade. Use the 'winget pin' command to view and edit pins. Using the --include-pinned argument may show more results.");

    private static readonly int[] SearchWidths = [30, 28, 9, 17, 6];

    /// <summary>search output preceded by the busy spinner (carriage-return redraws) and VT codes.</summary>
    public static readonly string SearchWithSpinner =
        "\r   - \r   \\ \r   | \r   / \r          \r\u001b[0m" + Join(
            Line(SearchWidths, "Name", "Id", "Version", "Match", "Source"),
            Rule(SearchWidths),
            Line(SearchWidths, "\u001b[1mVLC media player\u001b[0m", "VideoLAN.VLC", "3.0.21", "Moniker: vlc", "winget"),
            Line(SearchWidths, "VLC UWP", "9NBLGGH4VVNH", "Unknown", "", "msstore"));

    private static readonly int[] TruncatedWidths = [34, 32, 10, 6];

    /// <summary>A console-width-limited table: long values end in "…".</summary>
    public static readonly string Truncated = Join(
        Line(TruncatedWidths, "Name", "Id", "Version", "Source"),
        Rule(TruncatedWidths),
        Line(TruncatedWidths, "Microsoft Visual Studio Code Ins…", "Microsoft.VisualStudioCode.Ins…", "1.127.0", "winget"),
        Line(TruncatedWidths, "7-Zip", "7zip.7zip", "24.08", "winget"));

    private static readonly int[] GermanWidths = [18, 26, 9, 10, 6];

    /// <summary>upgrade with German column titles and footer.</summary>
    public static readonly string GermanUpgrade = Join(
        Line(GermanWidths, "Name", "ID", "Version", "Verfügbar", "Quelle"),
        Rule(GermanWidths),
        Line(GermanWidths, "Windows Terminal", "Microsoft.WindowsTerminal", "1.24.1", "1.25.2", "winget"),
        Line(GermanWidths, "Notepad++", "Notepad++.Notepad++", "8.8.4", "8.8.5", "winget"),
        "2 Aktualisierungen verfügbar.");

    private static readonly int[] JapaneseWidths = [14, 22, 11, 9, 6];

    /// <summary>list with Japanese titles and names: wide characters take two cells each.</summary>
    public static readonly string JapaneseList = Join(
        Line(JapaneseWidths, "名前", "ID", "バージョン", "利用可能", "ソース"),
        Rule(JapaneseWidths),
        Line(JapaneseWidths, "メモ帳", "Microsoft.Notepad", "11.2507.26", "", ""),
        Line(JapaneseWidths, "秀丸エディタ", "Hidemaru.Hidemaru", "9.25", "9.30", "winget"),
        Line(JapaneseWidths, "Git", "Git.Git", "2.53.0", "2.55.0", "winget"));

    private static readonly int[] FourColumnWidths = [18, 54, 9, 7];

    /// <summary>list without an Available column (nothing to update), so the 4th column is Source.</summary>
    public static readonly string ListWithoutAvailable = Join(
        Line(FourColumnWidths, "Nom", "ID", "Version", "Origine"),
        Rule(FourColumnWidths),
        Line(FourColumnWidths, "Windows Terminal", "Microsoft.WindowsTerminal", "1.25.2", "winget"),
        Line(FourColumnWidths, "Paint", @"MSIX\Microsoft.Paint_11.2508.361.0_x64__8wekyb3d8bbwe", "11.2508", ""),
        Line(FourColumnWidths, "Spotify Music", "9NCBCSZSJRSB", "1.2.70", "msstore"));

    private static readonly int[] PinWidths = [14, 19, 8, 7, 8];

    /// <summary>pin list (the last title, "Pin type", has a space in it).</summary>
    public static readonly string Pins = Join(
        Line(PinWidths, "Name", "Id", "Version", "Source", "Pin type"),
        Rule(PinWidths),
        Line(PinWidths, "Python 3.12.4", "Python.Python.3.12", "3.12.4", "winget", "Pinning"),
        Line(PinWidths, "Git", "Git.Git", "2.53.0", "winget", "Blocking"));

    public const string NoPins = "There are no pins configured.\r\n";

    public const string NoPackage = "No package found matching input criteria.\r\n";

    /// <summary>An install as winget streams it: status lines and redrawn progress bars.</summary>
    public static readonly string InstallTranscript =
        "Found Microsoft Visual Studio Code [Microsoft.VisualStudioCode] Version 1.141.1\r\n" +
        "This application is licensed to you by its owner.\r\n" +
        "Microsoft is not responsible for, nor does it grant any licenses to, third-party packages.\r\n" +
        "Downloading https://vscode.download.prss.microsoft.com/VSCodeUserSetup-x64-1.141.1.exe\r\n" +
        "\r  ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  0.00 B / 101 MB" +
        "\r  ██████▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  20.0 MB / 101 MB" +
        "\r  ██████████████████████████████   101 MB / 101 MB\r\n" +
        "Successfully verified installer hash\r\n" +
        "Starting package install...\r\n" +
        "\r  ████████████▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  40%" +
        "\r  ██████████████████████████████  100%\r\n" +
        "Successfully installed\r\n";

    /// <summary>A failed upgrade: the installer reported an error after the download.</summary>
    public static readonly string FailedUpgrade =
        "Found Git [Git.Git] Version 2.55.0.5\r\n" +
        "Downloading https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.5/Git-2.55.0.5-64-bit.exe\r\n" +
        "\r  ██████████████████████████████  64.0 MB / 64.0 MB\r\n" +
        "Successfully verified installer hash\r\n" +
        "Starting package install...\r\n" +
        "Installer failed with exit code: 1618\r\n" +
        "Another installation is already in progress. Complete that installation before proceeding with this install.\r\n";
}
