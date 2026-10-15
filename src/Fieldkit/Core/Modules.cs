// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;

namespace Fieldkit.Core;

/// <summary>
/// A feature module. Every non-abstract class implementing this interface (with a
/// parameterless constructor) is discovered by reflection at startup, so adding a
/// feature never means editing a shared list: drop a file under <c>Features/</c>.
///
/// <see cref="Register"/> runs once on the UI thread. It declares services and UI
/// surfaces; it must not start work, install hooks or touch hardware.
/// </summary>
public interface IFeatureModule
{
    void Register(ModuleRegistry r);
}

/// <summary>
/// Background behaviour owned by one feature. The runtime starts it while the
/// feature is installed (Features hub) AND <see cref="ShouldRun"/> is true, and
/// stops it otherwise. Start/Stop are called on the UI thread.
/// </summary>
public abstract class FeatureService : IDisposable
{
    public abstract string FeatureId { get; }

    /// <summary>
    /// Settings keys that switch the behaviour on. The runtime re-evaluates
    /// <see cref="ShouldRun"/> whenever one of them changes. Empty means "runs whenever installed".
    /// </summary>
    public virtual IReadOnlyList<string> EnableKeys => [];

    /// <summary>Default for every key in <see cref="EnableKeys"/> when the user never chose.</summary>
    public virtual bool EnabledByDefault => true;

    /// <summary>Default rule: any enable key on (or no enable keys).</summary>
    public virtual bool ShouldRun() =>
        EnableKeys.Count == 0 || EnableKeys.Any(k => Settings.GetBool(k, EnabledByDefault));

    public bool IsRunning { get; private set; }

    internal void StartInternal()
    {
        if (IsRunning) return;
        try
        {
            OnStart();
            IsRunning = true;
            Log.Info($"Started {GetType().Name}");
        }
        catch (Exception ex)
        {
            Log.Error($"Starting {GetType().Name} failed", ex);
            try { OnStop(); } catch { }
        }
    }

    internal void StopInternal()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try
        {
            OnStop();
            Log.Info($"Stopped {GetType().Name}");
        }
        catch (Exception ex)
        {
            Log.Error($"Stopping {GetType().Name} failed", ex);
        }
    }

    protected abstract void OnStart();
    protected abstract void OnStop();

    public void Dispose()
    {
        StopInternal();
        GC.SuppressFinalize(this);
    }
}

/// <summary>A tab (icon) of the tray panel.</summary>
/// <param name="Order">Upstream order: keepAwake 0, brightness 10, mixer 20, system 30, network 40,
/// disk 50, power 60, fanControl 70, utilities 80, controls 90, toggles 100, wallpaper 110.</param>
/// <param name="Build">Builds the section content. Called every time the tab is shown
/// (the panel is rebuilt on open). Dispose subscriptions in <c>Unloaded</c>.</param>
/// <param name="HeaderAccessory">Optional element shown at the right of the section title.</param>
public sealed record PanelSectionDef(
    string Id,
    string Title,
    string Glyph,
    int Order,
    string[] FeatureGate,
    Func<FrameworkElement> Build,
    Func<FrameworkElement>? HeaderAccessory = null);

/// <summary>A row of the Utilities tab: opens a tool.</summary>
public sealed record UtilityEntry(
    string FeatureId,
    string Title,
    string Description,
    string Glyph,
    Action Open,
    int Order = 100,
    string? ShortcutKey = null)
{
    /// <summary>Upstream's per-tool "Show in panel" switch (Settings → General → Utilities in the panel).</summary>
    public string VisibilityKey => $"panel.utility.{FeatureId}.{new string(Title.Where(char.IsLetterOrDigit).ToArray())}";

    public bool IsShownInPanel => Settings.GetBool(VisibilityKey, true);
}

/// <summary>Sub-sections of the Controls tab (mirrors upstream WINDOWS / MOUSE AND KEYBOARD / FILES).</summary>
public enum ControlGroup { Windows, MouseKeyboard, Files }

/// <summary>A toggle card of the Controls tab bound to a boolean setting.</summary>
public sealed record ControlEntry(
    string FeatureId,
    ControlGroup Group,
    string Title,
    string Description,
    string Glyph,
    string SettingKey,
    bool DefaultValue,
    int Order = 100,
    ControlSubToggle[]? SubToggles = null);

public sealed record ControlSubToggle(string Title, string Description, string SettingKey, bool DefaultValue);

/// <summary>Sidebar categories of the Settings window, in display order.</summary>
public enum SettingsCategory { Essentials, WindowControls, Files, Sound, Utilities, DynamicIsland, Advanced }

/// <summary>A page of the Settings window.</summary>
/// <param name="FeatureGate">Hidden when every listed feature is uninstalled. Empty = always shown.</param>
/// <param name="Keywords">Extra words matched by the settings search.</param>
public sealed record SettingsPageDef(
    string Id,
    SettingsCategory Category,
    string Title,
    string Glyph,
    int Order,
    string[] FeatureGate,
    Func<FrameworkElement> Build,
    string[]? Keywords = null);

/// <summary>A one-tap tile of the Quick toggles section / quick panel.</summary>
/// <param name="IsOn">Null for one-shot actions (e.g. Lock screen).</param>
/// <param name="FeatureId">The owning feature; the tile is hidden while it is uninstalled.</param>
public sealed record QuickToggleDef(
    string Id,
    string Title,
    string Glyph,
    Func<bool>? IsOn,
    Action Toggle,
    int Order = 100,
    string? FeatureId = null);

/// <summary>An invocable tool action (Command Bar, Quick panel, Radial menu, tray menu).</summary>
public sealed record ToolAction(
    string Id,
    string FeatureId,
    string Title,
    string Glyph,
    Action Run,
    string? ShortcutKey = null,
    string DefaultShortcut = "");

/// <summary>A parity declaration made by a module.</summary>
public sealed record ParityClaim(string FeatureId, Parity Parity, string? Note);

/// <summary>Collects what modules declare. Read by the runtime and the UI.</summary>
public sealed class ModuleRegistry
{
    private readonly List<(string FeatureId, Func<FeatureService> Factory)> _services = [];

    public List<PanelSectionDef> PanelSections { get; } = [];
    public List<UtilityEntry> Utilities { get; } = [];
    public List<ControlEntry> Controls { get; } = [];
    public List<SettingsPageDef> SettingsPages { get; } = [];
    public List<QuickToggleDef> QuickToggles { get; } = [];
    public List<ToolAction> Actions { get; } = [];
    public Dictionary<string, ParityClaim> Parity { get; } = new(StringComparer.Ordinal);

    internal IReadOnlyList<(string FeatureId, Func<FeatureService> Factory)> ServiceFactories => _services;

    /// <summary>Declares how completely this module covers an upstream feature.</summary>
    public void Implements(string featureId, Parity parity, string? note = null)
    {
        EnsureKnown(featureId);
        Parity[featureId] = new ParityClaim(featureId, parity, note);
    }

    public void Service(string featureId, Func<FeatureService> factory)
    {
        EnsureKnown(featureId);
        _services.Add((featureId, factory));
    }

    public void PanelSection(PanelSectionDef def) => PanelSections.Add(def);

    public void Utility(UtilityEntry entry)
    {
        EnsureKnown(entry.FeatureId);
        Utilities.Add(entry);
    }

    public void Control(ControlEntry entry)
    {
        EnsureKnown(entry.FeatureId);
        Controls.Add(entry);
    }

    public void SettingsPage(SettingsPageDef def) => SettingsPages.Add(def);

    public void QuickToggle(QuickToggleDef def)
    {
        if (def.FeatureId is not null) EnsureKnown(def.FeatureId);
        QuickToggles.Add(def);
    }

    public void Action(ToolAction action)
    {
        EnsureKnown(action.FeatureId);
        Actions.Add(action);
    }

    private static void EnsureKnown(string featureId)
    {
        if (!FeatureCatalog.TryGet(featureId, out _))
            throw new ArgumentException($"Unknown feature id '{featureId}'. Add it to FeatureCatalog first.");
    }
}
