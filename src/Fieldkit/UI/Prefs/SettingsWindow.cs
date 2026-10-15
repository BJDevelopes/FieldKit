// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fieldkit.Core;

namespace Fieldkit.UI.Prefs;

/// <summary>
/// The Settings window: a searchable sidebar of pages grouped by category, and the selected
/// page on the right. Pages come from modules (<see cref="ModuleRegistry.SettingsPage"/>).
/// </summary>
public sealed class SettingsWindow : Window
{
    private const string LastPageKey = "settings.lastPage";
    private readonly StackPanel _sidebar = new();
    private readonly ContentControl _content = new();
    private readonly TextBox _search = new();
    private string? _selectedId;

    public SettingsWindow()
    {
        Title = $"{AppInfo.Name} Settings";
        Width = 1080;
        Height = 740;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowFit.ToDisplay(this);
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SetResourceReference(BackgroundProperty, "WindowBackground");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
            TrayIcon.RenderBrandIcon(false, 32).Handle, Int32Rect.Empty,
            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

        _search.ToolTip = "Search settings";
        _search.Margin = new Thickness(12, 12, 12, 8);
        _search.TextChanged += (_, _) => BuildSidebar();
        var searchHost = new Grid();
        searchHost.Children.Add(_search);
        var placeholder = Ui.Text("  Search settings", 13, brush: "TextTertiary", wrap: false);
        placeholder.IsHitTestVisible = false;
        placeholder.Margin = new Thickness(22, 12, 12, 8);
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        searchHost.Children.Add(placeholder);
        _search.TextChanged += (_, _) => placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var sidebarScroll = new ScrollViewer
        {
            Content = _sidebar,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 0, 8, 12),
        };
        var sidebarGrid = new Grid();
        sidebarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sidebarGrid.Children.Add(searchHost);
        Grid.SetRow(sidebarScroll, 1);
        sidebarGrid.Children.Add(sidebarScroll);
        var sidebar = new Border { Width = 260, Child = sidebarGrid };
        sidebar.SetResourceReference(Border.BackgroundProperty, "SidebarBackground");

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(sidebar);
        Grid.SetColumn(_content, 1);
        root.Children.Add(_content);
        Content = root;

        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        FeatureRuntime.AvailabilityChanged += OnAvailabilityChanged;
        Closed += (_, _) => FeatureRuntime.AvailabilityChanged -= OnAvailabilityChanged;

        BuildSidebar();
        Navigate(Settings.GetString(LastPageKey, "features"));
    }

    private void OnAvailabilityChanged()
    {
        BuildSidebar();
        // The hub rebuilds itself; other pages may have lost their feature.
        if (_selectedId is not null && !Pages().Any(p => p.Id == _selectedId)) Navigate("features");
    }

    public static string CategoryTitle(SettingsCategory c) => c switch
    {
        SettingsCategory.Essentials => "Essentials",
        SettingsCategory.WindowControls => "Window controls",
        SettingsCategory.Files => "Files",
        SettingsCategory.Sound => "Sound",
        SettingsCategory.Utilities => "Utilities",
        SettingsCategory.DynamicIsland => "Dynamic Island",
        SettingsCategory.Advanced => "Advanced",
        _ => c.ToString(),
    };

    private static IEnumerable<SettingsPageDef> Pages() =>
        FeatureRuntime.Registry.SettingsPages
            .Where(p => p.FeatureGate.Length == 0 || p.FeatureGate.Any(FeatureCatalog.IsAvailable))
            .OrderBy(p => p.Category).ThenBy(p => p.Order).ThenBy(p => p.Title, StringComparer.CurrentCulture);

    private void BuildSidebar()
    {
        _sidebar.Children.Clear();
        var query = _search.Text.Trim();
        var pages = Pages().Where(p => Matches(p, query)).ToList();
        foreach (var group in pages.GroupBy(p => p.Category))
        {
            var header = Ui.Text(CategoryTitle(group.Key), 12.5, FontWeights.SemiBold, "TextSecondary", wrap: false);
            header.Margin = new Thickness(10, _sidebar.Children.Count == 0 ? 4 : 16, 0, 4);
            _sidebar.Children.Add(header);
            foreach (var page in group)
            {
                var icon = Ui.Icon(page.Glyph, 15);
                icon.Width = 22;
                var item = new RadioButton
                {
                    GroupName = "settingsPages",
                    Content = Ui.Row(10, icon, Ui.Text(page.Title, 14, wrap: false)),
                    IsChecked = page.Id == _selectedId,
                    Margin = new Thickness(0, 1, 0, 1),
                };
                item.SetResourceReference(StyleProperty, "SidebarItem");
                // Selected items use white text; let the icon follow the item foreground.
                item.Checked += (_, _) => icon.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccent");
                item.Unchecked += (_, _) => icon.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                if (page.Id == _selectedId) icon.SetResourceReference(TextBlock.ForegroundProperty, "TextOnAccent");
                var id = page.Id;
                item.Click += (_, _) => Navigate(id);
                _sidebar.Children.Add(item);
            }
        }
        if (pages.Count == 0) _sidebar.Children.Add(Ui.Caption("No settings match."));
    }

    private static bool Matches(SettingsPageDef page, string query)
    {
        if (query.Length == 0) return true;
        bool Has(string s) => s.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        return Has(page.Title) || (page.Keywords?.Any(Has) ?? false) ||
               page.FeatureGate.Any(id => FeatureCatalog.TryGet(id, out var f) && (Has(f.Title) || Has(f.Description)));
    }

    /// <summary>The open page's full content (inside its scroll area), for the site export.</summary>
    internal FrameworkElement? PageContent
    {
        get
        {
            // Some pages (Features) wrap their scroll area in a host that rebuilds it; look through to the content.
            var content = _content.Content;
            while (true)
            {
                if (content is ScrollViewer scroll) content = scroll.Content;
                else if (content is ContentControl { Content: ScrollViewer inner }) content = inner;
                else return content as FrameworkElement;
            }
        }
    }

    public void Navigate(string pageId)
    {
        var page = Pages().FirstOrDefault(p => p.Id == pageId) ?? Pages().FirstOrDefault();
        if (page is null) return;
        _selectedId = page.Id;
        Settings.Set(LastPageKey, page.Id);
        foreach (var item in _sidebar.Children.OfType<RadioButton>()) item.IsChecked = false;
        BuildSidebar();
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _content.Content = page.Build();
            UpdateLayout();
            if (watch.ElapsedMilliseconds > 40) Log.Info($"Perf: settings page {page.Id} took {watch.ElapsedMilliseconds} ms to build");
        }
        catch (Exception ex)
        {
            Log.Error($"Building settings page {page.Id} failed", ex);
            _content.Content = Ui.Page(page.Title, $"This page failed to load: {ex.Message}");
        }
    }
}
