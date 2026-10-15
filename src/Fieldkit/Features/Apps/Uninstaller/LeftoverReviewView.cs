// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fieldkit.Features.Apps.Common;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Uninstaller;

/// <summary>Whether the review comes before the app's uninstaller ran, or after (what it left behind).</summary>
public enum ReviewStage { BeforeUninstall, AfterUninstall }

/// <summary>What the review screen shows and does.</summary>
public sealed class ReviewModel
{
    public required string AppName { get; init; }
    public string Subtitle { get; init; } = string.Empty;
    public ImageSource? Icon { get; init; }
    public required IReadOnlyList<Leftover> Items { get; init; }
    public ReviewStage Stage { get; init; }
    public bool IsAdmin { get; init; }
    /// <summary>Whether the uninstaller row may be toggled (false when the app has no runnable uninstaller).</summary>
    public bool UninstallerAvailable { get; init; } = true;
    public string? Notice { get; init; }
    public Action OnPrimary { get; init; } = () => { };
    public Action OnCancel { get; init; } = () => { };
    public Action OnClose { get; init; } = () => { };
    public Action<string> OnReveal { get; init; } = UninstallerUi.Reveal;

    /// <summary>"Uninstall" while the app's uninstaller is selected, otherwise "Move to Recycle Bin".</summary>
    public string PrimaryText => Items.Any(i => i.Kind == LeftoverKind.Uninstaller && i.Include)
        ? UninstallerText.Uninstall
        : UninstallerText.Remove;
}

/// <summary>
/// The review screen (upstream resultsState): the app with the total found, leftovers grouped by
/// section with checkboxes, sizes and "Reveal in File Explorer", and a footer with the selection and
/// the destructive action.
/// </summary>
public static class LeftoverReviewView
{
    public static FrameworkElement Build(ReviewModel m)
    {
        // ---------------------------------------------------------------- header
        var icon = UninstallerUi.AppIcon(m.Icon, 44);
        var name = Ui.Text(m.AppName, 16, FontWeights.SemiBold, wrap: false);
        var subtitle = Ui.Text(m.Subtitle, 12, brush: "TextSecondary", wrap: false);
        var titles = Ui.Stack(2, name, subtitle);
        titles.Margin = new Thickness(12, 0, 12, 0);
        titles.VerticalAlignment = VerticalAlignment.Center;
        var total = Ui.Text(ByteFormat.Format(m.Items.Sum(i => i.Size)), 15, FontWeights.SemiBold, wrap: false);
        total.HorizontalAlignment = HorizontalAlignment.Right;
        var found = Ui.Text(UninstallerText.FoundTitle, 11, brush: "TextSecondary", wrap: false);
        found.HorizontalAlignment = HorizontalAlignment.Right;
        var totals = Ui.Stack(2, total, found);
        totals.VerticalAlignment = VerticalAlignment.Center;
        var close = Ui.IconButton(Glyphs.Close, UninstallerText.Cancel, m.OnClose, 13);
        close.Margin = new Thickness(10, 0, 0, 0);
        close.VerticalAlignment = VerticalAlignment.Center;
        var header = new Grid { Margin = new Thickness(18, 16, 14, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(icon);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        Grid.SetColumn(totals, 2);
        header.Children.Add(totals);
        Grid.SetColumn(close, 3);
        header.Children.Add(close);

        // ---------------------------------------------------------------- footer
        var selected = Ui.Text(string.Empty, 12.5, FontWeights.Medium, wrap: false);
        var selectedSize = Ui.Text(string.Empty, 11.5, brush: "TextSecondary", wrap: false);
        var primaryLabel = Ui.Text(string.Empty, 13, FontWeights.SemiBold, "TextOnAccent", wrap: false);
        var primary = UninstallerUi.DangerButton(m.PrimaryText, m.OnPrimary);
        primary.Content = Ui.Row(7, Ui.Icon(Glyphs.Delete, 13, "TextOnAccent"), primaryLabel);
        var cancel = UninstallerUi.PlainButton(m.Stage == ReviewStage.AfterUninstall ? UninstallerText.KeepLeftovers : UninstallerText.Cancel, m.OnCancel);
        void Refresh()
        {
            var chosen = m.Items.Where(i => i.Include).ToList();
            selected.Text = string.Format(UninstallerText.SelectedFormat, chosen.Count, m.Items.Count);
            selectedSize.Text = ByteFormat.Format(chosen.Sum(i => i.Size));
            primaryLabel.Text = m.PrimaryText;
            primary.IsEnabled = chosen.Count > 0;
        }
        var footerButtons = Ui.Row(8, cancel, primary);
        var footer = Ui.Split(Ui.Stack(1, selected, selectedSize), footerButtons);
        footer.Margin = new Thickness(18, 12, 16, 14);

        // ------------------------------------------------------------------ list
        var list = new StackPanel { Margin = new Thickness(14, 6, 14, 10) };
        if (!string.IsNullOrEmpty(m.Notice))
        {
            var notice = UninstallerUi.Notice(Glyphs.Warning, m.Notice);
            notice.Margin = new Thickness(4, 6, 4, 4);
            list.Children.Add(notice);
        }
        if (!m.IsAdmin && m.Items.Any(i => i.RequiresAdmin))
        {
            var admin = UninstallerUi.Notice(Glyphs.Shield, UninstallerText.AdminNote, "TextSecondary");
            admin.Margin = new Thickness(4, 6, 4, 4);
            list.Children.Add(admin);
        }
        foreach (var group in m.Items.GroupBy(i => i.Category).OrderBy(g => g.Key))
        {
            var label = Ui.SectionHeader(UninstallerText.Category(group.Key));
            label.FontSize = 11;
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
            label.Margin = new Thickness(6, 12, 0, 4);
            list.Children.Add(label);
            foreach (var item in group) list.Children.Add(Row(item, m, Refresh));
        }
        if (m.Items.Count == 0)
        {
            var none = Ui.Caption("Nothing else was found.");
            none.HorizontalAlignment = HorizontalAlignment.Center;
            none.Margin = new Thickness(0, 30, 0, 30);
            list.Children.Add(none);
        }
        var scroller = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Refresh();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(header);
        var d1 = Ui.Divider(0);
        Grid.SetRow(d1, 1);
        root.Children.Add(d1);
        Grid.SetRow(scroller, 2);
        root.Children.Add(scroller);
        var d2 = Ui.Divider(0);
        Grid.SetRow(d2, 3);
        root.Children.Add(d2);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);
        return root;
    }

    private static FrameworkElement Row(Leftover item, ReviewModel m, Action refresh)
    {
        var selectable = item.Kind == LeftoverKind.Uninstaller ? m.UninstallerAvailable : LeftoverRules.CanSelect(item, m.IsAdmin);
        var check = new CheckBox
        {
            IsChecked = item.Include && selectable,
            IsEnabled = selectable,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        if (!selectable) item.Include = false;
        check.Click += (_, _) =>
        {
            item.Include = check.IsChecked == true;
            refresh();
        };

        FrameworkElement icon = item.Kind == LeftoverKind.Uninstaller
            ? UninstallerUi.AppIcon(m.Icon, 22)
            : Ui.Icon(ItemGlyph(item), 17, item.Kind == LeftoverKind.Folder ? "Accent" : "TextSecondary");
        icon.Width = 22;
        icon.VerticalAlignment = VerticalAlignment.Center;

        var name = Ui.Text(item.Name, 12.5, wrap: false);
        var location = Ui.Text(item.Location, 10.5, brush: "TextTertiary", wrap: false);
        location.ToolTip = item.Path;
        var meta = new StackPanel { Orientation = Orientation.Horizontal };
        if (item.Confidence == LeftoverMatch.Related)
        {
            meta.Children.Add(Ui.Icon(UninstallerGlyphs.Question, 11, "Orange"));
            var optional = Ui.Text(UninstallerText.OptionalSection, 10.5, brush: "Orange", wrap: false);
            optional.Margin = new Thickness(4, 0, 8, 0);
            meta.Children.Add(optional);
        }
        if (item.RequiresAdmin)
        {
            var shield = Ui.Icon(Glyphs.Shield, 11, m.IsAdmin ? "TextTertiary" : "Orange");
            shield.ToolTip = UninstallerText.AdminRequired;
            shield.Margin = new Thickness(0, 0, 5, 0);
            meta.Children.Add(shield);
        }
        meta.Children.Add(location);
        var text = Ui.Stack(1, name, meta);
        text.Margin = new Thickness(10, 0, 8, 0);
        text.VerticalAlignment = VerticalAlignment.Center;

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (item.Kind is LeftoverKind.File or LeftoverKind.Folder || (item.Kind == LeftoverKind.Uninstaller && System.IO.Directory.Exists(item.Path)))
        {
            var reveal = Ui.IconButton(Glyphs.Folder, UninstallerText.Reveal, () => m.OnReveal(item.Path), 13);
            reveal.Margin = new Thickness(0, 0, 6, 0);
            right.Children.Add(reveal);
        }
        var size = Ui.Text(item.Kind == LeftoverKind.RegistryKey ? "Registry key" : ByteFormat.Format(item.Size), 11.5, brush: "TextSecondary", wrap: false);
        size.MinWidth = 64;
        size.TextAlignment = TextAlignment.Right;
        size.VerticalAlignment = VerticalAlignment.Center;
        right.Children.Add(size);

        var g = new Grid { Margin = new Thickness(6, 3, 4, 3), MinHeight = 34 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(check);
        Grid.SetColumn(icon, 1);
        g.Children.Add(icon);
        Grid.SetColumn(text, 2);
        g.Children.Add(text);
        Grid.SetColumn(right, 3);
        g.Children.Add(right);
        if (!selectable) g.Opacity = 0.6;
        return g;
    }

    private static string ItemGlyph(Leftover item) => item.Kind switch
    {
        LeftoverKind.RegistryKey => UninstallerGlyphs.Key,
        LeftoverKind.Folder => Glyphs.Folder,
        _ when item.Category == LeftoverCategory.Shortcuts => Glyphs.Link,
        _ => Glyphs.Document,
    };

    /// <summary>Renders the review screen with sample leftovers (for review without a running app).</summary>
    public static void RenderPreview(string path, bool dark)
    {
        var view = Build(SampleModel());
        PreviewRenderer.Render(view, path, 720, 640, dark);
    }

    public static ReviewModel SampleModel()
    {
        const string profile = @"C:\Users\alex";
        List<Leftover> items =
        [
            new() { Path = @"C:\Program Files\VideoLAN\VLC", Kind = LeftoverKind.Uninstaller, Category = LeftoverCategory.App, Size = 170_000_000, Include = true,
                    DisplayName = "VLC media player", Detail = "\"C:\\Program Files\\VideoLAN\\VLC\\uninstall.exe\"" },
            new() { Path = profile + @"\AppData\Roaming\vlc", Kind = LeftoverKind.Folder, Category = LeftoverCategory.Support, Size = 2_400_000, Include = true },
            new() { Path = profile + @"\AppData\Local\VideoLAN\VLC", Kind = LeftoverKind.Folder, Category = LeftoverCategory.Support, Size = 860_000, Include = true },
            new() { Path = @"C:\ProgramData\VideoLAN", Kind = LeftoverKind.Folder, Category = LeftoverCategory.Support, Size = 12_000, Confidence = LeftoverMatch.Related, RequiresAdmin = true },
            new() { Path = profile + @"\AppData\Local\CrashDumps\vlc.exe.10244.dmp", Kind = LeftoverKind.File, Category = LeftoverCategory.Logs, Size = 31_000_000, Include = true },
            new() { Path = @"HKEY_CURRENT_USER\Software\VideoLAN\VLC", Kind = LeftoverKind.RegistryKey, Category = LeftoverCategory.Preferences, Include = true },
            new() { Path = @"HKEY_LOCAL_MACHINE\SOFTWARE\VideoLAN\VLC", Kind = LeftoverKind.RegistryKey, Category = LeftoverCategory.Preferences, RequiresAdmin = true },
            new() { Path = profile + @"\Desktop\VLC media player.lnk", Kind = LeftoverKind.File, Category = LeftoverCategory.Shortcuts, Size = 1_200, Include = true },
            new() { Path = profile + @"\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\VLC", Kind = LeftoverKind.Folder, Category = LeftoverCategory.Shortcuts, Size = 3_400, Confidence = LeftoverMatch.Related },
        ];
        return new ReviewModel
        {
            AppName = "VLC media player",
            Subtitle = "VideoLAN · 3.0.21",
            Items = items,
            Stage = ReviewStage.BeforeUninstall,
            IsAdmin = false,
        };
    }
}
