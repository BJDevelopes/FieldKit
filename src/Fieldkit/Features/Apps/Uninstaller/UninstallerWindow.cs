// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Apps.Uninstaller;

/// <summary>
/// The Uninstaller tool window: the app picker (drop zone, search, list), the review screens, the busy
/// states and the done screen. Dropping a program (.exe) or its shortcut (.lnk) anywhere selects its app.
/// </summary>
public sealed class UninstallerWindow : ToolWindow
{
    private readonly Grid _root = new();
    private readonly ContentControl _content = new() { Focusable = false };
    private readonly ContentControl _overlay = new() { Focusable = false, Visibility = Visibility.Collapsed };
    private Border? _dropZone;

    public UninstallerSession Session { get; } = new();

    /// <summary>Set when the feature is switched off: close even mid-removal (it stops between items).</summary>
    internal bool ForceClose { get; set; }

    public UninstallerWindow() : base(UninstallerText.Name, 720, 660)
    {
        MinWidth = 560;
        MinHeight = 480;
        AllowDrop = true;
        _root.Children.Add(_content);
        _root.Children.Add(_overlay);
        Content = _root;
        Session.Changed += Render;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => Highlight(false);
        Drop += OnDrop;
        Closing += (_, e) =>
        {
            if (Session.Phase == UninstallerPhase.Removing && !ForceClose)
            {
                // Like upstream, a removal in progress is left to finish.
                e.Cancel = true;
            }
        };
        Closed += (_, _) => Session.Dispose();
        Render();
        Session.LoadApps();
    }

    private void Render()
    {
        _overlay.Content = null;
        _overlay.Visibility = Visibility.Collapsed;
        _dropZone = null;
        var s = Session;
        _content.Content = s.Phase switch
        {
            UninstallerPhase.Pick => AppListView.Build(s.Apps, s.PickerNotice, s.Select, Browse, z => _dropZone = z,
                s.SearchQuery, q => s.SearchQuery = q),
            UninstallerPhase.Scanning => StatusViews.Busy(s.BusyMessage, s.Target?.Name, s.TargetIcon, s.BusyDetail, s.CancelScan),
            UninstallerPhase.Uninstalling => StatusViews.Busy(s.BusyMessage, s.Target?.Name, s.TargetIcon, s.BusyDetail,
                secondaryText: s.WaitingForHandOff ? "Stop waiting" : null,
                onSecondary: s.WaitingForHandOff ? s.UninstallerFinished : null),
            UninstallerPhase.Removing => StatusViews.Busy(s.BusyMessage, s.Target?.Name, s.TargetIcon),
            UninstallerPhase.Done => StatusViews.Done(s.Freed, s.Failed, s.Backups.Count > 0 ? RegistryBackupFolder : null, s.Reset),
            _ => LeftoverReviewView.Build(new ReviewModel
            {
                AppName = s.Target?.Name ?? string.Empty,
                Subtitle = Subtitle(s),
                Icon = s.TargetIcon,
                Items = s.Items,
                Stage = s.Stage,
                IsAdmin = s.IsAdmin,
                UninstallerAvailable = s.Plan?.CanRun == true,
                Notice = s.Notice,
                OnPrimary = ConfirmPrimary,
                OnCancel = () =>
                {
                    if (s.Stage == ReviewStage.AfterUninstall) s.KeepLeftovers();
                    else s.Reset();
                },
                OnClose = () =>
                {
                    if (s.Stage == ReviewStage.AfterUninstall) s.KeepLeftovers();
                    else s.Reset();
                },
            }),
        };
    }

    private static string RegistryBackupFolder => Path.Combine(Core.AppInfo.DataDirectory, "Registry backups");

    private static string Subtitle(UninstallerSession s)
    {
        if (s.Target is null) return string.Empty;
        if (s.Stage == ReviewStage.AfterUninstall) return "Uninstalled. Review what it left behind.";
        var parts = new[] { s.Target.Publisher, s.Target.Version, s.Target.IsStore ? "Microsoft Store" : null }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(" · ", parts);
    }

    /// <summary>Every destructive step asks first.</summary>
    private void ConfirmPrimary()
    {
        var s = Session;
        if (s.Target is null) return;
        var leftovers = s.Items.Where(i => i.Include && i.Kind != LeftoverKind.Uninstaller).ToList();
        var keys = leftovers.Count(i => i.Kind == LeftoverKind.RegistryKey);
        var keyNote = keys > 0 ? $" {keys} registry {(keys == 1 ? "key is" : "keys are")} exported to a .reg backup before being deleted." : string.Empty;
        if (s.PrimaryRunsUninstaller)
        {
            var admin = s.Plan?.Elevate == true && !s.IsAdmin ? " Windows will ask for administrator permission." : string.Empty;
            ShowConfirm($"Uninstall {s.Target.Name}?",
                $"{s.Target.Name}’s own uninstaller runs first.{admin} Afterwards you review what it left behind: nothing else is removed without your confirmation.",
                UninstallerText.Uninstall, s.Uninstall);
            return;
        }
        if (leftovers.Count == 0) return;
        ShowConfirm(leftovers.Count == 1 ? "Move 1 item to the Recycle Bin?" : $"Move {leftovers.Count} items to the Recycle Bin?",
            $"Files and folders go to the Recycle Bin, so you can restore them.{keyNote}",
            UninstallerText.Remove, s.RemoveSelected);
    }

    private void ShowConfirm(string title, string body, string action, Action onConfirm)
    {
        _overlay.Content = StatusViews.Confirm(title, body, action, () =>
        {
            HideConfirm();
            onConfirm();
        }, HideConfirm);
        _overlay.Visibility = Visibility.Visible;
    }

    private void HideConfirm()
    {
        _overlay.Content = null;
        _overlay.Visibility = Visibility.Collapsed;
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = UninstallerText.Choose,
            Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            DereferenceLinks = true,
        };
        if (dialog.ShowDialog(this) == true) Session.SelectFile(dialog.FileName);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = Session.Phase == UninstallerPhase.Pick && DroppedPath(e) is not null;
        e.Effects = ok ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
        Highlight(ok);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        Highlight(false);
        if (Session.Phase != UninstallerPhase.Pick) return;
        if (DroppedPath(e) is { } path) Session.SelectFile(path);
        e.Handled = true;
    }

    private void Highlight(bool on)
    {
        if (_dropZone is not null) UninstallerUi.SetDropTargeted(_dropZone, on);
    }

    /// <summary>The first dropped .exe or .lnk (upstream takes the first .app).</summary>
    private static string? DroppedPath(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;
        return files.FirstOrDefault(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase));
    }
}
