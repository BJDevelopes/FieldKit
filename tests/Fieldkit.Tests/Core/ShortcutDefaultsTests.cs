// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Tests.Core;

/// <summary>Default shortcuts come from many modules; they must not fight each other or typing.</summary>
public class ShortcutDefaultsTests
{
    private static List<(string Action, Shortcut Shortcut)> EffectiveDefaults()
    {
        FeatureRuntime.Initialize();
        return FeatureRuntime.Registry.Actions
            .Where(a => a.ShortcutKey is not null)
            .GroupBy(a => a.ShortcutKey!)
            .Select(g => g.First())
            .Select(a => (a.Id, Shortcut.FromSetting(a.ShortcutKey!, a.DefaultShortcut)))
            .Where(x => !x.Item2.IsEmpty)
            .ToList();
    }

    [Fact]
    public void DefaultShortcutsAreUniqueAcrossFeatures()
    {
        var clashes = EffectiveDefaults().GroupBy(x => x.Shortcut).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Action))}").ToList();
        Assert.True(clashes.Count == 0, string.Join("\n", clashes));
    }

    [Fact]
    public void DefaultShortcutsDoNotSwallowAltGrTyping()
    {
        var bad = EffectiveDefaults()
            .Where(x => x.Shortcut.Modifiers == (Modifiers.Ctrl | Modifiers.Alt) && x.Shortcut.Key is >= 0x30 and <= 0x5A or >= 0xBA and <= 0xE2)
            .Select(x => $"{x.Action} = {x.Shortcut}").ToList();
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }
}
