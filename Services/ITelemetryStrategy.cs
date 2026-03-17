using EchoLink.Models;
using Renci.SshNet;

namespace EchoLink.Services;

/// <summary>
/// One compound SSH command fetches CPU + RAM together to minimise round trips.
/// </summary>
public interface ITelemetryStrategy
{
    Task<TelemetrySnapshot> GetSnapshotAsync(SshClient client);
}
