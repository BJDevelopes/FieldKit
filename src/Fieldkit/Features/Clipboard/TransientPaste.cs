// SPDX-License-Identifier: GPL-3.0-or-later
using System.Media;
using Fieldkit.Core;
using Fieldkit.Interop;

namespace Fieldkit.Features.ClipboardTools;

/// <summary>
/// Puts text on the clipboard just long enough to paste it, then restores what was there before,
/// unless the user copied something else meanwhile (upstream TransientPaste). The temporary text is
/// marked so that Windows' clipboard history and other managers skip it.
/// </summary>
public static class TransientPaste
{
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(500);
    private static int _busy;

    /// <summary>Pastes <paramref name="text"/> into the focused app.</summary>
    /// <param name="beforePaste">Runs on the UI thread right before Ctrl+V (e.g. Backspaces for a snippet).</param>
    /// <param name="afterPaste">Runs on the UI thread right after Ctrl+V.</param>
    /// <param name="onFail">Runs on the UI thread when nothing could be pasted.</param>
    public static bool Paste(string text, Action? beforePaste = null, Action? afterPaste = null, Action? onFail = null)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return false;
        _ = PasteAsync(text, beforePaste, afterPaste, onFail);
        return true;
    }

    private static async Task PasteAsync(string text, Action? beforePaste, Action? afterPaste, Action? onFail)
    {
        try
        {
            var prepared = await ClipboardLane.Run(access =>
            {
                var snapshot = access.Snapshot();
                if (snapshot is null) return ((ClipboardSnapshot?)null, 0u);
                return access.WriteText(text, OwnWriteKind.Transient, excludeFromHistory: true)
                    ? (snapshot, access.Sequence)
                    : (null, 0u);
            });
            if (prepared.Item1 is not ClipboardSnapshot snapshot)
            {
                UI.Ui.OnUi(() => onFail?.Invoke());
                return;
            }
            var sent = new TaskCompletionSource();
            await PasteHelper.SendWhenReleasedAsync(() =>
            {
                beforePaste?.Invoke();
                InputSender.Chord(0x11, 0x56);
                afterPaste?.Invoke();
            }, () => sent.TrySetResult());
            await Task.WhenAny(sent.Task, Task.Delay(3000));
            await Task.Delay(RestoreDelay);
            await ClipboardLane.Run(access =>
            {
                // Something else was copied since: leave it.
                if (access.Sequence != prepared.Item2) return;
                if (snapshot.IsEmpty) access.Clear();
                else access.Restore(snapshot);
            });
        }
        catch (Exception ex)
        {
            Log.Error("Transient paste failed", ex);
            SystemSounds.Beep.Play();
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }
}
