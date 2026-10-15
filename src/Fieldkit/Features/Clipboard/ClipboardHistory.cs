// SPDX-License-Identifier: GPL-3.0-or-later
using System.Media;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>
/// The saved clipboard entries and every operation on them (UI thread). Loaded on first use, so the
/// history window and Settings work on the saved items even while capture is off, like upstream.
/// </summary>
public static class ClipboardHistory
{
    public const string FeatureId = "clipboardHistory";
    public const string EnabledKey = "clipboardHistory.enabled";
    public const string LimitKey = "clipboardHistory.limit";
    public const string MaxAgeKey = "clipboardHistory.maxAgeDays";
    public const string SkipSensitiveKey = "clipboardHistory.skipSensitive";
    public const string IncludeImagesFilesKey = "clipboardHistory.includeImagesFiles";
    public const string IgnoredAppsKey = "clipboardHistory.ignoredApps";
    public const string ShortcutEnabledKey = "clipboardHistory.shortcutEnabled";
    public const string ShortcutKey = "clipboardHistory.shortcut";
    public const string DefaultShortcut = "Ctrl+Alt+Win+V";
    public const string LayoutKey = "clipboardHistory.layout";
    public const string PreviewKey = "clipboardHistory.quickPreview";
    public const string WindowWidthKey = "clipboardHistory.windowWidth";
    public const string WindowHeightKey = "clipboardHistory.windowHeight";

    private static List<ClipboardEntry>? _entries;

    /// <summary>Raised on the UI thread after any change.</summary>
    public static event Action? Changed;

    public static IReadOnlyList<ClipboardEntry> Entries => _entries ??= ClipboardHistoryStore.Load();

    public static IEnumerable<ClipboardEntry> Pinned => Entries.Where(e => e.IsPinned);

    public static IEnumerable<ClipboardEntry> Recent => Entries.Where(e => !e.IsPinned);

    /// <summary>The entry that is on the clipboard right now, if a capture recorded it.</summary>
    public static Guid? LatestId { get; private set; }

    public static List<string> IgnoredApps
    {
        get => Settings.Get<List<string>>(IgnoredAppsKey, []) ?? [];
        set => Settings.Set(IgnoredAppsKey, value.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Whether a copy that came from <paramref name="paths"/> must be skipped.</summary>
    public static bool IsIgnored(params string?[] paths)
    {
        var ignored = IgnoredApps;
        if (ignored.Count == 0) return false;
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            var exe = System.IO.Path.GetFileName(path);
            if (ignored.Any(i => string.Equals(i, exe, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(i, path, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    // -------------------------------------------------------------- capture

    /// <summary>Records a fresh copy (UI thread).</summary>
    public static void Capture(CapturedContent content, string? sourcePath)
    {
        ClipboardEntry fresh;
        switch (content)
        {
            case CapturedContent.TextContent t:
                var text = ClipboardHistoryLogic.StorableText(t.Text);
                if (text is null) return;
                if (Settings.GetBool(SkipSensitiveKey, true) && SensitiveText.LooksSensitive(text)) return;
                fresh = new ClipboardEntry { Text = text };
                break;
            case CapturedContent.ImageContent i:
                var existing = Entries.FirstOrDefault(e => e.Kind == ClipboardEntryKind.Image && e.ImageHash == i.Hash);
                var file = existing?.ImageFile ?? ClipboardHistoryStore.StoreImage(i.Png);
                if (file is null) return;
                fresh = new ClipboardEntry { Kind = ClipboardEntryKind.Image, ImageFile = file, ImageHash = i.Hash, ImageWidth = i.Width, ImageHeight = i.Height };
                break;
            case CapturedContent.FilesContent f:
                fresh = new ClipboardEntry { Kind = ClipboardEntryKind.Files, FilePaths = f.Paths.ToList() };
                break;
            default:
                return;
        }
        fresh = fresh with { SourcePath = sourcePath };
        var (list, promoted) = ClipboardHistoryLogic.Promote(Entries, fresh, DateTime.Now);
        LatestId = promoted.Id;
        Commit(list);
    }

    /// <summary>The clipboard changed to something not recorded: nothing in the list is "current" any more.</summary>
    public static void ForgetLatest() => LatestId = null;

    // ----------------------------------------------------------- operations

    public static void TogglePin(Guid id) => Commit(ClipboardHistoryLogic.TogglePin(Entries, id, DateTime.Now));

    public static void Remove(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        Commit(Entries.Where(e => !set.Contains(e.Id)).ToList());
    }

    public static void Move(Guid id, MoveDirection direction) => Commit(ClipboardHistoryLogic.Move(Entries, id, direction));

    public static bool CanMove(Guid id, MoveDirection direction) => ClipboardHistoryLogic.CanMove(Entries, id, direction);

    public static void ClearRecent(IReadOnlyDictionary<Guid, DateTime> confirmed) =>
        Commit(ClipboardHistoryLogic.ClearRecent(Entries, confirmed));

    /// <summary>Replaces a text entry's content (the preview's Edit). False when the draft can't be stored.</summary>
    public static bool UpdateText(Guid id, string draft)
    {
        var text = ClipboardHistoryLogic.StorableText(draft);
        if (text is null) return false;
        var list = Entries.ToList();
        var index = list.FindIndex(e => e.Id == id && e.Kind == ClipboardEntryKind.Text);
        if (index < 0) return false;
        list[index] = list[index] with { Text = text };
        Commit(list);
        return true;
    }

    /// <summary>Applies the item limit and the "keep for" age (Settings changes, and hourly while capturing).</summary>
    public static void TrimToLimit()
    {
        var retained = Kept(Entries);
        if (retained.Count != Entries.Count) Commit(retained, trim: false);
    }

    private static List<ClipboardEntry> Kept(IReadOnlyList<ClipboardEntry> list)
    {
        var days = ClipboardHistoryLogic.SanitizedAgeDays(Settings.GetInt(MaxAgeKey, 0));
        var limit = ClipboardHistoryLogic.SanitizedLimit(Settings.GetInt(LimitKey, ClipboardHistoryLogic.DefaultLimit));
        return ClipboardHistoryLogic.Retained(ClipboardHistoryLogic.WithinAge(list, days, DateTime.Now), limit);
    }

    private static void Commit(List<ClipboardEntry> list, bool trim = true)
    {
        if (trim) list = Kept(list);
        _entries = list;
        if (LatestId is Guid id && list.All(e => e.Id != id)) LatestId = null;
        ClipboardHistoryStore.SaveAsync(list);
        Changed?.Invoke();
    }

    // --------------------------------------------------------- copy & paste

    /// <summary>
    /// Puts the entries on the clipboard (files as files, one picture as a picture, otherwise text) and
    /// moves them to the top. Then pastes into <paramref name="pasteTarget"/> when given.
    /// </summary>
    public static void Copy(IReadOnlyList<ClipboardEntry> selection, bool paste, IntPtr pasteTarget)
    {
        var mode = ClipboardHistoryLogic.PasteModeFor(selection);
        if (mode is null) return;
        byte[]? png = mode is PasteMode.Image img ? ClipboardHistoryStore.ReadImage(img.Entry.ImageFile) : null;
        if (mode is PasteMode.Image && png is null)
        {
            SystemSounds.Beep.Play();
            return;
        }
        var ids = selection.Select(e => e.Id).ToList();
        ClipboardLane.Run(access => mode switch
        {
            PasteMode.Files f => access.WriteFiles(f.Paths, OwnWriteKind.HistoryCopy),
            PasteMode.Text t => access.WriteText(t.Value, OwnWriteKind.HistoryCopy),
            PasteMode.Image => access.WriteImage(png!, OwnWriteKind.HistoryCopy),
            _ => false,
        }).ContinueWith(task =>
        {
            var ok = task.Status == TaskStatus.RanToCompletion && task.Result;
            Ui.OnUi(() =>
            {
                if (!ok)
                {
                    SystemSounds.Beep.Play();
                    return;
                }
                Commit(ClipboardHistoryLogic.Touch(Entries, ids, DateTime.Now));
                if (ids.Count == 1) LatestId = ids[0];
                if (paste) PasteHelper.PasteInto(pasteTarget);
            });
        }, TaskScheduler.Default);
    }
}
