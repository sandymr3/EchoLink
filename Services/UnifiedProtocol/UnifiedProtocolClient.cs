using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace EchoLink.Services.UnifiedProtocol;

/// <summary>
/// Unified protocol client for connecting to remote devices
/// and sending messages over the unified protocol (port 55555).
/// All communication is routed via Tailscale SOCKS5 proxy directly to the target port,
/// bypassing SSH tunneling to avoid "Broken Pipe" errors on restricted platforms like Android.
/// </summary>
public class UnifiedProtocolClient
{
    private static UnifiedProtocolClient? _instance;
    public static UnifiedProtocolClient Instance => _instance ??= new UnifiedProtocolClient();

    private readonly LoggingService _log = LoggingService.Instance;
    private Stream? _stream;
    private TcpClient? _tcpClient;

    /// <summary>
    /// Returns true if connected to a remote device.
    /// </summary>
    public bool IsConnected => _stream != null;

    /// <summary>
    /// Connect to a remote device via direct SOCKS5 connection to the Unified Protocol port (55555).
    /// </summary>
    /// <param name="targetIp">Target device Tailscale IP</param>
    /// <param name="pkeyPath">Not used (preserved for API compatibility with existing service calls)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if connection successful</returns>
    public async Task<bool> ConnectAsync(string targetIp, string pkeyPath, CancellationToken ct)
    {
        Disconnect();

        var settings = SettingsService.Instance.Load();
        if (!settings.PeerUsernames.TryGetValue(targetIp, out _))
        {
            _log.Error($"[Unified] Cannot connect - not paired: {targetIp}");
            return false;
        }

        try
        {
            _log.Debug($"[Unified] Dialing {targetIp}:{UnifiedProtocolService.UnifiedPort} via SOCKS5...");
            
            _tcpClient = await NetworkService.Instance.ConnectViaSocks5Async(
                targetIp, 
                UnifiedProtocolService.UnifiedPort, 
                ct);

            if (_tcpClient == null || !_tcpClient.Connected)
            {
                return false;
            }

            _stream = _tcpClient.GetStream();
            _log.Info($"[Unified] Connected to {targetIp}:{UnifiedProtocolService.UnifiedPort}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"[Unified] Connection failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Disconnect from the current remote device.
    /// </summary>
    public void Disconnect()
    {
        _stream?.Dispose();
        _stream = null;
        _tcpClient?.Close();
        _tcpClient = null;
        _log.Info("[Unified] Disconnected");
    }

    /// <summary>
    /// Send a message with the specified type and payload.
    /// Format: [Type:1][Length:4][Payload:N]
    /// </summary>
    public async Task SendMessageAsync(UnifiedMessageType type, byte[] payload, CancellationToken ct)
    {
        if (_stream == null)
        {
            _log.Warning("[Unified] Cannot send - not connected");
            return;
        }

        try
        {
            // Build header: [Type:1][Length:4 big-endian]
            var header = new byte[5];
            header[0] = (byte)type;
            // Big-endian for network byte order
            var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));
            Array.Copy(lengthBytes, 0, header, 1, 4);

            await _stream.WriteAsync(header, ct);
            if (payload.Length > 0)
            {
                await _stream.WriteAsync(payload, ct);
            }
            await _stream.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            _log.Error($"[Unified] Send failed: {ex.Message}");
            Disconnect();
        }
    }
}
