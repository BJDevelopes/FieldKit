// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Apps.Winget;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.AppUpdates;

/// <summary>The App updates tool window (upstream: the panel's App updates sub-view).</summary>
public sealed class AppUpdatesWindow : ToolWindow
{
    public const string WindowKey = "appUpdates";

    public AppUpdatesWindow() : base(AppUpdateStrings.PageTitle, 600, 660)
    {
        var controller = AppUpdatesController.Shared;
        Content = BuildContent(controller);
        Loaded += (_, _) => controller.CheckIfNeeded();
        Activated += (_, _) => controller.SurfaceActivated();
    }

    public static void Open()
    {
        if (!FeatureCatalog.IsAvailable(AppUpdatesController.FeatureId)) return;
        ShowSingle(WindowKey, () => new AppUpdatesWindow());
    }

    public static void CloseWindow() => CloseSingle(WindowKey);

    private static FrameworkElement BuildContent(AppUpdatesController controller)
    {
        SheetHost? sheet = null;
        var settings = Ui.IconButton(Glyphs.Settings, AppUpdateStrings.Settings, () => PanelHost.OpenSettings("appUpdates"), 14);
        var header = Ui.Split(
            Ui.Row(10, Ui.Icon(Glyphs.Download, 17), Ui.Text(AppUpdateStrings.PageTitle, 15, FontWeights.SemiBold, wrap: false)),
            settings);
        header.Margin = new Thickness(18, 12, 14, 12);
        var list = new AppUpdatesListView(controller, () => sheet);
        var card = Ui.Card(list, new Thickness(16, 14, 16, 16));
        card.Margin = new Thickness(16);
        var scroll = new ScrollViewer
        {
            Content = card,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(header);
        var divider = Ui.Divider(0);
        Grid.SetRow(divider, 1);
        root.Children.Add(divider);
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        sheet = new SheetHost(root);
        var host = new Border { Child = sheet };
        host.SetResourceReference(Border.BackgroundProperty, "WindowBackground");
        return host;
    }

    /// <summary>Renders the window content with sample rows (no winget involved), for visual review.</summary>
    public static void RenderPreview(string path, bool dark)
    {
        var lane = new WingetOperationLane();
        var items = new List<AppUpdateItem>
        {
            new("Winget:git.git", AppUpdateSource.Winget, "Git", "2.50.1", "2.55.0.5", "Git.Git", "winget"),
            new("Winget:microsoft.visualstudiocode", AppUpdateSource.Winget, "Microsoft Visual Studio Code (User)", "1.139.2", "1.141.1", "Microsoft.VisualStudioCode", "winget"),
            new("Winget:microsoft.windowsterminal", AppUpdateSource.Winget, "Windows Terminal", "1.23.11752.0", "1.25.2733.0", "Microsoft.WindowsTerminal", "winget"),
            new("Winget:videolan.vlc", AppUpdateSource.Winget, "VLC media player", "3.0.20", "3.0.21", "VideoLAN.VLC", "winget"),
            new("Store:9ncbcszsjrsb", AppUpdateSource.Store, "Spotify Music", "1.2.69.449", "1.2.70.409", "9NCBCSZSJRSB", "msstore"),
            new("Unknown:discord.discord", AppUpdateSource.Unknown, "Discord", "Unknown", "1.0.9205", "Discord.Discord", "winget"),
        };
        var rules = new List<AppUpdateRule> { new("Python.Python.3.12", "Python 3.12", "3.12.10") };
        var controller = AppUpdatesController.ForPreview(lane, items, rules, DateTime.Now.AddMinutes(-5));
        PreviewRenderer.Render(BuildContent(controller), path, 600, 660, dark);
    }
}
