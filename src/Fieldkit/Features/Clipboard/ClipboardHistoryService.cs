// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>
/// Records copies while "Save clipboard history" is on: a clipboard format listener (no polling)
/// reads each change on the clipboard lane, skips private copies, ignored apps and Fieldkit's own
/// writes, and hands the content to <see cref="ClipboardHistory"/>. Also owns the history shortcut.
/// </summary>
public sealed class ClipboardHistoryService : FeatureService
{
    private IDisposable? _subscription;
    private IDisposable? _hotkey;
    private IDisposable? _shortcutObserver;
    private IDisposable? _limitObserver;
    private readonly System.Windows.Threading.DispatcherTimer _ageTimer = new(System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromHours(1) };

    public ClipboardHistoryService() => _ageTimer.Tick += (_, _) => Ui.Guard(ClipboardHistory.TrimToLimit);

    public override string FeatureId => ClipboardHistory.FeatureId;

    public override IReadOnlyList<string> EnableKeys => [ClipboardHistory.EnabledKey];

    public override bool EnabledByDefault => false;

    protected override void OnStart()
    {
        _ = ClipboardHistory.Entries; // load before the first copy arrives
        _subscription = ClipboardLane.Subscribe(OnClipboardChanged, priority: 200);
        SyncHotkey();
        _shortcutObserver = Settings.Observe(SyncHotkey, ClipboardHistory.ShortcutEnabledKey);
        _limitObserver = Settings.Observe(ClipboardHistory.TrimToLimit, ClipboardHistory.LimitKey, ClipboardHistory.MaxAgeKey);
        ClipboardHistory.TrimToLimit(); // items that aged out while Fieldkit was closed
        _ageTimer.Start();
    }

    protected override void OnStop()
    {
        _subscription?.Dispose();
        _subscription = null;
        _shortcutObserver?.Dispose();
        _shortcutObserver = null;
        _limitObserver?.Dispose();
        _limitObserver = null;
        _ageTimer.Stop();
        _hotkey?.Dispose();
        _hotkey = null;
        ClipboardHistory.ForgetLatest();
        ClipboardHistoryWindow.HideIfOpen();
    }

    private void SyncHotkey()
    {
        _hotkey?.Dispose();
        _hotkey = Settings.GetBool(ClipboardHistory.ShortcutEnabledKey, true)
            ? Hotkeys.Bind(ClipboardHistory.ShortcutKey, ClipboardHistory.DefaultShortcut, ClipboardHistoryWindow.Toggle)
            : null;
    }

    // Lane thread.
    private static void OnClipboardChanged(ClipboardChange change, ClipboardAccess access)
    {
        switch (change.OwnWrite)
        {
            case OwnWriteKind.HistoryCopy or OwnWriteKind.Transient or OwnWriteKind.Restore or OwnWriteKind.Clear:
                return;
        }
        var source = change.OwnWrite switch
        {
            OwnWriteKind.Rewrite => change.OwnWriteSource,
            OwnWriteKind.Other => null,
            _ => change.SourcePath,
        };
        if (change.OwnWrite is null && ClipboardHistory.IsIgnored(change.SourcePath, change.ForegroundPath))
        {
            Ui.OnUi(ClipboardHistory.ForgetLatest);
            return;
        }
        // An app that marks its copy as private (a password manager) is taken at its word:
        // the content is never even read.
        if (access.IsPrivate())
        {
            Ui.OnUi(ClipboardHistory.ForgetLatest);
            return;
        }
        var content = Read(access, Settings.GetBool(ClipboardHistory.IncludeImagesFilesKey, true));
        Ui.OnUi(() =>
        {
            if (content is null) ClipboardHistory.ForgetLatest();
            else ClipboardHistory.Capture(content, source);
        });
    }

    /// <summary>
    /// Files first, then text, then pictures. Office apps (Excel cells, Word and OneNote selections)
    /// offer a rendered picture next to the text; the text is what was copied, and asking for the picture
    /// makes the app render it (slow in Excel), so a picture only wins when there is no text at all
    /// (screenshots, "Copy image") (upstream #89).
    /// </summary>
    internal static CapturedContent? Read(ClipboardAccess access, bool includeImagesFiles)
    {
        if (includeImagesFiles && access.ReadFiles() is { Count: > 0 } files) return new CapturedContent.FilesContent(files);
        var url = ClipboardAccess.HasFormat("UniformResourceLocatorW") ? ReadUrl(access) : null;
        var text = ClipboardHistoryLogic.PreferredText(url, access.ReadText());
        if (text is not null) return new CapturedContent.TextContent(text);
        if (includeImagesFiles && ClipboardAccess.HasImage() && access.ReadImage() is { } image)
            return new CapturedContent.ImageContent(image.Png, image.Width, image.Height, ClipboardHistoryStore.Hash(image.Png));
        return null;
    }

    private static string? ReadUrl(ClipboardAccess access)
    {
        // "UniformResourceLocatorW" is UTF-16; read it through the text path of a registered format.
        var raw = access.ReadUtf16("UniformResourceLocatorW", 8192);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }
}
