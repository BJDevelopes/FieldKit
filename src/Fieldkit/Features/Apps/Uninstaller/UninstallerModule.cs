// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Uninstaller;

/// <summary>
/// Public entry points of the Uninstaller, for other features (Command Bar, Radial menu, Cleaner, App updates).
/// Everything is a no-op while the feature is uninstalled in the Features hub.
/// </summary>
public static class UninstallerTool
{
    public const string FeatureId = "uninstaller";
    public const string WindowKey = "uninstaller";
    public const string ShortcutKey = "uninstaller.shortcut";
    public const string CommandBarKey = "uninstaller.commandBarEnabled";

    private static UninstallerWindow? _window;

    /// <summary>Opens (or brings forward) the Uninstaller; with <paramref name="preselect"/> it goes straight
    /// to scanning that app, like dropping it on upstream's window.</summary>
    public static void Open(InstalledApp? preselect = null)
    {
        if (!FeatureCatalog.IsAvailable(FeatureId)) return;
        var window = ToolWindow.ShowSingle(WindowKey, () =>
        {
            var w = new UninstallerWindow();
            w.Closed += (_, _) => { if (ReferenceEquals(_window, w)) _window = null; };
            return w;
        });
        _window = window;
        if (preselect is not null && !window.Session.IsBusy) window.Session.Select(preselect);
    }

    /// <summary>Opens the Uninstaller on the app a program (.exe) or shortcut (.lnk) belongs to.</summary>
    public static void OpenForFile(string path)
    {
        Open();
        if (_window is { } w && !w.Session.IsBusy) w.Session.SelectFile(path);
    }

    /// <summary>Whether the Uninstaller window is open.</summary>
    public static bool IsOpen => _window is not null;

    /// <summary>Closes the window and cancels its scans and waits (feature switched off).</summary>
    public static void Close()
    {
        if (_window is { } w) w.ForceClose = true;
        ToolWindow.CloseSingle(WindowKey);
        _window = null;
    }
}

/// <summary>Runs while the feature is installed: owns the optional global shortcut and closes the tool on stop.</summary>
public sealed class UninstallerService : FeatureService
{
    private IDisposable? _hotkey;

    public override string FeatureId => UninstallerTool.FeatureId;

    protected override void OnStart() =>
        _hotkey = Hotkeys.Bind(UninstallerTool.ShortcutKey, string.Empty, () => UninstallerTool.Open());

    protected override void OnStop()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        UninstallerTool.Close();
    }
}

public sealed class UninstallerModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements(UninstallerTool.FeatureId, Parity.Partial,
            "Runs the app's registered uninstaller (UAC for machine-wide installs, PackageManager for Store apps), then reviews " +
            "AppData, ProgramData, %TEMP%, crash dumps, Store package data, Start menu/Desktop shortcuts, the install folder and " +
            "HKCU/HKLM Software keys. Matching is by names, executables and install folder (Windows has no bundle ids or code-signing " +
            "groups), so more matches start unchecked; machine-wide leftovers need Fieldkit to run as administrator. No Homebrew/winget " +
            "hand-off, no Spotlight-style index search, and 'Show in Command Bar' is stored for the Command Bar to honour.");
        r.Service(UninstallerTool.FeatureId, () => new UninstallerService());
        r.SettingsPage(new SettingsPageDef("uninstaller", SettingsCategory.Utilities, UninstallerText.Name, Glyphs.Delete, 30,
            [UninstallerTool.FeatureId], UninstallerPage.Build, ["uninstall", "remove", "leftovers", "apps", "programs", "recycle bin"]));
        r.Utility(new UtilityEntry(UninstallerTool.FeatureId, UninstallerText.Name, UninstallerText.EnableCaption, Glyphs.Delete,
            () => UninstallerTool.Open(), 30, UninstallerTool.ShortcutKey));
        r.Action(new ToolAction("uninstaller.open", UninstallerTool.FeatureId, UninstallerText.MenuItem, Glyphs.Delete,
            () => UninstallerTool.Open(), UninstallerTool.ShortcutKey));
    }
}

/// <summary>Settings → Utilities → Uninstaller (upstream embeds the drop zone and the Command Bar switch in its page).</summary>
public static class UninstallerPage
{
    public static FrameworkElement Build()
    {
        var zone = UninstallerUi.DropZone(200, large: true);
        zone.Width = 360;
        zone.HorizontalAlignment = HorizontalAlignment.Center;
        zone.AllowDrop = true;
        zone.Cursor = System.Windows.Input.Cursors.Hand;
        zone.MouseLeftButtonUp += (_, _) => UninstallerTool.Open();
        zone.DragOver += (_, e) =>
        {
            var ok = DroppedApp(e) is not null;
            e.Effects = ok ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
            UninstallerUi.SetDropTargeted(zone, ok);
        };
        zone.DragLeave += (_, _) => UninstallerUi.SetDropTargeted(zone, false);
        zone.Drop += (_, e) =>
        {
            UninstallerUi.SetDropTargeted(zone, false);
            if (DroppedApp(e) is { } path) UninstallerTool.OpenForFile(path);
        };
        var choose = Ui.Button(UninstallerText.Choose, () => UninstallerTool.Open());
        choose.Padding = new Thickness(18, 8, 18, 8);
        choose.HorizontalAlignment = HorizontalAlignment.Center;
        var note = Ui.Text(UninstallerText.EmptyNote, 12, brush: "TextTertiary");
        note.HorizontalAlignment = HorizontalAlignment.Center;
        var hero = Ui.Stack(16, zone, choose, note);
        hero.Margin = new Thickness(0, 4, 0, 4);

        var steps = Ui.Group("How it works",
            Step(1, UninstallerText.Step1),
            Step(2, UninstallerText.Step2),
            Step(3, UninstallerText.Step3 + " Registry keys are exported to a .reg backup before they are deleted."));

        var options = Ui.Group("Options",
            Ui.ToggleRow(UninstallerText.CommandBarToggle, UninstallerText.CommandBarCaption, UninstallerTool.CommandBarKey, false),
            Ui.ToggleRow("Include Microsoft Store apps", "List the apps installed from the Microsoft Store for your account.",
                UninstallerSession.ShowStoreAppsKey, true),
            Ui.ToggleRow("Prefer the silent uninstaller",
                "When an app registers a quiet uninstaller, run it without its wizard. Windows Installer apps show only a progress bar.",
                UninstallerSession.PreferQuietKey, false),
            Ui.ShortcutRow(UninstallerText.MenuItem, UninstallerTool.ShortcutKey, string.Empty, "Opens the Uninstaller from anywhere."));

        var backups = Path.Combine(AppInfo.DataDirectory, "Registry backups");
        var openBackups = Ui.Button("Open backups folder", () =>
        {
            Directory.CreateDirectory(backups);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{backups}\"") { UseShellExecute = true });
        }, glyph: Glyphs.Folder);
        var safety = Ui.Group("Safety",
            Ui.Split(Ui.Stack(2, Ui.Text("Registry backups", 13.5, FontWeights.Medium), Ui.Caption(backups)), openBackups),
            Elevation.IsAdministrator
                ? Ui.Caption("Fieldkit is running as administrator, so machine-wide leftovers can be removed. Registry keys under HKLM always start unchecked.")
                : Ui.Caption("Machine-wide leftovers (ProgramData, Program Files, the all-users Start menu and HKLM) are shown but can only be removed when Fieldkit runs as administrator. Apps installed for all users still uninstall normally: Windows asks for permission."));

        return Ui.Page(UninstallerText.Name, UninstallerText.EnableCaption, hero, steps, options, safety);
    }

    private static FrameworkElement Step(int number, string text)
    {
        var badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Child = Ui.Text(number.ToString(System.Globalization.CultureInfo.InvariantCulture), 12, FontWeights.SemiBold, "Accent", wrap: false),
            VerticalAlignment = VerticalAlignment.Top,
        };
        ((TextBlock)badge.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)badge.Child).VerticalAlignment = VerticalAlignment.Center;
        badge.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
        var caption = Ui.Text(text, 13);
        caption.Margin = new Thickness(12, 2, 0, 0);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(caption, 1);
        g.Children.Add(badge);
        g.Children.Add(caption);
        return g;
    }

    private static string? DroppedApp(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            : null;
}
