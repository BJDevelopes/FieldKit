// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Energy;

public enum PowerTimerAction { Sleep, Hibernate, Shutdown, Restart }

/// <summary>Pure helpers for the sleep and shutdown timer (unit tested).</summary>
public static class PowerTimerLogic
{
    public static readonly int[] PresetMinutes = [15, 30, 60, 120];

    public static string Verb(PowerTimerAction a) => a switch
    {
        PowerTimerAction.Sleep => "Sleep",
        PowerTimerAction.Hibernate => "Hibernate",
        PowerTimerAction.Shutdown => "Shut down",
        _ => "Restart",
    };

    public static PowerTimerAction Parse(string? value) =>
        Enum.TryParse<PowerTimerAction>(value, true, out var a) ? a : PowerTimerAction.Sleep;

    /// <summary>"Shutting down in 1 h 05 min" style status.</summary>
    public static string Status(PowerTimerAction action, TimeSpan remaining)
    {
        var verb = action switch
        {
            PowerTimerAction.Sleep => "Sleeping",
            PowerTimerAction.Hibernate => "Hibernating",
            PowerTimerAction.Shutdown => "Shutting down",
            _ => "Restarting",
        };
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var text = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours} h {remaining.Minutes:00} min"
            : remaining.TotalMinutes >= 1 ? $"{(int)Math.Ceiling(remaining.TotalMinutes)} min" : $"{Math.Max(1, (int)remaining.TotalSeconds)} s";
        return $"{verb} in {text}";
    }

    /// <summary>The one-minute warning fires once, when less than a minute is left.</summary>
    public static bool ShouldWarn(TimeSpan remaining, bool warned) => !warned && remaining <= TimeSpan.FromMinutes(1);
}

/// <summary>
/// Sleep, hibernate, shut down or restart the PC after a delay (upstream #3044). Session only: the
/// timer does not survive quitting Fieldkit. Shutdown and restart are not forced, so apps with unsaved
/// work can still stop them, and a notification warns a minute before.
/// </summary>
public static class PowerTimer
{
    public const string ActionKey = "keepAwake.powerTimerAction";

    private static DispatcherTimer? _ticker;
    private static bool _warned;

    public static DateTime? Due { get; private set; }
    public static PowerTimerAction Action { get; private set; }
    public static event Action? Changed;

    public static bool IsRunning => Due is not null;

    public static void Start(PowerTimerAction action, TimeSpan delay)
    {
        Action = action;
        Due = DateTime.Now + delay;
        _warned = delay <= TimeSpan.FromMinutes(1);
        Settings.Set(ActionKey, action.ToString());
        _ticker ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick -= Tick;
        _ticker.Tick += Tick;
        _ticker.Start();
        TrayStatus.Set("powerTimer", new TrayBadge(PowerTimerLogic.Status(action, delay), 0x0A84FF, 5));
        Log.Info($"Power timer: {action} in {delay}");
        Changed?.Invoke();
    }

    public static void Cancel()
    {
        if (Due is null) return;
        Due = null;
        _ticker?.Stop();
        TrayStatus.Set("powerTimer", null);
        Log.Info("Power timer cancelled");
        Changed?.Invoke();
    }

    private static void Tick(object? sender, EventArgs e)
    {
        if (Due is not { } due) return;
        var remaining = due - DateTime.Now;
        TrayStatus.Set("powerTimer", new TrayBadge(PowerTimerLogic.Status(Action, remaining), 0x0A84FF, 5));
        if (PowerTimerLogic.ShouldWarn(remaining, _warned))
        {
            _warned = true;
            Notifier.Show($"{PowerTimerLogic.Verb(Action)} in 1 minute",
                "Open Fieldkit's Keep awake tab to cancel.", warning: true);
        }
        Changed?.Invoke();
        if (remaining > TimeSpan.Zero) return;
        var action = Action;
        Cancel();
        Execute(action);
    }

    private static void Execute(PowerTimerAction action)
    {
        Log.Info($"Power timer: {action} now");
        Settings.Flush();
        EnergyNative.EnableShutdownPrivilege();
        switch (action)
        {
            case PowerTimerAction.Sleep:
                EnergyNative.SetSuspendState(false, false, false);
                break;
            case PowerTimerAction.Hibernate:
                EnergyNative.SetSuspendState(true, false, false);
                break;
            case PowerTimerAction.Shutdown:
                ExitWindowsEx(EWX_SHUTDOWN | EWX_POWEROFF, ReasonPlanned);
                break;
            case PowerTimerAction.Restart:
                ExitWindowsEx(EWX_REBOOT, ReasonPlanned);
                break;
        }
    }

    private const uint EWX_SHUTDOWN = 0x1, EWX_REBOOT = 0x2, EWX_POWEROFF = 0x8;
    private const uint ReasonPlanned = 0x80000000; // SHTDN_REASON_FLAG_PLANNED

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    // ------------------------------------------------------------------ panel block

    /// <summary>The timer block under Keep awake in the panel.</summary>
    public static FrameworkElement View()
    {
        var host = new ContentControl();
        void Render()
        {
            if (Due is { } due)
            {
                var status = Ui.Text(PowerTimerLogic.Status(Action, due - DateTime.Now), 13.5, FontWeights.Medium);
                host.Content = Ui.Split(Ui.Row(8, Ui.Icon(Glyphs.Clock, 15), status), Ui.Button("Cancel", Cancel));
                return;
            }
            var choice = PowerTimerLogic.Parse(Settings.GetString(ActionKey, nameof(PowerTimerAction.Sleep)));
            var combo = Ui.Choice(ActionKey,
                [("Sleep", "Sleep"), ("Hibernate", "Hibernate"), ("Shutdown", "Shut down"), ("Restart", "Restart")], choice.ToString());
            combo.MinWidth = 120;
            var chips = new WrapPanel();
            foreach (var minutes in PowerTimerLogic.PresetMinutes)
            {
                var label = minutes >= 60 ? $"{minutes / 60} h" : $"{minutes}m";
                var button = Ui.Button(label, () => Start(PowerTimerLogic.Parse(Settings.GetString(ActionKey, "Sleep")), TimeSpan.FromMinutes(minutes)));
                button.Margin = new Thickness(0, 0, 6, 6);
                button.Padding = new Thickness(10, 4, 10, 4);
                chips.Children.Add(button);
            }
            host.Content = Ui.Stack(8, Ui.Split(Ui.Row(8, Ui.Icon(Glyphs.Power, 15), Ui.Text("Timer", 13.5, FontWeights.Medium)), combo), chips);
        }
        Render();
        Ui.Track(host, () =>
        {
            void OnChanged() => Ui.OnUi(Render);
            Changed += OnChanged;
            return new ActionDisposable(() => Changed -= OnChanged);
        });
        return Ui.Card(host, new Thickness(14, 12, 14, 8));
    }
}
