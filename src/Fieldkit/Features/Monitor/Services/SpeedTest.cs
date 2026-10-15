// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Fieldkit.Core;
using Fieldkit.UI;

namespace Fieldkit.Features.Monitor.Services;

/// <summary>
/// A user-started internet speed test (port of upstream SpeedTest): latency, then download, then
/// upload, against Cloudflare's public speed endpoints (the backend of speed.cloudflare.com).
/// Each transfer is time-boxed to 5 s. Nothing about the user is sent: the upload body is zeros.
/// </summary>
public static class SpeedTest
{
    public enum Phase { Idle, Latency, Download, Upload, Done, Failed }

    private const string Host = "https://speed.cloudflare.com";
    private const int DownloadChunk = 90_000_000; // __down caps requests just under 100 MB
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    private static CancellationTokenSource? _cts;

    public static Phase State { get; private set; } = Phase.Idle;
    public static double? LatencyMs { get; private set; }
    public static double? DownloadMbps { get; private set; }
    public static double? UploadMbps { get; private set; }

    public static bool IsRunning => State is Phase.Latency or Phase.Download or Phase.Upload;

    /// <summary>Raised on the UI thread whenever the phase or a result changes.</summary>
    public static event Action? Changed;

    public static void Start()
    {
        if (IsRunning) return;
        LatencyMs = DownloadMbps = UploadMbps = null;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => RunAsync(token));
    }

    public static void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
        if (IsRunning) Set(Phase.Idle);
    }

    private static void Set(Phase phase)
    {
        State = phase;
        Ui.OnUi(() => Changed?.Invoke());
    }

    private static async Task RunAsync(CancellationToken token)
    {
        using var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        try
        {
            Set(Phase.Latency);
            var best = double.MaxValue;
            for (var i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                using var response = await client.GetAsync($"{Host}/__down?bytes=0", HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            LatencyMs = best;

            Set(Phase.Download);
            DownloadMbps = await MeasureDownload(client, token);
            Set(Phase.Download);

            Set(Phase.Upload);
            UploadMbps = await MeasureUpload(client, token);
            Set(Phase.Done);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // cancelled by the user
        }
        catch (Exception ex)
        {
            Log.Warn($"Speed test failed: {ex.Message}");
            Set(Phase.Failed);
        }
    }

    private static async Task<double> MeasureDownload(HttpClient client, CancellationToken token)
    {
        using var box = CancellationTokenSource.CreateLinkedTokenSource(token);
        box.CancelAfter(Window);
        var sw = Stopwatch.StartNew();
        long bytes = 0;
        var buffer = new byte[81920];
        try
        {
            while (!box.IsCancellationRequested)
            {
                using var response = await client.GetAsync($"{Host}/__down?bytes={DownloadChunk}", HttpCompletionOption.ResponseHeadersRead, box.Token);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(box.Token);
                int read;
                while ((read = await stream.ReadAsync(buffer, box.Token)) > 0) bytes += read;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // time box reached
        }
        catch (IOException) when (box.IsCancellationRequested && !token.IsCancellationRequested)
        {
        }
        token.ThrowIfCancellationRequested();
        return Mbps(bytes, sw.Elapsed);
    }

    private static async Task<double> MeasureUpload(HttpClient client, CancellationToken token)
    {
        using var box = CancellationTokenSource.CreateLinkedTokenSource(token);
        box.CancelAfter(Window);
        var content = new ZeroContent(100_000_000);
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await client.PostAsync($"{Host}/__up", content, box.Token);
        }
        catch (Exception) when (box.IsCancellationRequested && !token.IsCancellationRequested)
        {
            // time box reached mid-upload
        }
        token.ThrowIfCancellationRequested();
        if (content.Sent == 0) throw new HttpRequestException("Nothing was uploaded.");
        return Mbps(content.Sent, sw.Elapsed);
    }

    private static double Mbps(long bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds > 0 ? Math.Max(0, bytes * 8 / elapsed.TotalSeconds / 1_000_000) : 0;

    /// <summary>An upload body of zeros that counts what was actually sent.</summary>
    private sealed class ZeroContent(long length) : HttpContent
    {
        private long _sent;
        public long Sent => Interlocked.Read(ref _sent);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
        {
            var chunk = new byte[64 * 1024];
            var remaining = length;
            while (remaining > 0)
            {
                var n = (int)Math.Min(chunk.Length, remaining);
                await stream.WriteAsync(chunk.AsMemory(0, n), token);
                remaining -= n;
                Interlocked.Add(ref _sent, n);
            }
        }

        protected override bool TryComputeLength(out long len)
        {
            len = length;
            return true;
        }
    }
}
