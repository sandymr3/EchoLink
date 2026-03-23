using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EchoLink.Models;
using EchoLink.Services.UnifiedProtocol;

namespace EchoLink.Services;

public class SystemMonitorService
{
    private static SystemMonitorService? _instance;
    public static SystemMonitorService Instance => _instance ??= new SystemMonitorService();

    private readonly LoggingService _log = LoggingService.Instance;

    private SystemMonitorService() { }

    public void InitializeUnifiedProtocol()
    {
        UnifiedProtocolService.Instance.RegisterHandler(
            UnifiedMessageType.MonitorRequest,
            async (payload, reply, ct) => await HandleMonitorRequestAsync(payload, reply, ct));
        
        // Response handler for when we send a request to another device
        UnifiedProtocolService.Instance.RegisterHandler(
            UnifiedMessageType.MonitorResponse,
            async (payload, reply, ct) => await HandleMonitorResponseAsync(payload, ct));

        _log.Info("[SystemMonitor] Unified protocol handlers registered");
    }

    public event Action<TelemetrySnapshot>? SnapshotReceived;

    // --- Server Side (Handling requests from other devices) ---

    private async Task HandleMonitorRequestAsync(byte[] payload, Func<UnifiedMessageType, byte[], Task> reply, CancellationToken ct)
    {
        try
        {
            ITelemetryStrategy strategy = GetStrategyForCurrentOs();
            var snapshot = await strategy.GetLocalSnapshotAsync();

            var json = JsonSerializer.Serialize(snapshot);
            var responsePayload = Encoding.UTF8.GetBytes(json);

            await reply(UnifiedMessageType.MonitorResponse, responsePayload);
        }
        catch (Exception ex)
        {
            _log.Error($"[SystemMonitor] Failed to handle monitor request: {ex.Message}");
        }
    }

    private ITelemetryStrategy GetStrategyForCurrentOs()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsTelemetryStrategy();
        if (OperatingSystem.IsAndroid())
            return new AndroidTelemetryStrategy();
        return new LinuxTelemetryStrategy(); // Default for Linux
    }

    // --- Client Side (Requesting stats from other devices) ---

    public async Task<bool> ConnectAsync(Device targetDevice, string pkeyPath, CancellationToken ct)
    {
        if (UnifiedProtocolClient.Instance.IsConnected)
        {
            return true;
        }

        return await UnifiedProtocolClient.Instance.ConnectAsync(targetDevice.IpAddress, pkeyPath, ct);
    }

    public async Task RequestSnapshotAsync()
    {
        if (UnifiedProtocolClient.Instance.IsConnected)
        {
            try
            {
                await UnifiedProtocolClient.Instance.SendMessageAsync(UnifiedMessageType.MonitorRequest, Array.Empty<byte>(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Warning($"[SystemMonitor] Request failed: {ex.Message}");
            }
        }
    }

    private Task HandleMonitorResponseAsync(byte[] payload, CancellationToken ct)
    {
        try
        {
            var json = Encoding.UTF8.GetString(payload);
            var snapshot = JsonSerializer.Deserialize<TelemetrySnapshot>(json);
            if (snapshot != null)
            {
                SnapshotReceived?.Invoke(snapshot);
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"[SystemMonitor] Failed to parse response: {ex.Message}");
        }

        return Task.CompletedTask;
    }
}
