// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Files;

/// <summary>Settings → Files → Installer helper.</summary>
public static class InstallerPage
{
    public static FrameworkElement Build()
    {
        var folderCaption = Ui.Caption(InstallerSettings.PortableFolder);
        folderCaption.TextTrimming = TextTrimming.CharacterEllipsis;
        folderCaption.TextWrapping = TextWrapping.NoWrap;
        var change = Ui.Button("Change…", () => ChangeFolder(folderCaption));
        var reset = Ui.IconButton(Glyphs.Undo, "Use the default folder", () =>
        {
            Settings.Remove(InstallerSettings.PortableFolderKey);
        });
        void SyncFolder()
        {
            folderCaption.Text = InstallerSettings.PortableFolder;
            folderCaption.ToolTip = folderCaption.Text;
            reset.Visibility = Settings.Has(InstallerSettings.PortableFolderKey) ? Visibility.Visible : Visibility.Collapsed;
        }
        SyncFolder();
        var folderRow = Ui.Split(
            Ui.Stack(2, Ui.Text("Portable apps folder", 13.5, FontWeights.Medium),
                Ui.Caption("Where an app from a ZIP without an installer is copied to."), folderCaption),
            Ui.Row(4, reset, change));
        Ui.Track(folderRow, () => Settings.Observe(SyncFolder, InstallerSettings.PortableFolderKey));

        var options = Ui.Group("Options",
            Ui.ToggleRow(InstallerText.TrashDownloadOption,
                "After a successful install, the disk image or archive goes to the Recycle Bin, where you can restore it.",
                InstallerSettings.TrashDownloadKey, InstallerSettings.TrashDownloadDefault),
            Ui.ToggleRow(InstallerText.RevealAppOption,
                "For portable apps copied from a ZIP archive. Installers show their own finish screen.",
                InstallerSettings.RevealAppKey, InstallerSettings.RevealAppDefault),
            folderRow);

        var steps = Ui.Group("How it works",
            Step("1", "Pick a download", "Open the helper and choose one of the newest ISO, IMG, VHD, VHDX or ZIP files in Downloads, or any file elsewhere."),
            Step("2", "Fieldkit opens it", "Disk images are mounted as a drive; archives are extracted to a private temporary folder. VHD and VHDX need administrator rights."),
            Step("3", "Confirm and install", "It finds the setup program (autorun, setup.exe, a single .msi, …) and asks before running it, then waits until the installer and anything it started from the image have closed."),
            Step("4", "Clean up", "The image is ejected, temporary files are removed and, if you like, the download goes to the Recycle Bin. Nothing is ever deleted permanently."));

        var open = Ui.Button("Open installer helper", InstallerWindow.Open, accent: true, glyph: Glyphs.Disc);
        open.HorizontalAlignment = HorizontalAlignment.Left;

        return Ui.Page(InstallerText.Title,
            "Install an app from a disk image or ZIP archive you downloaded, then eject the image and tidy up the download. " +
            "Windows does not say when an image is opened, so you choose the file here instead.",
            open,
            options,
            steps);
    }

    private static FrameworkElement Step(string number, string title, string description)
    {
        var badge = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Child = Ui.Text(number, 12, FontWeights.SemiBold, "Accent", wrap: false),
            VerticalAlignment = VerticalAlignment.Top,
        };
        ((TextBlock)badge.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)badge.Child).VerticalAlignment = VerticalAlignment.Center;
        badge.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
        var text = Ui.Stack(2, Ui.Text(title, 13.5, FontWeights.Medium), Ui.Caption(description));
        text.Margin = new Thickness(12, 0, 0, 0);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(badge);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        return g;
    }

    private static void ChangeFolder(TextBlock caption)
    {
        var current = InstallerSettings.PortableFolder;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where portable apps go",
            InitialDirectory = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        if (dialog.ShowDialog(Window.GetWindow(caption)) == true && !string.IsNullOrEmpty(dialog.FolderName))
            Settings.Set(InstallerSettings.PortableFolderKey, dialog.FolderName);
    }
}
