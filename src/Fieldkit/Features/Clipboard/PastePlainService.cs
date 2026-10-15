// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>
/// Paste as plain text on a global shortcut: puts only the clipboard's plain text there, pastes,
/// and quietly restores the original rich content so later pastes keep their formatting
/// (upstream PastePlainService + TransientPaste).
/// </summary>
public sealed class PastePlainService : FeatureService
{
    public const string Id = "pastePlain";
    public const string EnabledKey = "pastePlain.enabled";
    public const string ShortcutKey = "pastePlain.shortcut";
    public const string DefaultShortcut = "Ctrl+Alt+Shift+V";

    private IDisposable? _hotkey;

    public override string FeatureId => Id;

    public override IReadOnlyList<string> EnableKeys => [EnabledKey];

    public override bool EnabledByDefault => false;

    protected override void OnStart() => Bind();

    protected override void OnStop()
    {
        _hotkey?.Dispose();
        _hotkey = null;
    }

    private void Bind() => _hotkey = Hotkeys.Bind(ShortcutKey, DefaultShortcut, Perform);

    /// <summary>Pastes the clipboard's plain text into the focused app (also used by the tool action).</summary>
    public void Perform()
    {
        ClipboardLane.Run(access => access.IsPrivate() ? null : access.ReadText()).ContinueWith(task =>
        {
            var plain = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            UI.Ui.OnUi(() => PastePlain(string.IsNullOrEmpty(plain) ? null : plain));
        }, TaskScheduler.Default);
    }

    private void PastePlain(string? plain)
    {
        // A shortcut that is the standard paste would catch our own Ctrl+V: step aside while it is sent.
        var standard = Shortcut.FromSetting(ShortcutKey, DefaultShortcut) == new Shortcut(Modifiers.Ctrl, 0x56);
        if (standard)
        {
            _hotkey?.Dispose();
            _hotkey = null;
        }
        void Rebind()
        {
            if (standard && IsRunning && _hotkey is null) Bind();
        }
        if (plain is null)
        {
            // Nothing textual (a picture, files): forward an ordinary paste untouched.
            _ = PasteHelper.SendWhenReleasedAsync(() => InputSender.Chord(0x11, 0x56), Rebind);
            return;
        }
        if (!TransientPaste.Paste(plain, afterPaste: Rebind, onFail: Rebind)) Rebind();
    }
}
