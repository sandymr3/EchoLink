using Avalonia;
using Avalonia.Win32;
using System;

namespace EchoLink.Windows;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Console.WriteLine("[DEBUG] Starting background services...");

        // Register Windows-native App Shield implementation.
        EchoLink.Services.AppShieldService.Instance = new EchoLink.Services.WindowsAppShieldService();

        // Start clipboard sync before UI to keep background synchronization active.
        try
        {
            var windowsClipboard = new EchoLink.Services.WindowsNativeClipboard();
            var clipboardService = EchoLink.Services.ClipboardSyncService.Instance;
            clipboardService.SetPlatformClipboard(windowsClipboard);
            _ = clipboardService.StartAsync();
            Console.WriteLine("[DEBUG] Clipboard sync daemon started.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Warning: Failed to start clipboard service: {ex.Message}");
        }

        Console.WriteLine("[DEBUG] Background services started. Building UI...");

        BuildAvaloniaApp()
            .WithDeveloperTools()
            .StartWithClassicDesktopLifetime(args);

        Console.WriteLine("[DEBUG] App closed.");
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } });
}
