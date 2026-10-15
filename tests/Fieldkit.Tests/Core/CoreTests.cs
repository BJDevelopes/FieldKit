// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Tests.Core;

public class CoreTests
{
    [Fact]
    public void FeatureIdsAreUnique()
    {
        var ids = FeatureCatalog.All.Select(f => f.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void ExtensionsPointAtExistingParents()
    {
        foreach (var f in FeatureCatalog.All.Where(f => f.Parent is not null))
            Assert.True(FeatureCatalog.TryGet(f.Parent!, out _), f.Id);
    }

    [Theory]
    [InlineData("Ctrl+Alt+V")]
    [InlineData("Ctrl+Shift+Win+F12")]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+`")]
    public void ShortcutRoundTrips(string text)
    {
        Assert.Equal(text, Shortcut.Parse(text).ToString());
    }

    [Fact]
    public void EmptyShortcutParsesAsNone()
    {
        Assert.True(Shortcut.Parse("").IsEmpty);
        Assert.True(Shortcut.Parse(null).IsEmpty);
        Assert.True(Shortcut.Parse("Ctrl+").IsEmpty);
    }
}
