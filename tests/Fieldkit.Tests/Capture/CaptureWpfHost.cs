// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Tests.Support;

namespace Fieldkit.Tests.Capture;

/// <summary>Capture preview tests run on the shared <see cref="WpfTestHost"/> (one Application per process).</summary>
internal static class CaptureWpfHost
{
    /// <summary>Where preview PNGs go (FIELDKIT_PREVIEW_DIR, or %TEMP%\fieldkit-capture-previews).</summary>
    public static string PreviewDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("FIELDKIT_PREVIEW_DIR") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Path.GetTempPath(), "fieldkit-capture-previews");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void Run(Action action) => WpfTestHost.Run(action);

    /// <summary>Runs async UI work on the host thread without blocking it (continuations come back to it).</summary>
    public static Task<T> RunAsync<T>(Func<Task<T>> func) => WpfTestHost.RunAsync(func);

    public static T Run<T>(Func<T> func)
    {
        T result = default!;
        Run(() => result = func());
        return result;
    }
}
