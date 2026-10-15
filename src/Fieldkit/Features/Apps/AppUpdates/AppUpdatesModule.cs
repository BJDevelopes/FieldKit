// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows.Threading;
using Microsoft.Win32;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Winget;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Apps.AppUpdates;

/// <summary>
/// App updates: every app with a newer version in one list (winget, including its Microsoft
/// Store source), updated together or one by one, with skip/exclude rules and an optional
/// daily or weekly background check that notifies.
/// </summary>
public sealed class AppUpdatesModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        var id = AppUpdatesController.FeatureId;
        r.Implements(id, Parity.Partial,
            "One list from winget upgrade (winget and Microsoft Store sources, optional unknown-version apps), Update N / per-app update " +
            "through winget, open app, skip version / don't check rules, daily or weekly background check with notification. " +
            "Microsoft Store apps winget does not know are not checked (the Store's own update API needs package identity); " +
            "there is no publisher-feed or online-catalog check beyond winget; no \"show in panel\" toggle.");
        r.Service(id, () => new AppUpdatesService());
        r.SettingsPage(new SettingsPageDef("appUpdates", SettingsCategory.Utilities, AppUpdateStrings.PageTitle, Glyphs.Download, 50,
            [id], AppUpdatesPage.Build, ["update", "upgrade", "winget", "store", "outdated", "version"]));
        r.Utility(new UtilityEntry(id, AppUpdateStrings.PageTitle, AppUpdateStrings.PanelCaption, Glyphs.Download,
            AppUpdatesWindow.Open, 50, AppUpdatesController.ShortcutKey));
        r.Action(new ToolAction("appUpdates.open", id, AppUpdateStrings.ShortcutRow, Glyphs.Download, AppUpdatesWindow.Open,
            AppUpdatesController.ShortcutKey));
        r.Action(new ToolAction("appUpdates.check", id, AppUpdateStrings.CheckAction, Glyphs.Refresh, () =>
        {
            AppUpdatesWindow.Open();
            AppUpdatesController.Shared.Check();
        }));
    }
}

/// <summary>
/// Runs while App updates is installed. Holds the optional shortcut and, only while a schedule is
/// chosen, a one-shot timer for the next background check (re-armed after each check and on
/// resume from sleep). Stopping closes the window and cancels this feature's scan and update.
/// </summary>
public sealed class AppUpdatesService : FeatureService
{
    private readonly List<IDisposable> _subscriptions = [];
    private DispatcherTimer? _timer;
    private bool _powerHooked;

    public override string FeatureId => AppUpdatesController.FeatureId;

    protected override void OnStart()
    {
        var controller = AppUpdatesController.Shared;
        _subscriptions.Add(Hotkeys.Bind(AppUpdatesController.ShortcutKey, string.Empty, AppUpdatesWindow.Open));
        _subscriptions.Add(Settings.Observe(Reschedule, AppUpdatesController.FrequencyKey, AppUpdatesController.LastCheckKey));
        controller.CheckFinished += OnCheckFinished;
        Reschedule();
    }

    protected override void OnStop()
    {
        AppUpdatesController.Shared.CheckFinished -= OnCheckFinished;
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        StopTimer();
        UnhookPower();
        AppUpdatesController.Shared.NextCheck = null;
        AppUpdatesWindow.CloseWindow();
        AppUpdatesController.Shared.CancelCheck();
        WingetOperationLane.Shared.CancelOwnedBy(AppUpdatesController.Owner);
    }

    private void OnCheckFinished(bool automatic) => Reschedule();

    private void Reschedule()
    {
        if (!IsRunning && _subscriptions.Count == 0) return;
        StopTimer();
        var controller = AppUpdatesController.Shared;
        var next = AppUpdateRules.NextCheckDate(controller.LastCheck, AppUpdatesController.Frequency, DateTime.Now);
        controller.NextCheck = next;
        if (next is null)
        {
            UnhookPower();
            return;
        }
        HookPower();
        var delay = next.Value - DateTime.Now;
        if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
        // DispatcherTimer intervals are capped near 24.8 days; a weekly check fits easily.
        if (delay > TimeSpan.FromDays(20)) delay = TimeSpan.FromDays(20);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
        _timer.Tick += (_, _) =>
        {
            StopTimer();
            if (DateTime.Now + TimeSpan.FromSeconds(5) < next.Value)
            {
                Reschedule();
                return;
            }
            AppUpdatesController.Shared.Check(automatic: true);
        };
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    // A check that came due during sleep never fires its timer; waking recomputes it.
    private void HookPower()
    {
        if (_powerHooked) return;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _powerHooked = true;
    }

    private void UnhookPower()
    {
        if (!_powerHooked) return;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _powerHooked = false;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Ui.OnUi(Reschedule);
    }
}
