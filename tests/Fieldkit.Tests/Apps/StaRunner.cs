// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.ExceptionServices;

namespace Fieldkit.Tests.Apps;

/// <summary>Runs WPF code on a fresh STA thread (for RenderPreview tests of tool windows).</summary>
public static class StaRunner
{
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    /// <summary>A scratch folder for preview PNGs (kept so a human can look at them).</summary>
    public static string PreviewPath(string fileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "fieldkit-previews");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }
}
