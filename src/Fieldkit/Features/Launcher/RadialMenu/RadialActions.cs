// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Launcher.RadialMenu;

/// <summary>Runs what a slice points at (upstream <c>RadialMenuService.run</c>).</summary>
internal static class RadialActions
{
    private const int VkMediaNext = 0xB0;
    private const int VkMediaPrev = 0xB1;
    private const int VkMediaPlayPause = 0xB3;

    /// <summary>The beat screen-touching tools get so the wheel is really gone before they capture.</summary>
    private static readonly TimeSpan ToolDelay = TimeSpan.FromMilliseconds(150);

    public static void Run(RadialMenuItem item, RadialCatalog catalog, Action<RadialNowPlayingSnapshot?> showNowPlaying)
    {
        switch (item.Kind)
        {
            case RadialItemKind.App:
                Launch(item.Payload, requireExisting: false);
                break;
            case RadialItemKind.File:
                Launch(item.Payload, requireExisting: true);
                break;
            case RadialItemKind.Url:
                if (RadialMenuSupport.NormalizedUrl(item.Payload) is { } url) ShellOpen(url);
                break;
            case RadialItemKind.Shortcut:
                var shortcut = Shortcut.Parse(item.Payload);
                if (!shortcut.IsEmpty) WhenModifiersReleased(() => SendShortcut(shortcut));
                break;
            case RadialItemKind.Media:
                switch (item.MediaKey)
                {
                    case RadialMediaKey.NowPlaying: showNowPlaying(null); break;
                    case RadialMediaKey.PlayPause: WhenModifiersReleased(() => InputSender.Tap(VkMediaPlayPause)); break;
                    case RadialMediaKey.NextTrack: WhenModifiersReleased(() => InputSender.Tap(VkMediaNext)); break;
                    case RadialMediaKey.PreviousTrack: WhenModifiersReleased(() => InputSender.Tap(VkMediaPrev)); break;
                }
                break;
            case RadialItemKind.Tool:
                if (catalog.Tool(item.Payload) is { } tool) After(ToolDelay, tool.Run);
                else SystemSounds.Beep.Play();
                break;
            case RadialItemKind.WindowLayout:
                if (catalog.Layout(item.Payload) is { } layout) After(ToolDelay, layout.Run);
                else SystemSounds.Beep.Play();
                break;
            case RadialItemKind.QuickToggle:
                if (catalog.Toggle(item.Payload) is { } toggle) After(ToolDelay, toggle.Toggle);
                else SystemSounds.Beep.Play();
                break;
        }
    }

    /// <summary>Opens an app (path, shortcut or "shell:AppsFolder\…" entry) or a file/folder.</summary>
    public static void Launch(string payload, bool requireExisting)
    {
        var path = RadialMenuSupport.ExpandPath(payload);
        try
        {
            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
                return;
            }
            if (requireExisting && !File.Exists(path) && !Directory.Exists(path))
            {
                SystemSounds.Beep.Play();
                return;
            }
            ShellOpen(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Radial menu could not open {path}: {ex.Message}");
            SystemSounds.Beep.Play();
        }
    }

    private static void ShellOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Radial menu could not open {target}: {ex.Message}");
            SystemSounds.Beep.Play();
        }
    }

    public static void SendShortcut(Shortcut shortcut)
    {
        var keys = new List<int>();
        if (shortcut.Modifiers.HasFlag(Modifiers.Ctrl)) keys.Add(0xA2);
        if (shortcut.Modifiers.HasFlag(Modifiers.Alt)) keys.Add(0xA4);
        if (shortcut.Modifiers.HasFlag(Modifiers.Shift)) keys.Add(0xA0);
        if (shortcut.Modifiers.HasFlag(Modifiers.Win)) keys.Add(0x5B);
        keys.Add(shortcut.Key);
        InputSender.Chord(keys.ToArray());
    }

    /// <summary>
    /// Waits for the summoning chord to leave the keyboard before posting, or the synthetic key
    /// merges with the still-held modifiers (checked every 15 ms for up to ~1.5 s, plus a beat).
    /// </summary>
    public static void WhenModifiersReleased(Action post, int attempt = 0)
    {
        if (Shortcut.CurrentModifiers() == Modifiers.None || attempt >= 100)
        {
            After(TimeSpan.FromMilliseconds(60), post);
            return;
        }
        After(TimeSpan.FromMilliseconds(15), () => WhenModifiersReleased(post, attempt + 1));
    }

    public static void After(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Ui.Guard(action);
        };
        timer.Start();
    }
}
