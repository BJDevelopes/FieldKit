// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json;
using Fieldkit.Core;

namespace Fieldkit.Features.Island.Notes;

/// <summary>
/// The island's quick notes, kept in <c>Notes\notes.json</c> under the data directory. They stay out of the
/// settings store, so a settings export never carries what you wrote. Every change is saved at once (a note is
/// small); an unreadable file is kept as <c>notes.json.corrupt</c> and the notes start empty.
/// </summary>
public static class NotesStore
{
    private const long MaxFileBytes = 8L * 1024 * 1024;
    private static readonly object Gate = new();
    private static NotesCollection? _collection;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Directory => Path.Combine(AppInfo.DataDirectory, "Notes");

    public static string FilePath => Path.Combine(Directory, "notes.json");

    /// <summary>The notes, loaded on first use and saved after every change.</summary>
    public static NotesCollection Notes
    {
        get
        {
            lock (Gate)
            {
                if (_collection is null)
                {
                    var collection = new NotesCollection();
                    collection.Reset(Load(FilePath));
                    collection.Changed += () => Save(FilePath, collection.Items);
                    _collection = collection;
                }
                return _collection;
            }
        }
    }

    // ------------------------------------------------------------- pure parts

    public static string Serialize(IEnumerable<QuickNote> notes) => JsonSerializer.Serialize(notes.ToList(), Json);

    /// <summary>Parses saved notes; unreadable data gives null.</summary>
    public static List<QuickNote>? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<List<QuickNote>>(json, Json); }
        catch (JsonException) { return null; }
    }

    // ------------------------------------------------------------- disk

    public static List<QuickNote> Load(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxFileBytes) return [];
            var parsed = Deserialize(File.ReadAllText(file));
            if (parsed is null)
            {
                Log.Warn("The saved notes could not be read; keeping a copy and starting empty");
                try { File.Copy(file, file + ".corrupt", overwrite: true); } catch { }
                return [];
            }
            return NotesRules.Sanitize(parsed, DateTime.Now);
        }
        catch (Exception ex)
        {
            Log.Error("Reading the notes failed", ex);
            return [];
        }
    }

    public static void Save(string file, IEnumerable<QuickNote> notes)
    {
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var temp = file + ".tmp";
                File.WriteAllText(temp, Serialize(notes));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("Saving the notes failed", ex);
            }
        }
    }
}
