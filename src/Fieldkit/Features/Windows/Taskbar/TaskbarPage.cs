// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.WindowTools;

/// <summary>Settings → Taskbar (upstream DockSettings): Taskbar Preview, taskbar clicks, desktop order, thumbnails.</summary>
public static class TaskbarPage
{
    public static FrameworkElement Build() =>
        Ui.Page("Taskbar", "See an app’s windows from its taskbar icon, and choose what a click on it does.",
            FeatureCatalog.IsAvailable("dockPreview") ? PreviewCard() : null,
            FeatureCatalog.IsAvailable("dockClick") ? ClickCard() : null,
            FeatureCatalog.IsAvailable("spacesOrder") ? DesktopOrderCard() : null,
            FeatureCatalog.IsAvailable("dockPreview")
                ? SwitcherPage.PreviewsCard(TaskbarPreviewService.PreviewSizeKey, TaskbarPreviewService.PreviewExcludedKey,
                    TaskbarPreviewService.PreviewExcludedApps)
                : null);

    private static FrameworkElement PreviewCard()
    {
        var details = WindowsUi.VisibleWhen(Ui.Stack(14,
            Ui.ToggleRow("Show only the current desktop",
                "When off, shows windows from all desktops. Choosing a window on another desktop takes you there.",
                TaskbarPreviewService.CurrentDesktopOnlyKey, false),
            Ui.SliderRow("Open delay", TaskbarPreviewService.OpenDelayKey, 200, 900, TaskbarPreviewService.DefaultOpenDelay,
                v => $"{v:0} ms", 50, "How long the pointer has to rest on an icon before its panel opens."),
            Ui.ToggleRow("Quit the app with the × button",
                "In Taskbar Preview, × quits the whole app instead of closing only that window.",
                TaskbarPreviewService.QuitAppOnCloseKey, false),
            WindowsUi.Note(Glyphs.Info,
                "Windows still shows its own small thumbnails on hover. Fieldkit’s panel adds every window across desktops, larger, with close and minimize buttons.")),
            TaskbarPreviewService.EnabledKey, false);
        return Ui.Group("Taskbar Preview", Ui.Stack(14,
            Ui.ToggleRow("Preview windows from the taskbar",
                "Hover over an open app in the taskbar to see its windows, then click the one you want.",
                TaskbarPreviewService.EnabledKey, false),
            details));
    }

    private static FrameworkElement ClickCard() =>
        Ui.Group("Taskbar clicks",
            Ui.ToggleRow("Click the taskbar icon to minimize",
                "The active app’s windows minimize when you click its taskbar icon. Click again to bring them back.",
                TaskbarClickService.MinimizeKey, false),
            Ui.ToggleRow("Click the taskbar icon to cycle windows",
                "Click an active app’s taskbar icon to rotate through its windows, like Alt+`.",
                TaskbarClickService.CycleKey, false));

    private static FrameworkElement DesktopOrderCard() =>
        Ui.Group(null,
            Ui.Split(
                Ui.Stack(2, Ui.Text("Keep virtual desktops in a fixed order", 13.5, FontWeights.Medium),
                    Ui.Caption("Windows never rearranges virtual desktops by most recent use, so they always stay in the order you set. Nothing to turn on.")),
                Ui.Badge("Built into Windows", "GreenSubtle", "Green")),
            WindowsUi.Left(Ui.Button("Open Multitasking settings", () => WindowsUi.OpenUri("ms-settings:multitasking"))));
}
