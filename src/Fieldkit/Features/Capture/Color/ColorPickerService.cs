// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Capture;

/// <summary>
/// Color picker: opens the shared selector on Color (loupe magnifier); a click copies the pixel in
/// the configured format and remembers it among the recent colours.
/// </summary>
public sealed class ColorPickerService : FeatureService
{
    private IDisposable? _hotkey;

    public override string FeatureId => "colorPicker";

    protected override void OnStart() =>
        _hotkey = Hotkeys.Bind(CaptureSettings.ColorShortcut, string.Empty, () => CaptureEntry.Start(CaptureTool.Color, fromShortcut: true));

    protected override void OnStop()
    {
        _hotkey?.Dispose();
        _hotkey = null;
        if (CaptureSelector.IsActive) CaptureSelector.CancelActive();
    }

    public static void Pick() => CaptureEntry.Start(CaptureTool.Color, fromShortcut: false);

    /// <summary>The string the configured format produces (what the loupe readout shows).</summary>
    public static string Formatted(byte r, byte g, byte b) =>
        ColorFormats.Format(r, g, b, ColorFormats.Parse(Settings.GetString(CaptureSettings.ColorFormatKey, "hex")),
            Settings.GetBool(CaptureSettings.ColorBareHex, false));

    /// <summary>Copies without a HUD (the selector shows its own confirmation in the loupe).</summary>
    public static string CopyQuietly(byte r, byte g, byte b)
    {
        var value = Formatted(r, g, b);
        CaptureClipboard.CopyText(value);
        Remember(r, g, b);
        return value;
    }

    public static void Receive(byte r, byte g, byte b)
    {
        if (!FeatureCatalog.IsAvailable("colorPicker")) return;
        var value = CopyQuietly(r, g, b);
        Hud.Show(Glyphs.Eyedropper, value, System.Windows.Media.Color.FromRgb(r, g, b));
    }

    public static IReadOnlyList<string> Recent => Settings.Get<List<string>>(CaptureSettings.ColorRecent, []);

    public static void ClearRecent() => Settings.Set(CaptureSettings.ColorRecent, new List<string>());

    private static void Remember(byte r, byte g, byte b) =>
        Settings.Set(CaptureSettings.ColorRecent, ColorFormats.PushRecent(Recent, $"#{r:X2}{g:X2}{b:X2}"));
}
