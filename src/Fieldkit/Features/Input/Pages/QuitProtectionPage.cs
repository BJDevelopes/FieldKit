// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using Fieldkit.Core;
using Fieldkit.Features.Input.Common;
using Fieldkit.Features.Input.QuitProtection;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Input.Pages;

/// <summary>Settings → Quit protection (upstream QuitProtectionSettings): one section per protected action.</summary>
internal static class QuitProtectionPage
{
    public static FrameworkElement Build() =>
        Ui.Page("Quit protection",
            "Configure each shortcut independently. The original action passes only after the selected confirmation.",
            InputUi.InfoLine(Glyphs.Shield, "Protection watches only Alt+F4, Ctrl+Q, Ctrl+W and Ctrl+F4 through the keyboard hook."),
            Section(ProtectedAction.Quit),
            Section(ProtectedAction.Close));

    private static FrameworkElement Section(ProtectedAction action)
    {
        var p = QuitProtectionMath.Prefix(action);
        return InputUi.Live(() =>
        {
            var enabled = Settings.GetBool($"{p}.enabled");
            var mode = QuitProtectionMath.ParseMode(Settings.GetString($"{p}.mode", "hold"));
            var scope = QuitProtectionMath.ParseScope(Settings.GetString($"{p}.scope", "all"));
            var combos = QuitProtectionMath.Combos.Where(c => c.Action == action).ToList();

            UIElement modeDetail = mode switch
            {
                ConfirmationMode.Hold => InputUi.SliderLine("Hold duration", $"{p}.holdMs", QuitProtectionMath.HoldMinMs,
                    QuitProtectionMath.HoldMaxMs, QuitProtectionMath.DefaultHoldMs, 50, v => $"{v:0} ms"),
                ConfirmationMode.DoublePress => InputUi.SliderLine("Double press interval", $"{p}.doubleMs", QuitProtectionMath.DoubleMinMs,
                    QuitProtectionMath.DoubleMaxMs, QuitProtectionMath.DefaultDoubleMs, 50, v => $"{v:0} ms"),
                _ => Ui.Stack(4,
                    Ui.ChoiceRow("Extra modifier", $"{p}.extraModifier", [("shift", "Shift"), ("alt", "Alt"), ("ctrl", "Ctrl")], "shift"),
                    Ui.Caption(string.Join("   ", combos.Select(c =>
                        new Shortcut(c.Base | QuitProtectionMath.EffectiveExtra(c,
                            QuitProtectionMath.ParseExtraModifier(Settings.GetString($"{p}.extraModifier", "shift"))), c.Key).ToString())), 12)),
            };

            var card = InputUi.Card(QuitProtectionMath.Title(action),
                Ui.Stack(4,
                    Ui.Split(Ui.Text("Protect this shortcut", 13.5, FontWeights.Medium), Ui.Toggle($"{p}.enabled", false)),
                    Ui.Caption("Other shortcuts continue to work normally.", 12)),
                Ui.ChoiceRow("Confirmation mode", $"{p}.mode",
                    [("hold", "Hold to confirm"), ("doublePress", "Double press"), ("extraModifier", "Require extra modifier")], "hold"),
                modeDetail,
                Ui.ChoiceRow("Applications", $"{p}.scope",
                    [("all", "All applications"), ("selectedOnly", "Selected applications only"), ("allExceptSelected", "All except selected applications")], "all"),
                scope == AppScope.All ? null : InputUi.ExceptionList(p, string.Empty, "Selected applications", "Add application…", "No applications selected"),
                Ui.Split(Ui.Text("Show visual feedback", 13.5), Ui.Toggle($"{p}.feedback", true)));
            return card;
        }, $"{p}.enabled", $"{p}.mode", $"{p}.scope", $"{p}.extraModifier");
    }
}
