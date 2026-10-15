// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Fieldkit.Core;

namespace Fieldkit.Features.QuickTools;

/// <summary>
/// Scratchpad persistence: one JSON document in the data folder, loaded when the pad first
/// opens, saved 0.8 s after the last edit (and immediately on hide, tab changes and export),
/// written atomically and read back to verify.
/// </summary>
public static class ScratchpadStore
{
    public const string RetentionKey = "scratchpad.retention";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static DispatcherTimer? _debounce;

    public static string FilePath => AppInfo.DataPath("scratchpad.json");

    public static ScratchpadDocument? Document { get; private set; }

    /// <summary>True after a failed save; the edits stay in memory and the next change retries.</summary>
    public static bool SaveFailed { get; private set; }

    public static event Action? SaveStateChanged;

    /// <summary>Loads (once) and applies retention. False when the file exists but cannot be read.</summary>
    public static bool EnsureLoaded()
    {
        if (Document is null)
        {
            try
            {
                ScratchpadDocument? loaded = null;
                if (File.Exists(FilePath)) loaded = JsonSerializer.Deserialize<ScratchpadDocument>(File.ReadAllText(FilePath), Json);
                Document = ScratchpadLogic.Sanitized(loaded);
            }
            catch (Exception ex)
            {
                Log.Error("Reading the scratchpad failed", ex);
                return false;
            }
        }
        var retention = ScratchpadLogic.ParseRetention(Settings.GetString(RetentionKey, "never"));
        if (ScratchpadLogic.ApplyRetention(Document, retention, DateTime.Now)) SaveNow();
        return true;
    }

    public static ScratchPad Selected =>
        Document!.Pads.FirstOrDefault(p => p.Id == Document.SelectedId) ?? Document.Pads[0];

    public static void UpdateText(string text)
    {
        if (Document is null) return;
        var pad = Selected;
        if (pad.Text == text) return;
        pad.Text = text;
        pad.ModifiedAt = text.Length == 0 ? null : DateTime.Now;
        ScheduleSave();
    }

    public static void ScheduleSave()
    {
        _debounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _debounce.Tick -= OnDebounce;
        _debounce.Tick += OnDebounce;
        _debounce.Stop();
        _debounce.Start();
    }

    private static void OnDebounce(object? sender, EventArgs e) => SaveNow();

    public static void SaveNow()
    {
        _debounce?.Stop();
        if (Document is null) return;
        var ok = false;
        try
        {
            var json = JsonSerializer.Serialize(Document, Json);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
            ok = File.ReadAllText(FilePath) == json;
        }
        catch (Exception ex)
        {
            Log.Error("Saving the scratchpad failed", ex);
        }
        if (ok != !SaveFailed)
        {
            SaveFailed = !ok;
            SaveStateChanged?.Invoke();
        }
    }

    public static ScratchPad? AddPad()
    {
        if (Document is null || Document.Pads.Count >= ScratchpadLogic.MaxPads) return null;
        var pad = new ScratchPad { Name = ScratchpadLogic.NextName(Document.Pads.Select(p => p.Name).ToList()) };
        Document.Pads.Add(pad);
        Document.SelectedId = pad.Id;
        SaveNow();
        return pad;
    }

    public static void Select(string id)
    {
        if (Document is null || Document.SelectedId == id) return;
        Document.SelectedId = id;
        SaveNow();
    }

    public static void Rename(string id, string name)
    {
        if (Document?.Pads.FirstOrDefault(p => p.Id == id) is not { } pad || ScratchpadLogic.CleanName(name) is not { } clean) return;
        pad.Name = clean;
        SaveNow();
    }

    public static void Close(string id)
    {
        if (Document is null || Document.Pads.Count <= 1) return;
        var index = Document.Pads.FindIndex(p => p.Id == id);
        if (index < 0) return;
        Document.Pads.RemoveAt(index);
        if (Document.SelectedId == id)
            Document.SelectedId = Document.Pads[ScratchpadLogic.SelectionAfterClose(index, Document.Pads.Count)].Id;
        SaveNow();
    }
}
