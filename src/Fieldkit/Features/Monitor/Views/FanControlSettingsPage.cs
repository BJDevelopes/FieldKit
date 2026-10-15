// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Fieldkit.Core;
using Fieldkit.Features.Monitor.Fans;
using Fieldkit.UI;

namespace Fieldkit.Features.Monitor.Views;

/// <summary>Settings › Fan Control: the opt-in it needs, panel visibility, resume and safety notes.</summary>
internal static class FanControlSettingsPage
{
    public static FrameworkElement Build()
    {
        var title = Ui.Row(8, Ui.Text("Fan Control", 13.5, FontWeights.Medium, wrap: false), Ui.Badge("Beta"));
        var state = Ui.Text(string.Empty, 12.5, brush: "TextSecondary");
        var returnButton = Ui.Button("Use system control", () => FanController.ReturnToSystem());
        returnButton.HorizontalAlignment = HorizontalAlignment.Left;
        void Sync()
        {
            state.Text = FanController.IsControlling
                ? $"Controlling the fans ({(FanController.AppliedMode == Logic.FanMode.Manual ? "manual" : "curve")}). Use the panel to change the level or curve."
                : FanController.Message ?? "The fans follow the system (BIOS/firmware) control.";
            returnButton.IsEnabled = FanController.IsControlling;
        }
        Sync();
        Ui.Track(state, () =>
        {
            FanController.Changed += Sync;
            return new ActionDisposable(() => FanController.Changed -= Sync);
        });

        return Ui.Page("Fan Control",
            "Control fans manually or with temperature curves while seeing live RPM. Speeds are set through LibreHardwareMonitor on boards whose fan controller it supports.",
            Ui.Group(null,
                Ui.Split(Ui.Stack(2, title, Ui.Caption("Adds manual fan speeds and temperature curves to the tray panel.")),
                    Ui.Toggle("panel.show.fanControl", true)),
                Ui.Caption("Beta. You may run into some bugs.")),
            MonitorSettingsPage.SensorsCard(),
            Ui.Group("Control",
                state,
                Ui.ToggleRow("Resume after restart or sleep",
                    "Off: the fans go back to system control when Fieldkit starts or the PC wakes, until you apply again.",
                    MonitorKeys.FanResume, false),
                returnButton),
            Ui.Group("Safety",
                Ui.Caption(FanControlView.SafetyCaption),
                Ui.Caption("Fieldkit restores automatic control when it quits normally. If it is ended forcibly, the board keeps the last speed until Fieldkit runs again, the PC restarts or you change it in the firmware.")));
    }
}
