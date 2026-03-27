namespace EchoLink.Models;

public class SystemMetricsSnapshot
{
    public double CpuUsagePercent { get; set; }

    public long TotalMemoryBytes { get; set; }
    public long UsedMemoryBytes { get; set; }
    public long FreeMemoryBytes { get; set; }

    public long DiskTotalBytes { get; set; }
    public long DiskFreeBytes { get; set; }

    public long NetworkBytesReceived { get; set; }
    public long NetworkBytesSent { get; set; }

    public int ProcessCount { get; set; }

    public double LoadAverage1m { get; set; } // Linux only
}
