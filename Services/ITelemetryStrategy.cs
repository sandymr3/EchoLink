using Renci.SshNet;

namespace EchoLink.Services;

public interface ITelemetryStrategy
{
    Task<double> GetCpuUsageAsync(SshClient client);
    Task<double> GetRamUsageAsync(SshClient client);
}
