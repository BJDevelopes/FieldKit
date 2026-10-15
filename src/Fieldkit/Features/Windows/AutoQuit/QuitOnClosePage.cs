// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.WindowTools;

/// <summary>Settings → Quit on close (upstream AutoQuitSettings).</summary>
public static class QuitOnClosePage
{
    // Segoe Fluent Icons: ChromeClose and Lightning (checked with --render-glyph-range).
    private const string GlyphWindowClose = "\uE8BB";
    private const string GlyphBolt = "\uE945";

    public static FrameworkElement Build()
    {
        var status = WindowsUi.Status(Glyphs.Check, "Active now", "Green");
        status.Visibility = Visibility.Collapsed;
        Ui.Every(status, TimeSpan.FromSeconds(1), () =>
            status.Visibility = FeatureRuntime.Service<AutoQuitService>()?.IsRunning == true ? Visibility.Visible : Visibility.Collapsed);

        var exceptions = WindowsUi.AppListEditor("Exceptions", "Apps on this list stay open even with no windows.",
            "Add app…", "Remove", AutoQuitService.ExceptionsKey,
            () => AutoQuitService.Exceptions.Items, AutoQuitService.Exceptions.Add, AutoQuitService.Exceptions.Remove);
        var listed = WindowsUi.AppListEditor("Apps to quit", "Only these apps quit when their last window closes.",
            "Add app…", "Remove", AutoQuitService.AppsKey,
            () => AutoQuitService.Apps.Items, AutoQuitService.Apps.Add, AutoQuitService.Apps.Remove);
        void SyncScope()
        {
            var onlyListed = AutoQuitLogic.ParseScope(Settings.GetString(AutoQuitService.ScopeKey, "all")) == AutoQuitScope.OnlyListed;
            exceptions.Visibility = onlyListed ? Visibility.Collapsed : Visibility.Visible;
            listed.Visibility = onlyListed ? Visibility.Visible : Visibility.Collapsed;
        }
        SyncScope();
        Ui.Track(exceptions, () => Settings.Observe(SyncScope, AutoQuitService.ScopeKey));

        return Ui.Page("Quit on close", null,
            Ui.Group(null,
                Ui.ToggleRow("Quit an app when its last window closes", "Closing an app’s last window also quits it.",
                    AutoQuitService.EnabledKey, false).WithBelow(status)),
            Ui.Group("How it works",
                Bullet(GlyphWindowClose, "Close an app’s last window (Alt+F4 or the close button)."),
                Bullet(GlyphBolt, "The app quits on its own. “Save changes?” dialogs still appear."),
                Ui.Caption("Apps that normally run without a window are never quit, and neither are parts of Windows such as File Explorer."),
                WindowsUi.Note(Glyphs.Warning,
                    "Some apps close their last window while they keep working, such as a screen recorder once capture starts, or apps that hide in the notification area. Those are quit too, unless they are on the Exceptions list.")),
            WindowsUi.EnabledWhen(Ui.Group("Apps",
                WindowsUi.ChipRow("Applies to", null, AutoQuitService.ScopeKey, "all",
                    [("all", "Every app except the exceptions"), ("listed", "Only the apps I choose")]),
                Ui.Stack(0, exceptions, listed),
                Ui.ToggleRow("End apps that do not quit within 5 seconds",
                    "Off: apps are only asked to quit, never ended. On: an app that ignores the request is ended unless it opened a window again.",
                    AutoQuitService.ForceKey, false)), AutoQuitService.EnabledKey, false));
    }

    private static FrameworkElement Bullet(string glyph, string text)
    {
        return WindowsUi.Note(glyph, text, "TextPrimary", "Accent");
    }
}
