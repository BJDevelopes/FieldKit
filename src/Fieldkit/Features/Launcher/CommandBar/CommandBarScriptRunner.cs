// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using Fieldkit.Core;

namespace Fieldkit.Features.Launcher.CommandBar;

/// <summary>
/// Runs a saved script with what followed its name as its one argument and keeps what it printed
/// for this opening of the bar (port of upstream CommandBarScriptRunner). Unlike upstream, which
/// runs while typing pauses, a script only ever runs on an explicit Enter.
/// PowerShell (.ps1) runs through powershell.exe -File, batch files (.cmd/.bat) through cmd.exe,
/// anything else directly. No window is shown; output is capped at 64 KB; runs time out after 20 s.
/// </summary>
public sealed class CommandBarScriptRunner
{
    private const int MaxOutput = 64 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private int _generation;

    /// <summary>Raised on the UI thread when a result becomes ready.</summary>
    public event Action? ResultReady;

    public bool IsAwaiting => _inFlight.Count > 0;

    public bool IsRunning(Guid linkId, string argument) => _inFlight.Contains(Key(linkId, argument));

    public void Reset()
    {
        _generation++;
        _cache.Clear();
        _inFlight.Clear();
    }

    public string? Cached(Guid linkId, string argument) => _cache.TryGetValue(Key(linkId, argument), out var text) ? text : null;

    public void RunNow(CommandBarLink link, string argument)
    {
        var key = Key(link.Id, argument);
        if (_cache.ContainsKey(key) || !_inFlight.Add(key)) return;
        var generation = _generation;
        var path = CommandBarLinks.ExpandHome(link.Destination.Trim());
        Task.Run(async () =>
        {
            var (exit, output) = await RunAsync(path, argument).ConfigureAwait(false);
            var text = CommandBarLinks.ResultText(output) ?? (exit == 0 ? null : CommandBarStrings.ScriptRunFailed);
            await _dispatcher.InvokeAsync(() =>
            {
                if (generation != _generation) return;
                _inFlight.Remove(key);
                if (text is null) return;
                _cache[key] = text;
                ResultReady?.Invoke();
            });
        });
    }

    /// <summary>A script marked to run from its own shortcut: no argument, nothing on screen, a beep on failure.</summary>
    public static void RunDirectly(CommandBarLink link)
    {
        var path = CommandBarLinks.ExpandHome(link.Destination.Trim());
        Task.Run(async () =>
        {
            var (exit, _) = await RunAsync(path, null).ConfigureAwait(false);
            if (exit != 0) CommandBarSystem.Beep();
        });
    }

    /// <summary>The program and arguments that run a script file.</summary>
    public static ProcessStartInfo StartInfo(string path, string? argument)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        ProcessStartInfo psi;
        switch (extension)
        {
            case ".ps1":
                psi = new ProcessStartInfo("powershell.exe");
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(path);
                break;
            case ".cmd" or ".bat":
                psi = new ProcessStartInfo("cmd.exe");
                psi.ArgumentList.Add("/d");
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(path);
                break;
            default:
                psi = new ProcessStartInfo(path);
                break;
        }
        if (!string.IsNullOrEmpty(argument)) psi.ArgumentList.Add(argument);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) psi.WorkingDirectory = directory;
        return psi;
    }

    private static async Task<(int Exit, string Output)> RunAsync(string path, string? argument)
    {
        if (!File.Exists(path)) return (-1, string.Empty);
        try
        {
            using var process = Process.Start(StartInfo(path, argument));
            if (process is null) return (-1, string.Empty);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(Timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, string.Empty);
            }
            var output = await stdout.ConfigureAwait(false);
            if (output.Trim().Length == 0) output = await stderr.ConfigureAwait(false);
            if (output.Length > MaxOutput) output = output[..MaxOutput];
            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            Log.Warn($"Command Bar: running {path} failed: {ex.Message}");
            return (-1, string.Empty);
        }
    }

    private static string Key(Guid id, string argument) => id.ToString("N") + "|" + argument;
}
