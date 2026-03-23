using EchoLink.Models;

namespace EchoLink.Services;

public interface ITelemetryStrategy
{
    Task<TelemetrySnapshot> GetLocalSnapshotAsync();
}
