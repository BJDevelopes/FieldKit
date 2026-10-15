// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.UI;
using Glyphs = Fieldkit.UI.Glyphs;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>The Clean URL tool (Utilities → Clean URL) and the Clean URL settings page (upstream PanelURLCleanerView / URLCleanerSettings).</summary>
public static class CleanUrlViews
{
    public const string Caption = "Removes tracking parameters from a link the moment it reaches the clipboard.";

    /// <summary>Input, Paste / Copy and the cleaned result, recomputed from the field and the current rules.</summary>
    public static FrameworkElement ManualCleaner(bool compact)
    {
        var input = new TextBox { Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
        var placeholder = Ui.Text("Paste a URL", compact ? 12 : 13, brush: "TextTertiary", wrap: false);
        placeholder.IsHitTestVisible = false;
        placeholder.Margin = new Thickness(9, 0, 0, 0);
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        var field = new Grid { Children = { input, placeholder } };
        var clear = Ui.IconButton(ClearGlyph, "Clear field", () => input.Text = string.Empty, 12);
        var inputRow = new DockPanel();
        DockPanel.SetDock(clear, Dock.Right);
        clear.Margin = new Thickness(6, 0, 0, 0);
        inputRow.Children.Add(clear);
        inputRow.Children.Add(field);

        string? copied = null;
        var output = Ui.Text(string.Empty, compact ? 11 : 12, wrap: true);
        output.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        output.TextTrimming = TextTrimming.CharacterEllipsis;
        output.MaxHeight = (compact ? 15 : 17) * 3;
        var message = Ui.Text(string.Empty, compact ? 11 : 12, brush: "TextSecondary");
        var copy = Ui.Button("Copy", () => { }, accent: true);
        var paste = Ui.Button("Paste", () =>
        {
            UrlCleanerService.ReadClipboardTextAsync().ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion) Ui.OnUi(() => input.Text = t.Result);
            }, TaskScheduler.Default);
        });

        void Update()
        {
            placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            clear.IsEnabled = input.Text.Length > 0;
            var result = UrlCleaning.Clean(input.Text, UrlCleanerService.CurrentRules);
            copy.IsEnabled = result is not null;
            if (result is not null)
            {
                output.Visibility = Visibility.Visible;
                output.Text = result.Url;
                message.Text = copied == result.Url ? "Copied." : UrlCleaning.Message(UrlCleaning.OutcomeFor(result, input.Text));
                message.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            }
            else
            {
                output.Visibility = Visibility.Collapsed;
                message.Text = input.Text.Length == 0 ? "The clean URL appears here" : "Paste a valid URL.";
                message.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
            }
        }
        copy.Click += (_, _) =>
        {
            if (UrlCleaning.Clean(input.Text, UrlCleanerService.CurrentRules) is not { } result) return;
            UrlCleanerService.CopyAsync(result.Url).ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result)
                    Ui.OnUi(() =>
                    {
                        copied = result.Url;
                        Update();
                    });
            }, TaskScheduler.Default);
        };
        input.TextChanged += (_, _) =>
        {
            copied = null;
            Update();
        };
        Update();

        var buttons = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(copy, Dock.Right);
        buttons.Children.Add(paste);
        buttons.Children.Add(copy);
        if (compact) paste.Padding = copy.Padding = new Thickness(10, 3, 10, 4);
        var stack = Ui.Stack(8, inputRow, buttons, Ui.Stack(5, output, message));
        Ui.Track(stack, () => Settings.ObservePrefix("urlCleaner.", Update));
        return stack;
    }

    private static FrameworkElement AutoCleanStatus()
    {
        var active = Ui.Row(5, Ui.Icon(Glyphs.Check, 11, "Green"), Ui.Text("Active now", 11.5, brush: "Green", wrap: false));
        var removed = Ui.Caption(string.Empty, 11.5);
        var stack = Ui.Stack(3, active, removed);
        void Refresh()
        {
            var running = FeatureRuntime.Service<UrlCleanerService>()?.IsRunning == true;
            stack.Visibility = running && Settings.GetBool(UrlCleanerService.EnabledKey) ? Visibility.Visible : Visibility.Collapsed;
            removed.Text = UrlCleanerService.LastCleaned is { } last ? $"Removed {string.Join(", ", last.Removed)}" : string.Empty;
            removed.Visibility = removed.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        Refresh();
        Ui.Track(stack, () => Settings.Observe(() => stack.Dispatcher.BeginInvoke(Refresh), UrlCleanerService.EnabledKey));
        Ui.Track(stack, () =>
        {
            UrlCleanerService.Cleaned += Refresh;
            return new ActionDisposable(() => UrlCleanerService.Cleaned -= Refresh);
        });
        return stack;
    }

    // ------------------------------------------------------------- the tool

    /// <summary>Opens the Clean URL tool window.</summary>
    public static void OpenTool()
    {
        var window = new FloatingWindow { Width = 380, SizeToContent = SizeToContent.Height, Title = "Clean URL" };
        var header = new DockPanel();
        var close = Ui.IconButton(Glyphs.Close, "Close", window.Dismiss, 11);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(Ui.Row(7, Ui.Icon(Glyphs.Link, 13, "Accent"), Ui.Text("Clean URL", 13, FontWeights.SemiBold, wrap: false)));

        var toggle = Ui.Check("Clean URLs as you copy them", UrlCleanerService.EnabledKey, false);
        var autoCard = Ui.Card(Ui.Stack(6, toggle, Ui.Caption(Caption, 11.5), AutoCleanStatus()), new Thickness(12, 10, 12, 10));
        var manualTitle = Ui.Text("CLEAN NOW", 10, FontWeights.SemiBold, "TextTertiary", wrap: false);
        var manualCard = Ui.Card(Ui.Stack(8, manualTitle, ManualCleaner(compact: true)), new Thickness(12, 10, 12, 12));
        var body = Ui.Stack(10, header, autoCard, manualCard);
        body.Margin = new Thickness(14, 12, 14, 14);
        window.Body = body;
        window.Closed += (_, _) => { };
        window.HideOnDeactivate = true;
        window.IsVisibleChanged += (_, _) =>
        {
            if (!window.IsVisible) window.Close();
        };
        window.Opacity = 0;
        window.Show();
        window.UpdateLayout();
        window.CenterOnPointerScreen(0.38);
        window.Opacity = 1;
        window.Activate();
    }

    // -------------------------------------------------------- settings page

    public static FrameworkElement BuildPage()
    {
        var status = AutoCleanStatus();
        var main = Ui.Group(null,
            Ui.ToggleRow("Clean URLs as you copy them", Caption, UrlCleanerService.EnabledKey, false),
            Ui.Stack(4, Ui.Text("Local. No network.", 11.5, brush: "TextTertiary"), status));

        var rulesHost = new StackPanel();
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        void RebuildRules()
        {
            rulesHost.Children.Clear();
            var rules = UrlCleanerService.CurrentRules;
            var first = true;
            foreach (var group in UrlCleaning.RuleGroupsFor(rules))
            {
                if (!first) rulesHost.Children.Add(Ui.Divider(8));
                first = false;
                rulesHost.Children.Add(RuleGroupView(group, expanded, RebuildRules));
            }
            rulesHost.Children.Add(Ui.Divider(8));
            rulesHost.Children.Add(AddSiteRow(expanded, RebuildRules));
        }
        RebuildRules();
        var rulesCard = Ui.Group("Cleaning rules",
            rulesHost,
            Ui.Stack(4,
                Ui.Caption("A site appends these parameters to its own share links to track where the link came from. When on, the name is removed when a link is cleaned; when off, it stays. Names you add can be deleted."),
                Ui.Caption("The list covers a site's different share paths (the page, the app, a live room), which is why it is long; a real link usually carries only two to four of them.")));

        var manual = Ui.Group("Clean now", ManualCleaner(compact: false));
        return Ui.Page("Clean URL", "Copied links lose their tracking junk.", main, rulesCard, manual);
    }

    private static FrameworkElement RuleGroupView(UrlCleaning.RuleGroup group, HashSet<string> expanded, Action rebuild)
    {
        var title = group.Site == UrlCleaning.AllSites ? "All sites" : group.Site;
        var isOpen = expanded.Contains(group.Site);
        var chevron = Ui.Icon(isOpen ? Glyphs.ChevronDown : Glyphs.ChevronRight, 10, "TextSecondary");
        var count = Ui.Text(group.EnabledCount == 1 ? "1 parameter" : $"{group.EnabledCount} parameters", 12.5, brush: "TextSecondary", wrap: false);
        var isOff = group.EnabledCount == 0;
        var siteSwitch = Ui.IconButton(isOff ? Glyphs.Add : Glyphs.Remove,
            isOff ? "Turn on every rule for this site" : "Turn off every rule for this site",
            () =>
            {
                var disabled = UrlCleaning.Tokens(Settings.GetString(UrlCleanerService.DisabledParametersKey));
                if (isOff) disabled.Remove(group.Site);
                else disabled[group.Site] = group.Entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
                Settings.Set(UrlCleanerService.DisabledParametersKey, UrlCleaning.StorageValue(disabled));
                rebuild();
            }, 11);
        var header = Ui.Split(Ui.Row(8, chevron, Ui.Text(title, 13.5, FontWeights.Medium, wrap: false)), Ui.Row(8, count, siteSwitch));
        header.Background = Brushes.Transparent;
        header.Cursor = System.Windows.Input.Cursors.Hand;
        header.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside<ButtonBase>(d)) return;
            if (!expanded.Remove(group.Site)) expanded.Add(group.Site);
            rebuild();
        };
        if (!isOpen) return header;

        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(18, 6, 0, 4) };
        foreach (var entry in group.Entries)
        {
            var check = new CheckBox { Content = Ui.Text(entry.Name, 12.5, wrap: false), IsChecked = entry.IsEnabled, Margin = new Thickness(0, 2, 8, 2) };
            check.Click += (_, _) =>
            {
                var disabled = UrlCleaning.Tokens(Settings.GetString(UrlCleanerService.DisabledParametersKey));
                if (check.IsChecked == true)
                {
                    if (disabled.TryGetValue(group.Site, out var set)) set.Remove(entry.Name);
                }
                else
                {
                    if (!disabled.TryGetValue(group.Site, out var set)) disabled[group.Site] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(entry.Name);
                }
                Settings.Set(UrlCleanerService.DisabledParametersKey, UrlCleaning.StorageValue(disabled.Where(p => p.Value.Count > 0).ToDictionary(p => p.Key, p => p.Value)));
                rebuild();
            };
            FrameworkElement cell = check;
            if (!entry.IsBuiltIn)
            {
                var remove = Ui.IconButton(Glyphs.Remove, "Delete name", () => RemoveName(group.Site, entry.Name, rebuild), 10);
                cell = Ui.Row(2, check, remove);
            }
            grid.Children.Add(cell);
        }
        var nameBox = new TextBox { MinWidth = 200, Padding = new Thickness(6, 3, 6, 3) };
        var add = Ui.Button("Add", () => AddName(group.Site, nameBox.Text, rebuild));
        add.IsEnabled = false;
        nameBox.TextChanged += (_, _) => add.IsEnabled = UrlCleaning.ParameterName(nameBox.Text) is not null;
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) AddName(group.Site, nameBox.Text, rebuild);
        };
        var addRow = Ui.Stack(4,
            Ui.Row(8, Labeled(nameBox, "Parameter name"), add),
            Ui.Text("Write the name left of the = , like utm_source. A matching name takes that parameter out of the link and leaves the rest as it is.", 11, brush: "TextTertiary"));
        addRow.Margin = new Thickness(18, 4, 0, 4);
        return Ui.Stack(0, header, grid, addRow);
    }

    private static FrameworkElement AddSiteRow(HashSet<string> expanded, Action rebuild)
    {
        var open = expanded.Contains("\0addSite");
        var chevron = Ui.Icon(open ? Glyphs.ChevronDown : Glyphs.ChevronRight, 10, "TextSecondary");
        var header = Ui.Split(Ui.Row(8, chevron, Ui.Text("Add site", 13.5, FontWeights.Medium, wrap: false)), null);
        header.Background = Brushes.Transparent;
        header.Cursor = System.Windows.Input.Cursors.Hand;
        header.MouseLeftButtonUp += (_, _) =>
        {
            if (!expanded.Remove("\0addSite")) expanded.Add("\0addSite");
            rebuild();
        };
        if (!open) return header;
        var site = new TextBox { MinWidth = 180, Padding = new Thickness(6, 3, 6, 3) };
        var name = new TextBox { MinWidth = 160, Padding = new Thickness(6, 3, 6, 3) };
        var add = Ui.Button("Add", () => { });
        void Validate() => add.IsEnabled = UrlCleaning.SiteKey(site.Text) is not null && UrlCleaning.ParameterName(name.Text) is not null;
        void Add()
        {
            if (UrlCleaning.SiteKey(site.Text) is not { } key || UrlCleaning.ParameterName(name.Text) is not { } parameter) return;
            var added = UrlCleaning.Tokens(Settings.GetString(UrlCleanerService.SiteParametersKey));
            if (!added.TryGetValue(key, out var set)) added[key] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(parameter);
            Settings.Set(UrlCleanerService.SiteParametersKey, UrlCleaning.StorageValue(added));
            expanded.Add(key);
            rebuild();
        }
        add.Click += (_, _) => Add();
        site.TextChanged += (_, _) => Validate();
        name.TextChanged += (_, _) => Validate();
        Validate();
        var row = Ui.Row(8, Labeled(site, "example.com"), Labeled(name, "Parameter name"), add);
        row.Margin = new Thickness(18, 6, 0, 0);
        return Ui.Stack(0, header, row);
    }

    private static void AddName(string site, string text, Action rebuild)
    {
        if (UrlCleaning.ParameterName(text) is not { } name) return;
        var disabled = UrlCleaning.Tokens(Settings.GetString(UrlCleanerService.DisabledParametersKey));
        if (disabled.TryGetValue(site, out var off)) off.Remove(name);
        Settings.Set(UrlCleanerService.DisabledParametersKey, UrlCleaning.StorageValue(disabled.Where(p => p.Value.Count > 0).ToDictionary(p => p.Key, p => p.Value)));
        if (site == UrlCleaning.AllSites)
        {
            var names = UrlCleaning.CustomParameters(Settings.GetString(UrlCleanerService.CustomParametersKey));
            names.Add(name);
            Settings.Set(UrlCleanerService.CustomParametersKey, UrlCleaning.StorageValue(names));
        }
        else
        {
            var added = UrlCleaning.Tokens(Settings.GetString(UrlCleanerService.SiteParametersKey));
            if (!added.TryGetValue(site, out var set)) added[site] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(name);
            Settings.Set(UrlCleanerService.SiteParametersKey, UrlCleaning.StorageValue(added));
        }
        rebuild();
    }

    private static void RemoveName(string site, string name, Action rebuild)
    {
        var disabled = UrlCleaning.Tokens(Settings.GetString(UrlCleanerService.DisabledParametersKey));
        if (disabled.TryGetValue(site, out var off)) off.Remove(name);
        Settings.Set(UrlCleanerService.DisabledParametersKey, UrlCleaning.StorageValue(disabled.Where(p => p.Value.Count > 0).ToDictionary(p => p.Key, p => p.Value)));
        if (site == UrlCleaning.AllSites)
        {
            var names = UrlCleaning.CustomParameters(Settings.GetString(UrlCleanerService.CustomParametersKey));
            names.Remove(name);
            Settings.Set(UrlCleanerService.CustomParametersKey, UrlCleaning.StorageValue(names));
        }
        else
        {
            var added = UrlCleaning.Tokens(Settings.GetString(UrlCleanerService.SiteParametersKey));
            if (added.TryGetValue(site, out var set)) set.Remove(name);
            Settings.Set(UrlCleanerService.SiteParametersKey, UrlCleaning.StorageValue(added.Where(p => p.Value.Count > 0).ToDictionary(p => p.Key, p => p.Value)));
        }
        rebuild();
    }

    /// <summary>A text box with a grey placeholder while empty.</summary>
    internal static FrameworkElement Labeled(TextBox box, string placeholder)
    {
        var hint = Ui.Text(placeholder, 12.5, brush: "TextTertiary", wrap: false);
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(8, 0, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        void Sync() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Sync();
        Sync();
        return new Grid { Children = { box, hint } };
    }

    private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        for (DependencyObject? current = d; current is not null;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is T) return true;
        }
        return false;
    }

    // "Clear field": a circled x (verified with --render-glyph-range).
    private const string ClearGlyph = "\uEA39";
}
