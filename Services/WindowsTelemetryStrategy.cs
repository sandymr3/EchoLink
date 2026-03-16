using System.Text.RegularExpressions;
using Renci.SshNet;

namespace EchoLink.Services;

public class WindowsTelemetryStrategy : ITelemetryStrategy
{
    public async Task<double> GetCpuUsageAsync(SshClient client) =>
        await Task.Run(() =>
        {
            var cmd = client.CreateCommand("wmic cpu get loadpercentage /value");
            cmd.Execute();
            var match = Regex.Match(cmd.Result, @"LoadPercentage=(\d+)", RegexOptions.IgnoreCase);
            return match.Success ? double.Parse(match.Groups[1].Value) : 0.0;
        });

    public async Task<double> GetRamUsageAsync(SshClient client) =>
        await Task.Run(() =>
        {
            var totalCmd = client.CreateCommand("wmic ComputerSystem get TotalPhysicalMemory /value");
            totalCmd.Execute();
            var freeCmd = client.CreateCommand("wmic OS get FreePhysicalMemory /value");
            freeCmd.Execute();

            var totalMatch = Regex.Match(totalCmd.Result, @"TotalPhysicalMemory=(\d+)");
            var freeMatch  = Regex.Match(freeCmd.Result,  @"FreePhysicalMemory=(\d+)");
            if (!totalMatch.Success || !freeMatch.Success) return 0.0;

            long totalBytes = long.Parse(totalMatch.Groups[1].Value);
            long freeKb     = long.Parse(freeMatch.Groups[1].Value);
            long usedBytes  = totalBytes - (freeKb * 1024L);
            return totalBytes > 0 ? (double)usedBytes / totalBytes * 100.0 : 0.0;
        });
}
