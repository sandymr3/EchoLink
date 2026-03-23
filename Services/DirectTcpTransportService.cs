using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EchoLink.Services;

public class DirectTcpTransportService
{
    private static readonly Lazy<DirectTcpTransportService> _instance = new(() => new DirectTcpTransportService());
    public static DirectTcpTransportService Instance => _instance.Value;

    public const int SharedPort = 6969;

    private readonly LoggingService _log = LoggingService.Instance;
    private readonly ConcurrentDictionary<string, Func<Stream, CancellationToken, Task>> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;

    private DirectTcpTransportService() { }

    public void RegisterHandler(string channel, Func<Stream, CancellationToken, Task> handler)
    {
        if (string.IsNullOrWhiteSpace(channel))
            throw new ArgumentException("Channel is required.", nameof(channel));

        _handlers[channel] = handler;
        EnsureStarted();
    }

    public void UnregisterHandler(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
            return;

        _handlers.TryRemove(channel, out _);
    }

    public async Task<Stream> ConnectAsync(string targetIp, string channel, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetIp))
            throw new ArgumentException("Target IP is required.", nameof(targetIp));
        if (string.IsNullOrWhiteSpace(channel))
            throw new ArgumentException("Channel is required.", nameof(channel));

        var tcpClient = new TcpClient { NoDelay = true };
        await tcpClient.ConnectAsync(targetIp, SharedPort, ct);

        var stream = tcpClient.GetStream();
        var preface = Encoding.UTF8.GetBytes(channel + "\n");
        await stream.WriteAsync(preface, 0, preface.Length, ct);
        await stream.FlushAsync(ct);

        return new ClientOwnedStream(tcpClient, stream);
    }

    private void EnsureStarted()
    {
        if (_cts is not null)
            return;

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, SharedPort);
        _listener.Start();

        _log.Info($"[DirectTcp] Shared transport listening on 0.0.0.0:{SharedPort}");

        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        if (_listener is null)
            return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        catch (Exception ex)
        {
            _log.Warning($"[DirectTcp] Accept loop error: {ex.Message}");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;

        using var stream = client.GetStream();
        string? channel = await ReadPrefaceLineAsync(stream, 64, ct);
        if (string.IsNullOrWhiteSpace(channel))
            return;

        if (!_handlers.TryGetValue(channel, out var handler))
        {
            _log.Warning($"[DirectTcp] No handler for channel '{channel}'.");
            return;
        }

        try
        {
            await handler(stream, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Warning($"[DirectTcp] Channel '{channel}' handler failed: {ex.Message}");
        }
    }

    private static async Task<string?> ReadPrefaceLineAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var buffer = new List<byte>(Math.Min(32, maxBytes));
        var one = new byte[1];

        while (buffer.Count < maxBytes)
        {
            int read = await stream.ReadAsync(one, 0, 1, ct);
            if (read == 0)
                break;

            if (one[0] == (byte)'\n')
                break;

            if (one[0] != (byte)'\r')
                buffer.Add(one[0]);
        }

        if (buffer.Count == 0)
            return null;

        return Encoding.UTF8.GetString(buffer.ToArray()).Trim();
    }

    private sealed class ClientOwnedStream : Stream
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        public ClientOwnedStream(TcpClient client, NetworkStream stream)
        {
            _client = client;
            _stream = stream;
        }

        public override bool CanRead => _stream.CanRead;
        public override bool CanSeek => _stream.CanSeek;
        public override bool CanWrite => _stream.CanWrite;
        public override long Length => _stream.Length;
        public override long Position { get => _stream.Position; set => _stream.Position = value; }

        public override void Flush() => _stream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _stream.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _stream.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);
        public override void SetLength(long value) => _stream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _stream.WriteAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _stream.Dispose(); } catch { }
                try { _client.Dispose(); } catch { }
            }

            base.Dispose(disposing);
        }
    }
}
