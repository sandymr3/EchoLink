using System;
using System.Threading.Tasks;
using Android.Content;
using Android.App;

namespace EchoLink.Services;

/// <summary>
/// Android-native clipboard implementation using ClipboardManager system service.
/// No UIThread required. No Avalonia dependencies.
///
/// Privileges: Runs in context of EchoLinkForegroundService with ForegroundServiceType.TypeDataSync | TypeSpecialUse
/// This grants elevated clipboard access on Android 10+ (API 29+)
/// 
/// NOTE: Background clipboard monitoring (PrimaryClipChanged) has been removed.
/// Android 10+ blocks background clipboard access for privacy reasons.
/// Android→PC sync now uses user-initiated "Send to PC" button via ProcessTextActivity.
/// PC→Android sync still works via SetTextAsync when receiving over Unified Protocol.
/// </summary>
public class AndroidNativeClipboard : Java.Lang.Object, IPlatformClipboard
{
    private readonly ClipboardManager _clipboardManager;

    public event EventHandler<string>? OnClipboardChanged;

    public AndroidNativeClipboard()
    {
        // Get system ClipboardManager directly - no UI context needed
        _clipboardManager = (ClipboardManager?)Application.Context?.GetSystemService(Context.ClipboardService)
            ?? throw new InvalidOperationException("ClipboardManager service unavailable");
    }

    /// <summary>
    /// Synchronously get current clipboard text.
    /// Android ClipboardManager methods are blocking but fast.
    /// Used when receiving clipboard from PC→Android.
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
    /// Used when receiving clipboard from PC→Android direction.
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
    /// Start monitoring clipboard changes.
    /// 
    /// INTENTIONALLY LEFT EMPTY - Android 10+ blocks background clipboard access for privacy.
    /// The PrimaryClipChanged event will not fire when the app is in the background.
    /// 
    /// Android→PC sync now uses user-initiated "Send to PC" button via ProcessTextActivity.
    /// This provides 100% reliable sync without violating Android's privacy restrictions.
    /// </summary>
    public void StartMonitoring()
    {
        // Intentionally left empty - we no longer monitor Android clipboard in background
        // Android OS blocks background clipboard access for privacy (Android 10+)
        // We rely entirely on user-initiated "Send to PC" button via ProcessTextActivity
    }

    /// <summary>
    /// Stop monitoring clipboard changes.
    /// Intentionally left empty since StartMonitoring does nothing.
    /// </summary>
    public void StopMonitoring()
    {
        // Intentionally left empty - no listener to detach
    }
}
