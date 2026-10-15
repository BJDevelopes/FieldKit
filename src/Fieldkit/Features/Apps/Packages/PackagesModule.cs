// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Features.Apps.Winget;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.Packages;

/// <summary>
/// "Packages": upstream's Homebrew manager mapped to winget (search, install, update, uninstall,
/// update all, source refresh) in a tool window, with its Settings page and Utilities entry.
/// </summary>
public sealed class PackagesModule : IFeatureModule
{
    public const string FeatureId = "homebrew";
    public const string ShortcutKey = "homebrew.shortcut";

    public void Register(ModuleRegistry r)
    {
        r.Implements(FeatureId, Parity.Partial,
            "winget search (winget / Microsoft Store / all sources), installed list with update badges, details, install, update, " +
            "uninstall with a removal review, update all, source refresh, pinned updates and live progress. Not available from " +
            "winget: popularity counts, dependency tree and orphans; sources are listed but not added or removed (needs admin).");
        r.Service(FeatureId, () => new PackagesService());
        r.SettingsPage(new SettingsPageDef("packages", SettingsCategory.Utilities, PackagesStrings.Name, Glyphs.Package, 20,
            [FeatureId], PackagesPage.Build, ["winget", "homebrew", "install", "uninstall", "upgrade", "software", "sources", "store"]));
        r.Utility(new UtilityEntry(FeatureId, PackagesStrings.Name, PackagesStrings.EnableCaption, Glyphs.Package,
            PackagesWindow.Open, 20, ShortcutKey));
        r.Action(new ToolAction("homebrew.open", FeatureId, PackagesStrings.OpenPackages, Glyphs.Package, PackagesWindow.Open, ShortcutKey));
    }
}

/// <summary>Lives while the feature is installed: owns the optional shortcut, and on stop closes the
/// window, cancels this feature's running operation and every pending read.</summary>
public sealed class PackagesService : FeatureService
{
    private IDisposable? _hotkey;

    public override string FeatureId => PackagesModule.FeatureId;

    protected override void OnStart()
    {
        _hotkey = Hotkeys.Bind(PackagesModule.ShortcutKey, string.Empty, PackagesWindow.Open);
    }

    protected override void OnStop()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        PackagesWindow.CloseWindow();
        WingetOperationLane.Shared.CancelOwnedBy(PackageManager.Owner);
        PackageManager.Shared.CancelReads();
    }
}
