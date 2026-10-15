// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.Features.Island.Agents;
using Fieldkit.Features.Island.Views;
using Fieldkit.UI;

namespace Fieldkit.Features.Island.Prefs;

/// <summary>Settings → AI agents (port of upstream <c>NotchAgentsSettings</c>).</summary>
public static class AgentsSettingsPage
{
    public static FrameworkElement Build()
    {
        return Ui.Page("AI agents",
            "Reads the usage Claude Code, Codex, OpenCode and GitHub Copilot record on this PC and the plan limits the Claude app saves. " +
            "Prompts, replies and files are never kept, and your usage never leaves your PC.",
            Ui.Group(null,
                Ui.ToggleRow("Show AI agents", "Follow plan limits, tokens, API value and the work in progress in the Dynamic Island.", IslandKeys.AgentsEnabled, false)),
            Providers(),
            Cards(),
            Ui.Group("Limits",
                Ui.ChoiceRow("Show limits as", IslandKeys.AgentsLimitDisplay, [("remaining", "Left"), ("used", "Used")], "remaining"),
                Ui.ChoiceRow("Limit to show", IslandKeys.AgentsLimitFocus, [("mostUsed", "Most used"), ("session", "Session"), ("weekly", "Week")], "mostUsed")),
            WhileWorking(),
            Alerts(),
            ClaudeLimits());
    }

    private static FrameworkElement Providers()
    {
        var rows = AgentProviders.All.Select(p =>
        {
            var mark = Ui.Icon(AgentGlyphs.For(p), 16, AgentGlyphs.TintKey(p));
            mark.Width = 22;
            var found = AgentUsageService.Found(p);
            var text = Ui.Stack(1, Ui.Text(p.DisplayName(), 13.5, FontWeights.Medium), Ui.Caption(found ? "Found on this PC" : "Not found on this PC", 12));
            var toggle = Ui.Toggle(IslandKeys.AgentsProvider(p), true, on =>
            {
                // At least one must stay on.
                if (!on && AgentProviders.All.All(x => !Settings.GetBool(IslandKeys.AgentsProvider(x), true))) Settings.Set(IslandKeys.AgentsProvider(p), true);
            });
            return (UIElement)Ui.Split(Ui.Row(10, mark, text), toggle);
        }).ToArray();
        return Ui.Group("Agents", rows);
    }

    private static FrameworkElement Cards()
    {
        var list = new StackPanel();
        void Rebuild()
        {
            list.Children.Clear();
            var order = IslandLogic.Order(Settings.GetString(IslandKeys.AgentsCardOrder, string.Empty), AgentsPage.Cards.Select(c => c.Id));
            var hidden = IslandLogic.Csv(Settings.GetString(IslandKeys.AgentsHiddenCards, string.Empty)).ToHashSet();
            for (var i = 0; i < order.Count; i++)
            {
                var id = order[i];
                var index = i;
                var title = AgentsPage.Cards.First(c => c.Id == id).Title;
                var check = new CheckBox { Content = Ui.Text(title, 13), IsChecked = !hidden.Contains(id) };
                check.Click += (_, _) =>
                {
                    var h = IslandLogic.Csv(Settings.GetString(IslandKeys.AgentsHiddenCards, string.Empty)).ToHashSet();
                    if (check.IsChecked == true) h.Remove(id); else h.Add(id);
                    Settings.Set(IslandKeys.AgentsHiddenCards, string.Join(",", h.OrderBy(x => x)));
                };
                var up = Ui.IconButton(Glyphs.ChevronUp, "Move up", () => Move(order, index, -1));
                var down = Ui.IconButton(Glyphs.ChevronDown, "Move down", () => Move(order, index, 1));
                up.IsEnabled = index > 0;
                down.IsEnabled = index < order.Count - 1;
                var row = Ui.Split(check, Ui.Row(2, up, down));
                row.Margin = new Thickness(0, i == 0 ? 0 : 4, 0, 0);
                list.Children.Add(row);
            }
        }
        Rebuild();
        Ui.Track(list, () => Settings.Observe(Rebuild, IslandKeys.AgentsCardOrder, IslandKeys.AgentsHiddenCards));
        return Ui.Group("Cards", Ui.Caption("Use the arrows to reorder. Charts take the full width of the island."), list);
    }

    private static void Move(List<string> order, int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= order.Count) return;
        (order[index], order[target]) = (order[target], order[index]);
        Settings.Set(IslandKeys.AgentsCardOrder, string.Join(",", order));
    }

    private static FrameworkElement WhileWorking() => Ui.Group("While an agent works",
        Ui.ToggleRow("Show it in the closed Dynamic Island", null, IslandKeys.AgentsLiveActivity, true),
        Ui.ChoiceRow("Beside the camera", IslandKeys.AgentsReadout, [("elapsed", "Time"), ("tokens", "Tokens written"), ("cost", "API value"), ("limit", "Limit")], "elapsed",
            "Shown at the right of the closed island while a task runs, for example 12:34, 20K or $4.56."));

    private static FrameworkElement Alerts()
    {
        var finishMinimum = Ui.ChoiceRow("For tasks longer than", "notchAgents.finishMinimumUi",
            [("0", "Any length"), ("30", "30 s"), ("60", "1 min"), ("120", "2 min"), ("300", "5 min")],
            Settings.GetDouble(IslandKeys.AgentsFinishMinimum, IslandDefaults.AgentsFinishMinimum).ToString("0"),
            onChanged: v => Settings.Set(IslandKeys.AgentsFinishMinimum, double.Parse(v)));
        var threshold = Ui.ChoiceRow("Warn at", "notchAgents.limitThresholdUi",
            [("50", "50% used"), ("75", "75% used"), ("80", "80% used"), ("90", "90% used"), ("95", "95% used")],
            Settings.GetDouble(IslandKeys.AgentsLimitThreshold, IslandDefaults.AgentsLimitThreshold).ToString("0"),
            onChanged: v => Settings.Set(IslandKeys.AgentsLimitThreshold, double.Parse(v)));
        var budget = Ui.ChoiceRow("Daily API value budget", "notchAgents.dailyBudgetUi",
            [("0", "Off"), ("5", "$5.00"), ("10", "$10.00"), ("25", "$25.00"), ("50", "$50.00"), ("100", "$100"), ("250", "$250")],
            Settings.GetDouble(IslandKeys.AgentsDailyBudget, 0).ToString("0"),
            onChanged: v => Settings.Set(IslandKeys.AgentsDailyBudget, double.Parse(v)));
        return Ui.Group("Alerts",
            Ui.ToggleRow("When a task finishes", null, IslandKeys.AgentsFinishAlert, true),
            finishMinimum,
            Ui.ToggleRow("Near a plan limit", null, IslandKeys.AgentsLimitAlert, true),
            threshold,
            budget,
            Ui.Caption("API value is what the same work would cost at API list prices. Plans charge a fixed price instead. " +
                       $"Price list of {AgentPricing.List.Updated:MMM d, yyyy}, bundled with Fieldkit."));
    }

    private static FrameworkElement ClaudeLimits()
    {
        var file = ClaudeAccount.LimitFiles().FirstOrDefault(File.Exists);
        UIElement status;
        if (file is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromMinutes(30))
            status = Ui.Row(8, Ui.Icon(Glyphs.Check, 14, "Green"),
                Ui.Caption("Read from the Claude app, updated " + AgentFormat.Relative(File.GetLastWriteTimeUtc(file), DateTimeOffset.UtcNow)));
        else if (file is null && !ClaudeAccount.AppInstalled())
            status = Ui.Row(8, Ui.Icon(Glyphs.Info, 14, "TextSecondary"),
                Ui.Caption("Plan limits come from the Claude app, which is not on this PC. Until then, the 5-hour session is estimated from Claude Code activity."));
        else
            status = Ui.Row(8, Ui.Icon(Glyphs.Warning, 14, "Orange"),
                Ui.Caption((file is not null ? "The Claude app last checked them " + AgentFormat.Relative(File.GetLastWriteTimeUtc(file), DateTimeOffset.UtcNow) + ". " : string.Empty) +
                           "Claude checks your limits only while it runs. Open the Claude app, and your limits appear here within minutes."));
        if (status is StackPanel sp) foreach (var c in sp.Children.OfType<TextBlock>()) c.TextWrapping = TextWrapping.Wrap;
        return Ui.Group("Claude plan limits", status,
            Ui.Caption("Fieldkit reads the percentages the Claude app saves on this PC. No sign-in is used and nothing is sent."));
    }
}
