// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Cleaner;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Energy;
using Fieldkit.UI;

namespace Fieldkit.Features.Files.Duplicates;

/// <summary>Settings keys of the duplicate finder.</summary>
public static class DuplicateKeys
{
    public const string FeatureId = "duplicateFinder";
    public const string Folders = "duplicateFinder.folders";
    public const string MinSize = "duplicateFinder.minSize";
    public const string Keep = "duplicateFinder.keep";
}

/// <summary>The duplicate finder window (upstream #3022): pick folders, scan, review, move extra copies to the Recycle Bin.</summary>
public sealed class DuplicateFinderWindow : ToolWindow
{
    public static void Open() =>
        ShowSingle(DuplicateKeys.FeatureId, () => new DuplicateFinderWindow());

    public static void CloseInstance() => CloseSingle(DuplicateKeys.FeatureId);

    private readonly DuplicateFinderView _view;

    private DuplicateFinderWindow() : base("Duplicate files", 780, 700)
    {
        _view = new DuplicateFinderView(this);
        Content = _view.Root;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_view.IsScanning) Close();
        };
        Closed += (_, _) => _view.Cancel();
    }

    /// <summary>The window's content with sample results, for previews and tests.</summary>
    public static FrameworkElement BuildPreview(DuplicateScanResult? result)
    {
        var view = new DuplicateFinderView(null);
        if (result is not null) view.ShowResult(result);
        return view.Root;
    }
}

/// <summary>Everything inside the window; holds the scan state.</summary>
internal sealed class DuplicateFinderView
{
    private const int GroupsPerPage = 150;

    private readonly Window? _owner;
    private readonly StackPanel _folders = new();
    private readonly StackPanel _results = new();
    private readonly TextBlock _status = Ui.Caption(string.Empty);
    private readonly TextBlock _selection = Ui.Text(string.Empty, 13, FontWeights.Medium, wrap: false);
    private readonly Button _scan;
    private readonly Button _recycle;
    private readonly FrameworkElement _footer;
    private CancellationTokenSource? _cts;
    private List<DuplicateGroup> _groups = [];
    private HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private int _shown = GroupsPerPage;

    public FrameworkElement Root { get; }
    public bool IsScanning => _cts is not null;

    public DuplicateFinderView(Window? owner)
    {
        _owner = owner;
        _scan = Ui.Button("Scan", ToggleScan, accent: true, glyph: Glyphs.Search);
        _recycle = Ui.Button("Move to Recycle Bin", Recycle, accent: true, glyph: Glyphs.Delete);
        var header = Ui.Row(10, Ui.Icon(Glyphs.Copy, 20), Ui.Stack(2, Ui.Title("Duplicate files", 18),
            Ui.Caption("Finds files with the same contents. Extra copies go to the Recycle Bin, so you can restore them.")));

        RenderFolders();
        var add = Ui.Button("Add folder…", AddFolder, glyph: Glyphs.Add);
        var reset = Ui.Button("Use my folders", () =>
        {
            Settings.Set(DuplicateKeys.Folders, DuplicateLogic.DefaultFolders().ToList());
            RenderFolders();
        });
        var minSize = Ui.Choice(DuplicateKeys.MinSize,
            DuplicateLogic.MinSizeChoices.Select(v => (v.ToString(CultureInfo.InvariantCulture), v == 0 ? "Any size" : "Over " + ByteFormat.Format(v))).ToList(),
            DuplicateLogic.DefaultMinSize.ToString(CultureInfo.InvariantCulture));
        minSize.MinWidth = 130;
        var keep = Ui.Choice(DuplicateKeys.Keep,
            [(nameof(KeepRule.Oldest), "Keep the oldest copy"), (nameof(KeepRule.Newest), "Keep the newest copy"),
             (nameof(KeepRule.ShortestPath), "Keep the shortest path")],
            nameof(KeepRule.Oldest), _ =>
            {
                _selected = DuplicateLogic.DefaultSelection(_groups, Rule);
                RenderResults();
            });
        keep.MinWidth = 190;
        var options = Ui.Row(10, Ui.Text("Files", 13, wrap: false), minSize, keep);
        options.VerticalAlignment = VerticalAlignment.Center;
        var setup = Ui.Card(Ui.Stack(10,
            Ui.Text("Look in", 13.5, FontWeights.Medium),
            _folders,
            Ui.Row(8, add, reset),
            Ui.Divider(2),
            Ui.Split(options, _scan)));

        var resultsScroll = new ScrollViewer { Content = _results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var selectAll = Ui.Button("Select extra copies", () =>
        {
            _selected = DuplicateLogic.DefaultSelection(_groups, Rule);
            RenderResults();
        });
        var clear = Ui.Button("Clear", () =>
        {
            _selected.Clear();
            RenderResults();
        });
        _footer = Ui.Split(_selection, Ui.Row(8, selectAll, clear, _recycle));
        _footer.Visibility = Visibility.Collapsed;

        var grid = new Grid { Margin = new Thickness(18) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });
        Place(grid, header, 0, new Thickness(0, 0, 0, 14));
        Place(grid, setup, 1, new Thickness(0, 0, 0, 10));
        Place(grid, _status, 2, new Thickness(2, 0, 0, 8));
        Place(grid, resultsScroll, 3, new Thickness(0));
        Place(grid, _footer, 4, new Thickness(0, 12, 0, 0));
        Root = grid;
        _status.Text = "Choose where to look and press Scan. Hidden, system and online-only OneDrive files are skipped.";
    }

    private static void Place(Grid grid, FrameworkElement element, int row, Thickness margin)
    {
        element.Margin = margin;
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    private static KeepRule Rule =>
        Enum.TryParse<KeepRule>(Settings.GetString(DuplicateKeys.Keep, nameof(KeepRule.Oldest)), out var r) ? r : KeepRule.Oldest;

    private static long MinSize =>
        DuplicateLogic.SanitizedMinSize(long.TryParse(Settings.GetString(DuplicateKeys.MinSize, string.Empty), out var v) ? v : DuplicateLogic.DefaultMinSize);

    private static List<string> Folders =>
        Settings.Get<List<string>?>(DuplicateKeys.Folders, null) is { Count: > 0 } saved ? saved : DuplicateLogic.DefaultFolders().ToList();

    // ------------------------------------------------------------------ folders

    private void RenderFolders()
    {
        _folders.Children.Clear();
        var folders = Folders;
        foreach (var folder in folders)
        {
            var path = Ui.Text(folder, 12.5, wrap: false);
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            path.ToolTip = folder;
            var remove = Ui.IconButton(Glyphs.Close, "Remove", () =>
            {
                Settings.Set(DuplicateKeys.Folders, Folders.Where(f => !f.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList());
                RenderFolders();
            }, 10);
            remove.IsEnabled = folders.Count > 1;
            var row = Ui.Split(Ui.Row(8, Ui.Icon(Glyphs.Folder, 14, "TextSecondary"), path), remove);
            row.Margin = new Thickness(0, 0, 0, 2);
            _folders.Children.Add(row);
        }
    }

    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Add a folder to search", Multiselect = true };
        if (dialog.ShowDialog(_owner) != true) return;
        Settings.Set(DuplicateKeys.Folders, DuplicateLogic.Normalized(Folders.Concat(dialog.FolderNames)));
        RenderFolders();
    }

    // ------------------------------------------------------------------ scanning

    private void ToggleScan()
    {
        if (_cts is not null)
        {
            Cancel();
            return;
        }
        var cts = new CancellationTokenSource();
        _cts = cts;
        _scan.Content = Ui.Row(8, Ui.Icon(Glyphs.Stop, 14, "TextOnAccent"), Ui.Text("Stop", 13, wrap: false));
        _groups = [];
        _selected.Clear();
        _results.Children.Clear();
        _footer.Visibility = Visibility.Collapsed;
        _status.Text = "Looking for files…";
        var roots = Folders;
        var minSize = MinSize;
        var progress = new Progress<DuplicateProgress>(p =>
        {
            if (_cts != cts) return;
            _status.Text = p.ToHash > 0
                ? $"{p.Phase}… {p.Hashed:N0} of {p.ToHash:N0} ({p.FilesSeen:N0} files found)"
                : $"{p.Phase}… {p.FilesSeen:N0} files";
        });
        Task.Run(() => DuplicateScanner.Scan(roots, minSize, progress, cts.Token), cts.Token).ContinueWith(t => Ui.OnUi(() =>
        {
            if (_cts != cts) return;
            _cts = null;
            ResetScanButton();
            if (t.IsCanceled || t.Exception?.InnerException is OperationCanceledException)
            {
                _status.Text = "Scan stopped.";
                return;
            }
            if (t.Exception is { } ex)
            {
                Log.Error("Duplicate scan failed", ex);
                _status.Text = "The scan failed. See the log for details.";
                return;
            }
            ShowResult(t.Result);
        }), TaskScheduler.Default);
    }

    public void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
        ResetScanButton();
    }

    private void ResetScanButton() =>
        _scan.Content = Ui.Row(8, Ui.Icon(Glyphs.Search, 14, "TextOnAccent"), Ui.Text("Scan", 13, wrap: false));

    public void ShowResult(DuplicateScanResult result)
    {
        _groups = result.Groups.ToList();
        _selected = DuplicateLogic.DefaultSelection(_groups, Rule);
        _shown = GroupsPerPage;
        var wasted = _groups.Sum(g => g.Wasted);
        var notes = new List<string>();
        if (result.CloudSkipped > 0) notes.Add($"{result.CloudSkipped:N0} online-only files skipped");
        if (result.Unreadable > 0) notes.Add($"{result.Unreadable:N0} could not be read");
        var extra = notes.Count > 0 ? " · " + string.Join(" · ", notes) : string.Empty;
        _status.Text = _groups.Count == 0
            ? $"No duplicates among {result.FilesSeen:N0} files{extra}."
            : $"{_groups.Count:N0} {(_groups.Count == 1 ? "set" : "sets")} of duplicates among {result.FilesSeen:N0} files · {ByteFormat.Format(wasted)} can be freed{extra}";
        RenderResults();
    }

    // ------------------------------------------------------------------ results

    private void RenderResults()
    {
        _results.Children.Clear();
        _footer.Visibility = _groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var group in _groups.Take(_shown)) _results.Children.Add(GroupCard(group));
        if (_groups.Count > _shown)
        {
            var more = Ui.Button($"Show more ({_groups.Count - _shown:N0} left)", () =>
            {
                _shown += GroupsPerPage;
                RenderResults();
            });
            more.HorizontalAlignment = HorizontalAlignment.Center;
            more.Margin = new Thickness(0, 4, 0, 4);
            _results.Children.Add(more);
        }
        UpdateSelection();
    }

    private FrameworkElement GroupCard(DuplicateGroup group)
    {
        var name = Ui.Text(Path.GetFileName(group.Files[0].Path), 13.5, FontWeights.SemiBold, wrap: false);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var info = Ui.Caption($"{group.Files.Count} copies · {ByteFormat.Format(group.Size)} each", 12);
        var saves = Ui.Text("Saves " + ByteFormat.Format(group.Wasted), 12, brush: "Green", wrap: false);
        var rows = Ui.Stack(4, Ui.Split(Ui.Stack(1, name, info), saves));
        foreach (var file in group.Files) Ui.Append(rows, 4, FileRow(file));
        var card = Ui.Card(rows, new Thickness(14, 10, 10, 10));
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }

    private FrameworkElement FileRow(FileCandidate file)
    {
        var check = new CheckBox { IsChecked = _selected.Contains(file.Path), VerticalAlignment = VerticalAlignment.Center };
        check.Click += (_, _) => Ui.Guard(() =>
        {
            if (check.IsChecked == true) _selected.Add(file.Path);
            else _selected.Remove(file.Path);
            UpdateSelection();
        });
        var folder = Ui.Text(Path.GetDirectoryName(file.Path) ?? file.Path, 12.5, wrap: false);
        folder.TextTrimming = TextTrimming.CharacterEllipsis;
        folder.ToolTip = file.Path;
        var date = Ui.Text(file.ModifiedUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture), 12, brush: "TextTertiary", wrap: false);
        var reveal = Ui.IconButton(Glyphs.Folder, "Show in File Explorer", () => CleanerUi.Reveal(file.Path), 12);
        var left = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(check, Dock.Left);
        check.Margin = new Thickness(0, 0, 8, 0);
        left.Children.Add(check);
        left.Children.Add(folder);
        return Ui.Split(left, Ui.Row(6, date, reveal));
    }

    private void UpdateSelection()
    {
        var removable = DuplicateLogic.SafeToRemove(_groups, _selected, Rule);
        var bytes = DuplicateLogic.SelectedBytes(_groups, _selected, Rule);
        _selection.Text = removable.Count == 0
            ? "Nothing selected"
            : $"{removable.Count:N0} {(removable.Count == 1 ? "file" : "files")} selected · {ByteFormat.Format(bytes)}";
        _recycle.IsEnabled = removable.Count > 0;
    }

    private void Recycle()
    {
        var paths = DuplicateLogic.SafeToRemove(_groups, _selected, Rule);
        if (paths.Count == 0) return;
        var bytes = DuplicateLogic.SelectedBytes(_groups, _selected, Rule);
        if (!EnergyUi.Confirm($"Move {paths.Count:N0} {(paths.Count == 1 ? "file" : "files")} to the Recycle Bin?",
                $"Frees {ByteFormat.Format(bytes)}. One copy of every file stays where it is, and you can restore these from the Recycle Bin.",
                "Move to Recycle Bin", destructive: true, owner: _owner)) return;
        var result = RecycleBin.Send(paths);
        var gone = new HashSet<string>(result.Recycled, StringComparer.OrdinalIgnoreCase);
        _groups = _groups
            .Select(g => g with { Files = g.Files.Where(f => !gone.Contains(f.Path)).ToList() })
            .Where(g => g.Files.Count > 1)
            .ToList();
        _selected.ExceptWith(gone);
        _status.Text = result.Failed.Count == 0
            ? $"Moved {result.Recycled.Count:N0} files to the Recycle Bin, freeing {ByteFormat.Format(result.FreedBytes)}."
            : $"Moved {result.Recycled.Count:N0} files to the Recycle Bin. {result.Failed.Count:N0} could not be moved: {result.Failed[0].Error}";
        RenderResults();
    }
}
