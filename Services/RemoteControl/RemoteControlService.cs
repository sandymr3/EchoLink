using System;
using System.Threading;
using System.Threading.Tasks;
using EchoLink.Models;
using EchoLink.Services.UnifiedProtocol;

namespace EchoLink.Services;

public class RemoteControlService
{
    private static RemoteControlService? _instance;
    public static RemoteControlService Instance => _instance ??= new RemoteControlService();

    private readonly LoggingService _log = LoggingService.Instance;

    public void StartServer()
    {
        // Legacy port listener removed. The server is now handled entirely by UnifiedProtocolService.
    }

    public void StopServer()
    {
        // Legacy port listener removed. 
    }

    // Client side
    public async Task<bool> ConnectToTargetAsync(Device targetDevice, string pkeyPath, CancellationToken ct)
    {
        if (UnifiedProtocolClient.Instance.IsConnected)
        {
            _log.Info($"RemoteControl using existing Unified connection to {targetDevice.IpAddress}");
            return true;
        }

        return await UnifiedProtocolClient.Instance.ConnectAsync(targetDevice.IpAddress, pkeyPath, ct);
    }

    public void Disconnect()
    {
        // Don't forcefully disconnect the unified client if other services might be using it.
    }

    public async Task SendMoveAsync(double dx, double dy)
    {
        if (UnifiedProtocolClient.Instance.IsConnected)
        {
            try
            {
                // Multiplier for sensitivity
                await UnifiedProtocolClient.Instance.SendMouseMoveAsync((short)(dx * 2.5), (short)(dy * 2.5), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Warning($"RemoteControl send failed: {ex.Message}");
            }
        }
    }

    public async Task SendCommandAsync(string cmd)
    {
        if (UnifiedProtocolClient.Instance.IsConnected)
        {
            try
            {
                byte actionId = cmd switch
                {
                    "Lock" => 0,
                    "Restart" => 1,
                    "Shutdown" => 2,
                    _ => 255
                };

                if (actionId != 255)
                {
                    await UnifiedProtocolClient.Instance.SendSystemActionAsync(actionId, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"RemoteControl send failed: {ex.Message}");
            }
        }
    }

    // === Unified Protocol Integration ===

    /// <summary>
    /// Initialize unified protocol handlers.
    /// Call this once at application startup.
    /// </summary>
    public void InitializeUnifiedProtocol()
    {
        UnifiedProtocolService.Instance.RegisterHandler(
            UnifiedMessageType.MouseMove,
            async (payload, reply, ct) => await MouseControlService.Instance.HandleMouseMoveAsync(payload, ct));
        
        UnifiedProtocolService.Instance.RegisterHandler(
            UnifiedMessageType.MouseClick,
            async (payload, reply, ct) => await MouseControlService.Instance.HandleMouseClickAsync(payload, ct));
        
        UnifiedProtocolService.Instance.RegisterHandler(
            UnifiedMessageType.SystemAction,
            async (payload, reply, ct) => await SystemControlService.Instance.HandleSystemActionAsync(payload, ct));
        
        _log.Info("[RemoteControl] Unified protocol handlers registered");
    }
}
