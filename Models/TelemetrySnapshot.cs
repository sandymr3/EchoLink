namespace EchoLink.Models;

/// <summary>
/// Unified telemetry data returned by every OS strategy.
/// </summary>
public record TelemetrySnapshot(
    double CpuLoadPercentage,
    long   TotalRamBytes,
    long   UsedRamBytes,
    long   TotalDiskBytes = 0,
    long   UsedDiskBytes = 0,
    double NetworkUpMbps = 0,
    double NetworkDownMbps = 0,
    double BatteryPercentage = -1, // -1 means no battery or unknown
    bool   IsCharging = false,
    TimeSpan Uptime = default,
    double CpuTemperature = -1) // -1 means unknown
{
    public static readonly TelemetrySnapshot Empty = new(0, 0, 0);

    public double RamLoadPercentage =>
        TotalRamBytes > 0 ? (double)UsedRamBytes / TotalRamBytes * 100.0 : 0.0;

    public double DiskLoadPercentage =>
        TotalDiskBytes > 0 ? (double)UsedDiskBytes / TotalDiskBytes * 100.0 : 0.0;

    public string UsedRamDisplay  => FormatBytes(UsedRamBytes);
    public string TotalRamDisplay => FormatBytes(TotalRamBytes);
    public string UsedDiskDisplay => FormatBytes(UsedDiskBytes);
    public string TotalDiskDisplay => FormatBytes(TotalDiskBytes);
    public string UptimeDisplay => $"{(int)Uptime.TotalDays}d {Uptime.Hours}h {Uptime.Minutes}m";

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576     => $"{bytes / 1_048_576.0:F0} MB",
            _                => $"{bytes / 1024.0:F0} KB"
        };
}
