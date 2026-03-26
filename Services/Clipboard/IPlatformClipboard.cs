using System;
using System.Threading.Tasks;

namespace EchoLink.Services;

/// <summary>
/// Platform-agnostic clipboard abstraction.
/// Enables headless clipboard monitoring without Avalonia UI dependencies.
/// Implementations handle platform-specific details (Android ClipboardManager, Linux xclip/wl-paste, Windows Win32/Avalonia).
/// </summary>
public interface IPlatformClipboard
{
    /// <summary>
    /// Fetches the current clipboard text asynchronously.
    /// No UIThread required. Platform-specific implementations handle OS access.
    /// </summary>
    Task<string> GetTextAsync();

    /// <summary>
    /// Sets the clipboard text asynchronously.
    /// No UIThread required. Platform-specific implementations handle OS write.
    /// </summary>
    Task SetTextAsync(string text);

    /// <summary>
    /// Fired when the OS clipboard content changes.
    /// For Android: instant event via ClipboardManager.PrimaryClipChanged
    /// For Linux: 500ms polling loop (fallback for headless)
    /// For Windows: 500ms polling loop (acceptable, UI thread always running)
    /// </summary>
    event EventHandler<string>? OnClipboardChanged;

    /// <summary>
    /// Begins monitoring clipboard changes.
    /// Must be called before OnClipboardChanged events will fire.
    /// </summary>
    void StartMonitoring();

    /// <summary>
    /// Stops monitoring clipboard changes.
    /// Releases resources (threads, listeners, processes).
    /// </summary>
    void StopMonitoring();
}
