// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text;

namespace Fieldkit.Features.Island.Agents;

/// <summary>A folder an agent writes its logs to, relative to the user profile.</summary>
public sealed record AgentLogRoot(AgentProvider Provider, string Path);

/// <summary>Where each log file was read up to, with the parser state carried between reads.</summary>
public sealed class AgentLogCursor(string path, AgentProvider provider)
{
    public string Path { get; } = path;
    public AgentProvider Provider { get; } = provider;
    public long Offset { get; set; }
    public long Identity { get; set; }
    public ulong Fingerprint { get; set; }
    public byte[] Pending { get; set; } = [];
    /// <summary>Inside a line longer than <see cref="AgentLogReader.MaximumLine"/>, skipped until its newline.</summary>
    public bool Skipping { get; set; }
    public AgentLogState State { get; set; } = new();
    public DateTime Modified { get; set; }
    public long Size { get; set; }
    /// <summary>OpenCode only: rows still being written, re-read on every pass (id → updated, length).</summary>
    public Dictionary<string, (long Updated, long Length, DateTime Changed)> OpenRows { get; } = new(StringComparer.Ordinal);

    /// <summary>Claude subagent logs count toward their parent's turn; Codex side threads never track turns.</summary>
    public bool TracksTurns => Provider switch
    {
        AgentProvider.Claude => AgentLogReader.ClaudeParent(Path) is null,
        AgentProvider.Codex => !System.IO.Path.GetFileName(Path).Contains('_'),
        _ => true,
    };

    public void StartOver()
    {
        Offset = 0;
        Pending = [];
        Skipping = false;
        State = new AgentLogState();
        OpenRows.Clear();
    }
}

/// <summary>
/// Finds and incrementally reads the agents' JSONL logs (port of upstream <c>AgentLogReader</c>):
/// 4 MiB chunks split on newlines, partial lines kept for the next read, lines over 32 MiB skipped,
/// and a fingerprint of the bytes already read so a rewritten file is read again from its start.
/// </summary>
public static class AgentLogReader
{
    public const int ChunkSize = 4 << 20;
    public const int MaximumLine = 32 << 20;

    public static List<AgentLogRoot> Roots(string home) =>
    [
        new(AgentProvider.Claude, System.IO.Path.Combine(home, ".claude", "projects")),
        new(AgentProvider.Claude, System.IO.Path.Combine(home, ".config", "claude", "projects")),
        new(AgentProvider.Codex, System.IO.Path.Combine(home, ".codex", "sessions")),
        new(AgentProvider.Codex, System.IO.Path.Combine(home, ".codex", "archived_sessions")),
        new(AgentProvider.OpenCode, System.IO.Path.Combine(home, ".local", "share", "opencode")),
        new(AgentProvider.Copilot, System.IO.Path.Combine(home, ".copilot", "session-state")),
    ];

    /// <summary>The parent log of a Claude subagent file (…/&lt;session&gt;/subagents/x.jsonl → …/&lt;session&gt;.jsonl).</summary>
    public static string? ClaudeParent(string path)
    {
        foreach (var marker in new[] { "\\subagents\\", "/subagents/" })
        {
            var i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i > 0) return path[..i] + ".jsonl";
        }
        return null;
    }

    /// <summary>Log files modified since <paramref name="horizon"/>, oldest first, Claude subagent logs last.</summary>
    public static List<(string Path, AgentProvider Provider)> Discover(IEnumerable<AgentLogRoot> roots, DateTime horizon)
    {
        var found = new List<(string Path, AgentProvider Provider, DateTime Modified, bool Sub)>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Path)) continue;
            try
            {
                switch (root.Provider)
                {
                    case AgentProvider.Claude or AgentProvider.Codex:
                        foreach (var f in Directory.EnumerateFiles(root.Path, "*.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                            Add(f, root.Provider);
                        break;
                    case AgentProvider.Copilot:
                        // Exactly <root>/<session>/events.jsonl: session folders can hold whole workspaces.
                        foreach (var dir in Directory.EnumerateDirectories(root.Path))
                        {
                            if (System.IO.Path.GetFileName(dir).StartsWith('.')) continue;
                            var f = System.IO.Path.Combine(dir, "events.jsonl");
                            if (File.Exists(f)) Add(f, root.Provider);
                        }
                        break;
                    case AgentProvider.OpenCode:
                        var db = System.IO.Path.Combine(root.Path, "opencode.db");
                        if (File.Exists(db)) Add(db, root.Provider);
                        break;
                }
            }
            catch (Exception ex)
            {
                Core.Log.Warn($"Listing {root.Provider} logs failed: {ex.Message}");
            }
        }
        return found
            .OrderBy(f => f.Sub)
            .ThenBy(f => f.Modified)
            .Select(f => (f.Path, f.Provider))
            .ToList();

        void Add(string path, AgentProvider provider)
        {
            var modified = Modified(path);
            if (modified < horizon) return;
            found.Add((path, provider, modified, provider == AgentProvider.Claude && ClaudeParent(path) is not null));
        }
    }

    /// <summary>Last write time, including OpenCode's write-ahead log.</summary>
    public static DateTime Modified(string path)
    {
        var t = File.GetLastWriteTimeUtc(path);
        var wal = path + "-wal";
        if (path.EndsWith(".db", StringComparison.OrdinalIgnoreCase) && File.Exists(wal))
        {
            var w = File.GetLastWriteTimeUtc(wal);
            if (w > t) t = w;
        }
        return t;
    }

    /// <summary>Reads the lines appended since the cursor (complete lines only).</summary>
    public static List<string> ReadAppended(AgentLogCursor cursor)
    {
        var lines = new List<string>();
        FileStream stream;
        try
        {
            stream = new FileStream(cursor.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        }
        catch (IOException) { return lines; }
        catch (UnauthorizedAccessException) { return lines; }
        using (stream)
        {
            var size = stream.Length;
            var identity = File.GetCreationTimeUtc(cursor.Path).Ticks;
            if (cursor.Offset > 0 && (identity != cursor.Identity || size < cursor.Offset || Fingerprint(stream, cursor.Offset) != cursor.Fingerprint))
                cursor.StartOver();
            cursor.Identity = identity;
            cursor.Size = size;
            cursor.Modified = File.GetLastWriteTimeUtc(cursor.Path);
            if (size == cursor.Offset) return lines;
            stream.Position = cursor.Offset;
            var buffer = new byte[(int)Math.Min(ChunkSize, Math.Max(1, size - cursor.Offset))];
            var pending = new MemoryStream();
            pending.Write(cursor.Pending);
            var skipping = cursor.Skipping;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    if (!skipping)
                    {
                        pending.Write(buffer, start, i - start);
                        if (pending.Length > 0) lines.Add(Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length).TrimEnd('\r'));
                    }
                    pending.SetLength(0);
                    skipping = false;
                    start = i + 1;
                }
                if (!skipping)
                {
                    pending.Write(buffer, start, read - start);
                    if (pending.Length > MaximumLine)
                    {
                        pending.SetLength(0);
                        skipping = true;
                    }
                }
                cursor.Offset += read;
            }
            cursor.Pending = skipping ? [] : pending.ToArray();
            cursor.Skipping = skipping;
            cursor.Fingerprint = Fingerprint(stream, cursor.Offset);
        }
        return lines;
    }

    /// <summary>FNV-1a 64 over the offset, the first 4 KB and the 4 KB before the offset.</summary>
    public static ulong Fingerprint(Stream stream, long offset)
    {
        const ulong prime = 1099511628211;
        var hash = 14695981039346656037;
        void Mix(ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
            {
                hash ^= b;
                hash *= prime;
            }
        }
        Mix(BitConverter.GetBytes(offset));
        var saved = stream.Position;
        var head = new byte[(int)Math.Min(4096, offset)];
        stream.Position = 0;
        stream.ReadExactly(head);
        Mix(head);
        var tailStart = Math.Max(0, offset - 4096);
        var tail = new byte[(int)(offset - tailStart)];
        stream.Position = tailStart;
        stream.ReadExactly(tail);
        Mix(tail);
        stream.Position = saved;
        return hash;
    }
}
