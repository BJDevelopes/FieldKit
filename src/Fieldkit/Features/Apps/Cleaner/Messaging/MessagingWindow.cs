// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Cleaner.Messaging;

/// <summary>
/// The messaging downloads review (upstream WhatsAppDownloadsSettings' manual, activity and
/// privacy sections; the options live on Settings → Cleaner).
/// </summary>
public sealed class MessagingWindow : ToolWindow
{
    public const string Key = "cleaner.messaging";

    public MessagingWindow() : base(S.MsgTitle, 640, 720)
    {
        var manager = MessagingDownloads.Shared;
        var view = new MessagingView(manager, MessagingPaths.Current(), interactive: true);
        Content = view;
        manager.ReviewVisible = true;
        Closed += (_, _) =>
        {
            manager.ReviewVisible = false;
            view.Detach();
        };
        if (manager.State == MessagingDownloads.Phase.Idle) _ = manager.ScanAsync();
    }

    public static void Open() => ShowSingle(Key, () => new MessagingWindow());

    /// <summary>"Last cleanup today at 9:00: 12 files · 48 MB · 0 failed" or "No cleanup has run yet."</summary>
    public static string LastRunLine()
    {
        var last = CleanerSchedule.FromStamp(Settings.GetDouble(MessagingKeys.LastCleanup, 0));
        if (last is null) return S.MsgNeverRun;
        return string.Format(S.MsgLastRunFormat, CleanerRules.RelativeDate(last.Value, DateTime.Now),
            Settings.GetInt(MessagingKeys.LastCleanupCount, 0), ByteFormat.Format(Settings.Get<long>(MessagingKeys.LastCleanupBytes, 0)),
            Settings.GetInt(MessagingKeys.LastCleanupFailed, 0));
    }

    /// <summary>"Organize eligible files now", "Undo last organization" and the status line.</summary>
    public static FrameworkElement OrganizerActions()
    {
        var organizer = MessagingOrganizer.Shared;
        var holder = new ContentControl();
        void Build()
        {
            var now = Ui.Button(S.OrgOrganizeNow, () => _ = organizer.RunAsync(manual: true), accent: true);
            now.IsEnabled = !organizer.IsBusy;
            now.HorizontalAlignment = HorizontalAlignment.Left;
            var undo = Ui.Button(S.OrgUndo, () => _ = organizer.UndoLastRunAsync());
            undo.IsEnabled = !organizer.IsBusy && organizer.CanUndo;
            holder.Content = Ui.Stack(8, Ui.Split(now, undo), OrganizerStatus(organizer), CleanerUi.Small(S.OrgUndoNote, "TextTertiary"));
        }
        Build();
        Ui.Track(holder, () =>
        {
            void OnChanged() => Ui.OnUi(Build);
            organizer.Changed += OnChanged;
            return new ActionDisposable(() => organizer.Changed -= OnChanged);
        });
        return holder;
    }

    private static TextBlock OrganizerStatus(MessagingOrganizer organizer)
    {
        switch (organizer.State)
        {
            case MessagingOrganizer.Phase.Organizing or MessagingOrganizer.Phase.Undoing:
                return CleanerUi.Small(S.OrgWorking);
            case MessagingOrganizer.Phase.Done or MessagingOrganizer.Phase.Failed:
                var (moved, dup, failed) = organizer.LastResult;
                if (organizer.State == MessagingOrganizer.Phase.Failed && failed == 0) failed = 1;
                return CleanerUi.Small(string.Format(S.OrgResultFormat, moved, dup, failed), failed == 0 ? "Green" : "Orange");
            default:
                var last = CleanerSchedule.FromStamp(Settings.GetDouble(MessagingKeys.OrganizerLastRun, 0));
                return last is null
                    ? CleanerUi.Small(S.OrgNeverRun)
                    : CleanerUi.Small(string.Format(S.OrgLastRunFormat, CleanerRules.RelativeDate(last.Value, DateTime.Now),
                        Settings.GetInt(MessagingKeys.OrganizerLastMoved, 0), Settings.GetInt(MessagingKeys.OrganizerLastDuplicates, 0),
                        Settings.GetInt(MessagingKeys.OrganizerLastFailed, 0)));
        }
    }

    /// <summary>Renders the review with realistic sample data (for review in tests).</summary>
    public static void RenderPreview(string path, bool dark)
    {
        var paths = new MessagingPaths(@"C:\Users\alex\Downloads", @"C:\Users\alex\Downloads\Telegram Desktop",
            @"C:\Users\alex\AppData\Local\Packages\5319275A.WhatsAppDesktop_cv1g1gvanyjgm\LocalState\shared\transfers");
        var now = DateTime.Now;
        MessagingCandidate C(string name, MessagingCategory cat, long size, int daysAgo, bool rules, MessagingSource src, MessagingApp app,
            bool excluded = false) => new()
        {
            Id = name, Path = System.IO.Path.Combine(src == MessagingSource.TelegramFolder ? paths.TelegramFolder : paths.Downloads, name),
            Root = paths.Downloads, Source = src, App = app, Size = size, DownloadedAt = now.AddDays(-daysAgo), ModifiedAt = now.AddDays(-daysAgo),
            Category = cat, EligibleForRules = rules, Excluded = excluded, Include = rules && !excluded,
        };
        var manager = MessagingDownloads.ForPreview(
        [
            C("WhatsApp Image 2026-10-12 at 18.04.11.jpeg", MessagingCategory.Image, 284_000, 2, false, MessagingSource.Downloads, MessagingApp.WhatsApp),
            C("WhatsApp Video 2026-10-01 at 09.12.40.mp4", MessagingCategory.Video, 18_400_000, 13, true, MessagingSource.Downloads, MessagingApp.WhatsApp),
            C("voice note.ogg", MessagingCategory.Audio, 96_000, 16, true, MessagingSource.TelegramFolder, MessagingApp.Telegram),
            C("Invoice September.pdf", MessagingCategory.Document, 412_000, 20, false, MessagingSource.Downloads, MessagingApp.WhatsApp),
            C("holiday photos.zip", MessagingCategory.Archive, 64_000_000, 31, false, MessagingSource.TelegramFolder, MessagingApp.Telegram, excluded: true),
        ]);
        var view = new MessagingView(manager, paths, interactive: false);
        PreviewRenderer.Render(view, path, 640, 980, dark);
    }
}

/// <summary>The review content: watched folders, manual scan and list, activity, privacy.</summary>
internal sealed class MessagingView : ContentControl
{
    private readonly MessagingDownloads _manager;
    private readonly MessagingPaths _paths;

    public MessagingView(MessagingDownloads manager, MessagingPaths paths, bool interactive)
    {
        _manager = manager;
        _paths = paths;
        if (interactive) _manager.Changed += Rebuild;
        Rebuild();
    }

    public void Detach() => _manager.Changed -= Rebuild;

    private double _scrollOffset;

    private void Rebuild()
    {
        if (Content is ScrollViewer old) _scrollOffset = old.VerticalOffset;
        var page = Ui.Stack(18, IntroGroup(), ManualGroup(), ActivityGroup(), PrivacyGroup());
        page.Margin = new Thickness(24, 20, 24, 24);
        var scroll = new ScrollViewer
        {
            Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var offset = _scrollOffset;
        scroll.Loaded += (_, _) => scroll.ScrollToVerticalOffset(offset);
        Content = scroll;
    }

    private FrameworkElement IntroGroup()
    {
        var folders = Ui.Stack(6, Ui.Text(S.MsgFolder, 12, brush: "TextSecondary"),
            FolderRow(_paths.Downloads), FolderRow(_paths.TelegramFolder));
        var rows = new List<UIElement?> { Ui.Caption(S.MsgIntro, 13), folders };
        if (_manager.WhatsAppSkipped) rows.Add(CleanerUi.Small(S.MsgCloseWhatsApp, "Orange", 12));
        if (!Settings.GetBool(MessagingKeys.Enabled, false))
            rows.Insert(0, Ui.ToggleRow(S.MsgTitle, S.MsgHubDescription, MessagingKeys.Enabled, false));
        return Ui.Group(S.MsgTitle, rows.ToArray());
    }

    private static FrameworkElement FolderRow(string path)
    {
        var text = Ui.Text(CleanerRules.PrettyPath(path, CleanerPaths.Current()), 12, wrap: false);
        text.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        var reveal = Ui.Button(S.Reveal, () => CleanerUi.OpenFolder(path));
        return Ui.Split(Ui.Row(8, Ui.Icon(Glyphs.Folder, 14, "TextSecondary"), text), reveal);
    }

    private FrameworkElement ManualGroup()
    {
        var scan = Ui.Button(S.Scan, () => _ = _manager.ScanAsync(), accent: true, glyph: Glyphs.Search);
        scan.IsEnabled = !_manager.IsBusy;
        var scanRow = Ui.Row(10, scan, _manager.State == MessagingDownloads.Phase.Scanning ? CleanerUi.Small(S.Scanning) : null);
        var rows = new List<UIElement?> { Ui.Caption(S.MsgManualIntro, 12.5), scanRow };
        switch (_manager.State)
        {
            case MessagingDownloads.Phase.Results:
                rows.AddRange(Results());
                break;
            case MessagingDownloads.Phase.Cleaning:
                rows.Add(CleanerUi.Small(S.Cleaning));
                break;
            case MessagingDownloads.Phase.Done:
                var (moved, bytes, failed) = _manager.LastResult;
                rows.Add(Ui.Row(8, Ui.Icon(failed == 0 ? CleanerGlyphs.CheckCircle : CleanerGlyphs.Warning, 14, failed == 0 ? "Green" : "Orange"),
                    Ui.Text(string.Format(S.MsgNotificationFormat, moved, ByteFormat.Format(bytes), failed), 12)));
                break;
            case MessagingDownloads.Phase.Failed:
                rows.Add(CleanerUi.Small(S.MsgScanFailed, "Orange", 12));
                break;
        }
        return Ui.Group(S.MsgManualTitle, rows.ToArray());
    }

    private IEnumerable<UIElement> Results()
    {
        if (_manager.Candidates.Count == 0)
        {
            yield return Ui.Row(8, Ui.Icon(CleanerGlyphs.CheckCircle, 14, "TextSecondary"), Ui.Text(S.MsgNoFiles, 12.5, brush: "TextSecondary"));
            yield break;
        }
        yield return Ui.Split(CleanerUi.Small(string.Format(S.MsgResultsFormat, _manager.Candidates.Count, ByteFormat.Format(_manager.TotalBytes)), size: 12),
            Ui.Button(S.MsgSelectRules, _manager.SelectRules));

        var list = new StackPanel();
        foreach (var c in _manager.Candidates)
        {
            if (list.Children.Count > 0) list.Children.Add(Ui.Divider(0));
            list.Children.Add(CandidateRow(c));
        }
        var box = new Border
        {
            CornerRadius = new CornerRadius(8),
            Child = new ScrollViewer { Content = list, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        box.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        yield return box;

        var clean = Ui.Button(string.Format(S.MsgCleanSelectedFormat, _manager.SelectedCount, ByteFormat.Format(_manager.SelectedBytes)),
            () => _ = _manager.CleanSelectedAsync(automatic: false), accent: true);
        clean.IsEnabled = _manager.SelectedCount > 0;
        yield return Ui.Split(CleanerUi.Small(S.MsgTrashNote, "TextTertiary", 11), clean);
    }

    private FrameworkElement CandidateRow(MessagingCandidate c)
    {
        var check = CleanerUi.Check(c.Include, v => _manager.SetInclude(c, v), enabled: !c.Excluded);
        var icon = Ui.Icon(MessagingRules.CategoryGlyph(c.Category), 15, c.Excluded ? "TextSecondary" : "Accent");
        icon.Width = 20;
        var name = Ui.Text(c.Name, 12, wrap: false);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var source = c.Organized ? "Organized" : c.App.ToString();
        var sub = Ui.Text($"{MessagingRules.CategoryName(c.Category)} · {c.DownloadedAt:g} · {source}", 10.5, brush: "TextTertiary", wrap: false);
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        var text = Ui.Stack(2, name, sub);
        var size = Ui.Text(ByteFormat.Format(c.Size), 11, brush: "TextSecondary", wrap: false);
        FrameworkElement action;
        if (c.Excluded)
        {
            action = Ui.Button(S.MsgManageAgain, () => _manager.RemoveExclusion(c));
        }
        else
        {
            var more = Ui.IconButton(CleanerGlyphs.More, S.MsgKeep, () => { });
            var menu = new ContextMenu();
            var keep = new MenuItem { Header = S.MsgKeep };
            keep.Click += (_, _) => _manager.Exclude(c);
            var reveal = new MenuItem { Header = S.Reveal };
            reveal.Click += (_, _) => CleanerUi.Reveal(c.Path);
            menu.Items.Add(keep);
            menu.Items.Add(reveal);
            more.Click += (_, _) =>
            {
                menu.PlacementTarget = more;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
            };
            action = more;
        }
        var grid = new Grid { Margin = new Thickness(9, 6, 9, 6), Background = CleanerUi.Transparent };
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        (UIElement El, Thickness Margin)[] cells =
        [
            (check, new Thickness(0)), (icon, new Thickness(8, 0, 8, 0)), (text, new Thickness(0)), (size, new Thickness(8, 0, 8, 0)), (action, new Thickness(0)),
        ];
        for (var i = 0; i < cells.Length; i++)
        {
            if (cells[i].El is FrameworkElement fe)
            {
                fe.Margin = cells[i].Margin;
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            Grid.SetColumn(cells[i].El, i);
            grid.Children.Add(cells[i].El);
        }
        var context = new ContextMenu();
        var toggleKeep = new MenuItem { Header = c.Excluded ? S.MsgManageAgain : S.MsgKeep };
        toggleKeep.Click += (_, _) =>
        {
            if (c.Excluded) _manager.RemoveExclusion(c);
            else _manager.Exclude(c);
        };
        var revealItem = new MenuItem { Header = S.Reveal };
        revealItem.Click += (_, _) => CleanerUi.Reveal(c.Path);
        context.Items.Add(toggleKeep);
        context.Items.Add(revealItem);
        grid.ContextMenu = context;
        grid.ToolTip = c.Path;
        return grid;
    }

    private static FrameworkElement ActivityGroup()
    {
        var next = Settings.GetBool(MessagingKeys.Automatic, false) && MessagingService.NextFire is { } n
            ? CleanerUi.Small(string.Format(S.MsgNextRunFormat, CleanerRules.RelativeDate(n, DateTime.Now)), size: 12)
            : null;
        var organizer = Settings.GetBool(MessagingKeys.OrganizerEnabled, false)
            ? Ui.Stack(8, Ui.Row(8, Ui.Text(S.OrgTitle, 13.5, FontWeights.Medium), CleanerUi.ExperimentalBadge()), MessagingWindow.OrganizerActions())
            : null;
        return Ui.Group(S.MsgActivity, Ui.Text(MessagingWindow.LastRunLine(), 12.5), next, organizer);
    }

    private static FrameworkElement PrivacyGroup()
    {
        var note = Settings.GetBool(MessagingKeys.OrganizerEnabled, false) ? S.OrgPrivacyNote : S.MsgLocalNote;
        return Ui.Group(null, CleanerUi.IconLine(CleanerGlyphs.Hand, Ui.Caption(note, 12)), Ui.Caption(S.MsgTrashNote, 12));
    }
}
