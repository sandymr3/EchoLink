using System.Linq;
using System.Text.RegularExpressions;
using EchoLink.Models;
using Renci.SshNet;

namespace EchoLink.Services;

/// <summary>
/// One compound shell command: reads /proc/stat, sleeps 0.3 s, reads /proc/stat again,
/// then reads /proc/meminfo — all in a single SSH execution channel.
/// The delta between the two cpu-tick snapshots gives the real load percentage.
/// </summary>
public class LinuxTelemetryStrategy : ITelemetryStrategy
{
    private const string Command =
        "awk '/^cpu /{print}' /proc/stat; sleep 0.3; " +
        "awk '/^cpu /{print}' /proc/stat; " +
        "grep -E '^Mem(Total|Available)' /proc/meminfo";

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

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
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

        double cpu = 0;
        if (ticks1 is not null && ticks2 is not null)
        {
            long deltaTotal = ticks2.Sum() - ticks1.Sum();
            long deltaIdle  = ticks2[3]    - ticks1[3];
            cpu = deltaTotal > 0 ? Math.Clamp((1.0 - (double)deltaIdle / deltaTotal) * 100.0, 0, 100) : 0;
        }

        return new TelemetrySnapshot(cpu, totalKb * 1024L, (totalKb - availKb) * 1024L);
    }
}
