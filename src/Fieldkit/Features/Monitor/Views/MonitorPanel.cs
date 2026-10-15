// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.Features.Monitor.Sampling;
using Fieldkit.UI;

namespace Fieldkit.Features.Monitor.Views;

/// <summary>A show/hide choice of a section's display options.</summary>
internal sealed record PanelOption(string Title, string Key, bool Default = true, string? Caption = null, bool Indented = false);

/// <summary>Plumbing shared by the monitor panel sections.</summary>
internal static class MonitorPanel
{
    /// <summary>Which sections currently show their display options (toggled by the header sliders icon).</summary>
    private static readonly HashSet<string> OptionsOpen = [];

    public static event Action<string>? OptionsToggled;

    public static bool IsOptionsOpen(string section) => OptionsOpen.Contains(section);

    /// <summary>The sliders button at the right of the section title (upstream panel edit button).</summary>
    public static FrameworkElement OptionsButton(string section)
    {
        var button = Ui.IconButton(MonitorGlyphs.Sliders, "Display options", () =>
        {
            if (!OptionsOpen.Remove(section)) OptionsOpen.Add(section);
            OptionsToggled?.Invoke(section);
        }, 15);
        button.Margin = new Thickness(0, -4, -4, -4);
        // A freshly opened panel starts with its options folded, like upstream's edit mode.
        button.Unloaded += (_, _) => OptionsOpen.Remove(section);
        return button;
    }

    /// <summary>
    /// A live section: <paramref name="render"/> builds the content and returns its per-sample
    /// updater; it re-renders when one of <paramref name="keys"/> changes or the options toggle.
    /// While loaded, the shared sampler runs with <paramref name="needs"/>.
    /// </summary>
    public static FrameworkElement Live(string section, Func<MonitorNeeds> needs,
        Func<(FrameworkElement View, Action<MonitorSnapshot> Update)> render,
        IReadOnlyList<PanelOption>? options, params string[] keys)
    {
        var host = new ContentControl();
        Action<MonitorSnapshot> update = _ => { };
        IDisposable? lease = null;

        void Render()
        {
            var content = new StackPanel();
            if (options is not null && IsOptionsOpen(section)) content.Children.Add(OptionsCard(options));
            Action<MonitorSnapshot> sectionUpdate = _ => { };
            try
            {
                var (view, viewUpdate) = render();
                sectionUpdate = viewUpdate;
                content.Children.Add(view);
            }
            catch (Exception ex)
            {
                Log.Error($"Building monitor section {section} failed", ex);
                content.Children.Add(Ui.Caption("This section failed to load."));
            }
            host.Content = content;
            update = sectionUpdate;
            update(MonitorSampler.Latest);
        }

        void Reacquire()
        {
            // Take the new lease first so the sampler (and its history) keeps running.
            var previous = lease;
            lease = MonitorSampler.Acquire(needs(), foreground: true);
            previous?.Dispose();
        }

        Render();
        Ui.Track(host, () =>
        {
            Reacquire();
            void OnUpdate(MonitorSnapshot s) => Ui.Guard(() => update(s));
            void OnToggle(string id)
            {
                if (id == section) Render();
            }
            MonitorSampler.Updated += OnUpdate;
            OptionsToggled += OnToggle;
            var watch = Settings.Observe(() =>
            {
                Render();
                Reacquire();
            }, [.. keys, .. (options ?? []).Select(o => o.Key)]);
            return new ActionDisposable(() =>
            {
                MonitorSampler.Updated -= OnUpdate;
                OptionsToggled -= OnToggle;
                watch.Dispose();
                lease?.Dispose();
                lease = null;
            });
        });
        return host;
    }

    private static FrameworkElement OptionsCard(IReadOnlyList<PanelOption> options)
    {
        var stack = Ui.Stack(6, Ui.Text("Show in panel", 12.5, FontWeights.SemiBold, "TextSecondary"));
        foreach (var o in options)
        {
            var check = Ui.Check(o.Title, o.Key, o.Default);
            check.Margin = new Thickness(o.Indented ? 22 : 0, 0, 0, 0);
            Ui.Append(stack, 6, check);
            if (o.Caption is not null) Ui.Append(stack, 2, MonitorWidgets.Note(o.Caption, o.Indented ? 46 : 24));
        }
        var settings = Ui.Button("Monitor settings…", () =>
        {
            PanelHost.Hide();
            PanelHost.OpenSettings("monitor");
        });
        settings.HorizontalAlignment = HorizontalAlignment.Left;
        Ui.Append(stack, 10, settings);
        var card = Ui.Card(stack, new Thickness(14, 10, 14, 12));
        card.Margin = new Thickness(0, 0, 0, 10);
        return card;
    }
}
