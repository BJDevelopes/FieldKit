// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Fieldkit.Core.Updates;

namespace Fieldkit.Tests.Core;

public sealed class AppUpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fieldkit-updater-tests", Guid.NewGuid().ToString("N"));

    public AppUpdaterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private static AppUpdater Updater(Handler handler) => new(() => new HttpClient(handler, disposeHandler: false));

    private static HttpResponseMessage Bytes(byte[] content, Uri? finalUri = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        if (finalUri is not null) response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUri);
        return response;
    }

    private static string Sha(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static readonly byte[] Installer = Encoding.ASCII.GetBytes(new string('F', 200_000));

    private static UpdateRelease Release(string? digest = null, long? size = null, string? url = null) => new(
        new AppVersion(0, 5, 0), "v0.5.0", "https://github.com/BJDevelopes/FieldKit/releases/tag/v0.5.0", "- notes", false, true,
        [new UpdateAsset("Fieldkit-0.5.0-setup-x64.exe",
            url ?? "https://github.com/BJDevelopes/FieldKit/releases/download/v0.5.0/Fieldkit-0.5.0-setup-x64.exe",
            size ?? Installer.Length, UpdateLogic.NormalisedSha256(digest ?? Sha(Installer)))]);

    // ------------------------------------------------------------- looking

    private const string ListJson = """
        [{"tag_name":"v0.5.0","html_url":"https://github.com/BJDevelopes/FieldKit/releases/tag/v0.5.0","draft":false,"prerelease":true,"body":"- new","assets":[]},
         {"tag_name":"v0.4.0","html_url":"x","draft":false,"prerelease":true,"body":"","assets":[]}]
        """;

    [Fact]
    public async Task ANewerReleaseIsReported()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ListJson) });

        var check = await Updater(handler).CheckAsync(new AppVersion(0, 4, 0), includeBeta: false);

        Assert.Equal(UpdateStatus.Available, check.Status);
        Assert.Equal(new AppVersion(0, 5, 0), check.Release!.Version);
        Assert.Equal(UpdateLogic.ReleasesApi, Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task NothingNewerMeansUpToDate()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ListJson) });

        var check = await Updater(handler).CheckAsync(new AppVersion(0, 5, 0), includeBeta: false);

        Assert.Equal(UpdateStatus.UpToDate, check.Status);
        Assert.Null(check.Release);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "error")]
    [InlineData(HttpStatusCode.Forbidden, "limiting")]
    [InlineData(HttpStatusCode.TooManyRequests, "limiting")]
    public async Task AnErrorFromGitHubIsAFailureWithAReason(HttpStatusCode status, string word)
    {
        var handler = new Handler(_ => new HttpResponseMessage(status));

        var check = await Updater(handler).CheckAsync(new AppVersion(0, 4, 0), includeBeta: false);

        Assert.Equal(UpdateStatus.Failed, check.Status);
        Assert.Contains(word, check.Error);
    }

    [Fact]
    public async Task NoConnectionIsAFailureNotACrash()
    {
        var updater = new AppUpdater(() => new HttpClient(new Handler(_ => throw new HttpRequestException("offline"))));

        var check = await updater.CheckAsync(new AppVersion(0, 4, 0), includeBeta: false);

        Assert.Equal(UpdateStatus.Failed, check.Status);
        Assert.Contains("online", check.Error);
    }

    [Fact]
    public async Task AnAnswerThatIsFarTooLargeIsRefused()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[UpdateLogic.MaxApiBytes + 10]) });

        var check = await Updater(handler).CheckAsync(new AppVersion(0, 4, 0), includeBeta: false);

        Assert.Equal(UpdateStatus.Failed, check.Status);
    }

    // ------------------------------------------------------------- fetching

    [Fact]
    public async Task ACheckedInstallerIsKeptUnderItsName()
    {
        var handler = new Handler(_ => Bytes(Installer));
        var reports = new List<long>();

        var (path, asset) = await Updater(handler).DownloadInstallerAsync(Release(), Architecture.X64, _dir, new Progress<(long, long)>(p => reports.Add(p.Item1)));

        Assert.Equal(Path.Combine(_dir, "Fieldkit-0.5.0-setup-x64.exe"), path);
        Assert.Equal(Installer, File.ReadAllBytes(path));
        Assert.Equal("Fieldkit-0.5.0-setup-x64.exe", asset.Name);
        Assert.False(File.Exists(path + ".part"));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchItsChecksumIsThrownAway()
    {
        var tampered = (byte[])Installer.Clone();
        tampered[1000] ^= 0xFF;
        var handler = new Handler(_ => Bytes(tampered));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Updater(handler).DownloadInstallerAsync(Release(), Architecture.X64, _dir));

        Assert.Contains("checksum", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task ATruncatedDownloadIsThrownAway()
    {
        var handler = new Handler(_ => Bytes(Installer[..1000]));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Updater(handler).DownloadInstallerAsync(Release(), Architecture.X64, _dir));

        Assert.Contains("size", ex.Message + " size"); // either "not the size" or "stopped before it was complete"
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task ADownloadLargerThanPublishedIsStopped()
    {
        var bigger = Installer.Concat(new byte[500]).ToArray();
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(bigger)) };
            return response; // no Content-Length: the stream itself is too long
        });

        await Assert.ThrowsAsync<UpdateException>(() => Updater(handler).DownloadInstallerAsync(Release(), Architecture.X64, _dir));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task AReleaseWithoutAChecksumIsNeverFetched()
    {
        var handler = new Handler(_ => Bytes(Installer));
        var release = Release() with { Assets = [Release().Assets[0] with { Sha256 = null }] };

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Updater(handler).DownloadInstallerAsync(release, Architecture.X64, _dir));

        Assert.Contains("checksum", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnUnexpectedDownloadAddressIsNeverFetched()
    {
        var handler = new Handler(_ => Bytes(Installer));
        var release = Release(url: "https://evil.example/Fieldkit-0.5.0-setup-x64.exe");

        await Assert.ThrowsAsync<UpdateException>(() => Updater(handler).DownloadInstallerAsync(release, Architecture.X64, _dir));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ARedirectToAStrangerIsRefused()
    {
        var handler = new Handler(_ => Bytes(Installer, new Uri("https://downloads.evil.example/installer.exe")));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Updater(handler).DownloadInstallerAsync(Release(), Architecture.X64, _dir));

        Assert.Contains("trust", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task ARedirectToGitHubStorageIsFine()
    {
        var handler = new Handler(_ => Bytes(Installer, new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/abc")));

        var (path, _) = await Updater(handler).DownloadInstallerAsync(Release(), Architecture.X64, _dir);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task AFailedDownloadStatusLeavesNothingBehind()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<UpdateException>(() => Updater(handler).DownloadInstallerAsync(Release(), Architecture.X64, _dir));

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task AnInstallerForAnotherArchitectureIsNotPicked()
    {
        var handler = new Handler(_ => Bytes(Installer));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => Updater(handler).DownloadInstallerAsync(Release(), Architecture.Arm64, _dir));

        Assert.Contains("no installer", ex.Message);
    }

    [Fact]
    public async Task OldInstallersAreClearedBeforeANewDownload()
    {
        var old = Path.Combine(_dir, "Fieldkit-0.3.0-setup-x64.exe");
        File.WriteAllText(old, "old");
        var unrelated = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(unrelated, "keep");
        var handler = new Handler(_ => Bytes(Installer));

        await Updater(handler).DownloadInstallerAsync(Release(), Architecture.X64, _dir);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(unrelated));
    }

    // ------------------------------------------------------------- running

    [Fact]
    public void AnInstallerOutsideTheUpdateFolderIsNeverRun()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "Fieldkit-0.5.0-setup-x64.exe");
        var asset = Release().Assets[0];

        Assert.Throws<UpdateException>(() => AppUpdater.LaunchInstaller(elsewhere, asset, allUsers: false, directory: _dir));
    }

    [Fact]
    public void AnInstallerThatChangedAfterTheDownloadIsNeverRun()
    {
        var path = Path.Combine(_dir, "Fieldkit-0.5.0-setup-x64.exe");
        File.WriteAllBytes(path, Installer);
        var asset = Release().Assets[0] with { Sha256 = UpdateLogic.NormalisedSha256(Sha(Encoding.ASCII.GetBytes("something else"))) };

        var ex = Assert.Throws<UpdateException>(() => AppUpdater.LaunchInstaller(path, asset, allUsers: false, directory: _dir));

        Assert.Contains("changed", ex.Message);
    }

    [Fact]
    public void AnInstallerWithAnotherNameIsNeverRun()
    {
        var path = Path.Combine(_dir, "setup.exe");
        File.WriteAllBytes(path, Installer);

        Assert.Throws<UpdateException>(() => AppUpdater.LaunchInstaller(path, Release().Assets[0], allUsers: false, directory: _dir));
    }
}
