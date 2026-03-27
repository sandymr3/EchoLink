using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EchoLink.Models;

namespace EchoLink.Services.SystemMonitor;

public class PrometheusHttpBridge
{
    private static PrometheusHttpBridge? _instance;
    public static PrometheusHttpBridge Instance => _instance ??= new PrometheusHttpBridge();

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly LoggingService _log = LoggingService.Instance;

    public void Start(int port = 5000)
    {
        if (_listener != null && _listener.IsListening) return;

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{port}/");
            _listener.Start();
            _log.Info($"[PrometheusHttpBridge] Started on port {port}");

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptRequestsAsync(_cts.Token));
        }
        catch (HttpListenerException ex)
        {
            _log.Warning($"[PrometheusHttpBridge] Failed to start listener on port {port}. Run as admin or change port. {ex.Message}");
            // Optional: fallback to localhost only if wildcard fails
            try
            {
                if (_listener != null)
                {
                    _listener.Close();
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://localhost:{port}/");
                    _listener.Start();
                    _log.Info($"[PrometheusHttpBridge] Started on localhost:{port}");
                    
                    _cts = new CancellationTokenSource();
                    _ = Task.Run(() => AcceptRequestsAsync(_cts.Token));
                }
            }
            catch (Exception fallbackEx)
            {
                _log.Error($"[PrometheusHttpBridge] Fallback also failed: {fallbackEx.Message}");
            }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        _listener = null;
        _log.Info("[PrometheusHttpBridge] Stopped");
    }

    private async Task AcceptRequestsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context), ct);
            }
            catch (HttpListenerException) { break; } // Stopped
            catch (ObjectDisposedException) { break; } // Stopped
            catch (Exception ex)
            {
                _log.Error($"[PrometheusHttpBridge] Context error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.Url?.AbsolutePath == "/metrics")
            {
                // Retrieve snapshot via SystemMonitorService (which decides if local or remote)
                var snapshot = await SystemMonitorService.Instance.FetchSnapshotForBridgeAsync();
                var responseStr = ConvertToPrometheus(snapshot);
                var buffer = Encoding.UTF8.GetBytes(responseStr);

                context.Response.ContentType = "text/plain";
                context.Response.ContentLength64 = buffer.Length;
                
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            else
            {
                context.Response.StatusCode = 404;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[PrometheusHttpBridge] Failed to serve metrics: {ex.Message}");
            context.Response.StatusCode = 500;
        }
        finally
        {
            context.Response.Close();
        }
    }

    private string ConvertToPrometheus(SystemMetricsSnapshot m)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"system_cpu_usage_percent {m.CpuUsagePercent}");
        sb.AppendLine($"system_memory_used_bytes {m.UsedMemoryBytes}");
        sb.AppendLine($"system_memory_free_bytes {m.FreeMemoryBytes}");
        sb.AppendLine($"system_disk_free_bytes {m.DiskFreeBytes}");
        sb.AppendLine($"system_network_received_bytes {m.NetworkBytesReceived}");
        sb.AppendLine($"system_network_sent_bytes {m.NetworkBytesSent}");
        sb.AppendLine($"system_process_count {m.ProcessCount}");

        if (m.LoadAverage1m > 0)
            sb.AppendLine($"system_load_average_1m {m.LoadAverage1m}");

        return sb.ToString();
    }
}
