// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Cleaner;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Energy;
using Fieldkit.UI;

namespace Fieldkit.Features.Files.DiskMap;

public static class DiskMapKeys
{
    public const string FeatureId = "diskUsage";
    public const string LastTarget = "diskUsage.lastTarget";
    public const string Glyph = Glyphs.PieChart;
}

/// <summary>Disk usage as a sunburst (upstream #3071): what takes the space on a drive or in a folder.</summary>
public sealed class DiskMapWindow : ToolWindow
{
    public static void Open() => ShowSingle(DiskMapKeys.FeatureId, () => new DiskMapWindow());

    public static void CloseInstance() => CloseSingle(DiskMapKeys.FeatureId);

    private readonly DiskMapView _view;

    private DiskMapWindow() : base("Disk usage", 980, 700)
    {
        MinWidth = 760;
        MinHeight = 520;
        WindowFit.ToDisplay(this);
        _view = new DiskMapView(this);
        Content = _view.Root;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_view.IsScanning) Close();
            if (e.Key == Key.Back) _view.GoUp();
        };
        // The tree can be large: let it go as soon as the window closes.
        Closed += (_, _) => _view.Release();
    }

    /// <summary>The window's content showing <paramref name="result"/>, for previews and tests.</summary>
    public static FrameworkElement BuildPreview(DiskScanResult? result)
    {
        var view = new DiskMapView(null);
        if (result is not null) view.Show(result);
        return view.Root;
    }
}

internal sealed class DiskMapView
{
    private const double ChartSize = 480;
    private const double CenterRadius = 72;
    private const double OuterRadius = 236;

    /// <summary>One hue per first-ring branch; outer rings fade.</summary>
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0x30, 0xD1, 0x58), Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xBF, 0x5A, 0xF2),
        Color.FromRgb(0xFF, 0x37, 0x5F), Color.FromRgb(0x40, 0xC8, 0xE0), Color.FromRgb(0xFF, 0xD6, 0x0A), Color.FromRgb(0x5E, 0x5C, 0xE6),
    ];
    private static readonly Color Gray = Color.FromRgb(0x8E, 0x8E, 0x93);

    private readonly Window? _owner;
    private readonly ComboBox _target = new() { MinWidth = 260 };
    private readonly Button _scan;
    private readonly TextBlock _status = Ui.Caption(string.Empty);
    private readonly Canvas _chart = new() { Width = ChartSize, Height = ChartSize };
    private readonly WrapPanel _crumbs = new();
    private readonly StackPanel _list = new();
    private readonly FrameworkElement _body;
    private CancellationTokenSource? _cts;
    private DiskScanResult? _result;
    private DiskNode? _current;
    private string? _customFolder;

    public FrameworkElement Root { get; }
    public bool IsScanning => _cts is not null;

    public DiskMapView(Window? owner)
    {
        _owner = owner;
        _scan = Ui.Button("Scan", ToggleScan, accent: true, glyph: Glyphs.Search);
        FillTargets();
        var choose = Ui.Button("Choose folder…", ChooseFolder, glyph: Glyphs.Folder);
        var header = Ui.Row(10, Ui.Icon(DiskMapKeys.Glyph, 20), Ui.Stack(2, Ui.Title("Disk usage", 18),
            Ui.Caption("See what takes up space. Click a ring to go into a folder, and the middle to go back.")));
        var controls = Ui.Split(Ui.Row(8, _target, choose), _scan);

        var chartHost = new Viewbox { Child = _chart, Stretch = Stretch.Uniform, MaxWidth = ChartSize, MaxHeight = ChartSize };
        var listScroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var side = new DockPanel();
        DockPanel.SetDock(_crumbs, Dock.Top);
        _crumbs.Margin = new Thickness(0, 0, 0, 8);
        side.Children.Add(_crumbs);
        side.Children.Add(listScroll);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
        Grid.SetColumn(chartHost, 0);
        Grid.SetColumn(side, 2);
        body.Children.Add(chartHost);
        body.Children.Add(side);
        _body = body;
        _body.Visibility = Visibility.Collapsed;

        var grid = new Grid { Margin = new Thickness(18) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            grid.RowDefinitions.Add(new RowDefinition { Height = height });
        Place(grid, header, 0, new Thickness(0, 0, 0, 14));
        Place(grid, Ui.Card(controls, new Thickness(14, 10, 14, 10)), 1, new Thickness(0, 0, 0, 8));
        Place(grid, _status, 2, new Thickness(2, 0, 0, 10));
        Place(grid, _body, 3, new Thickness(0));
        Root = grid;
        _status.Text = "Pick a drive or a folder and press Scan. Folders Windows keeps private are counted only when Fieldkit runs as administrator.";
    }

    private static void Place(Grid grid, FrameworkElement element, int row, Thickness margin)
    {
        element.Margin = margin;
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    // ------------------------------------------------------------------ target

    private void FillTargets()
    {
        _target.Items.Clear();
        var last = Settings.GetString(DiskMapKeys.LastTarget, string.Empty);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                var label = string.IsNullOrEmpty(drive.VolumeLabel) ? drive.Name.TrimEnd('\\') : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                _target.Items.Add(new ComboBoxItem
                {
                    Content = $"{label} · {ByteFormat.Format(drive.TotalFreeSpace)} free of {ByteFormat.Format(drive.TotalSize)}",
                    Tag = drive.Name,
                });
            }
            catch { }
        }
        if (_customFolder is not null || (last.Length > 3 && Directory.Exists(last)))
        {
            _customFolder ??= last;
            _target.Items.Add(new ComboBoxItem { Content = _customFolder, Tag = _customFolder });
        }
        var items = _target.Items.OfType<ComboBoxItem>().ToList();
        _target.SelectedItem = items.FirstOrDefault(i => string.Equals((string)i.Tag, last, StringComparison.OrdinalIgnoreCase))
                               ?? items.FirstOrDefault(i => string.Equals((string)i.Tag, System.IO.Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
                               ?? items.FirstOrDefault();
    }

    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to measure" };
        if (dialog.ShowDialog(_owner) != true) return;
        _customFolder = dialog.FolderName;
        Settings.Set(DiskMapKeys.LastTarget, _customFolder);
        FillTargets();
    }

    // ------------------------------------------------------------------ scanning

    private void ToggleScan()
    {
        if (_cts is not null)
        {
            Cancel();
            _status.Text = "Scan stopped.";
            return;
        }
        if (_target.SelectedItem is not ComboBoxItem { Tag: string target }) return;
        Settings.Set(DiskMapKeys.LastTarget, target);
        ClearView();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _scan.Content = Ui.Row(8, Ui.Icon(Glyphs.Stop, 14, "TextOnAccent"), Ui.Text("Stop", 13, wrap: false));
        _status.Text = "Measuring…";
        var progress = new Progress<DiskScanProgress>(p =>
        {
            if (_cts == cts) _status.Text = $"Measuring… {p.Files:N0} files in {p.Folders:N0} folders";
        });
        Task.Run(() => DiskScanner.Scan(target, progress, cts.Token), cts.Token).ContinueWith(t => Ui.OnUi(() =>
        {
            if (_cts != cts) return;
            _cts = null;
            ResetScanButton();
            if (t.IsCanceled || t.Exception?.InnerException is OperationCanceledException) return;
            if (t.Exception is { } ex)
            {
                Log.Error("Disk scan failed", ex);
                _status.Text = "The scan failed. See the log for details.";
                return;
            }
            Show(t.Result);
        }), TaskScheduler.Default);
    }

    private void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
        ResetScanButton();
    }

    private void ResetScanButton() =>
        _scan.Content = Ui.Row(8, Ui.Icon(Glyphs.Search, 14, "TextOnAccent"), Ui.Text("Scan", 13, wrap: false));

    /// <summary>Stops a scan and lets the tree go (the window is closing).</summary>
    public void Release()
    {
        Cancel();
        ClearView();
    }

    private void ClearView()
    {
        _result = null;
        _current = null;
        _chart.Children.Clear();
        _list.Children.Clear();
        _crumbs.Children.Clear();
        _body.Visibility = Visibility.Collapsed;
    }

    public void Show(DiskScanResult result)
    {
        _result = result;
        _current = result.Root;
        var root = result.Root;
        var parts = new List<string> { $"{ByteFormat.Format(root.Size)} in {root.FileCount:N0} files" };
        if (result.DriveTotal > 0 && root.Name.Length <= 3)
        {
            var used = result.DriveTotal - result.DriveFree;
            parts.Add($"{ByteFormat.Format(used)} used of {ByteFormat.Format(result.DriveTotal)}");
            var hidden = used - root.Size;
            if (hidden > used / 50) parts.Add($"{ByteFormat.Format(hidden)} is in places that could not be read (system files, other users)");
        }
        else if (result.Unreadable > 0)
        {
            parts.Add($"{result.Unreadable:N0} folders could not be read");
        }
        _status.Text = string.Join(" · ", parts);
        _body.Visibility = Visibility.Visible;
        Render();
    }

    // ------------------------------------------------------------------ navigation

    public void GoUp()
    {
        if (_current?.Parent is { } parent) Navigate(parent);
    }

    private void Navigate(DiskNode node)
    {
        if (!node.IsFolder) return;
        _current = node;
        Render();
    }

    private void Render()
    {
        if (_current is null) return;
        RenderChart(_current);
        RenderCrumbs(_current);
        RenderList(_current);
    }

    // ------------------------------------------------------------------ chart

    private void RenderChart(DiskNode center)
    {
        _chart.Children.Clear();
        const double c = ChartSize / 2;
        var ringWidth = (OuterRadius - CenterRadius) / SunburstLayout.DefaultRings;
        foreach (var arc in SunburstLayout.Layout(center))
        {
            var inner = CenterRadius + arc.Ring * ringWidth + 1;
            var outer = inner + ringWidth - 2;
            var path = new System.Windows.Shapes.Path
            {
                Data = Sector(c, c, inner, outer, arc.Start, arc.Sweep),
                Fill = new SolidColorBrush(ColorOf(arc)),
                StrokeThickness = 1,
                Cursor = arc.Node.IsFolder ? Cursors.Hand : Cursors.Arrow,
                ToolTip = $"{arc.Node.Name}\n{ByteFormat.Format(arc.Node.Size)} · {SunburstLayout.Share(arc.Node.Size, center.Size)} of {Label(center)}",
            };
            path.SetResourceReference(Shape.StrokeProperty, "WindowBackground");
            path.Fill.Freeze();
            var node = arc.Node;
            path.MouseEnter += (_, _) => path.Opacity = 0.75;
            path.MouseLeave += (_, _) => path.Opacity = 1;
            path.MouseLeftButtonUp += (_, _) => Ui.Guard(() =>
            {
                if (node.IsFolder) Navigate(node);
                else if (node.Kind == DiskNodeKind.File) CleanerUi.Reveal(node.Path);
            });
            _chart.Children.Add(path);
        }

        var hub = new Ellipse { Width = CenterRadius * 2 - 6, Height = CenterRadius * 2 - 6, Cursor = center.Parent is null ? Cursors.Arrow : Cursors.Hand };
        hub.SetResourceReference(Shape.FillProperty, "ControlBackground");
        hub.ToolTip = center.Parent is null ? null : "Back to " + Label(center.Parent);
        Canvas.SetLeft(hub, c - hub.Width / 2);
        Canvas.SetTop(hub, c - hub.Height / 2);
        hub.MouseLeftButtonUp += (_, _) => Ui.Guard(GoUp);
        _chart.Children.Add(hub);

        var name = Ui.Text(Label(center), 13, FontWeights.SemiBold, wrap: false);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.MaxWidth = CenterRadius * 1.6;
        name.HorizontalAlignment = HorizontalAlignment.Center;
        var size = Ui.Text(ByteFormat.Format(center.Size), 16, FontWeights.SemiBold, wrap: false);
        size.HorizontalAlignment = HorizontalAlignment.Center;
        var label = Ui.Stack(0, size, name);
        if (center.Parent is not null)
        {
            var up = Ui.Icon("\uE74A", 11, "TextTertiary"); // Up
            up.HorizontalAlignment = HorizontalAlignment.Center;
            up.Margin = new Thickness(0, 3, 0, 0);
            label.Children.Add(up);
        }
        label.IsHitTestVisible = false;
        label.Width = CenterRadius * 1.7;
        label.Measure(new Size(label.Width, double.PositiveInfinity));
        Canvas.SetLeft(label, c - label.Width / 2);
        Canvas.SetTop(label, c - label.DesiredSize.Height / 2);
        _chart.Children.Add(label);
    }

    /// <summary>A scanned folder by its own name ("src"); a drive as "C:\".</summary>
    private static string Label(DiskNode node) =>
        node.Parent is null && node.Name.Length > 3 ? System.IO.Path.GetFileName(node.Name) : node.Name;

    private static Color ColorOf(SunburstArc arc)
    {
        var baseColor = arc.Node.Kind == DiskNodeKind.OtherFiles ? Gray : Palette[arc.Branch % Palette.Length];
        // Outer rings fade towards the background so the eye reads the inner rings first.
        var alpha = (byte)Math.Round(255 * Math.Max(0.38, 1.0 - arc.Ring * 0.2));
        return Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
    }

    /// <summary>A ring segment between two radii (degrees: 0 at the top, clockwise).</summary>
    private static Geometry Sector(double cx, double cy, double inner, double outer, double start, double sweep)
    {
        if (sweep >= 359.99)
        {
            var ring = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(cx, cy), outer, outer), new EllipseGeometry(new Point(cx, cy), inner, inner));
            ring.Freeze();
            return ring;
        }
        Point P(double r, double deg)
        {
            var (x, y) = SunburstLayout.PointAt(cx, cy, r, deg);
            return new Point(x, y);
        }
        var large = sweep > 180;
        var figure = new PathFigure { StartPoint = P(outer, start), IsClosed = true };
        figure.Segments.Add(new ArcSegment(P(outer, start + sweep), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(P(inner, start + sweep), true));
        figure.Segments.Add(new ArcSegment(P(inner, start), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    // ------------------------------------------------------------------ side list

    private void RenderCrumbs(DiskNode node)
    {
        _crumbs.Children.Clear();
        var chain = new List<DiskNode>();
        for (var n = node; n is not null; n = n.Parent) chain.Insert(0, n);
        for (var i = 0; i < chain.Count; i++)
        {
            var target = chain[i];
            var last = i == chain.Count - 1;
            var text = Ui.Text(target.Name.TrimEnd('\\'), 13, last ? FontWeights.SemiBold : FontWeights.Normal, last ? "TextPrimary" : "Accent", wrap: false);
            _crumbs.Children.Add(last ? text : Ui.Clickable(text, () => Navigate(target)));
            if (!last)
            {
                var sep = Ui.Icon("\uE76C", 9, "TextTertiary"); // ChevronRight
                sep.Margin = new Thickness(5, 0, 5, 0);
                sep.VerticalAlignment = VerticalAlignment.Center;
                _crumbs.Children.Add(sep);
            }
        }
    }

    private void RenderList(DiskNode node)
    {
        _list.Children.Clear();
        var arcs = SunburstLayout.Layout(node, rings: 1, minSweep: 0);
        var branchOf = arcs.ToDictionary(a => a.Node, a => a.Branch);
        foreach (var child in node.Largest().Take(200))
            _list.Children.Add(Row(node, child, branchOf.TryGetValue(child, out var b) ? b : 0));
        if (node.Children.Count == 0) _list.Children.Add(Ui.Caption("This folder is empty."));
    }

    private FrameworkElement Row(DiskNode parent, DiskNode child, int branch)
    {
        var glyph = child.Kind switch
        {
            DiskNodeKind.Folder => Glyphs.Folder,
            DiskNodeKind.OtherFiles => "\uE8B7",
            _ => Glyphs.Document,
        };
        var color = child.Kind == DiskNodeKind.OtherFiles ? Gray : Palette[branch % Palette.Length];
        var swatch = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(color), Margin = new Thickness(0, 2, 10, 2) };
        var name = Ui.Text(child.Name, 13, child.IsFolder ? FontWeights.Medium : FontWeights.Normal, wrap: false);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.ToolTip = child.Path;
        var detail = Ui.Text(child.IsFolder ? (child.FileCount == 1 ? "1 file" : $"{child.FileCount:N0} files")
            : child.Kind == DiskNodeKind.OtherFiles ? "Each under 1 MB" : "File",
            11.5, brush: "TextTertiary", wrap: false);
        var bar = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(color) };
        var track = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 4, 0, 0) };
        track.SetResourceReference(Border.BackgroundProperty, "ControlBackground");
        track.Child = bar;
        var share = parent.Size > 0 ? (double)child.Size / parent.Size : 0;
        track.SizeChanged += (_, _) => bar.Width = Math.Max(2, track.ActualWidth * share);
        var size = Ui.Text(ByteFormat.Format(child.Size), 12.5, FontWeights.Medium, wrap: false);
        var percent = Ui.Text(SunburstLayout.Share(child.Size, parent.Size), 11.5, brush: "TextTertiary", wrap: false);
        percent.HorizontalAlignment = HorizontalAlignment.Right;

        var actions = Ui.Row(0);
        if (child.Kind != DiskNodeKind.OtherFiles)
        {
            actions.Children.Add(Ui.IconButton(Glyphs.Folder, "Show in File Explorer", () => CleanerUi.Reveal(child.Path), 12));
            // Windows, Program Files, the profile root and the like are never offered (RecycleBin refuses them too).
            if (CanRecycle(child))
                actions.Children.Add(Ui.IconButton(Glyphs.Delete, "Move to Recycle Bin", () => Recycle(parent, child), 12));
        }
        else
        {
            actions.Children.Add(Ui.IconButton(Glyphs.Folder, "Open the folder", () => CleanerUi.OpenFolder(child.Path), 12));
        }

        var text = Ui.Stack(0, Ui.Split(name, size, 8), Ui.Split(detail, percent, 8), track);
        var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(swatch, Dock.Left);
        DockPanel.SetDock(actions, Dock.Right);
        actions.Margin = new Thickness(8, 0, 0, 0);
        actions.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(swatch);
        dock.Children.Add(actions);
        dock.Children.Add(text);
        if (!child.IsFolder) return dock;
        var clickable = Ui.Clickable(dock, () => Navigate(child), "Open " + child.Name);
        clickable.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        return clickable;
    }

    /// <summary>Not protected places, and not a folder sitting directly on a drive (Users, ProgramData, …).</summary>
    private static bool CanRecycle(DiskNode node)
    {
        if (ProtectedPaths.IsProtected(node.Path)) return false;
        var parent = System.IO.Path.GetDirectoryName(node.Path);
        return !(node.IsFolder && parent is not null && parent.Length <= 3);
    }

    private void Recycle(DiskNode parent, DiskNode child)
    {
        var what = child.IsFolder ? $"the folder “{child.Name}”" : $"“{child.Name}”";
        if (!EnergyUi.Confirm($"Move {what} to the Recycle Bin?",
                $"Frees about {ByteFormat.Format(child.Size)}{(child.IsFolder ? $" ({child.FileCount:N0} files)" : string.Empty)}. You can restore it from the Recycle Bin.",
                "Move to Recycle Bin", destructive: true, owner: _owner)) return;
        var result = RecycleBin.Send([child.Path], _ => child.Size);
        if (result.Recycled.Count == 0)
        {
            _status.Text = result.Failed.Count > 0 ? $"Could not move {child.Name}: {result.Failed[0].Error}" : $"{child.Name} was already gone.";
            if (result.Failed.Count > 0) return;
        }
        else
        {
            _status.Text = $"Moved {child.Name} to the Recycle Bin, freeing {ByteFormat.Format(child.Size)}.";
        }
        parent.Remove(child);
        Render();
    }
}
