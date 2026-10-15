// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;

namespace Fieldkit.Core;

/// <summary>
/// Lets scripts drive the running app: <c>Fieldkit.exe --action keepAwake.toggle</c> sends the action id
/// over a named pipe only the same Windows user can open, and the running instance runs the registered
/// <see cref="ToolAction"/> on its UI thread (upstream's Shortcuts actions, for Stream Deck, AutoHotkey,
/// Task Scheduler and the like).
/// </summary>
public static class CommandChannel
{
    private static readonly string PipeName = $"Fieldkit.Commands.{Process.GetCurrentProcess().SessionId}";
    private static Thread? _listener;

    /// <summary>Sends one command to the running instance and returns its reply (null when none is running).</summary>
    public static string? Send(string command, int timeoutMs = 2000)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(timeoutMs);
            // Actions that open a window (history, Command Bar) may take focus with the caller's rights.
            Interop.Native.AllowSetForegroundWindow(-1); // ASFW_ANY
            // Reader and writer leave the pipe open: disposing either would close it under the other.
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            writer.WriteLine(command);
            return reader.ReadLine();
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Starts serving commands (call once in the running instance).</summary>
    public static void Listen()
    {
        if (_listener is not null) return;
        _listener = new Thread(Serve) { IsBackground = true, Name = "Fieldkit commands" };
        _listener.Start();
    }

    private static void Serve()
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var line = reader.ReadLine() ?? string.Empty;
                writer.WriteLine(Handle(line));
            }
            catch (Exception ex)
            {
                Log.Warn($"Command channel: {ex.Message}");
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>Runs a command line ("action &lt;id&gt;", "panel [section]") and returns a one-line reply.</summary>
    public static string Handle(string line)
    {
        var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "error: empty command";
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return "error: not ready";
        return parts[0] switch
        {
            "action" when parts.Length == 2 => dispatcher.Invoke(() => RunAction(parts[1])),
            "panel" => dispatcher.Invoke(() =>
            {
                PanelHost.Show(parts.Length == 2 ? parts[1] : null);
                return "ok";
            }),
            _ => $"error: unknown command '{parts[0]}'",
        };
    }

    /// <summary>Runs a registered action by id (UI thread).</summary>
    public static string RunAction(string id)
    {
        var action = FeatureRuntime.Registry.Actions.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        if (action is null) return $"error: no action '{id}' (Fieldkit.exe --list-actions shows them)";
        if (!FeatureCatalog.IsAvailable(action.FeatureId))
            return $"error: '{FeatureCatalog.Get(action.FeatureId).Title}' is not installed (Settings > Features)";
        try
        {
            action.Run();
            Log.Info($"Command: ran {action.Id}");
            return "ok";
        }
        catch (Exception ex)
        {
            Log.Error($"Command {action.Id} failed", ex);
            return $"error: {ex.Message}";
        }
    }

    /// <summary>Lines describing every action, for <c>--list-actions</c>.</summary>
    public static IEnumerable<string> DescribeActions() =>
        FeatureRuntime.Registry.Actions
            .OrderBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => $"{a.Id,-44} {a.Title}  [{FeatureCatalog.Get(a.FeatureId).Title}{(FeatureCatalog.IsAvailable(a.FeatureId) ? "" : ", not installed")}]");

    // A GUI app has no console; attach to the one that started it so command output is visible.
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    public static void AttachParentConsole()
    {
        if (AttachConsole(-1))
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stdout);
            Console.WriteLine();
        }
    }
}
