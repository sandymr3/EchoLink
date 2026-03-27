using EchoLink.Models;

namespace EchoLink.Services.SystemMonitor;

public interface ISystemMetricsCollector
{
    SystemMetricsSnapshot Collect();
}
