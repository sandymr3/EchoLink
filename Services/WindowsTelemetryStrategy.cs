using System.Globalization;
using EchoLink.Models;
using Renci.SshNet;

namespace EchoLink.Services;

/// <summary>
/// Single PowerShell command returns "cpu,totalKB,freeKB" — culture-invariant, no wmic.
/// Resilient to non-English Windows locales because we control the output format.
/// </summary>
public class WindowsTelemetryStrategy : ITelemetryStrategy
{
    // Produces e.g. "5,16380584,10234456"
    private const string Command =
        "powershell -NoProfile -NonInteractive -Command " +
        "\"$c=(Get-CimInstance Win32_Processor).LoadPercentage;" +
        "$m=Get-CimInstance Win32_OperatingSystem;" +
        "'{0},{1},{2}' -f $c,$m.TotalVisibleMemorySize,$m.FreePhysicalMemory\"";

    public Task<TelemetrySnapshot> GetSnapshotAsync(SshClient client) =>
        Task.Run(() =>
        {
            try
            {
                using var cmd = client.CreateCommand(Command);
                cmd.CommandTimeout = TimeSpan.FromSeconds(12);
                var output = cmd.Execute().Trim();

                var parts = output.Split(',');
                if (parts.Length < 3
                    || !double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var cpu)
                    || !long.TryParse(parts[1].Trim(), out var totalKb)
                    || !long.TryParse(parts[2].Trim(), out var freeKb))
                {
                    return TelemetrySnapshot.Empty;
                }

                return new TelemetrySnapshot(
                    Math.Clamp(cpu, 0, 100),
                    totalKb * 1024L,
                    (totalKb - freeKb) * 1024L);
            }
            catch
            {
                return TelemetrySnapshot.Empty;
            }
        });
}
