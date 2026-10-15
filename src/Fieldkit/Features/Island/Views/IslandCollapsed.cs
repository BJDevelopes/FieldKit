// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fieldkit.Core;
using Fieldkit.Features.Island.Agents;
using Fieldkit.Features.Island.Mascot;
using Fieldkit.Features.Island.Timer;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Island.Views;

/// <summary>What the closed island shows right now.</summary>
public sealed record CollapsedState(
    IslandNotice? Notice,
    IslandActivity? Activity,
    IslandIdleContent Idle,
    bool CompanionRests,
    bool Hovered);

/// <summary>
/// The closed island (upstream capsule layout, one row): a mark at the left end, a reading at the right
/// end, text in between when there is room. Widths stay within upstream's maxima per activity.
/// </summary>
public static class IslandCollapsed
{
    public const double Height = 30;
    public const double RestWidth = 112;

    /// <summary>Decides the closed island's state from the context (upstream priority rules).</summary>
    public static CollapsedState State(IslandContext ctx, IslandNotice? notice, IslandActivity? preferred, bool hovered)
    {
        var music = ctx.Music();
        var idle = IslandLogic.ParseIdle(Settings.GetString(IslandKeys.IdleContent, IslandDefaults.IdleContent));
        var companion = ctx.MascotOn();
        var showMusic = Settings.GetBool(IslandKeys.ShowPlayingMusic, true);
        var timer = ctx.Timer();
        var watch = ctx.Watch();
        var agents = ctx.Agents();
        var activities = IslandLogic.CompactActivities(
            timer is { HasSession: true } && (timer.Completed || !Settings.GetBool(IslandKeys.TimerHideCountdown, false)),
            watch is { Active: true },
            ctx.Downloads()?.Any(d => d.Active && !d.Completed) == true,
            Settings.GetBool(IslandKeys.AgentsLiveActivity, true) && agents is { Live.Count: > 0 },
            ctx.Countdown() is not null,
            IslandLogic.ShowsMusicActivity(music is { IsPlaying: true }, idle, companion, showMusic),
            false);
        var current = IslandLogic.Current(activities, preferred);
        var visibleIdle = IslandLogic.VisibleIdleContent(idle, music is { IsPlaying: true }, companion, showMusic);
        if (visibleIdle == IslandIdleContent.Agents && agents is null) visibleIdle = IslandIdleContent.None;
        var rests = IslandLogic.CompanionRestsInView(companion, visibleIdle, tucked: false, current);
        return new CollapsedState(notice, current, visibleIdle, rests, hovered);
    }

    public static FrameworkElement Build(IslandContext ctx, CollapsedState state)
    {
        if (state.Notice is { } n) return Notice(n);
        return state.Activity switch
        {
            IslandActivity.Music => Music(ctx, state.Hovered),
            IslandActivity.Timer => Timer(ctx),
            IslandActivity.Watch => Watch(ctx),
            IslandActivity.Downloads => Downloads(ctx),
            IslandActivity.Agents => Agents(ctx),
            IslandActivity.Calendar => Calendar(ctx),
            _ => Rest(ctx, state),
        };
    }

    private static Grid Strip(UIElement? left, UIElement? middle, UIElement? right, double minWidth = RestWidth, double maxWidth = 260)
    {
        // mark | text | (stretch) | reading: the text never shrinks below its own width, the gap does.
        var g = new Grid { Height = Height, MinWidth = minWidth, MaxWidth = maxWidth };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (left is FrameworkElement l) { l.Margin = new Thickness(8, 0, 6, 0); l.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(l); }
        if (middle is FrameworkElement m)
        {
            m.Margin = new Thickness(0, 0, 10, 0);
            m.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(m, 1);
            g.Children.Add(m);
        }
        if (right is FrameworkElement r) { r.Margin = new Thickness(0, 0, 10, 0); r.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(r, 3); g.Children.Add(r); }
        return g;
    }

    private static FrameworkElement Rest(IslandContext ctx, CollapsedState state)
    {
        if (state.CompanionRests)
        {
            var mascot = new MascotView(Height - 6, animate: !ctx.IsPreview) { HorizontalAlignment = HorizontalAlignment.Center };
            var g = new Grid { Height = Height, Width = RestWidth };
            g.Children.Add(mascot);
            g.Tag = mascot;
            return g;
        }
        switch (state.Idle)
        {
            case IslandIdleContent.Battery when Native.GetSystemPowerStatus(out var power) && power.BatteryFlag != 128 && power.BatteryLifePercent <= 100:
            {
                var percent = power.BatteryLifePercent;
                var low = Settings.GetBool(IslandKeys.LowBatteryTint, false) && power.ACLineStatus == 0 &&
                          percent <= Settings.GetInt(IslandKeys.LowBatteryThreshold, 10);
                var brush = low ? IslandPalette.Red : power.ACLineStatus == 1 ? IslandPalette.Green : IslandPalette.Text;
                return Strip(IslandUi.Icon(power.ACLineStatus == 1 ? "\uE83E" : Glyphs.Battery, 13, brush), null,
                    IslandUi.Text($"{percent}%", 12, FontWeights.SemiBold, brush, mono: true));
            }
            case IslandIdleContent.Agents when ctx.Agents() is { } snap:
                return RestingAgents(snap);
            default:
                return new Border { Height = Height, Width = RestWidth };
        }
    }

    private static FrameworkElement RestingAgents(AgentUsageSnapshot snap)
    {
        var focus = Settings.GetString(IslandKeys.AgentsLimitFocus, "mostUsed");
        var best = snap.Limits.Values
            .Select(l => (l.Provider, Window: AgentSummary.Focus(l, focus, snap.Now)))
            .Where(x => x.Window is not null)
            .OrderByDescending(x => x.Window!.UsedPercent)
            .FirstOrDefault();
        if (best.Window is { } w)
        {
            var tint = w.UsedPercent >= 95 ? IslandPalette.Red : w.UsedPercent >= 80 ? IslandPalette.Orange : ProviderBrush(best.Provider);
            var used = Settings.GetString(IslandKeys.AgentsLimitDisplay, "remaining") == "used";
            return Strip(new Ring(w.UsedFraction, 15, tint), null,
                IslandUi.Text(AgentFormat.Percent(used ? w.UsedFraction : w.RemainingFraction), 11, FontWeights.SemiBold, tint, mono: true));
        }
        var provider = AgentProviders.All.FirstOrDefault(snap.Seen.Contains);
        return Strip(IslandUi.Icon(AgentGlyphs.For(provider), 12, ProviderBrush(provider)), null,
            IslandUi.Text(AgentFormat.Cost(snap.Totals[AgentPeriod.Today].Cost), 11, FontWeights.SemiBold, mono: true));
    }

    public static Brush ProviderBrush(AgentProvider p)
    {
        var (r, g, b) = p.Tint();
        return IslandPalette.Rgb(r, g, b);
    }

    private static FrameworkElement Music(IslandContext ctx, bool hovered)
    {
        var music = ctx.Music();
        var art = IslandUi.Artwork(music?.Artwork, Height - 8, 0.5);
        var bars = new EqualizerBars(ctx, 7, 1.8, 14);
        TextBlock? title = null;
        if (hovered && music is not null)
        {
            title = IslandUi.Text(music.Title, 12, FontWeights.SemiBold);
            title.MaxWidth = 150;
        }
        var strip = Strip(art, title, bars, minWidth: 150, maxWidth: 260);
        art.Margin = new Thickness(4, 0, 6, 0);
        return strip;
    }

    private static FrameworkElement Timer(IslandContext ctx)
    {
        var icon = IslandUi.Icon(Glyphs.Timer, 13, IslandPalette.Orange);
        var reading = IslandUi.Text(string.Empty, 13, FontWeights.SemiBold, IslandPalette.Orange, mono: true);
        var strip = Strip(icon, null, reading, maxWidth: 220);
        IslandUi.Live(strip, TimeSpan.FromMilliseconds(250), () =>
        {
            if (ctx.Timer() is not { } s) return;
            var now = ctx.TimerClock();
            reading.Text = TimerFormat.Compact(s, now);
            icon.Text = s.Completed ? Glyphs.Check : s.IsPaused ? Glyphs.Pause : s.CountsUp ? Glyphs.Clock : Glyphs.Timer;
        });
        return strip;
    }

    private static FrameworkElement Watch(IslandContext ctx)
    {
        var w = ctx.Watch();
        var headline = IslandUi.Text(w?.Headline is { Length: > 0 } h ? h : "…", 13, FontWeights.SemiBold, IslandPalette.Purple, mono: true);
        headline.MaxWidth = 130;
        return Strip(IslandUi.Icon(Glyphs.Eye, 13, IslandPalette.Purple), null, headline, minWidth: 130, maxWidth: 220);
    }

    private static FrameworkElement Downloads(IslandContext ctx)
    {
        var item = ctx.Downloads()?.FirstOrDefault(d => d.Active && !d.Completed);
        var name = IslandUi.Text(item?.Name ?? string.Empty, 11, FontWeights.Medium);
        name.MaxWidth = 120;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        UIElement right = item?.Fraction is { } f
            ? IslandUi.Text(AgentFormat.Percent(f), 10, FontWeights.Medium, IslandPalette.Secondary, mono: true)
            : IslandUi.Text(item is null ? string.Empty : Fieldkit.Features.Island.Downloads.DownloadTracker.Bytes(item.ReceivedBytes), 10, FontWeights.Medium, IslandPalette.Secondary, mono: true);
        return Strip(IslandUi.Icon(Glyphs.Download, 13), name, right, minWidth: 150, maxWidth: 280);
    }

    private static FrameworkElement Agents(IslandContext ctx)
    {
        var marks = new StackPanel { Orientation = Orientation.Horizontal };
        var reading = IslandUi.Text(string.Empty, 13, FontWeights.SemiBold, mono: true);
        var strip = Strip(marks, null, reading, maxWidth: 220);
        IslandUi.Live(strip, TimeSpan.FromSeconds(1), () =>
        {
            if (ctx.Agents() is not { } snap || snap.Live.Count == 0) return;
            var providers = AgentProviders.All.Where(p => snap.Live.Any(l => l.Provider == p)).ToList();
            if (marks.Children.Count != providers.Count)
            {
                marks.Children.Clear();
                foreach (var p in providers)
                {
                    var icon = IslandUi.Icon(AgentGlyphs.For(p), 12, ProviderBrush(p));
                    icon.Margin = new Thickness(0, 0, 4, 0);
                    marks.Children.Add(icon);
                }
            }
            var first = providers[0];
            reading.Foreground = ProviderBrush(first);
            reading.Text = AgentReadout(snap, first, ctx.Now());
        });
        return strip;
    }

    /// <summary>"Beside the camera": elapsed time, tokens written, API value or limit.</summary>
    public static string AgentReadout(AgentUsageSnapshot snap, AgentProvider first, DateTimeOffset now)
    {
        switch (Settings.GetString(IslandKeys.AgentsReadout, "elapsed"))
        {
            case "tokens":
                return AgentFormat.Tokens(snap.Live.Sum(l => l.Tokens.Output));
            case "cost":
                return AgentFormat.Cost(snap.Live.Sum(l => l.Cost));
            case "limit" when snap.Limits.TryGetValue(first, out var limits) &&
                              AgentSummary.Focus(limits, Settings.GetString(IslandKeys.AgentsLimitFocus, "mostUsed"), now) is { } w:
                return AgentFormat.Percent(Settings.GetString(IslandKeys.AgentsLimitDisplay, "remaining") == "used" ? w.UsedFraction : w.RemainingFraction);
            default:
                var start = snap.Live.Min(l => l.Started);
                return AgentFormat.Clock((now - start).TotalSeconds);
        }
    }

    private static FrameworkElement Calendar(IslandContext ctx)
    {
        var c = ctx.Countdown();
        var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = IslandPalette.Pink };
        var title = IslandUi.Text(c?.Event.Title is { Length: > 0 } t ? t : "Untitled event", 11, FontWeights.SemiBold);
        title.MaxWidth = 120;
        var clock = IslandUi.Text(string.Empty, 13, FontWeights.Medium, c is { Ongoing: true } ? IslandPalette.Mint : IslandPalette.Text, mono: true);
        var strip = Strip(dot, title, clock, minWidth: 160, maxWidth: 300);
        IslandUi.Live(strip, TimeSpan.FromSeconds(1), () =>
        {
            if (ctx.Countdown() is { } cd) clock.Text = Fieldkit.Features.Island.Calendar.CalendarLogic.CountdownText(cd.Target - ctx.Now().LocalDateTime);
        });
        return strip;
    }

    private static FrameworkElement Notice(IslandNotice n)
    {
        var icon = IslandUi.Icon(n.Glyph, 13, IslandPalette.Tint(n.Tint));
        var title = IslandUi.Text(n.Title, 12, FontWeights.SemiBold);
        title.MaxWidth = 170;
        UIElement right;
        if (n.Level is { } level && n.Event is IslandNoticeEvent.Volume or IslandNoticeEvent.Brightness)
            right = IslandUi.Meter(level, 4, IslandPalette.Text, 56);
        else
        {
            var detail = IslandUi.Text(n.Detail, 11.5, FontWeights.Normal, IslandPalette.Secondary);
            detail.MaxWidth = 150;
            right = detail;
        }
        return Strip(icon, title, right, minWidth: 150, maxWidth: 380);
    }
}
