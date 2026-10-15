// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Media;

/// <summary>
/// The local ffmpeg used by video tools. It is not bundled: Fieldkit looks for it on PATH and in
/// winget's install locations, and offers to install it with winget when the person asks.
/// </summary>
public static class Ffmpeg
{
    public const string WingetId = "Gyan.FFmpeg";
    private static string? _cached;
    private static DateTime _checkedAt;

    public static event Action? AvailabilityChanged;

    /// <summary>Full path of ffmpeg.exe, or null. Cached for a minute.</summary>
    public static string? Locate(bool refresh = false)
    {
        if (!refresh && DateTime.UtcNow - _checkedAt < TimeSpan.FromMinutes(1)) return _cached;
        _checkedAt = DateTime.UtcNow;
        _cached = Candidates().FirstOrDefault(File.Exists);
        return _cached;
    }

    public static bool IsAvailable => Locate() is not null;

    private static IEnumerable<string> Candidates()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string path;
            try { path = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe"); }
            catch { continue; }
            yield return path;
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
        {
            IEnumerable<string> found;
            try
            {
                found = Directory.EnumerateDirectories(packages, "*FFmpeg*")
                    .SelectMany(d => SafeEnumerate(d, "ffmpeg.exe"))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();
            }
            catch
            {
                found = [];
            }
            foreach (var f in found) yield return f;
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "ffmpeg", "bin", "ffmpeg.exe");
        yield return Path.Combine(programFiles, "WinGet", "Links", "ffmpeg.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "ffmpeg.exe");
        yield return @"C:\ProgramData\chocolatey\bin\ffmpeg.exe";
        yield return @"C:\ffmpeg\bin\ffmpeg.exe";
    }

    private static IEnumerable<string> SafeEnumerate(string root, string name)
    {
        try { return Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).ToList(); }
        catch { return []; }
    }

    /// <summary>Runs winget in a visible console so the person sees (and confirms) the install.</summary>
    public static void InstallWithWinget()
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k winget install --id {WingetId} -e --source winget")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Starting winget failed", ex);
            MessageBox.Show("winget could not be started. Install ffmpeg from https://www.gyan.dev/ffmpeg/builds/ or with \"winget install Gyan.FFmpeg\".",
                "Install ffmpeg", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>Reads duration, size and streams of a file from ffmpeg's banner.</summary>
    public static async Task<MediaProbe> ProbeAsync(string input, CancellationToken ct = default)
    {
        var exe = Locate() ?? throw new FileNotFoundException("ffmpeg is not installed.");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-i", input }) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return FfmpegArgs.ParseProbe(stderr);
    }

    /// <summary>
    /// Runs ffmpeg with progress (0…1 over <paramref name="expectedSeconds"/>). Cancelling kills the
    /// process; a failure throws with ffmpeg's last messages.
    /// </summary>
    public static async Task RunAsync(IReadOnlyList<string> args, double expectedSeconds, IProgress<double>? progress, CancellationToken ct)
    {
        var exe = Locate() ?? throw new FileNotFoundException("ffmpeg is not installed.");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var a in FfmpegArgs.WithProgress([.. args])) psi.ArgumentList.Add(a);
        using var process = new Process { StartInfo = psi };
        var tail = new Queue<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (tail)
            {
                tail.Enqueue(e.Data);
                while (tail.Count > 12) tail.Dequeue();
            }
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null || progress is null || expectedSeconds <= 0) return;
            if (FfmpegArgs.ParseProgressSeconds(e.Data) is { } seconds) progress.Report(Math.Clamp(seconds / expectedSeconds, 0, 1));
        };
        process.Start();
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        using (ct.Register(() =>
               {
                   try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                   catch { }
               }))
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            string message;
            lock (tail) message = string.Join(Environment.NewLine, tail);
            throw new InvalidOperationException($"ffmpeg failed ({process.ExitCode}): {message}");
        }
        progress?.Report(1);
    }

    public static void NotifyChanged() => AvailabilityChanged?.Invoke();

    /// <summary>The "ffmpeg is not installed" card with Install and Check again.</summary>
    public static FrameworkElement MissingCard(Action? onFound = null)
    {
        var status = Ui.Caption("Video tools use ffmpeg, which is not installed. Fieldkit does not bundle it; winget can install it for you.");
        var install = Ui.Button("Install ffmpeg with winget", InstallWithWinget, accent: true, glyph: Glyphs.Download);
        Button? check = null;
        check = Ui.Button("Check again", () =>
        {
            if (Locate(refresh: true) is not null)
            {
                NotifyChanged();
                onFound?.Invoke();
            }
            else
            {
                status.Text = "ffmpeg was not found yet. When winget finishes, choose Check again.";
            }
        });
        var content = Ui.Stack(10, Ui.Row(8, Ui.Icon(Glyphs.Info, 16, "Orange"), Ui.Text("ffmpeg needed", 14, FontWeights.SemiBold)), status, Ui.Row(8, install, check));
        return Ui.Card(content);
    }
}
