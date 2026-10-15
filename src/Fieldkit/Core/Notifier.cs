// SPDX-License-Identifier: GPL-3.0-or-later
namespace Fieldkit.Core;

/// <summary>
/// User notifications (alerts, finished tasks). Shown through the tray icon, which
/// Windows 10/11 renders as a toast. Safe to call from any thread.
/// </summary>
public static class Notifier
{
    internal static Action<string, string, bool>? Sink;

    public static void Show(string title, string message, bool warning = false)
    {
        if (!Settings.GetBool("general.notifications", true)) return;
        Log.Info($"Notify: {title}"); // the detail can name files, devices or projects; keep it out of the log
        UI.Ui.OnUi(() => Sink?.Invoke(title, message, warning));
    }
}
