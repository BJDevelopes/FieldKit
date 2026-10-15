// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Shell;

/// <summary>One-click starting points (mirrors upstream FeaturePreset).</summary>
public sealed record FeatureBundle(string Id, string Name, string Description, string Glyph, string[] Features, string[] EnableKeys)
{
    public static readonly FeatureBundle[] All =
    [
        new("essential", "Essentials", "Volume mixer, system monitor, keep awake and the Dynamic Island in the taskbar.", Glyphs.Star,
            ["mixer", "keepAwake", "monitorCPU", "monitorGPU", "monitorMemory", "monitorNetwork", "monitorDisk", "monitorPower", "notch"], []),
        new("windows", "Windows", "App switcher, window layout and the taskbar features.", Glyphs.Switcher,
            ["switcher", "windowLayout", "dockPreview", "dockClick", "windowMaximizer"],
            ["switcher.enabled", "dockPreview.enabled", "dockClick.minimize", "windowMaximizer.enabled"]),
        new("battery", "Battery and quiet", "A lean monitor with battery, memory and processor. Nothing listens to input.", Glyphs.Battery,
            ["monitorCPU", "monitorMemory", "monitorPower"], []),
    ];

    /// <summary>Installs this bundle's features and uninstalls the rest, one at a time with progress on the Features page.</summary>
    public void Apply() => FeatureInstaller.Run(FeatureCatalog.All.Select(f => (f.Id, Features.Contains(f.Id))), EnableKeys);
}

/// <summary>Settings → Features: install only what you use, plus the permissions portal.</summary>
public static class FeatureHubPage
{
    private const string TabKey = "settings.features.tab";

    public static FrameworkElement Build()
    {
        var host = new ContentControl();
        var pending = false;
        InstallProgress? progress = null;
        void Render() => host.Content = BuildContent(Render, out progress);
        // Install/Uninstall clicks change availability; rebuild after the click has drawn, and only once
        // for a burst of changes. While the installer works through a bundle the page only shows its progress
        // (rebuilding 80 rows after every feature would be slow); it is rebuilt once at the end.
        void Schedule()
        {
            if (pending) return;
            pending = true;
            host.Dispatcher.BeginInvoke(() =>
            {
                pending = false;
                if (!FeatureInstaller.Busy) Render();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        Render();
        Ui.Track(host, () => Settings.ObservePrefix("feature.", Schedule));
        Ui.Track(host, () =>
        {
            void OnInstaller()
            {
                progress?.Update();
                if (!FeatureInstaller.Busy) Schedule();
            }
            FeatureInstaller.Changed += OnInstaller;
            OnInstaller();
            return new Unsubscribe(() => FeatureInstaller.Changed -= OnInstaller);
        });
        return host;
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <summary>"Installing Screenshot… 12 of 83" with a bar, shown while the installer works.</summary>
    private sealed class InstallProgress : Border
    {
        private readonly TextBlock _text = Ui.Text(string.Empty, 13, brush: "TextSecondary");
        private readonly ProgressBar _bar = new() { Minimum = 0 };
        private readonly List<UIElement> _locked = [];

        public InstallProgress()
        {
            Child = Ui.Stack(6, _text, _bar);
            Update();
        }

        /// <summary>Controls that stay off while the installer works (the page is rebuilt, and they are right again, at the end).</summary>
        public void Lock(params UIElement[] controls) => _locked.AddRange(controls);

        public void Update()
        {
            Visibility = FeatureInstaller.Busy ? Visibility.Visible : Visibility.Collapsed;
            if (FeatureInstaller.Busy) foreach (var control in _locked) control.IsEnabled = false;
            _bar.Maximum = Math.Max(1, FeatureInstaller.Total);
            _bar.Value = FeatureInstaller.Done;
            _text.Text = $"{FeatureInstaller.Status}…  {FeatureInstaller.Done} of {FeatureInstaller.Total}";
        }
    }

    private static FrameworkElement BuildContent(Action rerender, out InstallProgress progress)
    {
        progress = new InstallProgress();
        var tab = Settings.GetString(TabKey, "features");
        var installed = FeatureCatalog.All.Count(f => f.IsAvailable);

        var segFeatures = Segment("Features", tab == "features", () => { Settings.Set(TabKey, "features"); rerender(); });
        var segPermissions = Segment("Permissions", tab == "permissions", () => { Settings.Set(TabKey, "permissions"); rerender(); });
        var segments = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = Ui.Row(2, segFeatures, segPermissions),
        };
        segments.SetResourceReference(Border.BackgroundProperty, "ControlBackground");

        var headerContent = Ui.Stack(12, segments);
        if (tab == "features")
        {
            Ui.Append(headerContent, 12, Ui.Caption("Install only what you use. Whatever you uninstall disappears from the whole app and stops loading.", 13.5));
            var installAll = Ui.Button("Install all", () => FeatureInstaller.Run(FeatureCatalog.All.Select(f => (f.Id, true))));
            var uninstallAll = Ui.Button("Uninstall all", () => FeatureInstaller.Run(FeatureCatalog.All.Select(f => (f.Id, false))));
            installAll.IsEnabled = installed < FeatureCatalog.All.Count && !FeatureInstaller.Busy;
            uninstallAll.IsEnabled = installed > 0 && !FeatureInstaller.Busy;
            Ui.Append(headerContent, 12, Ui.Split(
                Ui.Text($"{installed} of {FeatureCatalog.All.Count} features installed", 13, brush: "TextTertiary"),
                Ui.Row(8, installAll, uninstallAll)));
            Ui.Append(headerContent, 12, progress);
            progress.Lock(installAll, uninstallAll);
        }
        else
        {
            Ui.Append(headerContent, 12, Ui.Caption("What each permission does and which features use it. Other access is requested only when you use it.", 13.5));
        }
        var header = Ui.Card(headerContent, new Thickness(16));

        return tab == "features"
            ? Ui.Page("Features", null, [header, Bundles(), .. Groups(), Ui.Caption(
                "Uninstalling deletes nothing: the feature just leaves the app and stops loading. Install it again anytime and everything returns as it was.")])
            : Ui.Page("Features", null, [header, .. PermissionRows()]);
    }

    private static RadioButton Segment(string text, bool selected, Action onSelect)
    {
        var rb = new RadioButton { Content = text, IsChecked = selected, GroupName = "hubSegments" };
        rb.SetResourceReference(FrameworkElement.StyleProperty, "SegmentButton");
        rb.Checked += (_, _) => { if (!selected) onSelect(); };
        return rb;
    }

    private static FrameworkElement Bundles()
    {
        var cards = FeatureBundle.All.Select(b =>
        {
            var apply = Ui.Button("Apply", () =>
            {
                var answer = MessageBox.Show(
                    $"Install the {b.Name} bundle and uninstall the rest? Nothing is deleted, and everything comes back with one click.",
                    "Apply bundle", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.OK) b.Apply();
            });
            apply.HorizontalAlignment = HorizontalAlignment.Left;
            var content = Ui.Stack(8,
                Ui.Row(8, Ui.Icon(b.Glyph, 16), Ui.Title(b.Name)),
                Ui.Caption(b.Description, 13),
                apply);
            return (UIElement)Ui.Card(content, new Thickness(14));
        }).ToArray();
        // Three across; one per line when the page is narrow (a small display, or the website on a phone).
        var grid = Ui.Columns(cards.Length, 12, cards);
        grid.SizeChanged += (_, e) =>
        {
            var columns = e.NewSize.Width < 520 ? 1 : cards.Length;
            if (grid.Columns == columns) return;
            grid.Columns = columns;
            for (var i = 0; i < cards.Length; i++)
                if (cards[i] is FrameworkElement fe)
                    fe.Margin = columns == 1
                        ? new Thickness(0, i == 0 ? 0 : 10, 0, 0)
                        : new Thickness(i == 0 ? 0 : 6, 0, i == cards.Length - 1 ? 0 : 6, 0);
        };
        return Ui.Stack(10,
            Ui.Title("Start with a bundle", 17),
            grid,
            Ui.Caption("One click sets the app up for how you use your PC. Everything else stays one click away."));
    }

    private static IEnumerable<UIElement> Groups()
    {
        foreach (var group in FeatureCatalog.All.GroupBy(f => f.Group))
        {
            var rows = group.Select(f => (UIElement)FeatureRow(f)).ToArray();
            yield return Ui.Group(FeatureCatalog.GroupTitle(group.Key), rows);
        }
    }

    private static FrameworkElement FeatureRow(FeatureInfo f)
    {
        var claim = FeatureRuntime.Registry.Parity.GetValueOrDefault(f.Id);
        var info = Ui.Icon(UI.Glyphs.Info, 12, "TextTertiary");
        // Built on first hover: 77 eager tooltips made every rebuild of this page noticeably slower.
        info.ToolTip = string.Empty;
        info.ToolTipOpening += (_, _) =>
        {
            if (info.ToolTip is ToolTip) return;
            info.ToolTip = new ToolTip { Content = TooltipText() };
        };
        TextBlock TooltipText() => new()
        {
            Text = $"On Windows: {f.WindowsApproach}" + (claim?.Note is { Length: > 0 } note ? $"\n\nStatus: {note}" : string.Empty),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
        };
        ToolTipService.SetInitialShowDelay(info, 150);

        // The badges wrap under the title when the page is narrow, instead of running under the button.
        var titleRow = new WrapPanel();
        foreach (var part in new UIElement?[]
                 {
                     Ui.Text(f.Title, 14.5, FontWeights.SemiBold, wrap: false),
                     info,
                     Ui.Badge(FeatureCatalog.EnergyLabel(f.Energy)),
                     f.Beta ? Ui.Badge("Experimental", "AccentSubtle", "Accent") : null,
                     f.Extra ? Ui.Badge("Fieldkit extra", "AccentSubtle", "Accent") : null,
                     StatusBadge(claim),
                     f.IsAvailable ? InstalledBadge(f.Id) : null,
                 })
        {
            if (part is not FrameworkElement fe) continue;
            fe.Margin = new Thickness(0, 1, 8, 1);
            fe.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(fe);
        }
        var text = Ui.Stack(3, titleRow, Ui.Caption(f.Description, 13));
        text.Margin = new Thickness(12, 0, 0, 0);

        // The button answers at once; the installer then works through its queue and the page is rebuilt when it is done.
        Button button = null!;
        void Change(bool available)
        {
            button.IsEnabled = false;
            button.Content = available ? "Installing…" : "Uninstalling…";
            FeatureInstaller.Run([(f.Id, available)]);
        }
        button = f.IsAvailable
            ? Ui.Button("Uninstall", () => Change(false))
            : Ui.Button("Install", () => Change(true), accent: true);
        button.MinWidth = 96;
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, "feature:" + f.Id);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = Ui.IconTile(f.Glyph, 38, "TabBarBackground", "TextPrimary");
        row.Children.Add(tile);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Grid.SetColumn(button, 2);
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Margin = new Thickness(12, 0, 0, 0);
        row.Children.Add(button);
        return row;
    }

    /// <summary>Green "Installed" next to the name (the website's demo hides it for features it uninstalls).</summary>
    private static Border InstalledBadge(string featureId)
    {
        var badge = Ui.Badge("Installed", "GreenSubtle", "Green");
        var id = "installed:" + featureId;
        System.Windows.Automation.AutomationProperties.SetAutomationId(badge, id);
        if (badge.Child is DependencyObject text) System.Windows.Automation.AutomationProperties.SetAutomationId(text, id);
        return badge;
    }

    // "Partial" only said that the macOS app has options Fieldkit lacks; beside every feature it read like an install
    // state. That detail stays in the info tooltip; the badges here are for what the user can act on.
    private static UIElement? StatusBadge(ParityClaim? claim) => claim?.Parity switch
    {
        null or Parity.Planned => Ui.Badge("Not built yet", "BadgeBackground", "Orange"),
        Parity.Native => Ui.Badge("Built into Windows", "BadgeBackground", "Cyan"),
        _ => null,
    };

    // ------------------------------------------------------------- permissions

    private sealed record PermissionInfo(string Title, string Explanation, string[] UsedBy, Func<bool?> Granted, string SettingsUri);

    private static IEnumerable<UIElement> PermissionRows()
    {
        var permissions = new[]
        {
            new PermissionInfo("Administrator", "Lets fan control, some temperature sensors and system-wide cleaning reach hardware and protected folders. Everything else runs as a normal user.",
                ["fanControl", "monitorCPU", "monitorGPU", "cleaner"], IsElevated, ""),
            new PermissionInfo("Microphone", "Lets screen recordings include your voice and the microphone tools read input levels.",
                ["screenRecorder", "micMute"], () => null, "ms-settings:privacy-microphone"),
            new PermissionInfo("Camera", "Lets the camera preview show your camera.",
                ["cameraPreview"], () => null, "ms-settings:privacy-webcam"),
            new PermissionInfo("Notifications", "Lets the app notify you about alerts you turned on.",
                ["monitorCPU", "monitorMemory", "monitorDisk", "monitorPower", "appUpdates", "cleaner"], () => null, "ms-settings:notifications"),
            new PermissionInfo("Calendar", "Lets the Dynamic Island show upcoming events.",
                ["notchCalendar"], () => null, "ms-settings:privacy-calendar"),
            new PermissionInfo("Input monitoring", "Windows lets any desktop app observe keys and mouse with low-level hooks, without a prompt. These features use them while on; uninstalled or switched-off features install no hook.",
                FeatureCatalog.All.Where(f => f.Energy is EnergyProfile.Keyboard or EnergyProfile.Mouse or EnergyProfile.Inputs).Select(f => f.Id).ToArray(),
                () => true, ""),
        };

        foreach (var p in permissions)
        {
            var users = p.UsedBy.Where(FeatureCatalog.IsAvailable).Select(id => FeatureCatalog.Get(id).Title).ToList();
            var granted = p.Granted();
            var status = granted switch
            {
                true => Ui.Badge("Granted", "GreenSubtle", "Green"),
                false => Ui.Badge("Not granted", "BadgeBackground", "Orange"),
                null => Ui.Badge("Managed by Windows", "BadgeBackground", "TextSecondary"),
            };
            var actions = Ui.Row(8);
            if (p.SettingsUri.Length > 0)
                actions.Children.Add(Ui.Button("Open Windows Settings", () => OpenUri(p.SettingsUri)));
            if (p.Title == "Administrator" && granted == false)
                Ui.Append(actions, 8, Ui.Button("Restart as administrator", RestartElevated));
            yield return Ui.Card(Ui.Stack(6,
                Ui.Split(Ui.Title(p.Title), status),
                Ui.Caption(p.Explanation, 13),
                Ui.Text(users.Count > 0 ? $"Used by {string.Join(", ", users)}" : "Nothing installed uses this right now.", 12.5, brush: "TextTertiary"),
                actions.Children.Count > 0 ? actions : null), new Thickness(16));
        }
    }

    public static bool? IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Opening {uri} failed", ex); }
    }

    public static void RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.ExecutablePath, "--elevated-restart") { UseShellExecute = true, Verb = "runas" });
            App.Quit();
        }
        catch (Exception ex)
        {
            Log.Warn($"Elevation declined: {ex.Message}");
        }
    }
}
