// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Launcher.RadialMenu;
using Fieldkit.Tests.Apps;
using Fieldkit.UI;

namespace Fieldkit.Tests.Launcher.RadialMenu;

/// <summary>Offscreen renders of the wheel, the settings canvas and the editor, kept in
/// %TEMP%\fieldkit-previews for a human to look at.</summary>
public class RadialPreviewTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RendersWheel(bool dark)
    {
        var path = StaRunner.PreviewPath($"radial-wheel-{(dark ? "dark" : "light")}.png");
        StaRunner.Run(() => RadialWheelView.RenderPreview(path, dark));
        Assert.True(new FileInfo(path).Length > 5000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RendersSettingsCanvasEditorAndCard(bool dark)
    {
        var theme = dark ? "dark" : "light";
        var canvas = StaRunner.PreviewPath($"radial-canvas-{theme}.png");
        var editor = StaRunner.PreviewPath($"radial-editor-{theme}.png");
        var card = StaRunner.PreviewPath($"radial-card-{theme}.png");
        StaRunner.Run(() =>
        {
            RadialMenuCanvas.RenderPreview(canvas, dark);
            RadialItemEditor.RenderPreview(editor, dark);
            RadialNowPlayingCard.RenderPreview(card, dark);
        });
        Assert.True(new FileInfo(canvas).Length > 5000);
        Assert.True(new FileInfo(editor).Length > 5000);
        Assert.True(new FileInfo(card).Length > 2000);
    }

    [Fact]
    public void RendersSymbolSheet()
    {
        var path = StaRunner.PreviewPath("radial-symbols.png");
        StaRunner.Run(() =>
        {
            var panel = new WrapPanel { Width = 900 };
            var extra = typeof(RadialIcons).GetFields()
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (f.Name, (string)f.GetRawConstantValue()!));
            foreach (var (name, glyph) in RadialIcons.Symbols.Select(s => (s.Symbol, s.Glyph)).Concat(extra))
            {
                var cell = Ui.Stack(2, Ui.Icon(glyph, 20, "TextPrimary"), Ui.Text(name, 9, wrap: false));
                cell.Width = 100;
                cell.Margin = new Thickness(2, 4, 2, 4);
                panel.Children.Add(cell);
            }
            PreviewRenderer.Render(new Border { Child = panel, Padding = new Thickness(8) }, path, 916, 1100, true);
        });
        Assert.True(new FileInfo(path).Length > 5000);
    }
}
