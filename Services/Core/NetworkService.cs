using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace EchoLink.Services;

/// <summary>
/// Centralized networking utilities for the EchoLink mesh.
/// </summary>
public class NetworkService
{
    private static readonly Lazy<NetworkService> _instance = new(() => new NetworkService());
    public static NetworkService Instance => _instance.Value;

    private readonly LoggingService _log = LoggingService.Instance;
    public const int TailscaleSocks5Port = 1055;

    private NetworkService() { }

    /// <summary>
    /// Connects to a remote Tailscale IP and port using the local SOCKS5 proxy.
    /// Supports both IPv4 (100.x.y.z) and IPv6 (fd7a:x:y::z).
    /// </summary>
    public async Task<TcpClient?> ConnectViaSocks5Async(string targetIp, int port, CancellationToken ct = default)
    {
        var client = new TcpClient();
        try
        {
            // Connect to the local SOCKS5 proxy provided by Tailscale
            await client.ConnectAsync("127.0.0.1", TailscaleSocks5Port, ct);
            var stream = client.GetStream();

            // 1. SOCKS5 Greeting (No authentication required)
            // [Version: 5][Num Methods: 1][Method: 0 (No Auth)]
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct);
            
            byte[] response1 = new byte[2];
            int read = await stream.ReadAsync(response1.AsMemory(0, 2), ct);
            if (read != 2 || response1[0] != 0x05 || response1[1] != 0x00)
            {
                _log.Error($"[Network] SOCKS5 greeting failed. Response: {BitConverter.ToString(response1)}");
                client.Dispose();
                return null;
            }

            // 2. SOCKS5 Connection Request
            // [Version: 5][Cmd: 1 (CONNECT)][RSV: 0][Atyp: ?][Addr: ?][Port: 2]
            
            if (!IPAddress.TryParse(targetIp, out var ip))
            {
                _log.Error($"[Network] Invalid IP address: {targetIp}");
                client.Dispose();
                return null;
            }

            byte atyp = ip.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04;
            byte[] addrBytes = ip.GetAddressBytes();
            byte[] portBytes = BitConverter.GetBytes((short)port);
            if (BitConverter.IsLittleEndian) Array.Reverse(portBytes);

            byte[] request = new byte[6 + addrBytes.Length];
            request[0] = 0x05; // Version
            request[1] = 0x01; // CONNECT
            request[2] = 0x00; // RSV
            request[3] = atyp; // Address Type (1 = IPv4, 4 = IPv6)
            
            Array.Copy(addrBytes, 0, request, 4, addrBytes.Length);
            Array.Copy(portBytes, 0, request, 4 + addrBytes.Length, 2);

            await stream.WriteAsync(request.AsMemory(0, request.Length), ct);

            // 3. SOCKS5 Response
            // [Version: 5][Rep: ?][RSV: 0][Atyp: ?][Bnd.Addr: ?][Bnd.Port: 2]
            // Rep 0x00 = Success
            byte[] response2 = new byte[256]; // Oversized to be safe
            read = await stream.ReadAsync(response2.AsMemory(0, response2.Length), ct);
            
            if (read < 2 || response2[1] != 0x00)
            {
                _log.Error($"[Network] SOCKS5 connection to {targetIp}:{port} rejected. Error code: 0x{response2[1]:X2}");
                client.Dispose();
                return null;
            }

            return client;
        }
        catch (Exception ex)
        {
            _log.Error($"[Network] SOCKS5 connection to {targetIp}:{port} failed: {ex.Message}");
            client.Dispose();
            return null;
        }
    }
}
