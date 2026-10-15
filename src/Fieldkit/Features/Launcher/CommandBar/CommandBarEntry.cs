// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Media;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>How a row draws its icon.</summary>
public enum CommandBarIconKind
{
    /// <summary>A Segoe Fluent glyph on a tinted plate.</summary>
    Glyph,
    /// <summary>The shell icon of a file, folder, shortcut or AppsFolder item (<see cref="CommandBarEntry.IconPath"/>).</summary>
    Shell,
    /// <summary>A window's own icon (<see cref="CommandBarEntry.WindowHandle"/>).</summary>
    Window,
    /// <summary>A colour swatch (<see cref="CommandBarEntry.Swatch"/>).</summary>
    Color,
    /// <summary>A ready image (<see cref="CommandBarEntry.Image"/>), used by previews.</summary>
    Image,
    /// <summary>An emoji drawn as text.</summary>
    Emoji,
}

/// <summary>One row the bar can offer: something to find, read and run (port of upstream CommandBarEntry).</summary>
public sealed record CommandBarEntry
{
    public required string Id { get; init; }

    private readonly string? _stableKey;

    /// <summary>What a name, pin, habit or shortcut is filed under (survives what the id cannot).</summary>
    public string StableKey
    {
        get => _stableKey ?? Id;
        init => _stableKey = value;
    }

    public required string Title { get; init; }
    public string Subtitle { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;

    public CommandBarIconKind IconKind { get; init; } = CommandBarIconKind.Glyph;
    public string Glyph { get; init; } = Fieldkit.UI.Glyphs.Command;
    public string? IconPath { get; init; }
    public IntPtr WindowHandle { get; init; }
    public ColorValue? Swatch { get; init; }
    public ImageSource? Image { get; init; }

    /// <summary>The global shortcut text of the action this row runs ("Ctrl+Alt+V").</summary>
    public string? ShortcutText { get; init; }

    /// <summary>A live state worth the small green dot (a running app, a switch that is on).</summary>
    public bool IsActive { get; init; }

    /// <summary>Set when the feature is installed but switched off: Enter opens its Settings page.</summary>
    public string? NeedsSetupFeature { get; init; }
    public string? NeedsSetupPage { get; init; }

    /// <summary>Commands that take a number ("volume 40").</summary>
    public (int Min, int Max)? NumericRange { get; init; }
    public bool NumericIsOptional { get; init; }

    /// <summary>The phrase shown before a destructive Enter.</summary>
    public string? ConfirmationPrompt { get; init; }

    /// <summary>A fact this row answers, shown on the right in the accent colour.</summary>
    public string? AnswerValue { get; init; }

    /// <summary>The calculator's kind of row: pinned above everything and styled as a result.</summary>
    public bool IsAnswer { get; init; }

    /// <summary>Only durable identities may retain habits (window handles and pids are reused).</summary>
    public bool CountsUsage { get; init; } = true;

    /// <summary>The text ranking reads instead of the title (emoji rows lead with the glyph).</summary>
    public string? MatchTitle { get; init; }

    /// <summary>Rows that put something into the bar instead of leaving it.</summary>
    public bool KeepsBarOpen { get; init; }

    /// <summary>Rows that read whatever is typed after their name (saved searches, scripts).</summary>
    public bool TakesArgument { get; init; }

    /// <summary>Where the row lives on disk, for Ctrl+Enter and "Show in File Explorer".</summary>
    public string? RevealPath { get; init; }

    /// <summary>What Enter does (the number is the argument for numeric commands).</summary>
    public Action<int?> Run { get; init; } = _ => { };

    public bool CanReveal => RevealPath is not null;

    /// <summary>Whether running it asks something first (confirm, a required number, setup).</summary>
    public bool NeedsPrompt => ConfirmationPrompt is not null || (NumericRange is not null && !NumericIsOptional) || NeedsSetupFeature is not null;

    public bool UsesPlateIcon => IconKind is CommandBarIconKind.Glyph;
}
