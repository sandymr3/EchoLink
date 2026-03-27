using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EchoLink.Models;
using EchoLink.Services.UnifiedProtocol;

namespace EchoLink.Services.SystemMonitor;

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
        
        UnifiedProtocolService.Instance.RegisterHandler(
            UnifiedMessageType.MonitorResponse,
            async (payload, reply, ct) => await HandleMonitorResponseAsync(payload, ct));

        _log.Info("[SystemMonitor] Unified protocol handlers registered");
        
        // Start Http Bridge for Prometheus metrics
        PrometheusHttpBridge.Instance.Start(9000);
    }

    public event Action<SystemMetricsSnapshot>? SnapshotReceived;

    // --- Server Side (Handling requests from other devices) ---

    private async Task HandleMonitorRequestAsync(byte[] payload, Func<UnifiedMessageType, byte[], Task> reply, CancellationToken ct)
    {
        try
        {
            ISystemMetricsCollector collector = GetStrategyForCurrentOs();
            var snapshot = await Task.Run(() => collector.Collect(), ct);

            var json = JsonSerializer.Serialize(snapshot);
            _log.Info($"[SystemMonitor] Sending MonitorResponse: {json}");
            var responsePayload = Encoding.UTF8.GetBytes(json);

            await reply(UnifiedMessageType.MonitorResponse, responsePayload);
        }
        catch (Exception ex)
        {
            _log.Error($"[SystemMonitor] Failed to handle monitor request: {ex.Message}");
        }
    }

    private ISystemMetricsCollector? _collector;
    
    private ISystemMetricsCollector GetStrategyForCurrentOs()
    {
        if (_collector != null) return _collector;

        if (OperatingSystem.IsWindows())
            _collector = new WindowsMetricsCollector();
        else if (OperatingSystem.IsAndroid())
            _collector = new AndroidMetricsCollector();
        else
            _collector = new LinuxMetricsCollector(); // Default for Linux
            
        return _collector;
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
                _log.Debug($"[SystemMonitor] Sending MonitorRequest...");
                await UnifiedProtocolClient.Instance.SendMessageAsync(UnifiedMessageType.MonitorRequest, Array.Empty<byte>(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Warning($"[SystemMonitor] Request failed: {ex.Message}");
            }
        }
        else
        {
             _log.Warning($"[SystemMonitor] Request failed: Not Connected to UnifiedProtocol Client.");
        }
    }

    private Task HandleMonitorResponseAsync(byte[] payload, CancellationToken ct)
    {
        try
        {
            var json = Encoding.UTF8.GetString(payload);
            _log.Info($"[SystemMonitor] Received MonitorResponse: {json}");
            var snapshot = JsonSerializer.Deserialize<SystemMetricsSnapshot>(json);
            if (snapshot != null)
            {
                SnapshotReceived?.Invoke(snapshot);
            }
            else
            {
                _log.Warning($"[SystemMonitor] Deserialized snapshot is null");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"[SystemMonitor] Failed to parse response: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public async Task<SystemMetricsSnapshot> FetchSnapshotForBridgeAsync()
    {
        if (UnifiedProtocolClient.Instance.IsConnected)
        {
            var tcs = new TaskCompletionSource<SystemMetricsSnapshot>();
            Action<SystemMetricsSnapshot>? handler = null;
            handler = s => 
            {
                tcs.TrySetResult(s);
                SnapshotReceived -= handler;
            };
            SnapshotReceived += handler;
            
            await RequestSnapshotAsync();

            var timeoutTask = Task.Delay(2000);
            if (await Task.WhenAny(tcs.Task, timeoutTask) == tcs.Task)
            {
                return tcs.Task.Result;
            }
            SnapshotReceived -= handler;
        }
        
        // Fallback to local
        var collector = GetStrategyForCurrentOs();
        return await Task.Run(() => collector.Collect());
    }
}
