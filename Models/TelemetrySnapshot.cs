namespace EchoLink.Models;

/// <summary>
/// Unified telemetry data returned by every OS strategy via a single SSH command.
/// </summary>
public record TelemetrySnapshot(
    double CpuLoadPercentage,
    long   TotalRamBytes,
    long   UsedRamBytes)
{
    public static readonly TelemetrySnapshot Empty = new(0, 0, 0);

    public double RamLoadPercentage =>
        TotalRamBytes > 0 ? (double)UsedRamBytes / TotalRamBytes * 100.0 : 0.0;

    public string UsedRamDisplay  => FormatBytes(UsedRamBytes);
    public string TotalRamDisplay => FormatBytes(TotalRamBytes);

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576     => $"{bytes / 1_048_576.0:F0} MB",
            _                => $"{bytes / 1024.0:F0} KB"
        };
}
