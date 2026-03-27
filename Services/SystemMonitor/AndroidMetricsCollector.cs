using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EchoLink.Models;

namespace EchoLink.Services.SystemMonitor;

public class AndroidMetricsCollector : ISystemMetricsCollector
{
    private long[]? _lastTicks;

    public SystemMetricsSnapshot Collect()
    {
        var snapshot = new SystemMetricsSnapshot();
        if (!OperatingSystem.IsAndroid()) return snapshot;

        try
        {
            // CPU
            try
            {
                if (File.Exists("/proc/stat"))
                {
                    var statLines = File.ReadAllLines("/proc/stat");
                    var cpuLine = statLines.FirstOrDefault(l => l.StartsWith("cpu "))?.Trim();
                    if (cpuLine != null)
                    {
                        var ticks = cpuLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                           .Skip(1).Take(7)
                                           .Select(p => long.TryParse(p, out var n) ? n : 0L)
                                           .ToArray();
                        
                        if (_lastTicks != null && ticks.Length >= 4 && _lastTicks.Length >= 4)
                        {
                            long deltaTotal = ticks.Sum() - _lastTicks.Sum();
                            long deltaIdle  = ticks[3] - _lastTicks[3];
                            snapshot.CpuUsagePercent = deltaTotal > 0 ? Math.Clamp((1.0 - (double)deltaIdle / deltaTotal) * 100.0, 0, 100) : 0;
                        }
                        _lastTicks = ticks;
                    }
                }
            } catch { }

            // RAM
            long totalRam = 0, availRam = 0;
            try
            {
                if (File.Exists("/proc/meminfo"))
                {
                    var memLines = File.ReadAllLines("/proc/meminfo");
                    foreach (var line in memLines)
                    {
                        if (line.StartsWith("MemTotal:"))
                        {
                            var m = Regex.Match(line, @"\d+");
                            if (m.Success) totalRam = long.Parse(m.Value) * 1024L;
                        }
                        else if (line.StartsWith("MemAvailable:"))
                        {
                            var m = Regex.Match(line, @"\d+");
                            if (m.Success) availRam = long.Parse(m.Value) * 1024L;
                        }
                    }
                }
            } catch { }
            
            snapshot.TotalMemoryBytes = totalRam;
            snapshot.FreeMemoryBytes = availRam;
            snapshot.UsedMemoryBytes = totalRam - availRam;

            // Disk
            long totalDisk = 0, freeDisk = 0;
            try
            {
                var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.RootDirectory.FullName == "/data" || d.RootDirectory.FullName == "/");
                if (drive != null)
                {
                    totalDisk = drive.TotalSize;
                    freeDisk = drive.AvailableFreeSpace;
                }
            }
            catch { }
            
            snapshot.DiskTotalBytes = totalDisk;
            snapshot.DiskFreeBytes = freeDisk;

            // Load Average
            if (File.Exists("/proc/loadavg"))
            {
                var content = File.ReadAllText("/proc/loadavg");
                var parts = content.Split(' ');
                if (parts.Length > 0 && double.TryParse(parts[0], out var load))
                {
                    snapshot.LoadAverage1m = load;
                }
            }

            // Uptime or pseudo-process count could be extracted if needed
            // omitting for brevity matching original AndroidTelemetryStrategy
            snapshot.ProcessCount = Directory.GetDirectories("/proc").Count(d => int.TryParse(Path.GetFileName(d), out _));
            
            // Network
            if (File.Exists("/proc/net/dev"))
            {
                var lines = File.ReadAllLines("/proc/net/dev").Skip(2);
                long rx = 0, tx = 0;
                foreach (var line in lines)
                {
                    var parts = line.Split(':');
                    if (parts.Length < 2) continue;
                    var values = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (values.Length >= 9)
                    {
                        if (long.TryParse(values[0], out var r)) rx += r;
                        if (long.TryParse(values[8], out var t)) tx += t;
                    }
                }
                snapshot.NetworkBytesReceived = rx;
                snapshot.NetworkBytesSent = tx;
            }
        }
        catch { }

        return snapshot;
    }
}
