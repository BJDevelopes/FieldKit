// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Windows.Threading;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Apps.Winget;

/// <summary>A package an operation acts on.</summary>
public sealed record WingetTarget(string Name, string Id, string Source);

/// <summary>Live state of the current (or last) operation, shown by <see cref="WingetOperationView"/>.</summary>
public sealed record WingetOperationStatus
{
    public required WingetAction Action { get; init; }
    /// <summary>Feature that started it ("homebrew" or "appUpdates").</summary>
    public required string Owner { get; init; }
    /// <summary>The package being worked on right now (null for "update all" / sources).</summary>
    public WingetTarget? Target { get; init; }
    public IReadOnlyList<WingetTarget> Targets { get; init; } = [];
    public WingetPhase Phase { get; init; }
    public WingetResult Result { get; init; }
    public double? Progress { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public string? LastActivity { get; init; }
    /// <summary>1-based step of a multi-package run.</summary>
    public int Step { get; init; } = 1;
    public int Steps { get; init; } = 1;

    public bool IsActive => Result == WingetResult.Running;

    /// <summary>Whether this operation is (or was) about the package with this id.</summary>
    public bool Concerns(string id) =>
        Target is not null && string.Equals(Target.Id, id, StringComparison.OrdinalIgnoreCase)
        || Targets.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The single lane every installing, updating or removing winget command goes through, so the
/// Packages tool and App updates never run two package operations at once. Output streams into
/// <see cref="Log"/>; progress, phase and activity are read from it as it arrives. Events are
/// raised on the thread that started the operation (the UI thread).
/// </summary>
public sealed class WingetOperationLane
{
    /// <summary>A run that prints nothing for this long is considered hung and stopped.
    /// Big downloads are slow but never silent, so the bound is on silence, not total time.</summary>
    public static readonly TimeSpan SilenceTimeout = TimeSpan.FromMinutes(15);

    public static WingetOperationLane Shared { get; } = new();

    private readonly StringBuilder _log = new();
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _cleanup;
    private Dispatcher? _dispatcher;

    public WingetOperationStatus? Status { get; private set; }

    public string Log
    {
        get
        {
            lock (_log) return _log.ToString();
        }
    }

    /// <summary>The failed command for a person to run in a console (needs-terminal case).</summary>
    public string? TerminalCommand { get; private set; }

    public bool IsRunning => Status?.IsActive == true;

    /// <summary>Phase, progress, activity or result changed.</summary>
    public event Action? StatusChanged;

    /// <summary>New log text arrived (already appended to <see cref="Log"/>).</summary>
    public event Action<string>? LogAppended;

    /// <summary>An operation finished (any result).</summary>
    public event Action<WingetOperationStatus>? Completed;

    /// <summary>Starts an operation. Returns false when one is already running, winget is
    /// missing or a target cannot be put on a command line.</summary>
    public bool Start(WingetAction action, string owner, IReadOnlyList<WingetTarget> targets)
    {
        if (IsRunning) return false;
        var path = WingetCli.ExecutablePath;
        if (path is null) return false;
        var commands = Commands(action, targets);
        if (commands is null || commands.Count == 0) return false;

        _dispatcher = Dispatcher.CurrentDispatcher;
        _cleanup?.Stop();
        lock (_log) _log.Clear();
        TerminalCommand = null;
        Status = new WingetOperationStatus
        {
            Action = action,
            Owner = owner,
            Target = targets.Count > 0 ? targets[0] : null,
            Targets = targets,
            Phase = InitialPhase(action),
            Result = WingetResult.Running,
            StartedAt = DateTime.Now,
            Steps = commands.Count,
        };
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        StatusChanged?.Invoke();
        _ = Task.Run(() => RunAsync(path, action, targets, commands, token));
        return true;
    }

    /// <summary>The commands an action runs (one per package for a chosen set).</summary>
    public static List<List<string>>? Commands(WingetAction action, IReadOnlyList<WingetTarget> targets)
    {
        List<string>? Single(Func<WingetTarget, List<string>?> build) => targets.Count == 1 ? build(targets[0]) : null;
        switch (action)
        {
            case WingetAction.Install:
                return Single(t => WingetCommands.Install(t.Id, t.Source)) is { } i ? [i] : null;
            case WingetAction.Uninstall:
                return Single(t => WingetCommands.Uninstall(t.Id, t.Source)) is { } u ? [u] : null;
            case WingetAction.Upgrade:
                return Single(t => WingetCommands.Upgrade(t.Id, t.Source)) is { } g ? [g] : null;
            case WingetAction.UpgradeAll:
                return [WingetCommands.UpgradeAll()];
            case WingetAction.UpdateSources:
                return [WingetCommands.UpdateSources()];
            case WingetAction.UpgradeSelected:
                var list = new List<List<string>>();
                foreach (var t in targets)
                {
                    if (WingetCommands.Upgrade(t.Id, t.Source) is not { } c) return null;
                    list.Add(c);
                }
                return list;
            default:
                return null;
        }
    }

    public static WingetPhase InitialPhase(WingetAction action) =>
        action == WingetAction.Uninstall ? WingetPhase.Uninstalling
        : action == WingetAction.UpdateSources ? WingetPhase.Refreshing
        : WingetPhase.Preparing;

    public void Cancel()
    {
        if (!IsRunning) return;
        AppendLog("\nCancelled.\n");
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Stops the running operation if <paramref name="owner"/> started it (feature switched off).</summary>
    public void CancelOwnedBy(string owner)
    {
        if (IsRunning && Status?.Owner == owner) Cancel();
    }

    public void ClearLog()
    {
        _cleanup?.Stop();
        lock (_log) _log.Clear();
        TerminalCommand = null;
        if (!IsRunning) Status = null;
        StatusChanged?.Invoke();
    }

    /// <summary>Puts a finished or running state in place without running anything (previews, tests).</summary>
    public void SetPreview(WingetOperationStatus? status, string log, string? terminalCommand = null)
    {
        Status = status;
        lock (_log)
        {
            _log.Clear();
            _log.Append(log);
        }
        TerminalCommand = terminalCommand;
    }

    private async Task RunAsync(string path, WingetAction action, IReadOnlyList<WingetTarget> targets,
        List<List<string>> commands, CancellationToken token)
    {
        var cancelled = false;
        var timedOut = false;
        int? failedExit = null;
        string failedOutput = string.Empty;
        List<string>? failedArgs = null;
        var failures = 0;

        for (var i = 0; i < commands.Count; i++)
        {
            var args = commands[i];
            if (commands.Count > 1)
            {
                var step = i + 1;
                var target = i < targets.Count ? targets[i] : null;
                Post(() => Update(s => s with { Step = step, Target = target ?? s.Target, Progress = null, Phase = InitialPhase(action) }));
            }
            Post(() => AppendLog($"$ {WingetCommands.TerminalCommand(args)}\n"));
            using var silence = new CancellationTokenSource(SilenceTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, silence.Token);
            ProcessResult result;
            try
            {
                result = await ProcessRunner.RunAsync(path, WingetCommands.Join(args), line =>
                {
                    try { silence.CancelAfter(SilenceTimeout); } catch (ObjectDisposedException) { }
                    OnLine(line, action);
                }, linked.Token, null, Encoding.UTF8).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested) cancelled = true;
                else timedOut = true;
                break;
            }
            catch (Exception ex)
            {
                failures++;
                failedExit = -1;
                failedOutput = ex.Message;
                failedArgs = args;
                Post(() => AppendLog(ex.Message + "\n"));
                continue;
            }
            if (result.ExitCode != 0)
            {
                failures++;
                failedExit = result.ExitCode;
                failedOutput = result.Output + result.Error;
                failedArgs = args;
            }
        }

        Post(() => Finish(action, cancelled, timedOut, failures, failedExit ?? 0, failedOutput, failedArgs));
    }

    private void Finish(WingetAction action, bool cancelled, bool timedOut, int failures, int exitCode, string output, List<string>? args)
    {
        if (Status is null) return;
        WingetResult result;
        string? activity = null;
        if (cancelled)
        {
            result = WingetResult.Cancelled;
        }
        else if (timedOut)
        {
            result = WingetResult.Failed;
            activity = WingetStrings.OperationSilent;
        }
        else if (failures == 0)
        {
            result = WingetResult.Succeeded;
        }
        else
        {
            result = WingetProgress.Classify(exitCode, output, false);
            var visible = WingetProgress.VisibleError(output);
            var described = WingetCommands.DescribeExitCode(exitCode);
            activity = visible.Length > 0 ? visible : described;
            if (result == WingetResult.NeedsTerminal && args is not null) TerminalCommand = WingetCommands.TerminalCommand(args);
        }
        Update(s => s with
        {
            Result = result,
            FinishedAt = DateTime.Now,
            Progress = result == WingetResult.Succeeded ? 1 : s.Progress,
            Phase = result == WingetResult.Succeeded ? WingetPhase.Refreshing : s.Phase,
            LastActivity = activity ?? (result == WingetResult.Succeeded ? null : s.LastActivity),
        });
        var finished = Status;
        ScheduleCleanup(result, finished);
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        if (finished is not null) Completed?.Invoke(finished);
        Core.Log.Info($"winget {action} finished: {result}");
    }

    private void ScheduleCleanup(WingetResult result, WingetOperationStatus? finished)
    {
        _cleanup?.Stop();
        var (delay, whole) = result switch
        {
            WingetResult.Succeeded => (TimeSpan.FromSeconds(8), true),
            WingetResult.Cancelled => (TimeSpan.FromSeconds(6), true),
            _ => (TimeSpan.FromSeconds(20), false),
        };
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (IsRunning || !ReferenceEquals(Status, finished)) return;
            lock (_log) _log.Clear();
            if (whole)
            {
                TerminalCommand = null;
                Status = null;
            }
            StatusChanged?.Invoke();
        };
        _cleanup = timer;
        timer.Start();
    }

    private void OnLine(string raw, WingetAction action)
    {
        var line = WingetText.StripAnsi(raw);
        var fraction = WingetProgress.Fraction(line);
        var phase = WingetProgress.Phase(line, action);
        var activity = WingetProgress.Activity(line);
        var noise = WingetText.IsNoise(line);
        Post(() =>
        {
            if (!noise) AppendLog(line.TrimEnd() + "\n");
            if (Status is not { IsActive: true }) return;
            if (fraction is null && phase is null && activity is null) return;
            Update(s => s with
            {
                Progress = fraction ?? s.Progress,
                Phase = phase ?? s.Phase,
                LastActivity = activity ?? s.LastActivity,
            });
        });
    }

    private void AppendLog(string text)
    {
        lock (_log) _log.Append(text);
        LogAppended?.Invoke(text);
    }

    private void Update(Func<WingetOperationStatus, WingetOperationStatus> change)
    {
        if (Status is null) return;
        Status = change(Status);
        StatusChanged?.Invoke();
    }

    private void Post(Action action)
    {
        var d = _dispatcher;
        if (d is null || d.HasShutdownStarted) return;
        d.BeginInvoke(() =>
        {
            try { action(); }
            catch (Exception ex) { Core.Log.Error("winget operation update failed", ex); }
        });
    }
}
