// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fieldkit.Features.Apps.Common;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Uninstaller;

/// <summary>One installed app in the picker. Size can arrive later (measured install folder).</summary>
public sealed class AppRow(InstalledApp app, ImageSource? icon = null)
{
    private long _size = app.EstimatedSizeBytes;

    public InstalledApp App { get; } = app;
    public ImageSource? Icon { get; set; } = icon;
    public event Action? SizeChanged;

    public long Size
    {
        get => _size;
        set
        {
            if (_size == value) return;
            _size = value;
            SizeChanged?.Invoke();
        }
    }
}

/// <summary>
/// The app picker (upstream AppPickerView + the empty-state drop zone): drop zone, search, and the list
/// of installed apps with icon, publisher, version and size.
/// </summary>
public static class AppListView
{
    /// <param name="rows">Null while the list is loading.</param>
    /// <param name="notice">An optional warning shown above the list (e.g. a dropped file that isn't an app).</param>
    /// <param name="dropZone">Receives the drop zone so the window can highlight it while dragging.</param>
    public static FrameworkElement Build(IReadOnlyList<AppRow>? rows, string? notice, Action<InstalledApp> onSelect, Action onBrowse,
        Action<Border>? dropZone = null, string initialQuery = "", Action<string>? onQuery = null)
    {
        var title = Ui.Text(UninstallerText.PickerTitle, 18, FontWeights.SemiBold, wrap: false);
        var browse = UninstallerUi.PlainButton(UninstallerText.Browse, onBrowse, Glyphs.Folder);
        browse.ToolTip = "Choose a program or its shortcut";
        var header = Ui.Split(title, browse);

        var zone = UninstallerUi.DropZone(92, large: false);
        dropZone?.Invoke(zone);

        // Search box with an icon and a placeholder.
        var search = new TextBox { Text = initialQuery, Padding = new Thickness(30, 6, 8, 6), VerticalContentAlignment = VerticalAlignment.Center };
        var placeholder = Ui.Text(UninstallerText.PickerSearch, 13, brush: "TextTertiary", wrap: false);
        placeholder.IsHitTestVisible = false;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.Margin = new Thickness(31, 0, 0, 0);
        var searchIcon = Ui.Icon(Glyphs.Search, 13, "TextSecondary");
        searchIcon.HorizontalAlignment = HorizontalAlignment.Left;
        searchIcon.Margin = new Thickness(10, 0, 0, 0);
        searchIcon.IsHitTestVisible = false;
        var searchHost = new Grid();
        searchHost.Children.Add(search);
        searchHost.Children.Add(placeholder);
        searchHost.Children.Add(searchIcon);

        var list = new StackPanel();
        var scroller = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 2, 4, 2),
        };
        var count = Ui.Text(string.Empty, 11.5, brush: "TextTertiary", wrap: false);

        void Fill()
        {
            placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            list.Children.Clear();
            if (rows is null)
            {
                list.Children.Add(Centered(Ui.Stack(10, UninstallerUi.Spinner(26), Centered(Ui.Caption(UninstallerText.Scanning)))));
                count.Text = string.Empty;
                return;
            }
            var shown = rows.Where(r => UninstallRules.MatchesQuery(r.App, search.Text)).ToList();
            count.Text = shown.Count == 1 ? "1 app" : $"{shown.Count} apps";
            if (shown.Count == 0)
            {
                list.Children.Add(Centered(Ui.Stack(8, Ui.Icon(UninstallerGlyphs.AppDefault, 30, "TextSecondary"),
                    Centered(Ui.Caption(UninstallerText.PickerEmpty)))));
                return;
            }
            foreach (var row in shown) list.Children.Add(Row(row, onSelect));
        }
        search.TextChanged += (_, _) =>
        {
            Fill();
            onQuery?.Invoke(search.Text);
        };
        Fill();

        var note = Ui.Text(UninstallerText.EmptyNote, 11.5, brush: "TextTertiary", wrap: false);
        var footer = Ui.Split(note, count);

        var root = new Grid { Margin = new Thickness(20, 16, 20, 14) };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = h });
        Place(root, header, 0, new Thickness(0, 0, 0, 12));
        Place(root, zone, 1, new Thickness(0, 0, 0, 12));
        if (!string.IsNullOrEmpty(notice)) Place(root, UninstallerUi.Notice(Glyphs.Warning, notice), 2, new Thickness(0, 0, 0, 12));
        Place(root, searchHost, 3, new Thickness(0, 0, 0, 8));
        var listCard = Ui.Card(scroller, new Thickness(6, 6, 2, 6));
        Place(root, listCard, 4, new Thickness(0));
        Place(root, footer, 5, new Thickness(2, 10, 2, 0));
        return root;
    }

    private static FrameworkElement Row(AppRow row, Action<InstalledApp> onSelect)
    {
        var app = row.App;
        var icon = UninstallerUi.AppIcon(row.Icon, 28);
        var name = Ui.Text(app.Name, 13, FontWeights.Medium, wrap: false);
        var details = string.Join(" · ", new[] { app.Publisher, app.Version, app.IsStore ? "Microsoft Store" : null }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var sub = Ui.Text(details.Length == 0 ? app.InstallLocation : details, 11, brush: "TextSecondary", wrap: false);
        var text = Ui.Stack(1, name, sub);
        text.Margin = new Thickness(10, 0, 10, 0);
        text.VerticalAlignment = VerticalAlignment.Center;
        var size = Ui.Text(SizeText(row.Size), 11.5, brush: "TextSecondary", wrap: false);
        size.VerticalAlignment = VerticalAlignment.Center;
        Ui.Track(size, () =>
        {
            void Update() => Ui.OnUi(() => size.Text = SizeText(row.Size));
            row.SizeChanged += Update;
            return new ActionDisposable(() => row.SizeChanged -= Update);
        });
        var badges = Ui.Row(8, app.RequiresAdmin ? AdminBadge() : null, size, Ui.Icon(Glyphs.ChevronRight, 11, "TextTertiary"));
        badges.VerticalAlignment = VerticalAlignment.Center;

        var g = new Grid { Margin = new Thickness(8, 6, 8, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(icon);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        Grid.SetColumn(badges, 2);
        g.Children.Add(badges);
        var button = Ui.Clickable(g, () => onSelect(app));
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        return button;
    }

    private static FrameworkElement AdminBadge()
    {
        var icon = Ui.Icon(Glyphs.Shield, 11, "TextTertiary");
        icon.ToolTip = "Installed for all users: its uninstaller asks for administrator permission";
        return icon;
    }

    private static string SizeText(long bytes) => bytes > 0 ? ByteFormat.Format(bytes) : string.Empty;

    private static FrameworkElement Centered(FrameworkElement e)
    {
        e.HorizontalAlignment = HorizontalAlignment.Center;
        e.VerticalAlignment = VerticalAlignment.Center;
        e.Margin = new Thickness(0, 40, 0, 40);
        return e;
    }

    private static void Place(Grid grid, FrameworkElement e, int row, Thickness margin)
    {
        e.Margin = margin;
        Grid.SetRow(e, row);
        grid.Children.Add(e);
    }

    /// <summary>Renders the picker with sample apps (for review without a running app).</summary>
    public static void RenderPreview(string path, bool dark)
    {
        var rows = SampleApps().Select(a => new AppRow(a)).ToList();
        var view = Build(rows, null, _ => { }, () => { });
        PreviewRenderer.Render(view, path, 720, 640, dark);
    }

    public static List<InstalledApp> SampleApps() =>
    [
        new() { Id = "1", Name = "7-Zip 23.01 (x64)", Publisher = "Igor Pavlov", Version = "23.01", EstimatedSizeBytes = 5_600_000, Source = InstalledAppSource.MachineRegistry, InstallLocation = @"C:\Program Files\7-Zip" },
        new() { Id = "2", Name = "Discord", Publisher = "Discord Inc.", Version = "1.0.9164", EstimatedSizeBytes = 98_000_000, Source = InstalledAppSource.UserRegistry },
        new() { Id = "3", Name = "Mozilla Firefox (x64 en-US)", Publisher = "Mozilla", Version = "131.0", EstimatedSizeBytes = 254_000_000, Source = InstalledAppSource.MachineRegistry },
        new() { Id = "4", Name = "Spotify", Publisher = "Spotify AB", Version = "1.2.47", EstimatedSizeBytes = 410_000_000, Source = InstalledAppSource.UserRegistry },
        new() { Id = "5", Name = "Windows Terminal", Publisher = "Microsoft Corporation", Version = "1.21.2911.0", Source = InstalledAppSource.StorePackage, PackageFamilyName = "Microsoft.WindowsTerminal_8wekyb3d8bbwe" },
        new() { Id = "6", Name = "VLC media player", Publisher = "VideoLAN", Version = "3.0.21", EstimatedSizeBytes = 170_000_000, Source = InstalledAppSource.MachineRegistry },
        new() { Id = "7", Name = "Visual Studio Code", Publisher = "Microsoft Corporation", Version = "1.94.2", EstimatedSizeBytes = 380_000_000, Source = InstalledAppSource.UserRegistry },
    ];
}
