// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Apps.Winget;

namespace Fieldkit.Features.Apps.Packages;

/// <summary>The Packages tool window (upstream: the Homebrew manager in the panel and Settings).</summary>
public sealed class PackagesWindow : ToolWindow
{
    public const string WindowKey = "packages";

    public PackagesWindow() : base(PackagesStrings.Name, 980, 680)
    {
        MinWidth = 760;
        MinHeight = 480;
        var manager = PackageManager.Shared;
        Content = new PackagesView(manager);
        Loaded += (_, _) => manager.EnsureLoaded();
    }

    /// <summary>Opens (or brings forward) the Packages window.</summary>
    public static void Open()
    {
        if (!Core.FeatureCatalog.IsAvailable("homebrew")) return;
        ShowSingle(WindowKey, () => new PackagesWindow());
    }

    public static void CloseWindow() => CloseSingle(WindowKey);

    /// <summary>Renders the window content with sample data (no winget involved), for visual review.</summary>
    public static void RenderPreview(string path, bool dark)
    {
        var lane = new WingetOperationLane();
        lane.SetPreview(new WingetOperationStatus
        {
            Action = WingetAction.Upgrade,
            Owner = PackageManager.Owner,
            Target = new WingetTarget("Git", "Git.Git", "winget"),
            Targets = [new WingetTarget("Git", "Git.Git", "winget")],
            Phase = WingetPhase.Downloading,
            Result = WingetResult.Running,
            Progress = 0.42,
            StartedAt = DateTime.Now.AddSeconds(-14),
            LastActivity = "Downloading https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.5/Git-2.55.0.5-64-bit.exe",
        }, "$ winget upgrade --id Git.Git --exact --source winget\nFound Git [Git.Git] Version 2.55.0.5\nThis application is licensed to you by its owner.\n");
        var installed = SampleInstalled();
        var results = new List<PackageItem>
        {
            PackageRules.FromSearch(new WingetPackage { Name = "Microsoft Visual Studio Code", Id = "Microsoft.VisualStudioCode", Version = "1.141.1", Match = "Moniker: vscode", Source = "winget" }, installed),
            PackageRules.FromSearch(new WingetPackage { Name = "VSCodium", Id = "VSCodium.VSCodium", Version = "1.140.06880", Match = "Tag: vscode", Source = "winget" }, installed),
            PackageRules.FromSearch(new WingetPackage { Name = "Microsoft Visual Studio Code Insiders", Id = "Microsoft.VisualStudioCode.Insiders", Version = "1.127.0", Match = "Moniker: vscode-insiders", Source = "winget" }, installed),
        };
        var selected = installed.First(i => i.Id == "Microsoft.VisualStudioCode");
        var details = new WingetDetails
        {
            Name = "Microsoft Visual Studio Code",
            Id = "Microsoft.VisualStudioCode",
            Version = "1.141.1",
            Publisher = "Microsoft Corporation",
            Description = "Microsoft Visual Studio Code is a code editor redefined and optimized for building and debugging modern web and cloud applications.",
            Homepage = "https://code.visualstudio.com/",
            License = "Microsoft Software License",
            ReleaseNotesUrl = "https://code.visualstudio.com/updates/v1_141",
        };
        var manager = PackageManager.ForPreview(lane, installed, results, selected, details);
        var view = new PackagesView(manager);
        PreviewRenderer.Render(view, path, 980, 680, dark);
    }

    /// <summary>Renders the uninstall review sheet with sample data.</summary>
    public static void RenderUninstallPreview(string path, bool dark)
    {
        var item = SampleInstalled().First(i => i.Id == "7zip.7zip");
        var app = new InstalledApp
        {
            Id = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\7-Zip",
            Name = "7-Zip 24.08 (x64)",
            Publisher = "Igor Pavlov",
            Version = "24.08",
            InstallLocation = @"C:\Program Files\7-Zip",
            EstimatedSizeBytes = 5_800_000,
        };
        var host = new SheetHost(new System.Windows.Controls.Border());
        host.Show(host.Confirmation(PackageRules.ConfirmTitle(WingetAction.Uninstall), PackageRules.ConfirmBody(WingetAction.Uninstall, item),
            PackagesView.UninstallReviewCard(item, app), PackageRules.ActionTitle(WingetAction.Uninstall), true, () => { }, UI.Glyphs.Delete));
        PreviewRenderer.Render(host, path, 640, 560, dark);
    }

    private static List<PackageItem> SampleInstalled()
    {
        WingetPackage P(string name, string id, string version, string available = "", string source = "winget") =>
            new() { Name = name, Id = id, Version = version, Available = available, Source = source };
        var pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Python.Python.3.12" };
        return new[]
        {
            P("7-Zip 24.08 (x64)", "7zip.7zip", "24.08"),
            P("Fabrikam Reader (64-bit)", @"ARP\Machine\X64\{6F1C2B9E-0000-4F2A-9C11-000000000001}", "23.008.20470", source: ""),
            P("Git", "Git.Git", "2.47.1", "2.55.0.5"),
            P("Microsoft Visual Studio Code (User)", "Microsoft.VisualStudioCode", "1.139.2", "1.141.1"),
            P("Notepad++ (64-bit x64)", "Notepad++.Notepad++", "8.8.5"),
            P("Paint", @"MSIX\Microsoft.Paint_11.2508.361.0_x64__8wekyb3d8bbwe", "11.2508.361.0", source: ""),
            P("PowerToys (Preview) x64", "Microsoft.PowerToys", "0.94.1"),
            P("Python 3.12.4 (64-bit)", "Python.Python.3.12", "3.12.4", "3.12.10"),
            P("Spotify Music", "9NCBCSZSJRSB", "1.2.70.409", source: "msstore"),
            P("VLC media player", "VideoLAN.VLC", "3.0.21"),
            P("Windows Terminal", "Microsoft.WindowsTerminal", "1.23.11752.0", "1.25.2733.0"),
        }.Select(p => PackageRules.FromInstalled(p, pinned)).OrderBy(p => p.Name).ToList();
    }
}
