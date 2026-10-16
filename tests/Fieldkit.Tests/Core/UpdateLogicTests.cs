// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Runtime.InteropServices;
using Fieldkit.Core.Updates;

namespace Fieldkit.Tests.Core;

public class UpdateLogicTests
{
    private static AppVersion V(string text)
    {
        Assert.True(AppVersion.TryParse(text, out var v), text);
        return v;
    }

    // ------------------------------------------------------------- versions

    [Theory]
    [InlineData("0.4.0", 0, 4, 0, "")]
    [InlineData("v0.4.0", 0, 4, 0, "")]
    [InlineData("  v1.12.3  ", 1, 12, 3, "")]
    [InlineData("0.4.0+3709ad8", 0, 4, 0, "")]
    [InlineData("0.4", 0, 4, 0, "")]
    [InlineData("0.5.0-beta.1", 0, 5, 0, "beta.1")]
    [InlineData("v2.0.0-rc.2+abc", 2, 0, 0, "rc.2")]
    public void VersionsAreReadFromTagsAndAssemblies(string text, int major, int minor, int patch, string pre)
    {
        var v = V(text);

        Assert.Equal(new AppVersion(major, minor, patch, pre), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v.4.0")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("99999999999.0.0")]
    public void NonsenseIsNotAVersion(string? text) => Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void VersionsAreOrderedLikeSemanticVersions()
    {
        string[] ascending =
        [
            "0.3.9", "0.4.0-alpha", "0.4.0-alpha.1", "0.4.0-alpha.beta", "0.4.0-beta", "0.4.0-beta.2", "0.4.0-beta.10", "0.4.0-rc.1",
            "0.4.0", "0.4.1", "0.10.0", "1.0.0",
        ];
        for (var i = 0; i < ascending.Length; i++)
        for (var j = 0; j < ascending.Length; j++)
            Assert.Equal(Math.Sign(i.CompareTo(j)), Math.Sign(V(ascending[i]).CompareTo(V(ascending[j]))));
        Assert.True(V("0.5.0") > V("0.4.9"));
        Assert.True(V("0.5.0-beta.1") < V("0.5.0"));
        Assert.True(V("0.4.0") >= V("0.4.0"));
    }

    [Fact]
    public void AVersionShowsAsItsNumbers()
    {
        Assert.Equal("0.4.0", V("v0.4.0+abc").ToString());
        Assert.Equal("0.5.0-beta.1", V("0.5.0-beta.1").ToString());
    }

    // ------------------------------------------------------------- releases

    private const string Digest = "sha256:fb82f4aab064cec8625c39c53301fa96a1f09cf6b4377f50646198fb776a2a23";

    private static string ReleaseJson(string tag, bool draft = false, bool prerelease = true, string body = "- Added a thing", string digest = Digest) => $$"""
        {
          "tag_name": "{{tag}}", "html_url": "https://github.com/BJDevelopes/FieldKit/releases/tag/{{tag}}",
          "draft": {{draft.ToString().ToLowerInvariant()}}, "prerelease": {{prerelease.ToString().ToLowerInvariant()}}, "body": "{{body}}",
          "assets": [
            { "name": "Fieldkit-{{tag.TrimStart('v')}}-setup-x64.exe", "size": 54805721, "digest": "{{digest}}",
              "browser_download_url": "https://github.com/BJDevelopes/FieldKit/releases/download/{{tag}}/Fieldkit-{{tag.TrimStart('v')}}-setup-x64.exe" },
            { "name": "Fieldkit-{{tag.TrimStart('v')}}-setup-arm64.exe", "size": 48881719, "digest": null,
              "browser_download_url": "https://github.com/BJDevelopes/FieldKit/releases/download/{{tag}}/Fieldkit-{{tag.TrimStart('v')}}-setup-arm64.exe" },
            { "name": "Fieldkit-{{tag.TrimStart('v')}}-win-x64.zip", "size": 78086978,
              "browser_download_url": "https://github.com/BJDevelopes/FieldKit/releases/download/{{tag}}/Fieldkit-{{tag.TrimStart('v')}}-win-x64.zip" }
          ]
        }
        """;

    private static string List(params string[] releases) => "[" + string.Join(",", releases) + "]";

    [Fact]
    public void AReleaseListIsReadWithItsFiles()
    {
        var releases = UpdateLogic.ParseReleases(List(ReleaseJson("v0.5.0")));

        var release = Assert.Single(releases);
        Assert.Equal(V("0.5.0"), release.Version);
        Assert.Equal("v0.5.0", release.Tag);
        Assert.Equal("- Added a thing", release.Notes);
        Assert.Equal(3, release.Assets.Count);
        var x64 = release.Assets[0];
        Assert.Equal("Fieldkit-0.5.0-setup-x64.exe", x64.Name);
        Assert.Equal(54805721, x64.Size);
        Assert.Equal("fb82f4aab064cec8625c39c53301fa96a1f09cf6b4377f50646198fb776a2a23", x64.Sha256);
        Assert.Null(release.Assets[1].Sha256);   // a null digest
        Assert.Null(release.Assets[2].Sha256);   // no digest at all
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"message\":\"Not Found\"}")]
    [InlineData("[1, \"two\", null, {}]")]
    [InlineData("[{\"tag_name\":\"nightly\"}]")]
    public void SomethingThatIsNotAReleaseListGivesNothing(string json) => Assert.Empty(UpdateLogic.ParseReleases(json));

    [Fact]
    public void TheNewestReleaseAboveTheCurrentOneIsOffered()
    {
        var releases = UpdateLogic.ParseReleases(List(ReleaseJson("v0.3.0"), ReleaseJson("v0.5.0"), ReleaseJson("v0.4.0")));

        Assert.Equal(V("0.5.0"), UpdateLogic.Newest(releases, V("0.4.0"))!.Version);
        Assert.Equal(V("0.5.0"), UpdateLogic.Newest(releases, V("0.3.0"))!.Version);
        Assert.Null(UpdateLogic.Newest(releases, V("0.5.0")));
        Assert.Null(UpdateLogic.Newest(releases, V("0.6.0")));
    }

    [Fact]
    public void DraftsAndBetasAreNotOfferedUnlessAskedFor()
    {
        var releases = UpdateLogic.ParseReleases(List(
            ReleaseJson("v0.6.0", draft: true), ReleaseJson("v0.5.0-beta.1"), ReleaseJson("v0.4.0")));

        Assert.Null(UpdateLogic.Newest(releases, V("0.4.0")));
        Assert.Equal(V("0.5.0-beta.1"), UpdateLogic.Newest(releases, V("0.4.0"), includeBeta: true)!.Version);
    }

    [Fact]
    public void GitHubsPrereleaseFlagIsNotWhatDecides()
    {
        // every 0.x release is flagged a pre-release on GitHub; a plain version number still counts as a release
        var releases = UpdateLogic.ParseReleases(List(ReleaseJson("v0.5.0", prerelease: true)));

        Assert.NotNull(UpdateLogic.Newest(releases, V("0.4.0")));
    }

    // ------------------------------------------------------------- which file

    [Fact]
    public void TheInstallerMatchesThisPcsArchitecture()
    {
        var release = UpdateLogic.ParseReleases(List(ReleaseJson("v0.5.0")))[0];

        Assert.Equal("Fieldkit-0.5.0-setup-x64.exe", UpdateLogic.InstallerFor(release, Architecture.X64)!.Name);
        Assert.Equal("Fieldkit-0.5.0-setup-arm64.exe", UpdateLogic.InstallerFor(release, Architecture.Arm64)!.Name);
        Assert.Null(UpdateLogic.InstallerFor(release, Architecture.X86));
    }

    [Fact]
    public void AReleaseWithoutAnInstallerHasNoInstaller()
    {
        var release = UpdateLogic.ParseReleases(List(ReleaseJson("v0.5.0")))[0] with { Assets = [] };

        Assert.Null(UpdateLogic.InstallerFor(release, Architecture.X64));
    }

    [Theory]
    [InlineData("sha256:FB82F4AAB064CEC8625C39C53301FA96A1F09CF6B4377F50646198FB776A2A23", "fb82f4aab064cec8625c39c53301fa96a1f09cf6b4377f50646198fb776a2a23")]
    [InlineData("SHA256:fb82f4aab064cec8625c39c53301fa96a1f09cf6b4377f50646198fb776a2a23", "fb82f4aab064cec8625c39c53301fa96a1f09cf6b4377f50646198fb776a2a23")]
    [InlineData("sha256:fb82", null)]
    [InlineData("sha1:fb82f4aab064cec8625c39c53301fa96a1f09cf6b4377f50646198fb776a2a23", null)]
    [InlineData("fb82f4aab064cec8625c39c53301fa96a1f09cf6b4377f50646198fb776a2a23", null)]
    [InlineData("sha256:zz82f4aab064cec8625c39c53301fa96a1f09cf6b4377f50646198fb776a2a23", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyASha256DigestIsAccepted(string? digest, string? expected) => Assert.Equal(expected, UpdateLogic.NormalisedSha256(digest));

    // ------------------------------------------------------------- what to trust

    private const string Good = "https://github.com/BJDevelopes/FieldKit/releases/download/v0.5.0/Fieldkit-0.5.0-setup-x64.exe";

    [Fact]
    public void OnlyThisProjectsReleaseFileIsTheExpectedDownload()
    {
        Assert.True(UpdateLogic.IsExpectedAssetUrl(Good, "v0.5.0", "Fieldkit-0.5.0-setup-x64.exe"));
        Assert.True(UpdateLogic.IsExpectedAssetUrl(Good.Replace("BJDevelopes/FieldKit", "bjdevelopes/fieldkit"), "v0.5.0", "Fieldkit-0.5.0-setup-x64.exe"));

        Assert.False(UpdateLogic.IsExpectedAssetUrl(Good.Replace("https://", "http://"), "v0.5.0", "Fieldkit-0.5.0-setup-x64.exe"));
        Assert.False(UpdateLogic.IsExpectedAssetUrl(Good.Replace("github.com", "evil.example"), "v0.5.0", "Fieldkit-0.5.0-setup-x64.exe"));
        Assert.False(UpdateLogic.IsExpectedAssetUrl(Good.Replace("BJDevelopes", "someone-else"), "v0.5.0", "Fieldkit-0.5.0-setup-x64.exe"));
        Assert.False(UpdateLogic.IsExpectedAssetUrl(Good, "v0.6.0", "Fieldkit-0.5.0-setup-x64.exe"));
        Assert.False(UpdateLogic.IsExpectedAssetUrl(Good, "v0.5.0", "Fieldkit-0.5.0-setup-arm64.exe"));
        Assert.False(UpdateLogic.IsExpectedAssetUrl(Good + "?x=1", "v0.5.0", "Fieldkit-0.5.0-setup-x64.exe"));
        Assert.False(UpdateLogic.IsExpectedAssetUrl(Good.Replace("github.com", "github.com:8443"), "v0.5.0", "Fieldkit-0.5.0-setup-x64.exe"));
        Assert.False(UpdateLogic.IsExpectedAssetUrl(Good.Replace("github.com", "user:pw@github.com"), "v0.5.0", "Fieldkit-0.5.0-setup-x64.exe"));
        Assert.False(UpdateLogic.IsExpectedAssetUrl(null, "v0.5.0", "Fieldkit-0.5.0-setup-x64.exe"));
        Assert.False(UpdateLogic.IsExpectedAssetUrl("not a url", "v0.5.0", "Fieldkit-0.5.0-setup-x64.exe"));
    }

    [Theory]
    [InlineData("https://github.com/x", true)]
    [InlineData("https://objects.githubusercontent.com/abc", true)]
    [InlineData("https://release-assets.githubusercontent.com/abc", true)]
    [InlineData("http://objects.githubusercontent.com/abc", false)]
    [InlineData("https://githubusercontent.com.evil.example/abc", false)]
    [InlineData("https://evilgithubusercontent.com/abc", false)]
    [InlineData("https://evil.example/github.com", false)]
    public void ARedirectMayOnlyGoToGitHubsStorage(string url, bool trusted) =>
        Assert.Equal(trusted, UpdateLogic.IsTrustedDownloadHost(new Uri(url)));

    [Fact]
    public void NoAddressIsNotTrusted() => Assert.False(UpdateLogic.IsTrustedDownloadHost(null));

    // ------------------------------------------------------------- this installation

    [Fact]
    public void AnInstalledCopyHasItsUninstallerBesideIt()
    {
        var dir = @"C:\Users\me\AppData\Local\Programs\Fieldkit";

        Assert.Equal(InstallKind.Installed, UpdateLogic.DetectInstall(dir, p => p == Path.Combine(dir, "unins000.exe")));
        Assert.Equal(InstallKind.Portable, UpdateLogic.DetectInstall(dir, _ => false));
    }

    [Fact]
    public void ProgramFilesMeansAnAllUsersInstall()
    {
        string?[] folders = [@"C:\Program Files", @"C:\Program Files (x86)", null, ""];

        Assert.True(UpdateLogic.IsAllUsersInstall(@"C:\Program Files\Fieldkit", folders));
        Assert.True(UpdateLogic.IsAllUsersInstall(@"C:\Program Files (x86)\Fieldkit\", folders));
        Assert.False(UpdateLogic.IsAllUsersInstall(@"C:\Users\me\AppData\Local\Programs\Fieldkit", folders));
        Assert.False(UpdateLogic.IsAllUsersInstall(@"C:\Program Files Extra\Fieldkit", folders)); // not inside Program Files
    }

    [Fact]
    public void TheInstallerRunsQuietlyOverTheCurrentInstallAndRestartsFieldkit()
    {
        var perUser = UpdateLogic.InstallerArguments(allUsers: false);
        var allUsers = UpdateLogic.InstallerArguments(allUsers: true);

        Assert.Contains("/SILENT", perUser);
        Assert.DoesNotContain("/VERYSILENT", perUser); // a progress window stays visible
        Assert.Contains("/RELAUNCH=1", perUser);
        Assert.Contains("/CURRENTUSER", perUser);
        Assert.DoesNotContain("/ALLUSERS", perUser);
        Assert.Contains("/ALLUSERS", allUsers);
        Assert.DoesNotContain("/CURRENTUSER", allUsers);
    }

    // ------------------------------------------------------------- when

    [Fact]
    public void GitHubIsAskedAtMostEverySixHours()
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(UpdateLogic.NeedsNetworkCheck(now, null));
        Assert.False(UpdateLogic.NeedsNetworkCheck(now, now.AddMinutes(-5)));
        Assert.False(UpdateLogic.NeedsNetworkCheck(now, now.AddHours(-5.9)));
        Assert.True(UpdateLogic.NeedsNetworkCheck(now, now.AddHours(-6)));
        Assert.True(UpdateLogic.NeedsNetworkCheck(now, now.AddDays(-3)));
        Assert.True(UpdateLogic.NeedsNetworkCheck(now, now.AddHours(2))); // the clock moved back
    }

    // ------------------------------------------------------------- what to show

    [Fact]
    public void ReleaseNotesBecomePlainLines()
    {
        var notes = "## 0.5.0\r\n\r\n- **Auto update.** Fieldkit asks on start-up, see [the docs](https://example.com).\n* Uses `update.check`.\n\n**Full Changelog**: https://github.com/x/compare/v0.4.0...v0.5.0";

        var excerpt = UpdateLogic.NotesExcerpt(notes);

        Assert.Equal("0.5.0\n• Auto update. Fieldkit asks on start-up, see the docs.\n• Uses update.check.", excerpt);
    }

    [Fact]
    public void ABulletThatWrapsStaysOnePoint()
    {
        var notes = "- **A long point** that the changelog wraps\n  onto a second and\n  a third line.\n- The next point.";

        Assert.Equal("• A long point that the changelog wraps onto a second and a third line.\n• The next point.", UpdateLogic.NotesExcerpt(notes));
    }

    [Fact]
    public void LongNotesAreCutShort()
    {
        var notes = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"- line {i}"));

        var excerpt = UpdateLogic.NotesExcerpt(notes, maxLines: 5);

        Assert.Equal(6, excerpt.Split('\n').Length); // five lines and the ellipsis
        Assert.EndsWith("…", excerpt);
        Assert.True(UpdateLogic.NotesExcerpt(new string('x', 5000), maxChars: 100).Length <= 101);
        Assert.Equal(string.Empty, UpdateLogic.NotesExcerpt(null));
        Assert.Equal(string.Empty, UpdateLogic.NotesExcerpt("**Full Changelog**: https://x"));
    }
}
