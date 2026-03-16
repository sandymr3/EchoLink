using System.Linq;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace EchoLink.Services;

public class LinuxTelemetryStrategy : ITelemetryStrategy
{
    private long[]? _prevCpuStats;

    public async Task<double> GetCpuUsageAsync(SshClient client) =>
        await Task.Run(() =>
        {
            var cmd = client.CreateCommand("grep '^cpu ' /proc/stat");
            cmd.Execute();

            var parts = cmd.Result.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8) return 0.0;

            // user nice system idle iowait irq softirq
            long[] stats = parts.Skip(1).Take(7).Select(long.Parse).ToArray();

            if (_prevCpuStats is null)
            {
                _prevCpuStats = stats;
                return 0.0;
            }

            long deltaTotal = stats.Sum() - _prevCpuStats.Sum();
            long deltaIdle  = stats[3]    - _prevCpuStats[3];
            _prevCpuStats   = stats;

            return deltaTotal > 0 ? (1.0 - (double)deltaIdle / deltaTotal) * 100.0 : 0.0;
        });

    public async Task<double> GetRamUsageAsync(SshClient client) =>
        await Task.Run(() =>
        {
            var cmd = client.CreateCommand("grep -E 'MemTotal|MemAvailable' /proc/meminfo");
            cmd.Execute();

            var totalMatch = Regex.Match(cmd.Result, @"MemTotal:\s+(\d+)");
            var availMatch = Regex.Match(cmd.Result, @"MemAvailable:\s+(\d+)");
            if (!totalMatch.Success || !availMatch.Success) return 0.0;

            long total = long.Parse(totalMatch.Groups[1].Value);
            long avail = long.Parse(availMatch.Groups[1].Value);
            return total > 0 ? (double)(total - avail) / total * 100.0 : 0.0;
        });
}
