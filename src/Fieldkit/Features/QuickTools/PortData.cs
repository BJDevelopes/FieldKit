// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Runtime.InteropServices;
using Fieldkit.Core;
using static Fieldkit.Features.QuickTools.QuickToolsNative;

namespace Fieldkit.Features.QuickTools;

/// <summary>A listening socket and the process that owns it.</summary>
public sealed record PortEntry(string Protocol, string Host, int Port, uint Pid, string ProcessName, long CreateTime)
{
    /// <summary>"127.0.0.1:3000", "*:3000" style address (IPv6 hosts in brackets).</summary>
    public string Address => $"{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}";
}

/// <summary>Pure Port manager rules (upstream PortManagerSupport), unit-tested.</summary>
public static class PortLogic
{
    /// <summary>True when the host part is *, 0.0.0.0 or :: (brackets ignored).</summary>
    public static bool ListensOnAllInterfaces(string host)
    {
        var h = host.Trim('[', ']');
        return h is "*" or "0.0.0.0" or "::";
    }

    /// <summary>http://host:port for TCP ports 1–65535, mapping wildcard hosts to loopback.</summary>
    public static string? BrowserUrl(PortEntry e)
    {
        if (e.Protocol != "TCP" || e.Port is < 1 or > 65535) return null;
        var host = e.Host.Trim('[', ']') switch
        {
            "*" => "localhost",
            "0.0.0.0" => "127.0.0.1",
            "::" => "[::1]",
            var h when h.Contains(':') => $"[{h}]",
            var h => h,
        };
        return $"http://{host}:{e.Port}";
    }

    /// <summary>Case-insensitive substring of "port processName pid address".</summary>
    public static bool Matches(PortEntry e, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return true;
        return $"{e.Port} {e.ProcessName} {e.Pid} {e.Address}".Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes duplicates (protocol + port + address + PID) and sorts by port, then process name.</summary>
    public static List<PortEntry> Normalized(IEnumerable<PortEntry> entries) => entries
        .Where(e => e.Port > 0)
        .DistinctBy(e => (e.Protocol, e.Port, e.Address, e.Pid))
        .OrderBy(e => e.Port)
        .ThenBy(e => e.ProcessName, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(e => e.Protocol, StringComparer.Ordinal)
        .ToList();

    /// <summary>Port numbers in the tables are in network byte order in the low 16 bits.</summary>
    public static int PortFromNetwork(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
}

/// <summary>Reads the listening TCP (and optionally UDP) endpoints with their owning PIDs.</summary>
public static class PortTable
{
    public static List<PortEntry> Read(bool includeUdp)
    {
        var samples = ProcessSnapshot.Take().ToDictionary(s => s.Pid, s => s);
        var raw = new List<(string Protocol, string Host, int Port, uint Pid)>();
        raw.AddRange(ReadTcp(AF_INET));
        raw.AddRange(ReadTcp(AF_INET6));
        if (includeUdp)
        {
            raw.AddRange(ReadUdp(AF_INET));
            raw.AddRange(ReadUdp(AF_INET6));
        }
        var entries = raw.Select(r =>
        {
            samples.TryGetValue(r.Pid, out var s);
            var name = r.Pid == 4 ? "System" : s is null ? $"PID {r.Pid}" : System.IO.Path.GetFileNameWithoutExtension(s.Name);
            return new PortEntry(r.Protocol, r.Host, r.Port, r.Pid, name, s?.CreateTime ?? 0);
        });
        return PortLogic.Normalized(entries);
    }

    private static IEnumerable<(string, string, int, uint)> ReadTcp(int family)
    {
        var buffer = Fetch((IntPtr b, ref int size) => GetExtendedTcpTable(b, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0));
        if (buffer is null) yield break;
        try
        {
            var count = Marshal.ReadInt32(buffer.Value);
            // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (6 × 4 bytes).
            // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid (56 bytes).
            var rowSize = family == AF_INET ? 24 : 56;
            for (var i = 0; i < count; i++)
            {
                var row = buffer.Value + 4 + i * rowSize;
                if (family == AF_INET)
                {
                    var addr = new IPAddress((uint)Marshal.ReadInt32(row, 4));
                    yield return ("TCP", addr.ToString(), PortLogic.PortFromNetwork((uint)Marshal.ReadInt32(row, 8)), (uint)Marshal.ReadInt32(row, 20));
                }
                else
                {
                    var bytes = new byte[16];
                    Marshal.Copy(row, bytes, 0, 16);
                    yield return ("TCP", new IPAddress(bytes).ToString(), PortLogic.PortFromNetwork((uint)Marshal.ReadInt32(row, 20)), (uint)Marshal.ReadInt32(row, 52));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer.Value);
        }
    }

    private static IEnumerable<(string, string, int, uint)> ReadUdp(int family)
    {
        var buffer = Fetch((IntPtr b, ref int size) => GetExtendedUdpTable(b, ref size, false, family, UDP_TABLE_OWNER_PID, 0));
        if (buffer is null) yield break;
        try
        {
            var count = Marshal.ReadInt32(buffer.Value);
            // MIB_UDPROW_OWNER_PID: localAddr, localPort, pid (12). MIB_UDP6ROW_OWNER_PID: addr[16], scope, port, pid (28).
            var rowSize = family == AF_INET ? 12 : 28;
            for (var i = 0; i < count; i++)
            {
                var row = buffer.Value + 4 + i * rowSize;
                if (family == AF_INET)
                {
                    var addr = new IPAddress((uint)Marshal.ReadInt32(row, 0));
                    yield return ("UDP", addr.ToString(), PortLogic.PortFromNetwork((uint)Marshal.ReadInt32(row, 4)), (uint)Marshal.ReadInt32(row, 8));
                }
                else
                {
                    var bytes = new byte[16];
                    Marshal.Copy(row, bytes, 0, 16);
                    yield return ("UDP", new IPAddress(bytes).ToString(), PortLogic.PortFromNetwork((uint)Marshal.ReadInt32(row, 20)), (uint)Marshal.ReadInt32(row, 24));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer.Value);
        }
    }

    private delegate uint TableCall(IntPtr buffer, ref int size);

    private static IntPtr? Fetch(TableCall call)
    {
        var size = 0;
        call(IntPtr.Zero, ref size);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            size += 4096;
            var buffer = Marshal.AllocHGlobal(size);
            var result = call(buffer, ref size);
            if (result == 0) return buffer;
            Marshal.FreeHGlobal(buffer);
            if (result != 122) // ERROR_INSUFFICIENT_BUFFER
            {
                Log.Warn($"Port table read failed: {result}");
                return null;
            }
        }
        return null;
    }
}
