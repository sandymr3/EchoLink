using System;
using System.Threading.Tasks;
using Android.Content;
using Android.App;

namespace EchoLink.Services;

/// <summary>
/// Android-native clipboard implementation using ClipboardManager system service.
/// No UIThread required. No Avalonia dependencies.
/// Uses instant event listener (ClipboardManager.PrimaryClipChanged) instead of polling.
/// 
/// Privileges: Runs in context of EchoLinkForegroundService with ForegroundServiceType.TypeDataSync | TypeSpecialUse
/// This grants elevated clipboard access on Android 10+ (API 29+)
/// </summary>
public class AndroidNativeClipboard : Java.Lang.Object, IPlatformClipboard
{
    private readonly ClipboardManager _clipboardManager;
    private bool _isMonitoring;
    
    public event EventHandler<string>? OnClipboardChanged;

    public AndroidNativeClipboard()
    {
        // Get system ClipboardManager directly - no UI context needed
        _clipboardManager = (ClipboardManager?)Application.Context?.GetSystemService(Context.ClipboardService)
            ?? throw new InvalidOperationException("ClipboardManager service unavailable");
        
        _isMonitoring = false;
    }

    /// <summary>
    /// Synchronously get current clipboard text.
    /// Android ClipboardManager methods are blocking but fast.
    /// </summary>
    public Task<string> GetTextAsync()
    {
        try
        {
            if (_clipboardManager.HasPrimaryClip && _clipboardManager.PrimaryClip?.ItemCount > 0)
            {
                var text = _clipboardManager.PrimaryClip.GetItemAt(0)?.Text;
                return Task.FromResult(text ?? string.Empty);
            }
            return Task.FromResult(string.Empty);
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[AndroidClipboard] GetTextAsync failed: {ex.Message}");
            return Task.FromResult(string.Empty);
        }
    }

    /// <summary>
    /// Synchronously set clipboard text.
    /// Android ClipboardManager is fast for this operation.
    /// </summary>
    public Task SetTextAsync(string text)
    {
        try
        {
            var clip = ClipData.NewPlainText("EchoLink", text);
            _clipboardManager.PrimaryClip = clip;
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[AndroidClipboard] SetTextAsync failed: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Start listening to Android clipboard changes.
    /// Uses native ClipboardManager.PrimaryClipChanged event (instant, not polling).
    /// </summary>
    public void StartMonitoring()
    {
        if (_isMonitoring)
            return;

        try
        {
            _clipboardManager.PrimaryClipChanged += ClipboardManager_PrimaryClipChanged;
            _isMonitoring = true;
            System.Console.WriteLine("[AndroidClipboard] Monitoring started");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[AndroidClipboard] StartMonitoring failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Stop listening to clipboard changes and clean up resources.
    /// </summary>
    public void StopMonitoring()
    {
        if (!_isMonitoring)
            return;

        try
        {
            _clipboardManager.PrimaryClipChanged -= ClipboardManager_PrimaryClipChanged;
            _isMonitoring = false;
            System.Console.WriteLine("[AndroidClipboard] Monitoring stopped");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"[AndroidClipboard] StopMonitoring failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Invoked instantly when user copies something on Android device.
    /// Reads new text and fires OnClipboardChanged event asynchronously.
    /// </summary>
    private void ClipboardManager_PrimaryClipChanged(object? sender, EventArgs e)
    {
        // Fire event on background thread to avoid blocking clipboard system
        _ = Task.Run(async () =>
        {
            try
            {
                string newText = await GetTextAsync();
                if (!string.IsNullOrEmpty(newText))
                {
                    OnClipboardChanged?.Invoke(this, newText);
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[AndroidClipboard] ClipboardChanged handler failed: {ex.Message}");
            }
        });
    }
}
