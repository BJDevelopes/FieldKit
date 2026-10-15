// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.UI;
using Windows.Devices.Enumeration;

namespace Fieldkit.Features.Capture;

/// <summary>Settings → Camera preview.</summary>
public static class CameraPage
{
    public static FrameworkElement Build()
    {
        var camera = new ComboBox { MinWidth = 220 };
        camera.Items.Add(new ComboBoxItem { Content = "Default camera", Tag = string.Empty });
        camera.SelectedIndex = 0;
        var updating = true;
        camera.SelectionChanged += (_, _) =>
        {
            if (!updating && camera.SelectedItem is ComboBoxItem { Tag: string id }) Settings.Set(CameraSettings.Device, id);
        };
        // Listing cameras reads device names only; the camera itself is not opened here.
        _ = FillAsync(camera, () => updating = false);

        var main = Ui.Group("Camera preview",
            Ui.Stack(8,
                CaptureUi.ActionButtons(("Open preview", Glyphs.Webcam, CameraPreviewWindow.Open)),
                Ui.Caption("Check how you look before a call. Esc, a click elsewhere or switching apps closes it.")),
            CaptureUi.ShortcutToggle("Camera preview shortcut", CameraSettings.Shortcut, CameraSettings.ShortcutDefault),
            Ui.Split(Ui.Text("Camera", 13.5, FontWeights.Medium), camera),
            Ui.ToggleRow("Mirror the picture", "Like looking in a mirror. The other people in a call see you unmirrored either way.", CameraSettings.Mirror, true),
            CaptureUi.SegmentedRow("Shape", null, CameraSettings.Shape, [("rounded", "Rounded"), ("circle", "Round")], "rounded"),
            CaptureUi.SegmentedRow("Size", null, CameraSettings.Size, [("small", "Small"), ("medium", "Medium"), ("large", "Large")], "medium"),
            Ui.ToggleRow("Close when clicking elsewhere", "Off: the mirror stays until you close it or press Esc.", CameraSettings.CloseOnClickAway, true));
        var privacy = Ui.Card(Ui.Stack(4,
            Ui.Row(8, Ui.Icon(Glyphs.Shield, 14), Ui.Text("Camera", 13.5, FontWeights.SemiBold)),
            Ui.Caption("Shows your camera only in the preview window, so you can check how you look before a call. Nothing is recorded or leaves your PC. Windows asks for camera access the first time.")));
        return Ui.Page("Camera preview", "Opens a floating mirror with your camera", main, privacy);
    }

    private static async Task FillAsync(ComboBox combo, Action done)
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            var current = Settings.GetString(CameraSettings.Device, string.Empty);
            foreach (var d in devices)
            {
                var item = new ComboBoxItem { Content = d.Name, Tag = d.Id };
                combo.Items.Add(item);
                if (d.Id == current) combo.SelectedItem = item;
            }
            if (devices.Count == 0) combo.Items.Add(new ComboBoxItem { Content = "No camera detected", IsEnabled = false });
        }
        catch (Exception ex)
        {
            Log.Warn($"Listing cameras failed: {ex.Message}");
        }
        finally
        {
            done();
        }
    }
}
