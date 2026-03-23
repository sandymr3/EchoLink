using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EchoLink.Models;

namespace EchoLink.Services;

public class AndroidTelemetryStrategy : ITelemetryStrategy
{
    private long[]? _lastTicks;

    public async Task<TelemetrySnapshot> GetLocalSnapshotAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                // CPU
                double cpu = 0;
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
                                cpu = deltaTotal > 0 ? Math.Clamp((1.0 - (double)deltaIdle / deltaTotal) * 100.0, 0, 100) : 0;
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
                                if (m.Success) totalRam = long.Parse(m.Value);
                            }
                            else if (line.StartsWith("MemAvailable:"))
                            {
                                var m = Regex.Match(line, @"\d+");
                                if (m.Success) availRam = long.Parse(m.Value);
                            }
                        }
                    }
                } catch { }

                // Disk
                long totalDisk = 0, freeDisk = 0;
                try
                {
                    // Android data directory
                    var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.RootDirectory.FullName == "/data" || d.RootDirectory.FullName == "/");
                    if (drive != null)
                    {
                        totalDisk = drive.TotalSize;
                        freeDisk = drive.AvailableFreeSpace;
                    }
                }
                catch { }

                // Uptime
                TimeSpan uptime = TimeSpan.Zero;
                try
                {
                    if (File.Exists("/proc/uptime"))
                    {
                        var upStr = File.ReadAllText("/proc/uptime").Split(' ')[0];
                        if (double.TryParse(upStr, out var upSecs))
                        {
                            uptime = TimeSpan.FromSeconds(upSecs);
                        }
                    }
                    else
                    {
                        uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
                    }
                }
                catch { }

                // Battery - we could use Android Java interop, but for now file read if possible
                double battery = -1;
                bool charging = false;
                try
                {
                    if (File.Exists("/sys/class/power_supply/battery/capacity"))
                    {
                        var batStr = File.ReadAllText("/sys/class/power_supply/battery/capacity").Trim();
                        if (double.TryParse(batStr, out var b)) battery = b;
                        
                        var status = File.ReadAllText("/sys/class/power_supply/battery/status").Trim();
                        charging = status.Equals("Charging", StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch { }

                return new TelemetrySnapshot(
                    cpu,
                    totalRam * 1024L,
                    (totalRam - availRam) * 1024L,
                    totalDisk,
                    totalDisk - freeDisk,
                    0, 0, battery, charging, uptime, -1
                );
            }
            catch
            {
                return TelemetrySnapshot.Empty;
            }
        });
    }
}