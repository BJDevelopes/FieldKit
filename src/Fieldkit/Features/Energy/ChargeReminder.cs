// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Energy;

/// <summary>Pure rule for the high-charge reminder (unit tested).</summary>
public static class ChargeReminderLogic
{
    /// <summary>
    /// Whether to remind now, and the new "already reminded" flag. It reminds once per charge: when plugged in
    /// and at or above the limit. Unplugging, or the charge dropping 5 points below the limit, re-arms it.
    /// </summary>
    public static (bool Remind, bool Reminded) Evaluate(bool pluggedIn, int percent, int limit, bool reminded)
    {
        if (!pluggedIn || percent < limit - 5) return (false, false);
        if (reminded || percent < limit) return (false, reminded);
        return (true, true);
    }
}

/// <summary>
/// "Unplug at 80 %" (upstream PR #2534): keeping a laptop battery full while plugged in ages it faster, so a
/// notification suggests unplugging once the chosen charge is reached. Part of the Power monitor.
/// </summary>
public sealed class ChargeReminderService : FeatureService
{
    public const string EnabledKey = "monitorPower.highChargeReminder";
    public const string LimitKey = "monitorPower.highChargeLimit";

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
    private bool _reminded;

    public override string FeatureId => "monitorPower";
    public override IReadOnlyList<string> EnableKeys => [EnabledKey];
    public override bool EnabledByDefault => false;

    public ChargeReminderService() => _timer.Tick += (_, _) => Ui.Guard(Check);

    protected override void OnStart()
    {
        _reminded = false;
        _timer.Start();
        Check();
    }

    protected override void OnStop() => _timer.Stop();

    private void Check()
    {
        if (!Native.GetSystemPowerStatus(out var status) || status.BatteryFlag == 128 || status.BatteryLifePercent > 100) return; // no battery
        var limit = Math.Clamp(Settings.GetInt(LimitKey, 80), 50, 100);
        (var remind, _reminded) = ChargeReminderLogic.Evaluate(status.ACLineStatus == 1, status.BatteryLifePercent, limit, _reminded);
        if (remind)
            Notifier.Show($"Battery at {status.BatteryLifePercent}%", "You can unplug the charger to help the battery last longer.");
    }

    /// <summary>Settings rows (Energy page).</summary>
    public static FrameworkElement SettingsGroup() => Ui.Group("Battery",
        Ui.ToggleRow("Remind me to unplug", "Keeping a battery full while plugged in ages it faster. A notification appears once per charge.",
            EnabledKey, false),
        Ui.SliderRow("Remind at", LimitKey, 50, 100, 80, v => $"{v:0}%", 5));
}
