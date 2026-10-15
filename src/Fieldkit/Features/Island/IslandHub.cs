// SPDX-License-Identifier: GPL-3.0-or-later
using Fieldkit.Core;
using Fieldkit.Features.Island.Mascot;
using Fieldkit.UI;

namespace Fieldkit.Features.Island;

/// <summary>
/// Where the island and its extensions meet (UI thread). Each extension's service registers itself
/// here while it runs and unregisters when it stops, so an uninstalled or switched-off extension leaves
/// nothing behind; the island window reads from here and listens to <see cref="Changed"/>.
/// </summary>
public static class IslandHub
{
    /// <summary>Raised (UI thread) when any live activity's state changes.</summary>
    public static event Action? Changed;

    /// <summary>Raised (UI thread) for a transient notice.</summary>
    public static event Action<IslandNotice>? NoticePosted;

    /// <summary>Raised (UI thread) when something should make the companion react.</summary>
    public static event Action<MascotEvent>? MascotEvent;

    /// <summary>Raised (UI thread) to open the island on a section.</summary>
    public static event Action<string?>? OpenRequested;

    /// <summary>Raised (UI thread) to toggle the island.</summary>
    public static event Action? ToggleRequested;

    public static IslandService? Island { get; internal set; }
    public static Timer.TimerService? Timer { get; internal set; }
    public static Agents.AgentUsageService? Agents { get; internal set; }
    public static Downloads.DownloadsService? Downloads { get; internal set; }
    public static Calendar.CalendarService? Calendar { get; internal set; }
    public static Watch.WatchService? Watch { get; internal set; }
    public static Accessories.AccessoriesService? Accessories { get; internal set; }
    public static Media.LiveEqualizerService? Equalizer { get; internal set; }
    public static Lyrics.LyricsService? Lyrics { get; internal set; }
    public static MascotService? Mascot { get; internal set; }
    public static GesturesService? Gestures { get; internal set; }
    public static QueueService? Queue { get; internal set; }

    /// <summary>Whether the island itself is up (an extension's notices fall back to toasts otherwise).</summary>
    public static bool IslandRunning => Island is { IsRunning: true };

    public static void RaiseChanged() => Ui.OnUi(() => Changed?.Invoke());

    /// <summary>Shows a notice in the island, or a system toast when the island cannot show it.</summary>
    public static void Post(IslandNotice notice, bool toastFallback = true)
    {
        Ui.OnUi(() =>
        {
            if (IslandRunning && NoticePosted is not null) NoticePosted.Invoke(notice);
            else if (toastFallback) Notifier.Show(notice.Title, notice.Detail);
        });
    }

    public static void React(MascotEvent e) => Ui.OnUi(() => MascotEvent?.Invoke(e));

    public static void Open(string? section = null) => Ui.OnUi(() => OpenRequested?.Invoke(section));

    public static void Toggle() => Ui.OnUi(() => ToggleRequested?.Invoke());

    /// <summary>The island and an extension are both on (installed, enabled, island running).</summary>
    public static bool ExtensionOn(string featureId, string enableKey, bool enabledByDefault = true) =>
        FeatureCatalog.IsAvailable("notch") && Settings.GetBool(IslandKeys.Enabled, IslandDefaults.Enabled) &&
        FeatureCatalog.IsAvailable(featureId) && Settings.GetBool(enableKey, enabledByDefault);
}

/// <summary>
/// Base for an extension's service: runs only while the island is installed and enabled and the extension
/// is installed and enabled; registers itself with the hub while running.
/// </summary>
public abstract class IslandExtensionService : FeatureService
{
    protected abstract string EnableKey { get; }

    public override IReadOnlyList<string> EnableKeys => [IslandKeys.Enabled, EnableKey];

    public override bool ShouldRun() =>
        FeatureCatalog.IsAvailable("notch") && Settings.GetBool(IslandKeys.Enabled, IslandDefaults.Enabled) &&
        Settings.GetBool(EnableKey, EnabledByDefault);
}

/// <summary>Companion: only marks that the companion may show; drawing happens in the island.</summary>
public sealed class MascotService : IslandExtensionService
{
    public override string FeatureId => "notchMascot";
    protected override string EnableKey => IslandKeys.MascotEnabled;
    public override bool EnabledByDefault => false;

    protected override void OnStart()
    {
        IslandHub.Mascot = this;
        IslandHub.RaiseChanged();
    }

    protected override void OnStop()
    {
        if (IslandHub.Mascot == this) IslandHub.Mascot = null;
        IslandHub.RaiseChanged();
    }
}

/// <summary>Gestures: while running, the island turns scrolls and swipes over it into actions.</summary>
public sealed class GesturesService : IslandExtensionService
{
    public override string FeatureId => "notchGestures";
    protected override string EnableKey => IslandKeys.GesturesEnabled;

    protected override void OnStart() => IslandHub.Gestures = this;

    protected override void OnStop()
    {
        if (IslandHub.Gestures == this) IslandHub.Gestures = null;
    }
}

/// <summary>Up next: Windows media sessions expose no queue, so the panel can only say so.</summary>
public sealed class QueueService : IslandExtensionService
{
    public override string FeatureId => "notchQueue";
    protected override string EnableKey => IslandKeys.QueueEnabled;

    protected override void OnStart() => IslandHub.Queue = this;

    protected override void OnStop()
    {
        if (IslandHub.Queue == this) IslandHub.Queue = null;
    }
}
