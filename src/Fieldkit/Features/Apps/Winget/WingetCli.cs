// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Fieldkit.Core;
using Fieldkit.Features.Apps.Common;

namespace Fieldkit.Features.Apps.Winget;

/// <summary>
/// The one-time agreement: winget sources (notably the Microsoft Store) and many packages come
/// with terms. Fieldkit only passes <c>--accept-source-agreements</c> /
/// <c>--accept-package-agreements</c> after the person agreed on its consent screen.
/// </summary>
public static class WingetConsent
{
    public const string Key = "homebrew.agreementsAccepted";

    public static bool Accepted => Settings.GetBool(Key, false);

    public static void Accept() => Settings.Set(Key, true);

    public static void Withdraw() => Settings.Set(Key, false);
}

/// <summary>A finished read-only winget command.</summary>
public sealed record WingetRunResult(int ExitCode, string Output, bool TimedOut, bool Missing)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Missing;
}

/// <summary>Runs read-only winget commands (list, search, show, upgrade listing) one at a time.</summary>
public static class WingetCli
{
    private static readonly SemaphoreSlim ReadLane = new(1, 1);
    private static string? _path;

    /// <summary>The winget executable, re-detected while it is missing (App Installer can arrive any time).</summary>
    public static string? ExecutablePath => _path ??= WingetCommands.FindExecutable();

    public static bool IsAvailable => ExecutablePath is not null;

    /// <summary>Forgets the cached path so the next call looks again (Refresh button).</summary>
    public static void Redetect() => _path = WingetCommands.FindExecutable();

    public static async Task<WingetRunResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var path = ExecutablePath;
        if (path is null) return new WingetRunResult(-1, string.Empty, false, true);
        await ReadLane.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await ProcessRunner.RunAsync(path, WingetCommands.Join(args), null, ct, timeout, Encoding.UTF8)
                .ConfigureAwait(false);
            var output = result.Error.Length > 0 ? result.Output + result.Error : result.Output;
            return new WingetRunResult(result.ExitCode, output, result.TimedOut, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"winget {string.Join(' ', args.Take(2))} failed to start: {ex.Message}");
            _path = null;
            return new WingetRunResult(-1, ex.Message, false, true);
        }
        finally
        {
            ReadLane.Release();
        }
    }
}
