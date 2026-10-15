// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Fieldkit.Features.Monitor.Sampling;

/// <summary>
/// Live download/upload rates, totals moved since the app started, and the adapters' own counters
/// (usually since Windows started, upstream #2975). Session totals survive the sampler pausing because
/// the OS counters keep running.
/// </summary>
public sealed class NetworkReader
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (long Rx, long Tx)> LastSeen = new(StringComparer.Ordinal);
    private static ulong _totalDown, _totalUp;
    private static readonly string[] VirtualMarkers =
        ["Hyper-V", "VirtualBox", "VMware", "Wintun", "TAP-Windows", "WireGuard", "Loopback", "Npcap"];

    private long _prevRx = -1, _prevTx = -1;
    private DateTime _prevAt;

    public (double? Down, double? Up, ulong TotalDown, ulong TotalUp, ulong SinceBootDown, ulong SinceBootUp) Read()
    {
        long rx = 0, tx = 0;
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            foreach (var nic in Interfaces())
            {
                IPInterfaceStatistics stats;
                try { stats = nic.GetIPStatistics(); }
                catch (NetworkInformationException) { continue; }
                var currentRx = stats.BytesReceived;
                var currentTx = stats.BytesSent;
                rx += currentRx;
                tx += currentTx;
                if (LastSeen.TryGetValue(nic.Id, out var last))
                {
                    _totalDown += (ulong)(currentRx >= last.Rx ? currentRx - last.Rx : currentRx);
                    _totalUp += (ulong)(currentTx >= last.Tx ? currentTx - last.Tx : currentTx);
                }
                LastSeen[nic.Id] = (currentRx, currentTx);
            }
        }

        double? down = null, up = null;
        var elapsed = (now - _prevAt).TotalSeconds;
        if (_prevRx >= 0 && elapsed > 0.2)
        {
            down = rx >= _prevRx ? (rx - _prevRx) / elapsed : 0;
            up = tx >= _prevTx ? (tx - _prevTx) / elapsed : 0;
        }
        (_prevRx, _prevTx, _prevAt) = (rx, tx, now);
        lock (Gate) return (down, up, _totalDown, _totalUp, (ulong)Math.Max(0, rx), (ulong)Math.Max(0, tx));
    }

    private static IEnumerable<NetworkInterface> Interfaces()
    {
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }
        foreach (var nic in all)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            // Virtual switches and VPN tunnels carry traffic the physical adapter also counts
            // (upstream likewise skips bridge/tun/tap interfaces).
            if (VirtualMarkers.Any(m => nic.Description.Contains(m, StringComparison.OrdinalIgnoreCase))) continue;
            yield return nic;
        }
    }

    /// <summary>Local IPv4 addresses of the connected interfaces. Nothing leaves the PC.</summary>
    public static List<string> LocalAddresses()
    {
        var result = new List<string>();
        foreach (var nic in Interfaces())
        {
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            foreach (var a in props.UnicastAddresses)
            {
                if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var text = a.Address.ToString();
                if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                if (!result.Contains(text)) result.Add(text);
            }
        }
        return result;
    }
}
