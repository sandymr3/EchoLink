using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EchoLink.Models;

namespace EchoLink.Services;

public class WindowsTelemetryStrategy : ITelemetryStrategy
{
    private DateTime _lastUpdate = DateTime.MinValue;
    private double _lastCpu = 0;
    private long _lastNetUp = 0;
    private long _lastNetDown = 0;

    public async Task<TelemetrySnapshot> GetLocalSnapshotAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                // CPU & RAM using CIM (WMI)
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-NoProfile -NonInteractive -Command \"$c=(Get-CimInstance Win32_Processor).LoadPercentage; $m=Get-CimInstance Win32_OperatingSystem; '{0},{1},{2}' -f $c,$m.TotalVisibleMemorySize,$m.FreePhysicalMemory\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                double cpu = 0;
                long totalRam = 0, freeRam = 0;

                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        var output = proc.StandardOutput.ReadToEnd().Trim();
                        proc.WaitForExit();
                        var parts = output.Split(',');
                        if (parts.Length == 3)
                        {
                            double.TryParse(parts[0], out cpu);
                            long.TryParse(parts[1], out totalRam);
                            long.TryParse(parts[2], out freeRam);
                        }
                    }
                }

                // Disk
                long totalDisk = 0, freeDisk = 0;
                try
                {
                    var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.Name.StartsWith("C"));
                    if (drive != null)
                    {
                        totalDisk = drive.TotalSize;
                        freeDisk = drive.AvailableFreeSpace;
                    }
                }
                catch { }

                // Uptime
                TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

                return new TelemetrySnapshot(
                    Math.Clamp(cpu, 0, 100),
                    totalRam * 1024L,
                    (totalRam - freeRam) * 1024L,
                    totalDisk,
                    totalDisk - freeDisk,
                    0, // NetUp (omitted for brevity, can use PerformanceCounter later)
                    0, // NetDown
                    -1, // Battery (omitted for brevity)
                    false, // IsCharging
                    uptime,
                    -1 // Temp
                );
            }
            catch
            {
                return TelemetrySnapshot.Empty;
            }
        });
    }
}
