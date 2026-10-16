// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Fieldkit.Core.Updates;

public enum UpdateStatus { UpToDate, Available, Failed }

public sealed record UpdateCheck(UpdateStatus Status, UpdateRelease? Release = null, string? Error = null);

/// <summary>A reason the update could not go ahead, written for the person who sees it.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Asks GitHub for the latest release and fetches its installer. The installer is only run after its SHA-256
/// matches the digest GitHub published for that exact file (Fieldkit is not code-signed, so this and HTTPS to
/// GitHub are what vouch for it); a release without a digest is never installed automatically.
/// </summary>
public sealed class AppUpdater(Func<HttpClient>? clientFactory = null)
{
    private readonly Func<HttpClient> _client = clientFactory ?? CreateClient;

    public static string TempDirectory => Path.Combine(Path.GetTempPath(), "Fieldkit-update");

    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Fieldkit", AppInfo.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    // ------------------------------------------------------------- looking

    public async Task<UpdateCheck> CheckAsync(AppVersion current, bool includeBeta, CancellationToken cancel = default)
    {
        try
        {
            using var client = _client();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await client.GetAsync(UpdateLogic.ReleasesApi, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return new UpdateCheck(UpdateStatus.Failed, Error: "GitHub is limiting requests right now. Try again in a while.");
            if (!response.IsSuccessStatusCode)
                return new UpdateCheck(UpdateStatus.Failed, Error: $"GitHub answered with an error ({(int)response.StatusCode}).");
            var json = await ReadTextAsync(response.Content, UpdateLogic.MaxApiBytes, timeout.Token);
            var newest = UpdateLogic.Newest(UpdateLogic.ParseReleases(json), current, includeBeta);
            return newest is null ? new UpdateCheck(UpdateStatus.UpToDate) : new UpdateCheck(UpdateStatus.Available, newest);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return new UpdateCheck(UpdateStatus.Failed, Error: "GitHub did not answer in time.");
        }
        catch (HttpRequestException)
        {
            return new UpdateCheck(UpdateStatus.Failed, Error: "Fieldkit could not reach GitHub. Are you online?");
        }
        catch (UpdateException ex)
        {
            return new UpdateCheck(UpdateStatus.Failed, Error: ex.Message);
        }
    }

    private static async Task<string> ReadTextAsync(HttpContent content, int maxBytes, CancellationToken cancel)
    {
        await using var stream = await content.ReadAsStreamAsync(cancel);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new UpdateException("GitHub's answer was larger than expected.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    // ------------------------------------------------------------- fetching

    /// <summary>
    /// Downloads the installer for <paramref name="architecture"/> into <paramref name="directory"/> and returns its
    /// path. Throws <see cref="UpdateException"/> (after removing what it wrote) when anything about the file is not
    /// exactly what GitHub published.
    /// </summary>
    public async Task<(string Path, UpdateAsset Asset)> DownloadInstallerAsync(
        UpdateRelease release, Architecture architecture, string directory,
        IProgress<(long Done, long Total)>? progress = null, CancellationToken cancel = default)
    {
        var asset = UpdateLogic.InstallerFor(release, architecture)
                    ?? throw new UpdateException("This release has no installer for this PC.");
        if (asset.Sha256 is null)
            throw new UpdateException("This release does not publish a checksum for its installer, so Fieldkit will not run it by itself.");
        if (!UpdateLogic.IsExpectedAssetUrl(asset.Url, release.Tag, asset.Name))
            throw new UpdateException("The download address is not the one Fieldkit expects.");
        if (asset.Size <= 0 || asset.Size > UpdateLogic.MaxInstallerBytes)
            throw new UpdateException("The installer has an unexpected size.");

        Directory.CreateDirectory(directory);
        RemoveOldInstallers(directory);
        var final = Path.Combine(directory, asset.Name);
        var part = final + ".part";
        try
        {
            using var client = _client();
            using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!response.IsSuccessStatusCode) throw new UpdateException($"The download failed ({(int)response.StatusCode}).");
            if (!UpdateLogic.IsTrustedDownloadHost(response.RequestMessage?.RequestUri))
                throw new UpdateException("The download was sent somewhere Fieldkit does not trust.");
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
                throw new UpdateException("The download is not the size GitHub published.");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(cancel))
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var chunk = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(chunk, cancel)) > 0)
                {
                    total += read;
                    if (total > asset.Size) throw new UpdateException("The download is larger than GitHub published.");
                    hash.AppendData(chunk, 0, read);
                    await target.WriteAsync(chunk.AsMemory(0, read), cancel);
                    progress?.Report((total, asset.Size));
                }
                if (total != asset.Size) throw new UpdateException("The download stopped before it was complete.");
            }
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The download does not match the checksum GitHub published, so it was thrown away.");
            File.Move(part, final, overwrite: true);
            return (final, asset);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            Discard(part);
            throw new UpdateException("The download took too long.");
        }
        catch (HttpRequestException)
        {
            Discard(part);
            throw new UpdateException("The download was interrupted. Are you online?");
        }
        catch
        {
            Discard(part);
            throw;
        }
    }

    /// <summary>Removes installers left in the update folder by an earlier update (the one that ran is no longer needed).</summary>
    public static void CleanUp(string? directory = null)
    {
        var folder = directory ?? TempDirectory;
        if (Directory.Exists(folder)) RemoveOldInstallers(folder);
    }

    private static void RemoveOldInstallers(string directory)
    {
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(directory, "Fieldkit-*-setup-*"))
                Discard(file);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cleaning old installers failed: {ex.Message}");
        }
    }

    private static void Discard(string path)
    {
        try { File.Delete(path); } catch { }
    }

    // ------------------------------------------------------------- running

    /// <summary>The SHA-256 of a file as lowercase hex.</summary>
    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Starts a downloaded installer over this installation. The file must be in the update folder and must still
    /// match its published checksum (it is read again, in case something changed it after the download).
    /// </summary>
    public static void LaunchInstaller(string installerPath, UpdateAsset asset, bool allUsers, string? directory = null)
    {
        var folder = Path.GetFullPath(directory ?? TempDirectory).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(installerPath);
        if (!full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetFileName(full), asset.Name, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("The installer is not where Fieldkit put it.");
        if (asset.Sha256 is null || !string.Equals(Sha256Of(full), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("The installer changed after it was downloaded, so it was not run.");
        Process.Start(new ProcessStartInfo(full, UpdateLogic.InstallerArguments(allUsers))
        {
            UseShellExecute = true, // lets Windows ask for permission when an all-users install needs it
            WorkingDirectory = Path.GetDirectoryName(full)!,
        });
    }
}
