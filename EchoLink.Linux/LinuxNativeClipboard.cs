using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace EchoLink.Services;

/// <summary>
/// Linux clipboard implementation with automatic X11/Wayland detection.
/// Headless-friendly: uses subprocess commands, no X11 library bindings required.
/// 
/// X11: Uses xclip command-line tool
/// Wayland: Uses wl-paste/wl-copy command-line tools
/// 
/// Automatically detects display server by checking $WAYLAND_DISPLAY environment variable.
/// Falls back to X11 if Wayland not detected.
/// Includes graceful error handling for missing tools.
/// </summary>
public class LinuxNativeClipboard : IPlatformClipboard
{
    private string _lastKnownText = "";
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private bool _isMonitoring;
    private bool _isWayland;

    public event EventHandler<string>? OnClipboardChanged;

    public LinuxNativeClipboard()
    {
        // Detect display server at construction time
        _isWayland = DetectWaylandDisplay();
        string displayServer = _isWayland ? "Wayland" : "X11";
        System.Console.WriteLine($"[LinuxClipboard] Detected {displayServer} display server");
        
        _isMonitoring = false;
    }

    /// <summary>
    /// Check if running under Wayland by inspecting $WAYLAND_DISPLAY environment variable.
    /// </summary>
    private bool DetectWaylandDisplay()
    {
        string? waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        return !string.IsNullOrEmpty(waylandDisplay);
    }

    /// <summary>
    /// Get current clipboard text via xclip (X11) or wl-paste (Wayland).
    /// </summary>
    public Task<string> GetTextAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                if (_isWayland)
                {
                    return GetClipboardViaWlPaste();
                }
                else
                {
                    return GetClipboardViaXclip();
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[LinuxClipboard] GetTextAsync failed: {ex.Message}");
                return string.Empty;
            }
        });
    }

    /// <summary>
    /// Set clipboard text via xclip (X11) or wl-copy (Wayland).
    /// </summary>
    public Task SetTextAsync(string text)
    {
        return Task.Run(() =>
        {
            try
            {
                if (_isWayland)
                {
                    SetClipboardViaWlCopy(text);
                }
                else
                {
                    SetClipboardViaXclip(text);
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[LinuxClipboard] SetTextAsync failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Start monitoring clipboard changes via 500ms polling loop.
    /// (Linux headless environments don't have reliable clipboard notification APIs)
    /// </summary>
    public void StartMonitoring()
    {
        if (_isMonitoring)
            return;

        _isMonitoring = true;
        _monitoringCts = new CancellationTokenSource();
        var token = _monitoringCts.Token;

        _monitoringTask = Task.Run(async () =>
        {
            System.Console.WriteLine("[LinuxClipboard] Monitoring started");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string currentText = await GetTextAsync();
                    if (currentText != _lastKnownText && !string.IsNullOrEmpty(currentText))
                    {
                        _lastKnownText = currentText;
                        OnClipboardChanged?.Invoke(this, currentText);
                    }
                    await Task.Delay(500, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[LinuxClipboard] Monitoring loop error: {ex.Message}");
                    await Task.Delay(1000, token);
                }
            }
        }, token);
    }

    /// <summary>
    /// Stop monitoring and clean up resources.
    /// </summary>
    public void StopMonitoring()
    {
        if (!_isMonitoring)
            return;

        try
        {
            _monitoringCts?.Cancel();
            if (_monitoringTask != null)
            {
                _monitoringTask.Wait(TimeSpan.FromSeconds(2));
            }
            _monitoringCts?.Dispose();
            _isMonitoring = false;
            System.Console.WriteLine("[LinuxClipboard] Monitoring stopped");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[LinuxClipboard] StopMonitoring error: {ex.Message}");
        }
    }

    // ── X11 Implementation (xclip) ──────────────────────────────────────────

    private string GetClipboardViaXclip()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "xclip",
            Arguments = "-selection clipboard -o",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return string.Empty;

        string text = process.StandardOutput.ReadToEnd();
        process.WaitForExit(1000);
        return text;
    }

    private void SetClipboardViaXclip(string text)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "xclip",
            Arguments = "-selection clipboard -i",
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return;

        process.StandardInput.Write(text);
        process.StandardInput.Close();
        process.WaitForExit(1000);
    }

    // ── Wayland Implementation (wl-paste / wl-copy) ─────────────────────────

    private string GetClipboardViaWlPaste()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wl-paste",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return string.Empty;

        string text = process.StandardOutput.ReadToEnd();
        process.WaitForExit(1000);
        return text;
    }

    private void SetClipboardViaWlCopy(string text)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wl-copy",
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return;

        process.StandardInput.Write(text);
        process.StandardInput.Close();
        process.WaitForExit(1000);
    }
}
