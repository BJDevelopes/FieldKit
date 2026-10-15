// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.Features.Input.Common;
using Fieldkit.Interop;
using Fieldkit.UI;

namespace Fieldkit.Features.Input.Remap;

/// <summary>Settings keys of key remapping. Each rule lives under its own id so the recorders can bind to it.</summary>
public static class KeyRemapKeys
{
    public const string FeatureId = "keyRemap";
    public const string Enabled = "keyRemap.enabled";
    public const string Ids = "keyRemap.ids";

    public static string From(string id) => $"keyRemap.rule.{id}.from";
    public static string To(string id) => $"keyRemap.rule.{id}.to";
    public static string App(string id) => $"keyRemap.rule.{id}.app";
    public static string On(string id) => $"keyRemap.rule.{id}.on";

    public static List<string> RuleIds() => Settings.Get<List<string>?>(Ids, null) ?? [];

    /// <summary>The switched-on rules as stored.</summary>
    public static List<KeyRemapRule> ReadRules() => RuleIds()
        .Where(id => Settings.GetBool(On(id), true))
        .Select(id => new KeyRemapRule(Shortcut.Parse(Settings.GetString(From(id), string.Empty)),
            Shortcut.Parse(Settings.GetString(To(id), string.Empty)), Settings.GetString(App(id), string.Empty).Trim()))
        .Where(r => r.IsValid)
        .ToList();
}

/// <summary>
/// Key remapping (upstream #2742): the keyboard hook swallows a remapped key and sends its target instead,
/// tagged as a remap so Fieldkit's own shortcuts and features still react to it. Remaps pause while a
/// Fieldkit window is in front, so keys can be recorded in Settings.
/// </summary>
internal sealed class KeyRemapService : FeatureService
{
    public override string FeatureId => KeyRemapKeys.FeatureId;
    public override IReadOnlyList<string> EnableKeys => [KeyRemapKeys.Enabled];

    private readonly KeyRemapEngine _engine = new();
    private readonly List<KeyOut> _output = [];
    private readonly Dictionary<uint, string> _appNames = [];
    private volatile List<KeyRemapRule>? _pendingRules;
    private IDisposable? _hook;
    private IDisposable? _inject;
    private IDisposable? _observer;

    protected override void OnStart()
    {
        Read();
        _observer = Settings.ObservePrefix("keyRemap.", Read);
        _inject = InjectQueue.Acquire();
        _hook = InputHooks.AddKeyboardHandler(OnKey, priority: 10);
    }

    protected override void OnStop()
    {
        _hook?.Dispose();
        _hook = null;
        lock (_output)
        {
            _output.Clear();
            _engine.ReleaseAll(_output);
            Send();
        }
        _observer?.Dispose();
        _observer = null;
        _inject?.Dispose();
        _inject = null;
    }

    // Built on the UI thread, picked up by the hook thread between key presses.
    private void Read() => _pendingRules = KeyRemapKeys.ReadRules();

    // ------------------------------------------------------------------ hook thread

    private bool OnKey(ref KeyEvent e)
    {
        if (e.IsOwn || e.IsRemap) return false;
        lock (_output)
        {
            if (_pendingRules is { } rules && !_engine.IsActive)
            {
                _engine.Rules = rules;
                _pendingRules = null;
            }
            _output.Clear();
            var swallow = _engine.Handle(e.VirtualKey, e.IsDown, ForegroundApp, !OwnWindowInFront(), _output);
            Send();
            return swallow;
        }
    }

    private void Send()
    {
        if (_output.Count == 0) return;
        var inputs = new Native.INPUT[_output.Count];
        for (var i = 0; i < _output.Count; i++) inputs[i] = InputSender.Key(_output[i].Vk, _output[i].Down);
        InjectQueue.Post(InputSender.AsRemap(inputs));
    }

    private static bool OwnWindowInFront()
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private string? ForegroundApp()
    {
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var pid);
        if (pid == 0) return null;
        if (_appNames.TryGetValue(pid, out var name)) return name;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch
        {
            return null;
        }
        if (_appNames.Count > 256) _appNames.Clear(); // process ids get reused; keep the cache small
        _appNames[pid] = name;
        return name;
    }
}

/// <summary>Settings → Key remapping.</summary>
internal static class KeyRemapPage
{
    public static FrameworkElement Build() => Ui.Page("Key remapping",
        "Make a key or shortcut act as another one: Caps Lock as Esc, or a shortcut you can reach as one you can't.",
        Ui.Group(null,
            Ui.ToggleRow("Remap keys", "A plain key swaps for another while modifiers still apply (Shift+Caps Lock gives Shift+Esc). " +
                "A shortcut fires only with exactly its modifiers held.", KeyRemapKeys.Enabled, true)),
        InputUi.Live(Rules, KeyRemapKeys.Ids),
        Ui.Group(null, InputUi.InfoLine(Glyphs.Info,
            "Remaps pause while a Fieldkit window is in front, so you can record keys here. They do not reach apps running as " +
            "administrator unless Fieldkit runs as administrator too.")));

    private static UIElement Rules()
    {
        var ids = KeyRemapKeys.RuleIds();
        var rows = new List<UIElement?>();
        if (ids.Count == 0) rows.Add(Ui.Caption("No remaps yet."));
        foreach (var id in ids) rows.Add(RuleRow(id));
        var add = Ui.Button("Add remap", () =>
        {
            var list = KeyRemapKeys.RuleIds();
            list.Add(Guid.NewGuid().ToString("N")[..8]);
            Settings.Set(KeyRemapKeys.Ids, list);
        }, glyph: Glyphs.Add);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        rows.Add(add);
        return Ui.Group("Remaps", rows.ToArray());
    }

    private static UIElement RuleRow(string id)
    {
        var from = new ShortcutRecorder(KeyRemapKeys.From(id), string.Empty, allowSingleKeys: true) { MinWidth = 130 };
        var to = new ShortcutRecorder(KeyRemapKeys.To(id), string.Empty, allowSingleKeys: true) { MinWidth = 130 };
        from.ToolTip = "The key or shortcut you press";
        to.ToolTip = "What apps receive instead";
        var arrow = Ui.Icon("\uE72A", 12, "TextTertiary"); // Forward
        arrow.VerticalAlignment = VerticalAlignment.Center;
        var app = Ui.TextInput(KeyRemapKeys.App(id), string.Empty, "Only in this app (its exe name, e.g. chrome). Empty: everywhere.");
        app.Width = 130;
        app.VerticalAlignment = VerticalAlignment.Center;
        var hint = Ui.Text("All apps", 12, brush: "TextTertiary", wrap: false);
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(9, 0, 0, 0);
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Visibility = app.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        app.TextChanged += (_, _) => hint.Visibility = app.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var appBox = new Grid();
        appBox.Children.Add(app);
        appBox.Children.Add(hint);
        var on = Ui.Toggle(KeyRemapKeys.On(id), true, small: true);
        on.ToolTip = "Use this remap";
        var remove = Ui.IconButton(Glyphs.Delete, "Remove", () =>
        {
            Settings.Set(KeyRemapKeys.Ids, KeyRemapKeys.RuleIds().Where(x => x != id).ToList());
            foreach (var key in new[] { KeyRemapKeys.From(id), KeyRemapKeys.To(id), KeyRemapKeys.App(id), KeyRemapKeys.On(id) })
                Settings.Remove(key);
        }, 12);
        var left = Ui.Row(8, from, arrow, to);
        var right = Ui.Row(8, appBox, on, remove);
        right.VerticalAlignment = VerticalAlignment.Center;
        return Ui.Split(left, right);
    }
}

/// <summary>Key remapping (a Fieldkit extra, upstream #2742).</summary>
public sealed class KeyRemapModule : IFeatureModule
{
    public void Register(ModuleRegistry r)
    {
        r.Implements(KeyRemapKeys.FeatureId, Parity.Full, "Fieldkit extra, from Vorssaint pull request #2742.");
        r.Service(KeyRemapKeys.FeatureId, () => new KeyRemapService());
        r.SettingsPage(new SettingsPageDef("keyRemap", SettingsCategory.WindowControls, "Key remapping", Glyphs.Keyboard, 6,
            [KeyRemapKeys.FeatureId], KeyRemapPage.Build,
            ["remap", "rebind", "swap keys", "caps lock", "escape", "key mapping", "shortcut"]));
    }
}
