// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Features.Apps.Cleaner;

/// <summary>How an item is removed.</summary>
public enum CleanerItemKind
{
    /// <summary>A file or folder, moved to the Recycle Bin.</summary>
    Path,
    /// <summary>A system cache folder whose children are moved to the Recycle Bin (the folder stays).</summary>
    FolderContents,
    /// <summary>A value under HKCU\...\Run, backed up to a .reg file and then removed.</summary>
    RunValue,
    /// <summary>The Recycle Bin itself, emptied (the one permanent action, never pre-checked).</summary>
    RecycleBin,
}

/// <summary>One find of a scan: what it is, how big, and whether it starts checked.</summary>
public sealed class CleanerItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required CleanerCategory Category { get; init; }
    public long Size { get; init; }
    /// <summary>A short secondary line (owning app, device and date, startup command).</summary>
    public string Detail { get; init; } = string.Empty;
    /// <summary>Safe enough to clean without a second look: starts checked, lives under "Safe cleanup".</summary>
    public bool Recommended { get; init; }
    public CleanerItemKind Kind { get; init; } = CleanerItemKind.Path;
    /// <summary>Identity observed at scan time; removal is refused if the path now points elsewhere.</summary>
    public FileIdentity? Identity { get; init; }
    /// <summary>Process names that must not be running when this is cleaned (browsers).</summary>
    public string[] Processes { get; init; } = [];
    /// <summary>Set when a required-closed app is running: shown, but cannot be checked.</summary>
    public string? BlockedBy { get; init; }
    /// <summary>For <see cref="CleanerItemKind.RunValue"/>: the value name and the command seen at scan time.</summary>
    public string? RunValueName { get; init; }
    public string? RunCommand { get; init; }
    public bool Include { get; set; }

    public bool IsBlocked => BlockedBy is not null;
    public DisplayGroup Group => CleanerRules.GroupOf(Category, Recommended);
}
