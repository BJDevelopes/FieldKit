// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.Capture;

/// <summary>
/// Screenshot feature service: its shortcuts (selector, whole screen, latest capture, clipboard image,
/// recent captures). Uninstalling closes every screenshot window and frees the shortcuts.
/// </summary>
public sealed class ScreenshotService : FeatureService
{
    private readonly List<IDisposable> _hotkeys = [];

    public override string FeatureId => "screenshot";

    protected override void OnStart()
    {
        _hotkeys.Add(Hotkeys.Bind(CaptureSettings.ScreenshotShortcut, string.Empty, () => CaptureEntry.Start(CaptureTool.Screenshot, fromShortcut: true)));
        _hotkeys.Add(Hotkeys.Bind(CaptureSettings.FullScreenShortcut, string.Empty, CaptureEntry.CaptureFullScreen));
        _hotkeys.Add(Hotkeys.Bind(CaptureSettings.EditLastShortcut, string.Empty, ScreenshotFlow.EditLatest));
        _hotkeys.Add(Hotkeys.Bind(CaptureSettings.ClipboardShortcut, string.Empty, ScreenshotFlow.EditClipboard));
        _hotkeys.Add(Hotkeys.Bind(CaptureSettings.RecentShortcut, string.Empty, RecentCapturesWindow.Open));
    }

    protected override void OnStop()
    {
        foreach (var h in _hotkeys) h.Dispose();
        _hotkeys.Clear();
        CaptureEntry.CancelCountdown();
        if (CaptureSelector.IsActive) CaptureSelector.CancelActive();
        ScrollingCapture.Cancel();
        QuickPreviewWindow.CloseCurrent();
        PinWindow.CloseAll();
        ScreenshotEditorWindow.CloseAll();
        RecentCapturesWindow.CloseCurrent();
    }
}
