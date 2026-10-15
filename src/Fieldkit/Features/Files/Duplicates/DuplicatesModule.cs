// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Features.QuickTools;
using Fieldkit.UI;

namespace Fieldkit.Features.Files.Duplicates;

/// <summary>Duplicate files (a Fieldkit extra, upstream #3022).</summary>
public sealed class DuplicatesModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements(DuplicateKeys.FeatureId, Parity.Full, "Fieldkit extra, from Vorssaint issue #3022.");
        // Closes the window when the tool is uninstalled.
        r.Service(DuplicateKeys.FeatureId, () => new HotkeyService(DuplicateKeys.FeatureId, null, string.Empty, () => { },
            DuplicateFinderWindow.CloseInstance));
        r.Utility(new UtilityEntry(DuplicateKeys.FeatureId, "Duplicate files", "Find copies of the same file and free up space",
            Glyphs.Copy, DuplicateFinderWindow.Open, 42));
        r.Action(new ToolAction("duplicateFinder.open", DuplicateKeys.FeatureId, "Find duplicate files", Glyphs.Copy, DuplicateFinderWindow.Open));
    }
}
