// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;

namespace Fieldkit.Core;

/// <summary>Tiny append-only diagnostics log at %APPDATA%\Fieldkit\fieldkit.log (rotated at 1 MB).</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = AppInfo.DataPath("fieldkit.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000)
                {
                    File.Copy(FilePath, FilePath + ".1", overwrite: true);
                    File.WriteAllText(FilePath, string.Empty);
                }
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
