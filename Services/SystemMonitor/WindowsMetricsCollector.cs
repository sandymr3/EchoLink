using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using EchoLink.Models;

namespace EchoLink.Services.SystemMonitor;

#pragma warning disable CA1416 // Validate platform compatibility
public class WindowsMetricsCollector : ISystemMetricsCollector
{
    private PerformanceCounter? _cpu;
    private PerformanceCounter? _memAvailable;
    private readonly ulong _totalMemory;

    public WindowsMetricsCollector()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _memAvailable = new PerformanceCounter("Memory", "Available Bytes");
                _totalMemory = (ulong)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            }
            catch
            {
                // Fallback or ignore if performance counters are corrupted
            }
        }
    }

    public SystemMetricsSnapshot Collect()
    {
        var snapshot = new SystemMetricsSnapshot();

        if (!OperatingSystem.IsWindows()) return snapshot;

        try
        {
            snapshot.CpuUsagePercent = _cpu?.NextValue() ?? 0;

            var available = (long)(_memAvailable?.NextValue() ?? 0);

            snapshot.TotalMemoryBytes = (long)_totalMemory;
            snapshot.FreeMemoryBytes = available;
            snapshot.UsedMemoryBytes = snapshot.TotalMemoryBytes - available;

            snapshot.ProcessCount = Process.GetProcesses().Length;

            CollectDisk(snapshot);
            CollectNetwork(snapshot);
        }
        catch (Exception)
        {
            // Ignore temporary metric collection failures
        }

        return snapshot;
    }

    private void CollectDisk(SystemMetricsSnapshot s)
    {
        long total = 0;
        long free = 0;

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;

            total += drive.TotalSize;
            free += drive.AvailableFreeSpace;
        }

        s.DiskTotalBytes = total;
        s.DiskFreeBytes = free;
    }

    private void CollectNetwork(SystemMetricsSnapshot s)
    {
        long received = 0;
        long sent = 0;

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            var stats = ni.GetIPv4Statistics();
            received += stats.BytesReceived;
            sent += stats.BytesSent;
        }

        s.NetworkBytesReceived = received;
        s.NetworkBytesSent = sent;
    }
}
#pragma warning restore CA1416
