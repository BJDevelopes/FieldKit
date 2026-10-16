// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Reflection;

namespace Fieldkit.Core;

/// <summary>Product identity in one place so a rename touches a single file.</summary>
public static class AppInfo
{
    public const string Name = "Fieldkit";
    public const string Tagline = "One tray icon doing the job of a dozen Windows utilities.";
    public const string RepositoryUrl = "https://github.com/BJDevelopes/FieldKit";
    public const string UpstreamUrl = "https://github.com/vorssaint/vorssaint-utils";
    public const string SupportUrl = "https://buymeacoffee.com/bjdevelopes";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>%APPDATA%\Fieldkit — settings, histories and other private state.</summary>
    public static string DataDirectory
    {
        get
        {
            // FIELDKIT_DATA_DIR isolates dev/test runs (UI snapshots, selftests) from real settings.
            var dir = Environment.GetEnvironmentVariable("FIELDKIT_DATA_DIR") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DataPath(string fileName) => Path.Combine(DataDirectory, fileName);

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Name + ".exe");
}
