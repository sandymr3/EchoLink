using System;
using System.IO;
using System.Linq;
using System.Threading;
using EchoLink.Models;

namespace EchoLink.Services.SystemMonitor;

public class LinuxMetricsCollector : ISystemMetricsCollector
{
    public SystemMetricsSnapshot Collect()
    {
        var snapshot = new SystemMetricsSnapshot();

        if (!OperatingSystem.IsLinux()) return snapshot;

        try
        {
            snapshot.CpuUsagePercent = GetCpuUsage();
            GetMemory(snapshot);
            GetDisk(snapshot);
            GetNetwork(snapshot);

            var procDirs = Directory.GetDirectories("/proc");
            snapshot.ProcessCount = procDirs.Count(d => int.TryParse(Path.GetFileName(d), out _));

            snapshot.LoadAverage1m = GetLoadAverage();
        }
        catch (Exception)
        {
            // Ignore collection errors (e.g., access denied on some /proc files)
        }

        return snapshot;
    }

    private (long idle, long total) ReadCpu()
    {
        var lines = File.ReadLines("/proc/stat");
        var parts = lines.First().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        long user = long.Parse(parts[1]);
        long nice = long.Parse(parts[2]);
        long system = long.Parse(parts[3]);
        long idle = long.Parse(parts[4]);

        long total = user + nice + system + idle;

        return (idle, total);
    }

    private double GetCpuUsage()
    {
        var (idle1, total1) = ReadCpu();
        Thread.Sleep(200); // Shorter sleep to avoid blocking Collect() too long
        var (idle2, total2) = ReadCpu();

        var idleDelta = idle2 - idle1;
        var totalDelta = total2 - total1;

        if (totalDelta == 0) return 0;

        return 100.0 * (1.0 - (double)idleDelta / totalDelta);
    }

    private void GetMemory(SystemMetricsSnapshot s)
    {
        var lines = File.ReadAllLines("/proc/meminfo");

        long total = ParseKb(lines, "MemTotal");
        long available = ParseKb(lines, "MemAvailable");

        s.TotalMemoryBytes = total;
        s.FreeMemoryBytes = available;
        s.UsedMemoryBytes = total - available;
    }

    private long ParseKb(string[] lines, string key)
    {
        var line = lines.FirstOrDefault(l => l.StartsWith(key));
        if (line == null) return 0;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && long.TryParse(parts[1], out var val))
        {
            return val * 1024;
        }
        return 0;
    }

    private double GetLoadAverage()
    {
        if (File.Exists("/proc/loadavg"))
        {
            var content = File.ReadAllText("/proc/loadavg");
            var parts = content.Split(' ');
            if (parts.Length > 0 && double.TryParse(parts[0], out var load))
            {
                return load;
            }
        }
        return 0;
    }

    private void GetNetwork(SystemMetricsSnapshot s)
    {
        if (!File.Exists("/proc/net/dev")) return;
        
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

        s.NetworkBytesReceived = rx;
        s.NetworkBytesSent = tx;
    }

    private void GetDisk(SystemMetricsSnapshot s)
    {
        long total = 0;
        long free = 0;

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;

                total += drive.TotalSize;
                free += drive.AvailableFreeSpace;
            }
        }
        catch { }

        s.DiskTotalBytes = total;
        s.DiskFreeBytes = free;
    }
}
