// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json.Serialization;

namespace Fieldkit.Features.ClipboardTools;

[JsonConverter(typeof(JsonStringEnumConverter<ClipboardEntryKind>))]
public enum ClipboardEntryKind { Text, Image, Files }

/// <summary>
/// One saved copy. Stored in <c>%APPDATA%\Fieldkit\Clipboard\history.json</c> (never in the settings
/// file, so it is never part of a settings export). Images live next to it as PNG files.
/// </summary>
public sealed record ClipboardEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The content of a text entry; empty for images and files.</summary>
    public string Text { get; init; } = string.Empty;

    public DateTime CopiedAt { get; init; } = DateTime.Now;

    public DateTime? PinnedAt { get; init; }

    public ClipboardEntryKind Kind { get; init; } = ClipboardEntryKind.Text;

    /// <summary>Absolute paths of a files entry, in copy order.</summary>
    public IReadOnlyList<string> FilePaths { get; init; } = [];

    /// <summary>PNG file name inside the image store, for image entries.</summary>
    public string? ImageFile { get; init; }

    /// <summary>SHA-256 of the PNG, so copying the same picture again refreshes one entry.</summary>
    public string? ImageHash { get; init; }

    public int? ImageWidth { get; init; }

    public int? ImageHeight { get; init; }

    /// <summary>The executable the copy came from, when known.</summary>
    public string? SourcePath { get; init; }

    [JsonIgnore]
    public bool IsPinned => PinnedAt is not null;

    [JsonIgnore]
    public IEnumerable<string> FileNames => FilePaths.Select(Path.GetFileName).OfType<string>();

    [JsonIgnore]
    public string DimensionsLabel => ImageWidth is int w && ImageHeight is int h ? $"{w}×{h}" : string.Empty;

    /// <summary>Same content, whenever it was copied (copying it again refreshes the entry).</summary>
    public bool MatchesContent(ClipboardEntry other)
    {
        if (Kind != other.Kind) return false;
        return Kind switch
        {
            ClipboardEntryKind.Text => Text == other.Text,
            ClipboardEntryKind.Image => ImageHash is not null && ImageHash == other.ImageHash,
            _ => FilePaths.SequenceEqual(other.FilePaths, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>A one-line preview for list rows (bounded, whitespace folded).</summary>
    [JsonIgnore]
    public string Preview => Kind switch
    {
        ClipboardEntryKind.Text => ClipboardHistoryLogic.PreviewText(Text, keepLineBreaks: false),
        ClipboardEntryKind.Image => DimensionsLabel,
        _ => string.Join(", ", FileNames),
    };

    /// <summary>What the search box can match.</summary>
    public string SearchableText(string imageLabel) => Kind switch
    {
        ClipboardEntryKind.Text => Text,
        ClipboardEntryKind.Image => $"{imageLabel} png {DimensionsLabel}",
        _ => FilePaths.Any(ClipboardHistoryLogic.IsImageFileName)
            ? $"{imageLabel} {string.Join(" ", FileNames)}"
            : string.Join(" ", FileNames),
    };
}

/// <summary>What one clipboard read produced, before it becomes an entry.</summary>
public abstract record CapturedContent
{
    public sealed record TextContent(string Text) : CapturedContent;
    public sealed record ImageContent(byte[] Png, int Width, int Height, string Hash) : CapturedContent;
    public sealed record FilesContent(IReadOnlyList<string> Paths) : CapturedContent;
}
