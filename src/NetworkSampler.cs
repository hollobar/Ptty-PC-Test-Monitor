using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace PttyPcTestMonitor;

public sealed class NetworkReading
{
    public double DownloadMbps, UploadMbps;
}

/// <summary>
/// Computes download/upload speed from adapter byte counters.
/// Only adapters with a default gateway are counted, so virtual switches and VPN side-adapters
/// that mirror the same traffic are not double-counted.
/// </summary>
public sealed class NetworkSampler
{
    private readonly Dictionary<string, (long Rx, long Tx)> _previous = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastSample;

    public NetworkReading Sample()
    {
        var now = _clock.Elapsed;
        var seconds = (now - _lastSample).TotalSeconds;
        _lastSample = now;

        long rxDelta = 0, txDelta = 0;
        var seen = new HashSet<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsInternetAdapter(nic)) continue;

            var stats = nic.GetIPStatistics();
            long rx = stats.BytesReceived, tx = stats.BytesSent;
            seen.Add(nic.Id);

            // Counters can reset when an adapter reconnects; skip that one sample.
            if (_previous.TryGetValue(nic.Id, out var prev) && rx >= prev.Rx && tx >= prev.Tx)
            {
                rxDelta += rx - prev.Rx;
                txDelta += tx - prev.Tx;
            }
            _previous[nic.Id] = (rx, tx);
        }

        foreach (var gone in _previous.Keys.Where(id => !seen.Contains(id)).ToList())
            _previous.Remove(gone);

        if (seconds <= 0) seconds = 1;
        return new NetworkReading
        {
            DownloadMbps = rxDelta * 8 / seconds / 1_000_000,
            UploadMbps = txDelta * 8 / seconds / 1_000_000,
        };
    }

    private static bool IsInternetAdapter(NetworkInterface nic)
    {
        if (nic.OperationalStatus != OperationalStatus.Up) return false;
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;

        return nic.GetIPProperties().GatewayAddresses.Any(g =>
            !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
    }
}
