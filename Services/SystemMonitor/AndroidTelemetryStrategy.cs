using System.Linq;
using System.Text.RegularExpressions;
using EchoLink.Models;
using Renci.SshNet;

namespace EchoLink.Services;

/// <summary>
/// Android (Termux SSH) telemetry strategy.
/// Uses a compound command: two /proc/stat reads for accurate CPU delta,
/// plus /proc/meminfo for RAM (available on all Android kernels).
/// Falls back to top-line parsing if /proc/stat is unavailable.
/// </summary>
public class AndroidTelemetryStrategy : ITelemetryStrategy
{
    // Same delta-stat trick as Linux; Android /proc/stat is identical
    private const string Command =
        "awk '/^cpu /{print}' /proc/stat 2>/dev/null; sleep 0.3; " +
        "awk '/^cpu /{print}' /proc/stat 2>/dev/null; " +
        "grep -E '^Mem(Total|Available)' /proc/meminfo 2>/dev/null; " +
        // Fallback: top output if /proc/stat yielded nothing
        "echo '---TOP---'; top -n 1 -b 2>/dev/null || top -n 1 2>/dev/null";

    public Task<TelemetrySnapshot> GetSnapshotAsync(SshClient client) =>
        Task.Run(() =>
        {
            try
            {
                using var cmd = client.CreateCommand(Command);
                cmd.CommandTimeout = TimeSpan.FromSeconds(15);
                return Parse(cmd.Execute());
            }
            catch
            {
                return TelemetrySnapshot.Empty;
            }
        });

    private static TelemetrySnapshot Parse(string output)
    {
        long[]? ticks1 = null, ticks2 = null;
        long totalKb = 0, availKb = 0;
        bool inTopSection = false;
        double topCpu = -1;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();

            if (line == "---TOP---") { inTopSection = true; continue; }

            if (!inTopSection)
            {
                // /proc/stat cpu line
                if (line.StartsWith("cpu "))
                {
                    var nums = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                   .Skip(1).Take(7)
                                   .Select(p => long.TryParse(p, out var n) ? n : 0L)
                                   .ToArray();
                    if (nums.Length >= 4)
                    {
                        if (ticks1 is null) ticks1 = nums;
                        else if (ticks2 is null) ticks2 = nums;
                    }
                }
                else if (line.StartsWith("MemTotal:"))
                {
                    var m = Regex.Match(line, @"\d+");
                    if (m.Success) totalKb = long.Parse(m.Value);
                }
                else if (line.StartsWith("MemAvailable:"))
                {
                    var m = Regex.Match(line, @"\d+");
                    if (m.Success) availKb = long.Parse(m.Value);
                }
            }
            else if (topCpu < 0)
            {
                // Try various Android top CPU line formats (manufacturer variations)
                // Format 1: "%Cpu(s):  5.2 us,  2.1 sy, ..."
                var m1 = Regex.Match(line, @"%Cpu.*?:\s*([\d.]+)\s*us.*?([\d.]+)\s*sy", RegexOptions.IgnoreCase);
                if (m1.Success
                    && double.TryParse(m1.Groups[1].Value, System.Globalization.NumberStyles.Any,
                                       System.Globalization.CultureInfo.InvariantCulture, out var us)
                    && double.TryParse(m1.Groups[2].Value, System.Globalization.NumberStyles.Any,
                                       System.Globalization.CultureInfo.InvariantCulture, out var sy))
                {
                    topCpu = Math.Clamp(us + sy, 0, 100);
                    continue;
                }

                // Format 2: "User 25%, System 10%"
                var m2 = Regex.Match(line, @"[Uu]ser\s+([\d.]+)%.*?[Ss]ystem\s+([\d.]+)%");
                if (m2.Success
                    && double.TryParse(m2.Groups[1].Value, System.Globalization.NumberStyles.Any,
                                       System.Globalization.CultureInfo.InvariantCulture, out var u2)
                    && double.TryParse(m2.Groups[2].Value, System.Globalization.NumberStyles.Any,
                                       System.Globalization.CultureInfo.InvariantCulture, out var s2))
                {
                    topCpu = Math.Clamp(u2 + s2, 0, 100);
                    continue;
                }

                // Format 3: "CPU:  75% usr  15% sys" (compact)
                var m3 = Regex.Match(line, @"CPU:\s*([\d.]+)%\s*usr\s*([\d.]+)%\s*sys", RegexOptions.IgnoreCase);
                if (m3.Success
                    && double.TryParse(m3.Groups[1].Value, System.Globalization.NumberStyles.Any,
                                       System.Globalization.CultureInfo.InvariantCulture, out var u3)
                    && double.TryParse(m3.Groups[2].Value, System.Globalization.NumberStyles.Any,
                                       System.Globalization.CultureInfo.InvariantCulture, out var s3))
                {
                    topCpu = Math.Clamp(u3 + s3, 0, 100);
                }
            }
        }

        // Choose best CPU source
        double cpu = 0;
        if (ticks1 is not null && ticks2 is not null)
        {
            long deltaTotal = ticks2.Sum() - ticks1.Sum();
            long deltaIdle  = ticks2[3]    - ticks1[3];
            cpu = deltaTotal > 0 ? Math.Clamp((1.0 - (double)deltaIdle / deltaTotal) * 100.0, 0, 100) : 0;
        }
        else if (topCpu >= 0)
        {
            cpu = topCpu;
        }

        return new TelemetrySnapshot(cpu, totalKb * 1024L, (totalKb - availKb) * 1024L);
    }
}
