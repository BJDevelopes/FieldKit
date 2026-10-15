// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Features.QuickTools;

namespace Fieldkit.Features.Files.DiskMap;

/// <summary>Disk usage (a Fieldkit extra, upstream #3071).</summary>
public sealed class DiskMapModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements(DiskMapKeys.FeatureId, Parity.Full, "Fieldkit extra, from Vorssaint issue #3071.");
        // Closes the window when the tool is uninstalled.
        r.Service(DiskMapKeys.FeatureId, () => new HotkeyService(DiskMapKeys.FeatureId, null, string.Empty, () => { },
            DiskMapWindow.CloseInstance));
        r.Utility(new UtilityEntry(DiskMapKeys.FeatureId, "Disk usage", "See what takes up space on a drive", DiskMapKeys.Glyph,
            DiskMapWindow.Open, 43));
        r.Action(new ToolAction("diskUsage.open", DiskMapKeys.FeatureId, "Show disk usage", DiskMapKeys.Glyph, DiskMapWindow.Open));
    }
}
