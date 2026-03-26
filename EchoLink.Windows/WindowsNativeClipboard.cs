using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace EchoLink.Services;

/// <summary>
/// Windows clipboard implementation using Avalonia IClipboard.
/// Uses 500ms polling loop for monitoring (acceptable on Windows where UI thread is continuous even when minimized).
/// 
/// Future upgrade path: Replace with Win32 native AddClipboardFormatListener() for true event-driven behavior.
/// Current approach: safe, proven, and sufficient since Windows UI threads continue running in background.
/// </summary>
public class WindowsNativeClipboard : IPlatformClipboard
{
    private string _lastKnownText = "";
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private bool _isMonitoring;

    public event EventHandler<string>? OnClipboardChanged;

    public WindowsNativeClipboard()
    {
        _isMonitoring = false;
    }

    /// <summary>
    /// Get current clipboard text via Avalonia.
    /// Requires Avalonia Application context and UI thread access.
    /// Safe because Windows keeps UI thread alive even when window is minimized.
    /// </summary>
    public async Task<string> GetTextAsync()
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var clipboard = GetAvaloniaClipboard();
                if (clipboard is null)
                    return string.Empty;

#pragma warning disable CS0618
                var text = await clipboard.GetTextAsync();
#pragma warning restore CS0618
                return text ?? string.Empty;
            });
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[WindowsClipboard] GetTextAsync failed: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Set clipboard text via Avalonia.
    /// </summary>
    public async Task SetTextAsync(string text)
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var clipboard = GetAvaloniaClipboard();
                if (clipboard is null)
                    return;

                await clipboard.SetTextAsync(text);
            });
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[WindowsClipboard] SetTextAsync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Start monitoring clipboard changes via 500ms polling loop.
    /// Acceptable on Windows because UI thread is continuous.
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
            System.Console.WriteLine("[WindowsClipboard] Monitoring started");
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
                    System.Console.WriteLine($"[WindowsClipboard] Monitoring loop error: {ex.Message}");
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
            System.Console.WriteLine("[WindowsClipboard] Monitoring stopped");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[WindowsClipboard] StopMonitoring error: {ex.Message}");
        }
    }

    /// <summary>
    /// Get Avalonia clipboard from active window context.
    /// Returns null if no window is active (app might be backgrounded or just starting).
    /// </summary>
    private Avalonia.Input.Platform.IClipboard? GetAvaloniaClipboard()
    {
        var app = Avalonia.Application.Current;
        if (app?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime dt && dt.MainWindow != null)
        {
            return Avalonia.Controls.TopLevel.GetTopLevel(dt.MainWindow)?.Clipboard;
        }
        else if (app?.ApplicationLifetime is ISingleViewApplicationLifetime single && single.MainView != null)
        {
            return Avalonia.Controls.TopLevel.GetTopLevel(single.MainView)?.Clipboard;
        }
        return null;
    }
}
