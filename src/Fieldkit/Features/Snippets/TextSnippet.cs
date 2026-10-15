// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fieldkit.Core;

namespace Fieldkit.Features.Snippets;

/// <summary>When a snippet fires relative to its trigger.</summary>
public enum SnippetExpansion
{
    /// <summary>The moment the last trigger character is typed.</summary>
    Immediate,
    /// <summary>Only when space, Tab or Enter follows the trigger (the delimiter is kept after the text).</summary>
    AfterDelimiter,
}

/// <summary>
/// One text snippet: typing the trigger inserts the replacement. Serialized with the same field names
/// and values as upstream, so snippet files can travel between the two apps.
/// </summary>
public sealed record TextSnippet
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Trigger { get; init; } = string.Empty;
    public string Replacement { get; init; } = string.Empty;
    public SnippetExpansion Expansion { get; init; } = SnippetExpansion.AfterDelimiter;
    public bool Enabled { get; init; } = true;
    public bool IgnoresCase { get; init; }
    /// <summary>Plain folder name for the library; empty means no folder.</summary>
    public string Folder { get; init; } = string.Empty;
    public bool ShowsInLibrary { get; init; } = true;

    [JsonIgnore]
    public string DisplayName => Name.Length > 0 ? Name : Trigger;
}

/// <summary>
/// The snippets file: <c>Snippets\snippets.json</c> under the data directory (private content, never
/// in the settings file or a settings export), plus import and export.
/// </summary>
public static class SnippetStore
{
    private static readonly object Gate = new();
    private static List<TextSnippet>? _snippets;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Raised after any change (on the thread that saved).</summary>
    public static event Action? Changed;

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(AppInfo.DataDirectory, "Snippets");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "snippets.json");
        }
    }

    public static IReadOnlyList<TextSnippet> All
    {
        get
        {
            lock (Gate) return _snippets ??= Load();
        }
    }

    public static void Save(IEnumerable<TextSnippet> snippets)
    {
        var list = snippets.ToList();
        lock (Gate)
        {
            _snippets = list;
            try
            {
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, Serialize(list));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("Saving snippets failed", ex);
            }
        }
        Changed?.Invoke();
    }

    public static void Upsert(TextSnippet snippet)
    {
        var list = All.ToList();
        var index = list.FindIndex(s => s.Id == snippet.Id);
        if (index >= 0) list[index] = snippet;
        else list.Add(snippet);
        Save(list);
    }

    public static void Remove(Guid id) => Save(All.Where(s => s.Id != id));

    public static void SetEnabled(Guid id, bool enabled) =>
        Save(All.Select(s => s.Id == id ? s with { Enabled = enabled } : s));

    public static string Serialize(IEnumerable<TextSnippet> snippets) => JsonSerializer.Serialize(snippets.ToList(), Json);

    /// <summary>Parses a snippets file; null when it is not one. Bad entries are skipped.</summary>
    public static List<TextSnippet>? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var result = new List<TextSnippet>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                try
                {
                    var snippet = element.Deserialize<TextSnippet>(Json);
                    if (snippet is null) continue;
                    var trigger = SnippetEngine.SanitizedTrigger(snippet.Trigger);
                    if (trigger.Length < SnippetEngine.MinTriggerLength || snippet.Replacement.Length == 0) continue;
                    result.Add(snippet with { Trigger = trigger, Folder = SnippetEngine.SanitizedFolder(snippet.Folder) });
                }
                catch (JsonException)
                {
                    // skip one bad entry, keep the rest
                }
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds imported snippets: an id already present replaces that snippet; a trigger another snippet
    /// already uses is skipped. Returns how many were added or replaced.
    /// </summary>
    public static (List<TextSnippet> Result, int Imported) Merge(IReadOnlyList<TextSnippet> existing, IEnumerable<TextSnippet> incoming)
    {
        var result = existing.ToList();
        var imported = 0;
        foreach (var snippet in incoming)
        {
            var index = result.FindIndex(s => s.Id == snippet.Id);
            var others = index >= 0 ? result.Where((_, i) => i != index) : result;
            if (SnippetEngine.IsDuplicateTrigger(snippet.Trigger, snippet.IgnoresCase, others)) continue;
            if (index >= 0) result[index] = snippet;
            else result.Add(snippet);
            imported++;
        }
        return (result, imported);
    }

    private static List<TextSnippet> Load()
    {
        try
        {
            return File.Exists(FilePath) ? Parse(File.ReadAllText(FilePath)) ?? [] : [];
        }
        catch (Exception ex)
        {
            Log.Error("Reading snippets failed", ex);
            return [];
        }
    }
}
