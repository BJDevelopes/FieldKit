// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Files;

/// <summary>File Explorer shortcuts: cut &amp; paste (native, plus pasting copied images as files) and rename with F2 (native).</summary>
public sealed class ExplorerModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements(ExplorerPasteImageService.Id, Parity.Partial,
            "File Explorer cuts (Ctrl+X) and pastes (Ctrl+V) files natively, without the floating cut indicator; " +
            "Fieldkit adds Ctrl+V of a copied image as a PNG file in the open folder.");
        r.Implements("finderRename", Parity.Native, "File Explorer renames the selected file or folder with F2; no custom shortcut.");

        r.Service(ExplorerPasteImageService.Id, () => new ExplorerPasteImageService());
        r.SettingsPage(new SettingsPageDef("explorer", SettingsCategory.Files, "Cut & paste", Glyphs.Cut, 10,
            [ExplorerPasteImageService.Id, "finderRename"], BuildPage,
            ["explorer", "cut", "paste", "image", "png", "rename", "f2", "move files"]));
        r.Control(new ControlEntry(ExplorerPasteImageService.Id, ControlGroup.Files, "Cut & paste",
            "File Explorer cuts and pastes files itself. On: Ctrl+V there also saves a copied image as a PNG file.",
            Glyphs.Cut, ExplorerPasteImageService.EnabledKey, false, Order: 10));
    }

    private static FrameworkElement BuildPage()
    {
        var content = new List<UIElement?>();
        if (FeatureCatalog.IsAvailable(ExplorerPasteImageService.Id))
        {
            var status = Ui.Row(5, Ui.Icon(Glyphs.Check, 11, "Green"), Ui.Text("Ready in File Explorer", 12, brush: "Green", wrap: false));
            void Sync() => status.Visibility = Settings.GetBool(ExplorerPasteImageService.EnabledKey) ? Visibility.Visible : Visibility.Collapsed;
            Sync();
            Ui.Track(status, () => Settings.Observe(Sync, ExplorerPasteImageService.EnabledKey));
            content.Add(Ui.Group(null, Ui.Stack(8,
                Ui.ToggleRow("Paste copied images as files",
                    "When File Explorer is active, Ctrl+V saves a copied image as a PNG in the current folder (\"Pasted image <date> <time>.png\").",
                    ExplorerPasteImageService.EnabledKey, false),
                status)));
            content.Add(Ui.Group("How to use",
                HowRow(["Ctrl", "X"], "Select items in File Explorer and press Ctrl+X to cut them."),
                HowRow(["Ctrl", "V"], "Open the destination folder and press Ctrl+V to move them there."),
                HowRow(["Ctrl", "V"], "Copy a picture in any app, open a folder and press Ctrl+V to save it there as a PNG file."),
                Ui.Caption("Cutting and moving files is built into File Explorer. In text fields (like when renaming), Ctrl+X and Ctrl+V keep working as usual.")));
        }
        if (FeatureCatalog.IsAvailable("finderRename"))
        {
            var open = Ui.Button("Open File Explorer", () => Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }), glyph: Glyphs.Folder);
            open.HorizontalAlignment = HorizontalAlignment.Left;
            content.Add(Ui.Group("Rename shortcut",
                HowRow(["F2"], "Rename the selected file or folder."),
                Ui.Caption("File Explorer already renames with F2, so there is nothing to switch on. On keyboards where F2 controls something else, press Fn+F2."),
                open));
        }
        return Ui.Page("Cut & paste", "File Explorer shortcuts.", content.ToArray());
    }

    private static FrameworkElement HowRow(string[] keys, string text)
    {
        var caps = Ui.Row(4, keys.Select(k => (UIElement)Ui.Badge(k)).ToArray());
        caps.Width = 76;
        caps.VerticalAlignment = VerticalAlignment.Center;
        var label = Ui.Text(text, 13);
        label.Margin = new Thickness(10, 0, 0, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        var row = new System.Windows.Controls.DockPanel();
        System.Windows.Controls.DockPanel.SetDock(caps, System.Windows.Controls.Dock.Left);
        row.Children.Add(caps);
        row.Children.Add(label);
        return row;
    }
}
