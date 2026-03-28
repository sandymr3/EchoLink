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
    private CancellationTokenSource? _readCts;
    private readonly SemaphoreSlim _streamIoLock = new(1, 1);

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
        // Legacy signature kept for compatibility with callers that still pass a key path.
        return await ConnectAsync(
            targetIp,
            UnifiedProtocolService.UnifiedPort,
            NetworkService.TailscaleSocks5Port,
            ct);
    }

    /// <summary>
    /// Connect to a remote device via SOCKS5 to a specific target port.
    /// This path is used by remote control to avoid any SSH dependency.
    /// </summary>
    public async Task<bool> ConnectAsync(
        string targetIp,
        int targetPort,
        int socks5Port,
        CancellationToken ct)
    {
        Disconnect();

        try
        {
            _log.Debug($"[Unified] Dialing {targetIp}:{targetPort} via SOCKS5 127.0.0.1:{socks5Port}...");
            
            _tcpClient = await NetworkService.Instance.ConnectViaSocks5Async(
                targetIp,
                targetPort,
                socks5Port,
                ct);

            if (_tcpClient == null || !_tcpClient.Connected)
            {
                return false;
            }

            _tcpClient.NoDelay = true;

            _stream = _tcpClient.GetStream();
            _log.Info($"[Unified] Connected to {targetIp}:{UnifiedProtocolService.UnifiedPort}");
            
            _readCts = new CancellationTokenSource();
            _ = Task.Run(() => ReadLoopAsync(_stream, _readCts.Token));

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
        _readCts?.Cancel();
        _readCts?.Dispose();
        _readCts = null;

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
            await _streamIoLock.WaitAsync(ct);
            try
            {
                await WriteMessageAsync(type, payload, ct);
            }
            finally
            {
                _streamIoLock.Release();
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[Unified] Send failed: {ex.Message}");
            Disconnect();
        }
    }

    /// <summary>
    /// Send a request and wait for a specific response type on the same stream.
    /// </summary>
    public async Task<byte[]?> SendRequestAndWaitForResponseAsync(
        UnifiedMessageType requestType,
        byte[] requestPayload,
        UnifiedMessageType expectedResponseType,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (_stream == null)
        {
            _log.Warning("[Unified] Cannot request/response - not connected");
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var linkedCt = timeoutCts.Token;

        try
        {
            await _streamIoLock.WaitAsync(linkedCt);
            try
            {
                await WriteMessageAsync(requestType, requestPayload, linkedCt);

                var header = new byte[5];
                int readHeader = await ReadExactAsync(_stream, header, 0, header.Length, linkedCt);
                if (readHeader < header.Length)
                {
                    _log.Warning("[Unified] Response header read incomplete.");
                    return null;
                }

                var responseType = (UnifiedMessageType)header[0];
                int payloadLen = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 1));
                if (payloadLen < 0)
                {
                    _log.Warning("[Unified] Invalid response payload length.");
                    return null;
                }

                byte[] payload = Array.Empty<byte>();
                if (payloadLen > 0)
                {
                    payload = new byte[payloadLen];
                    int readPayload = await ReadExactAsync(_stream, payload, 0, payloadLen, linkedCt);
                    if (readPayload < payloadLen)
                    {
                        _log.Warning("[Unified] Response payload read incomplete.");
                        return null;
                    }
                }

                if (responseType != expectedResponseType)
                {
                    _log.Warning($"[Unified] Unexpected response type: {responseType} (expected {expectedResponseType})");
                    return null;
                }

                return payload;
            }
            finally
            {
                _streamIoLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            _log.Warning("[Unified] Request/response timed out or was canceled.");
            return null;
        }
        catch (Exception ex)
        {
            _log.Error($"[Unified] Request/response failed: {ex.Message}");
            Disconnect();
            return null;
        }
    }

    private async Task WriteMessageAsync(UnifiedMessageType type, byte[] payload, CancellationToken ct)
    {
        if (_stream == null)
        {
            throw new IOException("Unified stream is not connected.");
        }

        var header = new byte[5];
        header[0] = (byte)type;
        var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));
        Array.Copy(lengthBytes, 0, header, 1, 4);

        await _stream.WriteAsync(header, ct);
        if (payload.Length > 0)
        {
            await _stream.WriteAsync(payload, ct);
        }
        await _stream.FlushAsync(ct);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, ct);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }

        return totalRead;
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        var headerBuffer = new byte[5];
        
        while (!ct.IsCancellationRequested && _tcpClient?.Connected == true)
        {
            try
            {
                // Read 5-byte header: [Type:1][Length:4]
                int headerBytes = await UnifiedProtocolService.Instance.ReadExactAsync(stream, headerBuffer, 5, ct);
                if (headerBytes < 5) break;

                byte messageType = headerBuffer[0];
                int payloadLen = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(headerBuffer, 1));

                // Read payload
                byte[] payload = Array.Empty<byte>();
                if (payloadLen > 0)
                {
                    payload = new byte[payloadLen];
                    await UnifiedProtocolService.Instance.ReadExactAsync(stream, payload, payloadLen, ct);
                }

                // Dispatch to registered handler
                await UnifiedProtocolService.Instance.DispatchMessageAsync((UnifiedMessageType)messageType, payload, stream, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Debug($"[Unified] Client read loop error: {ex.Message}");
                break;
            }
        }
        
        if (!ct.IsCancellationRequested)
        {
            Disconnect();
        }
    }
}
