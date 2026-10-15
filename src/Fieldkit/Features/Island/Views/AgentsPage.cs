// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.Features.Island.Agents;
using Fieldkit.UI;

namespace Fieldkit.Features.Island.Views;

/// <summary>
/// The island's AI Agents page (port of upstream <c>NotchAgentsView</c>): Limits, Spending, Now, Trend, Models,
/// Projects and Activity cards in the order chosen in Settings, two per row with charts across the full width.
/// </summary>
public static class AgentsPage
{
    public static readonly (string Id, string Title)[] Cards =
    [
        ("limits", "Limits"), ("spend", "Spending"), ("live", "Now"), ("trend", "Trend"),
        ("models", "Models"), ("projects", "Projects"), ("activity", "Activity"),
    ];

    public static List<string> CardOrder() =>
        IslandLogic.Order(Settings.GetString(IslandKeys.AgentsCardOrder, string.Empty), Cards.Select(c => c.Id))
            .Where(id => !IslandLogic.Csv(Settings.GetString(IslandKeys.AgentsHiddenCards, string.Empty)).Contains(id))
            .ToList();

    public static FrameworkElement Build(IslandContext ctx, Views.IslandNav nav)
    {
        var host = new ContentControl();
        AgentUsageSnapshot? shown = null;
        IslandUi.Live(host, TimeSpan.FromSeconds(15), Refresh);
        if (!ctx.IsPreview) Ui.Track(host, () => { IslandHub.Changed += OnChange; return new ActionDisposable(() => IslandHub.Changed -= OnChange); });
        return host;

        void OnChange()
        {
            if (!ReferenceEquals(ctx.Agents(), shown)) Refresh();
        }

        void Refresh()
        {
            var snap = ctx.Agents();
            shown = snap;
            host.Content = Content(ctx, nav, snap);
        }
    }

    private static FrameworkElement Content(IslandContext ctx, Views.IslandNav nav, AgentUsageSnapshot? snap)
    {
        if (!ctx.AgentsOn())
            return Ui.Stack(8, IslandUi.Text("Turn on AI agents in Settings to follow Claude Code, Codex, OpenCode and GitHub Copilot here.", 13, brush: IslandPalette.Secondary, wrap: true),
                IslandUi.Pill("Open Settings", () => { nav.Collapse(); PanelHost.OpenSettings("agents"); }));
        if (snap is null) return IslandUi.Text("Reading usage…", 13, brush: IslandPalette.Secondary);
        if (snap.Seen.Count == 0)
            return IslandUi.Text("No usage from Claude Code, Codex, OpenCode or GitHub Copilot yet. It appears here as soon as any of them works on this PC.", 13, brush: IslandPalette.Secondary, wrap: true);
        var order = CardOrder();
        if (order.Count == 0) return IslandUi.Text("Choose what this page shows in Dynamic Island settings.", 13, brush: IslandPalette.Secondary, wrap: true);

        var cards = new List<(FrameworkElement Card, bool Full)>();
        foreach (var id in order)
        {
            switch (id)
            {
                case "limits":
                    foreach (var p in AgentProviders.All.Where(p => snap.Seen.Contains(p) && p.ReportsLimits()))
                        cards.Add((LimitsCard(snap, p), false));
                    break;
                case "spend": cards.Add((SpendCard(snap), false)); break;
                case "live": cards.Add((LiveCard(snap, ctx), false)); break;
                case "trend": cards.Add((TrendCard(snap), true)); break;
                case "models": cards.Add((ShareCard("Models", snap, snap.Models), false)); break;
                case "projects": cards.Add((ShareCard("Projects", snap, snap.Projects), false)); break;
                case "activity": cards.Add((ActivityCard(snap), true)); break;
            }
        }
        var stack = new StackPanel();
        FrameworkElement? pending = null;
        void Flush()
        {
            if (pending is null) return;
            var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pending.Margin = new Thickness(0, 0, 5, 0);
            g.Children.Add(pending);
            pending = null;
            stack.Children.Add(g);
        }
        foreach (var (card, full) in cards)
        {
            card.MinHeight = full ? 118 : 96;
            if (full)
            {
                Flush();
                card.Margin = new Thickness(0, 0, 0, 10);
                stack.Children.Add(card);
                continue;
            }
            if (pending is null)
            {
                pending = card;
                continue;
            }
            var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pending.Margin = new Thickness(0, 0, 5, 0);
            card.Margin = new Thickness(5, 0, 0, 0);
            Grid.SetColumn(card, 1);
            g.Children.Add(pending);
            g.Children.Add(card);
            stack.Children.Add(g);
            pending = null;
        }
        Flush();
        return new ScrollViewer { Content = stack, MaxHeight = Math.Max(nav.Budget, 220), VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
    }

    private static Border Card(params UIElement?[] rows) => IslandUi.Card(Ui.Stack(6, rows), new Thickness(12, 10, 12, 10));

    private static FrameworkElement Mark(AgentProvider p, double size = 12) => IslandUi.Icon(AgentGlyphs.For(p), size, IslandCollapsed.ProviderBrush(p));

    private static FrameworkElement LimitsCard(AgentUsageSnapshot snap, AgentProvider p)
    {
        var plan = snap.Plans.TryGetValue(p, out var pl) ? new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 2), Background = IslandPalette.Card,
            Child = IslandUi.Text(pl.Name, 10, FontWeights.SemiBold, IslandPalette.Secondary),
        } : null;
        var working = snap.Live.Any(l => l.Provider == p)
            ? new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = IslandCollapsed.ProviderBrush(p) }
            : null;
        var header = IslandUi.Row(6, Mark(p), IslandUi.Text(p.DisplayName(), 12.5, FontWeights.SemiBold), plan, working);
        var rows = new List<UIElement?> { header };
        var used = Settings.GetString(IslandKeys.AgentsLimitDisplay, "remaining") == "used";
        if (snap.Limits.TryGetValue(p, out var limits))
        {
            foreach (var w in AgentSummary.CardRows(limits, snap.Now))
            {
                var tint = w.UsedPercent >= 95 ? IslandPalette.Red : w.UsedPercent >= 80 ? IslandPalette.Orange : IslandCollapsed.ProviderBrush(p);
                var reset = w.ResetsAt is { } r ? (w.EstimatedResetsAt is not null ? "≈ " : "") + "↻ " + AgentFormat.Countdown(w.EstimatedResetsAt ?? r, snap.Now) : string.Empty;
                var label = Ui.Split(IslandUi.Text(w.Label, 11.5, FontWeights.Medium, IslandPalette.Secondary),
                    IslandUi.Row(8, IslandUi.Text(reset, 10.5, brush: IslandPalette.Tertiary),
                        IslandUi.Text(AgentFormat.Percent(used ? w.UsedFraction : w.RemainingFraction) + (used ? " used" : " left"), 11.5, FontWeights.SemiBold, tint, mono: true)));
                var meter = IslandUi.Meter(w.UsedFraction, 4, tint);
                meter.ToolTip = $"{w.Label} · {AgentFormat.Percent(w.UsedFraction)} used · {AgentFormat.Percent(w.RemainingFraction)} left" +
                                (w.ResetsAt is { } at ? " · " + at.ToLocalTime().ToString("g") : string.Empty) +
                                (w.EstimatedResetsAt is not null ? " · Estimated from this PC" : string.Empty);
                rows.Add(Ui.Stack(3, label, meter));
            }
            if (snap.Now - limits.ObservedAt > TimeSpan.FromMinutes(10))
                rows.Add(IslandUi.Text("Updated " + AgentFormat.Relative(limits.ObservedAt, snap.Now), 10, brush: IslandPalette.Tertiary));
        }
        else if (p == AgentProvider.Claude && snap.ClaudeBlock is { } block)
        {
            var elapsed = (snap.Now - block.Start).TotalSeconds / (5 * 3600);
            rows.Add(Ui.Split(IslandUi.Text("Session", 11.5, FontWeights.Medium, IslandPalette.Secondary),
                IslandUi.Text("↻ " + AgentFormat.Countdown(block.End, snap.Now) + " · " + AgentFormat.Cost(block.Totals.Cost), 11, FontWeights.SemiBold, mono: true)));
            var meter = IslandUi.Meter(elapsed, 4, IslandPalette.Tertiary);
            meter.ToolTip = "Estimated from this PC";
            rows.Add(meter);
        }
        else if (p == AgentProvider.Codex)
        {
            rows.Add(IslandUi.Text("Limits appear after the next reply", 11.5, brush: IslandPalette.Secondary, wrap: true));
        }
        else if (p == AgentProvider.OpenCode && snap.ByProvider[AgentPeriod.Today].TryGetValue(p, out var today))
        {
            rows.Add(IslandUi.Text("Today", 11.5, FontWeights.Medium, IslandPalette.Secondary));
            rows.Add(IslandUi.Text((today.FullyPriced ? "" : "≥ ") + AgentFormat.Cost(today.Cost) + " · " + AgentFormat.Tokens(today.Tokens.Total) + " tokens", 12, FontWeights.SemiBold));
        }
        else
        {
            rows.Add(IslandUi.Text("No session running", 11.5, brush: IslandPalette.Secondary));
        }
        return Card(rows.ToArray());
    }

    private static FrameworkElement SpendCard(AgentUsageSnapshot snap)
    {
        var period = Settings.GetString(IslandKeys.AgentsPeriod, "today") switch { "week" => AgentPeriod.Week, "month" => AgentPeriod.Month, _ => AgentPeriod.Today };
        var totals = snap.Totals[period];
        var menu = new Border
        {
            Child = IslandUi.Row(3, IslandUi.Text(AgentSummary.Title(period), 11, FontWeights.Medium, IslandPalette.Secondary), IslandUi.Icon(Glyphs.ChevronDown, 8, IslandPalette.Secondary)),
            Cursor = Cursors.Hand, Background = Brushes.Transparent,
        };
        menu.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var m = new ContextMenu();
            foreach (var (id, p) in new[] { ("today", AgentPeriod.Today), ("week", AgentPeriod.Week), ("month", AgentPeriod.Month) })
            {
                var item = new MenuItem { Header = AgentSummary.Title(p), IsChecked = p == period };
                item.Click += (_, _) => Settings.Set(IslandKeys.AgentsPeriod, id);
                m.Items.Add(item);
            }
            m.PlacementTarget = menu;
            m.IsOpen = true;
        };
        var value = IslandUi.Row(6, IslandUi.Text((totals.FullyPriced ? "" : "≥ ") + AgentFormat.Cost(totals.Cost), 20, FontWeights.SemiBold),
            IslandUi.Text("API value", 11, brush: IslandPalette.Secondary));
        value.ToolTip = "API value is what the same work would cost at API list prices. Plans charge a fixed price instead." +
                        (totals.FullyPriced ? string.Empty : "\nSome models have no known price, so this is a minimum.");
        // Split bar by provider.
        var split = new Grid { Height = 4, Margin = new Thickness(0, 2, 0, 0) };
        var byProvider = snap.ByProvider[period];
        var sum = byProvider.Values.Sum(t => totals.FullyPriced ? t.Cost : t.Tokens.Total);
        foreach (var p in AgentProviders.All)
        {
            if (!byProvider.TryGetValue(p, out var t)) continue;
            var share = sum <= 0 ? 0 : (totals.FullyPriced ? t.Cost : t.Tokens.Total) / sum;
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, share), GridUnitType.Star) });
            var bar = new Border { Background = IslandCollapsed.ProviderBrush(p), Margin = new Thickness(0, 0, 1, 0), ToolTip = p.DisplayName() };
            Grid.SetColumn(bar, split.ColumnDefinitions.Count - 1);
            split.Children.Add(bar);
        }
        var cache = totals.Tokens.CacheHitRate is { } rate ? $" · {AgentFormat.Percent(rate)} from cache" : string.Empty;
        var footer = IslandUi.Text($"{AgentFormat.Tokens(totals.Tokens.Total)} tokens{cache}", 10.5, brush: IslandPalette.Tertiary);
        footer.ToolTip = $"{AgentFormat.Cost(totals.Savings)} saved by the cache";
        UIElement? multiple = null;
        if (period == AgentPeriod.Month)
        {
            var chips = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (p, plan) in snap.Plans)
            {
                if (plan.MonthlyPrice is not > 0 || !byProvider.TryGetValue(p, out var t)) continue;
                var x = t.Cost / plan.MonthlyPrice.Value;
                var chip = IslandUi.Text((x < 10 ? x.ToString("0.0") : x.ToString("0")) + "×", 10.5, FontWeights.SemiBold, IslandCollapsed.ProviderBrush(p));
                chip.ToolTip = $"{x:0.0}× the price of {p.DisplayName()} {plan.Name}";
                chip.Margin = new Thickness(0, 0, 8, 0);
                chips.Children.Add(chip);
            }
            multiple = chips.Children.Count > 0 ? chips : null;
        }
        return Card(Ui.Split(IslandUi.Text("Spending", 12.5, FontWeights.SemiBold), menu), value, multiple, split, footer);
    }

    private static FrameworkElement LiveCard(AgentUsageSnapshot snap, IslandContext ctx)
    {
        var rows = new List<UIElement?> { IslandUi.Text("Now", 12.5, FontWeights.SemiBold) };
        if (snap.Live.Count == 0)
        {
            foreach (var p in AgentProviders.All.Where(snap.Seen.Contains).Take(2))
                rows.Add(IslandUi.Row(6, Mark(p, 11), IslandUi.Text(p.DisplayName(), 11.5, FontWeights.Medium),
                    IslandUi.Text(snap.LastActivity.TryGetValue(p, out var last) ? AgentFormat.Relative(last, snap.Now) : "Idle", 11, brush: IslandPalette.Tertiary)));
        }
        foreach (var l in snap.Live.Take(2))
        {
            var model = AgentPricing.DisplayName(l.Model);
            var sub = string.Join(" · ", new[] { model, $"{AgentFormat.Tokens(l.Tokens.Output)} written", l.Cost > 0 ? AgentFormat.Cost(l.Cost) : null }.Where(s => !string.IsNullOrEmpty(s)));
            rows.Add(Ui.Stack(1,
                Ui.Split(IslandUi.Row(6, Mark(l.Provider, 11), IslandUi.Text(l.Project.Length > 0 ? l.Project : l.Provider.DisplayName(), 12, FontWeights.SemiBold)),
                    IslandUi.Text(AgentFormat.Clock((ctx.Now() - l.Started).TotalSeconds), 11.5, FontWeights.Medium, IslandCollapsed.ProviderBrush(l.Provider), mono: true)),
                IslandUi.Text(sub, 10.5, brush: IslandPalette.Tertiary)));
        }
        if (snap.Live.Count > 2) rows.Add(IslandUi.Text($"+{snap.Live.Count - 2}", 10.5, FontWeights.SemiBold, IslandPalette.Secondary));
        return Card(rows.ToArray());
    }

    private static FrameworkElement ShareCard(string title, AgentUsageSnapshot snap, IReadOnlyDictionary<AgentPeriod, IReadOnlyList<AgentShare>> shares)
    {
        var period = Settings.GetString(IslandKeys.AgentsPeriod, "today") switch { "week" => AgentPeriod.Week, "month" => AgentPeriod.Month, _ => AgentPeriod.Today };
        var list = shares[period].Take(3).ToList();
        var priced = snap.Totals[period].FullyPriced;
        var rows = new List<UIElement?> { IslandUi.Text(title, 12.5, FontWeights.SemiBold) };
        if (list.Count == 0) rows.Add(IslandUi.Text("Nothing in this period", 11.5, brush: IslandPalette.Tertiary));
        var max = list.Count == 0 ? 1 : list.Max(s => priced ? s.Totals.Cost : s.Totals.Tokens.Total);
        foreach (var s in list)
        {
            var v = priced ? s.Totals.Cost : s.Totals.Tokens.Total;
            var bar = new Border { Height = 4, Width = Math.Max(3, 44 * (max <= 0 ? 0 : v / max)), CornerRadius = new CornerRadius(2), Background = IslandCollapsed.ProviderBrush(s.Provider), VerticalAlignment = VerticalAlignment.Center };
            var name = IslandUi.Text(s.Name, 11.5, FontWeights.Medium);
            rows.Add(Ui.Split(name, IslandUi.Row(6, bar, IslandUi.Text(priced ? AgentFormat.Cost(s.Totals.Cost) : AgentFormat.Tokens(s.Totals.Tokens.Total), 11, FontWeights.Medium, IslandPalette.Secondary, mono: true))));
        }
        return Card(rows.ToArray());
    }

    private static FrameworkElement TrendCard(AgentUsageSnapshot snap)
    {
        var period = Settings.GetString(IslandKeys.AgentsPeriod, "today") switch { "week" => AgentPeriod.Week, "month" => AgentPeriod.Month, _ => AgentPeriod.Today };
        var buckets = period == AgentPeriod.Today ? snap.Hours : snap.Days.TakeLast(AgentSummary.Days(period)).ToList();
        var priced = snap.Totals[period].FullyPriced;
        double Value(AgentBucket b) => priced ? b.Cost : b.Tokens;
        var max = Math.Max(buckets.Count == 0 ? 0 : buckets.Max(Value), 1e-9);
        var bars = new UniformGrid { Rows = 1, Height = 60, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var b in buckets)
        {
            var column = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 1, 0) };
            foreach (var p in AgentProviders.All.Reverse())
            {
                if (!b.ByProvider.TryGetValue(p, out var t)) continue;
                var h = 60 * (priced ? t.Cost : t.Tokens.Total) / max;
                if (h <= 0) continue;
                column.Children.Add(new Border { Height = Math.Max(1, h), Background = IslandCollapsed.ProviderBrush(p) });
            }
            if (column.Children.Count == 0) column.Children.Add(new Border { Height = 2, Background = IslandPalette.Track, CornerRadius = new CornerRadius(1) });
            column.ToolTip = (period == AgentPeriod.Today ? b.Start.ToString("h tt") : b.Start.ToString("MMM d")) + " · " +
                             AgentFormat.Cost(b.Cost) + " · " + AgentFormat.Tokens(b.Tokens) + " tokens";
            bars.Children.Add(column);
        }
        var caption = $"{AgentSummary.Title(period)} · " + (priced ? AgentFormat.Cost(snap.Totals[period].Cost) : AgentFormat.Tokens(snap.Totals[period].Tokens.Total) + " tokens");
        return Card(Ui.Split(IslandUi.Text("Trend", 12.5, FontWeights.SemiBold), IslandUi.Text(caption, 11, brush: IslandPalette.Secondary)), bars);
    }

    private static FrameworkElement ActivityCard(AgentUsageSnapshot snap)
    {
        var values = snap.Days.Select(d => d.Cost > 0 ? d.Cost : d.Tokens).ToList();
        var active = values.Where(v => v > 0).ToList();
        var first = snap.Days[0].Start;
        var offset = ((int)first.DayOfWeek - (int)System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        var weeks = (snap.Days.Count + offset + 6) / 7;
        for (var c = 0; c < weeks; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(11) });
        for (var r = 0; r < 7; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(11) });
        for (var i = 0; i < snap.Days.Count; i++)
        {
            var slot = i + offset;
            var cell = new Border
            {
                Width = 9, Height = 9, CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromArgb((byte)(255 * AgentSummary.HeatOpacity(values[i], active)), 217, 120, 87)),
                ToolTip = snap.Days[i].Start.ToString("ddd, MMM d") + " · " + AgentFormat.Cost(snap.Days[i].Cost),
            };
            Grid.SetColumn(cell, slot / 7);
            Grid.SetRow(cell, slot % 7);
            grid.Children.Add(cell);
        }
        var busiest = snap.Days.MaxBy(d => d.Cost);
        var streak = AgentSummary.Streak(snap.Days);
        var stats = Ui.Stack(4,
            IslandUi.Text(AgentFormat.Cost(snap.Days.Sum(d => d.Cost)), 15, FontWeights.SemiBold),
            IslandUi.Text("13 weeks", 10.5, brush: IslandPalette.Tertiary),
            IslandUi.Text($"Active days  {active.Count}", 11, brush: IslandPalette.Secondary),
            busiest is { Cost: > 0 } ? IslandUi.Text($"Busiest day  {busiest.Start:MMM d} · {AgentFormat.Cost(busiest.Cost)}", 11, brush: IslandPalette.Secondary) : null,
            streak > 1 ? IslandUi.Text($"🔥 {streak}", 11, FontWeights.SemiBold, IslandPalette.Orange) : null);
        stats.Margin = new Thickness(14, 0, 0, 0);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(grid);
        Grid.SetColumn(stats, 1);
        row.Children.Add(stats);
        return Card(IslandUi.Text("Activity", 12.5, FontWeights.SemiBold), row);
    }
}
