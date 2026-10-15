// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using Fieldkit.Features.Apps.Common;
using Fieldkit.Features.Apps.Uninstaller;

namespace Fieldkit.Tests.Apps.Uninstaller;

/// <summary>Scans a fake user profile, ProgramData, Program Files and registry built under %TEMP%.</summary>
public sealed class LeftoverScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fieldkit-uninst-tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> _links = new(StringComparer.OrdinalIgnoreCase);
    private readonly FakeRegistry _registry = new();

    private string Profile => Path.Combine(_root, "Users", "alex");
    private string Roaming => Path.Combine(Profile, "AppData", "Roaming");
    private string Local => Path.Combine(Profile, "AppData", "Local");
    private string LocalLow => Path.Combine(Profile, "AppData", "LocalLow");
    private string ProgramData => Path.Combine(_root, "ProgramData");
    private string ProgramFiles => Path.Combine(_root, "Program Files");
    private string StartMenu => Path.Combine(Roaming, "Microsoft", "Windows", "Start Menu", "Programs");
    private string CommonStartMenu => Path.Combine(ProgramData, "Microsoft", "Windows", "Start Menu", "Programs");
    private string Desktop => Path.Combine(Profile, "Desktop");
    private string PublicDesktop => Path.Combine(_root, "Users", "Public", "Desktop");
    private string Temp => Path.Combine(Local, "Temp");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Dir(params string[] parts)
    {
        var p = Path.Combine([.. parts]);
        Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p, "data.bin"), new string('x', 1000));
        return p;
    }

    private string FileAt(string folder, string name, int bytes = 100)
    {
        Directory.CreateDirectory(folder);
        var p = Path.Combine(folder, name);
        File.WriteAllText(p, new string('x', bytes));
        return p;
    }

    private string Link(string folder, string name, string target)
    {
        var p = FileAt(folder, name, 10);
        _links[p] = target;
        return p;
    }

    private ScanEnvironment Env(bool admin = false) => new()
    {
        UserProfile = Profile,
        RoamingAppData = Roaming,
        LocalAppData = Local,
        LocalLow = LocalLow,
        ProgramData = ProgramData,
        Temp = Temp,
        UserStartMenu = StartMenu,
        CommonStartMenu = CommonStartMenu,
        UserDesktop = Desktop,
        PublicDesktop = PublicDesktop,
        ProgramFolders = [ProgramFiles, Path.Combine(Local, "Programs")],
        ExcludedPaths = [Path.Combine(Roaming, "Fieldkit")],
        RegistryRoots = [@"HKEY_CURRENT_USER\Software", @"HKEY_LOCAL_MACHINE\SOFTWARE"],
        Registry = _registry,
        ResolveShortcut = p => _links.TryGetValue(p, out var t) ? t : null,
        IsAdmin = admin,
    };

    private InstalledApp Spotify(string install) => new()
    {
        Id = "spotify",
        Name = "Spotify",
        Publisher = "Spotify AB",
        Version = "1.2.47",
        InstallLocation = install,
        Source = InstalledAppSource.UserRegistry,
    };

    private void BuildSpotifyFixture(string install)
    {
        Dir(install);
        FileAt(install, "Spotify.exe");
        Dir(Roaming, "Spotify");
        Dir(Roaming, "SpotifyWebHelper");
        Dir(Roaming, "Microsoft", "Windows", "Recent");
        Dir(Roaming, "Fieldkit");
        Dir(Roaming, "Discord");
        Dir(Local, "Spotify");
        Dir(Local, "Packages", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0");
        FileAt(Path.Combine(Local, "CrashDumps"), "Spotify.exe.4242.dmp", 5000);
        FileAt(Path.Combine(Local, "CrashDumps"), "Discord.exe.1.dmp", 5000);
        Dir(Temp, "Spotify");
        Dir(Temp, "SpotifyInstallerCache");
        Dir(ProgramData, "Spotify");
        Link(Desktop, "Spotify.lnk", Path.Combine(install, "Spotify.exe"));
        Link(Desktop, "Notes.lnk", Path.Combine(ProgramFiles, "Notes", "notes.exe"));
        Link(StartMenu, "Spotify.lnk", Path.Combine(install, "Spotify.exe"));
        Link(StartMenu, "Discord.lnk", Path.Combine(Local, "Discord", "Update.exe"));
        _registry.Add(@"HKEY_CURRENT_USER\Software", "Spotify", "Microsoft", "Classes", "Discord", "Spotify AB");
        _registry.Add(@"HKEY_CURRENT_USER\Software\Spotify AB", "Spotify", "Other");
        _registry.Add(@"HKEY_LOCAL_MACHINE\SOFTWARE", "Spotify", "Microsoft");
    }

    private static Leftover Find(List<Leftover> items, string path) =>
        items.SingleOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))
        ?? throw new Xunit.Sdk.XunitException($"{path} not found in:\n{string.Join("\n", items.Select(i => i.Path))}");

    private static void Missing(List<Leftover> items, string path) =>
        Assert.DoesNotContain(items, i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void FindsLeftoversBeforeUninstall()
    {
        var install = Path.Combine(Local, "Programs", "Spotify");
        BuildSpotifyFixture(install);
        var env = Env();
        var id = LeftoverRules.Identity(Spotify(install), [Path.Combine(install, "Spotify.exe")], env.ProgramFolders);
        var others = new OtherApps([new InstalledApp { Id = "discord", Name = "Discord", Publisher = "Discord Inc.", InstallLocation = Path.Combine(Local, "Discord") }]);
        var items = LeftoverScanner.Scan(id, others, env, afterUninstall: false);

        var roaming = Find(items, Path.Combine(Roaming, "Spotify"));
        Assert.Equal(LeftoverMatch.Exact, roaming.Confidence);
        Assert.True(roaming.Include);
        Assert.Equal(LeftoverCategory.Support, roaming.Category);
        Assert.True(roaming.Size >= 1000);

        var helper = Find(items, Path.Combine(Roaming, "SpotifyWebHelper"));
        Assert.Equal(LeftoverMatch.Related, helper.Confidence);
        Assert.False(helper.Include);

        Assert.True(Find(items, Path.Combine(Local, "Spotify")).Include);

        var dump = Find(items, Path.Combine(Local, "CrashDumps", "Spotify.exe.4242.dmp"));
        Assert.Equal(LeftoverCategory.Logs, dump.Category);
        Assert.True(dump.Include);

        var temp = Find(items, Path.Combine(Temp, "Spotify"));
        Assert.Equal(LeftoverCategory.Caches, temp.Category);
        Missing(items, Path.Combine(Temp, "SpotifyInstallerCache")); // %TEMP% only takes exact names

        var machine = Find(items, Path.Combine(ProgramData, "Spotify"));
        Assert.True(machine.RequiresAdmin);
        Assert.False(machine.Include);

        var desktop = Find(items, Path.Combine(Desktop, "Spotify.lnk"));
        Assert.Equal(LeftoverCategory.Shortcuts, desktop.Category);
        Assert.True(desktop.Include);
        Assert.True(Find(items, Path.Combine(StartMenu, "Spotify.lnk")).Include);

        var key = Find(items, @"HKEY_CURRENT_USER\Software\Spotify");
        Assert.Equal(LeftoverKind.RegistryKey, key.Kind);
        Assert.True(key.Include);
        Assert.True(Find(items, @"HKEY_CURRENT_USER\Software\Spotify AB\Spotify").Include);
        var hklm = Find(items, @"HKEY_LOCAL_MACHINE\SOFTWARE\Spotify");
        Assert.True(hklm.RequiresAdmin);
        Assert.False(hklm.Include);

        // Never: the install folder before its uninstaller ran, other apps, generic or excluded folders.
        Missing(items, install);
        Missing(items, Path.Combine(Roaming, "Discord"));
        Missing(items, Path.Combine(Roaming, "Microsoft"));
        Missing(items, Path.Combine(Roaming, "Fieldkit"));
        Missing(items, Path.Combine(Local, "CrashDumps", "Discord.exe.1.dmp"));
        Missing(items, Path.Combine(Desktop, "Notes.lnk"));
        Missing(items, Path.Combine(StartMenu, "Discord.lnk"));
        Missing(items, @"HKEY_CURRENT_USER\Software\Microsoft");
        Missing(items, @"HKEY_CURRENT_USER\Software\Spotify AB");
        Missing(items, @"HKEY_CURRENT_USER\Software\Spotify AB\Other");
        Missing(items, @"HKEY_CURRENT_USER\Software\Discord");
        // A Store package folder is only claimed through the app's own package family.
        Missing(items, Path.Combine(Local, "Packages", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0"));

        Assert.Equal(items.OrderBy(i => i.Category).Select(i => i.Category), items.Select(i => i.Category));
    }

    [Fact]
    public void AfterUninstallTheInstallFolderIsALeftover()
    {
        var install = Path.Combine(ProgramFiles, "Spotify");
        BuildSpotifyFixture(install);
        var env = Env(admin: true);
        var id = LeftoverRules.Identity(Spotify(install), [Path.Combine(install, "Spotify.exe")], env.ProgramFolders);
        var items = LeftoverScanner.Scan(id, OtherApps.Empty, env, afterUninstall: true);
        var folder = Find(items, install);
        Assert.Equal(LeftoverCategory.App, folder.Category);
        Assert.True(folder.RequiresAdmin); // Program Files is machine-wide
        Assert.True(folder.Include);       // elevated: exact machine-wide files start checked
        Assert.True(Find(items, Path.Combine(ProgramData, "Spotify")).Include);
        Assert.False(Find(items, @"HKEY_LOCAL_MACHINE\SOFTWARE\Spotify").Include); // HKLM stays opt-in
        // The vendor in the install path ("Program Files") is never a vendor folder.
        Assert.DoesNotContain("Program Files", id.VendorNames);
    }

    [Fact]
    public void DanglingShortcutsToTheAppExecutableAreFound()
    {
        var install = Path.Combine(ProgramFiles, "Gone App");
        Link(Desktop, "Launch it.lnk", Path.Combine(install, "goneapp.exe"));
        Link(PublicDesktop, "Something else.lnk", Path.Combine(ProgramFiles, "Else", "else.exe"));
        var app = new InstalledApp { Id = "g", Name = "Gone App", InstallLocation = install };
        var env = Env();
        var id = LeftoverRules.Identity(app, ["goneapp.exe"], env.ProgramFolders);
        var items = LeftoverScanner.Scan(id, OtherApps.Empty, env, afterUninstall: true);
        Assert.True(Find(items, Path.Combine(Desktop, "Launch it.lnk")).Include);
        Missing(items, Path.Combine(PublicDesktop, "Something else.lnk"));
    }

    [Fact]
    public void StartMenuFolderWhoseShortcutsAllPointIntoTheAppIsTaken()
    {
        var install = Path.Combine(ProgramFiles, "VideoLAN", "VLC");
        Dir(install);
        var folder = Path.Combine(CommonStartMenu, "VideoLAN");
        Link(folder, "VLC media player.lnk", Path.Combine(install, "vlc.exe"));
        Link(folder, "Documentation.lnk", Path.Combine(install, "Documentation.url"));
        var shared = Path.Combine(StartMenu, "Accessories");
        Link(shared, "VLC skins.lnk", Path.Combine(install, "skins"));
        Link(shared, "Paint.lnk", Path.Combine(_root, "Windows", "paint.exe"));
        var app = new InstalledApp { Id = "vlc", Name = "VLC media player", Publisher = "VideoLAN", InstallLocation = install, Source = InstalledAppSource.MachineRegistry };
        var env = Env();
        var id = LeftoverRules.Identity(app, [], env.ProgramFolders);
        Assert.Contains("VideoLAN", id.VendorNames);
        var items = LeftoverScanner.Scan(id, OtherApps.Empty, env, afterUninstall: false);
        var menu = Find(items, folder);
        Assert.Equal(LeftoverMatch.Exact, menu.Confidence);
        Assert.True(menu.RequiresAdmin);
        Assert.True(Find(items, Path.Combine(shared, "VLC skins.lnk")).Include);
        Missing(items, shared);
        Missing(items, Path.Combine(shared, "Paint.lnk"));
    }

    [Fact]
    public void AnotherInstalledVersionKeepsSharedData()
    {
        Dir(Roaming, "krita");
        Dir(Local, "kritarunner");
        Dir(ProgramFiles, "Krita 5.1");
        _registry.Add(@"HKEY_CURRENT_USER\Software", "Krita");
        var target = new InstalledApp { Id = "k51", Name = "Krita 5.1.5 (x64)", Publisher = "Krita Foundation", InstallLocation = Path.Combine(ProgramFiles, "Krita 5.1") };
        var other = new InstalledApp { Id = "k52", Name = "Krita 5.2.6 (x64)", Publisher = "Krita Foundation", InstallLocation = Path.Combine(ProgramFiles, "Krita 5.2") };
        var env = Env();
        var id = LeftoverRules.Identity(target, [], env.ProgramFolders);
        Assert.Equal(LeftoverMatch.Exact, LeftoverRules.Match("krita", id));
        var items = LeftoverScanner.Scan(id, new OtherApps([other]), env, afterUninstall: true);
        Missing(items, Path.Combine(Roaming, "krita"));
        Missing(items, Path.Combine(Local, "kritarunner"));
        Missing(items, @"HKEY_CURRENT_USER\Software\Krita");
        Assert.True(Find(items, target.InstallLocation).RequiresAdmin);
    }

    [Fact]
    public void StorePackageDataIsClaimedByFamilyName()
    {
        var family = "Microsoft.WindowsTerminal_8wekyb3d8bbwe";
        Dir(Local, "Packages", family);
        Dir(Local, "Packages", "Microsoft.WindowsCalculator_8wekyb3d8bbwe");
        var app = new InstalledApp { Id = family, Name = "Windows Terminal", Publisher = "Microsoft Corporation", PackageFamilyName = family, Source = InstalledAppSource.StorePackage };
        var env = Env();
        var items = LeftoverScanner.Scan(LeftoverRules.Identity(app, [], env.ProgramFolders), OtherApps.Empty, env, afterUninstall: true);
        var data = Find(items, Path.Combine(Local, "Packages", family));
        Assert.Equal(LeftoverCategory.Containers, data.Category);
        Assert.True(data.Include);
        Missing(items, Path.Combine(Local, "Packages", "Microsoft.WindowsCalculator_8wekyb3d8bbwe"));
    }

    [Fact]
    public void JunctionsAreNeverFollowed()
    {
        var real = Dir(_root, "Elsewhere", "Spotify");
        Directory.CreateDirectory(Roaming);
        var link = Path.Combine(Roaming, "Spotify");
        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (IOException)
        {
            return; // symlinks need Developer Mode or elevation; nothing to test then
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        var env = Env();
        var items = LeftoverScanner.Scan(LeftoverRules.Identity(Spotify(string.Empty), [], env.ProgramFolders), OtherApps.Empty, env, afterUninstall: true);
        Missing(items, link);
    }

    [Fact]
    public void RemovalRecheckRefusesRootsExclusionsAndOutsiders()
    {
        var spotify = Dir(Roaming, "Spotify");
        var roots = Env().FileRoots().ToList();
        Assert.True(LeftoverRemover.IsStillSafe(spotify, roots, []));
        Assert.False(LeftoverRemover.IsStillSafe(Roaming, roots, []));
        Assert.False(LeftoverRemover.IsStillSafe(spotify, roots, [Path.Combine(Roaming, "Spotify")]));
        Assert.False(LeftoverRemover.IsStillSafe(Path.Combine(_root, "Elsewhere"), roots, []));
    }

    [Fact]
    public void CancellationStopsTheScan()
    {
        BuildSpotifyFixture(Path.Combine(ProgramFiles, "Spotify"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var env = Env();
        Assert.Throws<OperationCanceledException>(() =>
            LeftoverScanner.Scan(LeftoverRules.Identity(Spotify(string.Empty), [], env.ProgramFolders), OtherApps.Empty, env, true, cts.Token));
    }

    private sealed class FakeRegistry : IRegistryBrowser
    {
        private readonly Dictionary<string, List<string>> _keys = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string parent, params string[] children)
        {
            if (!_keys.TryGetValue(parent, out var list)) _keys[parent] = list = [];
            list.AddRange(children);
        }

        public IReadOnlyList<string> SubKeyNames(string keyPath) => _keys.TryGetValue(keyPath, out var l) ? l : [];

        public bool Exists(string keyPath) => _keys.ContainsKey(keyPath)
            || _keys.Any(k => k.Value.Any(c => string.Equals($@"{k.Key}\{c}", keyPath, StringComparison.OrdinalIgnoreCase)));
    }
}
