using Avalonia;
using System;

namespace EchoLink.Windows;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Start headless clipboard daemon BEFORE UI loads
        try
        {
            var windowsClipboard = new EchoLink.Services.WindowsNativeClipboard();
            var clipboardService = EchoLink.Services.ClipboardSyncService.Instance;
            clipboardService.SetPlatformClipboard(windowsClipboard);
            _ = clipboardService.StartAsync(); // Fire and forget - runs in parallel
            Console.WriteLine("[Windows Startup] Clipboard sync daemon started");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Windows Startup] Warning: Failed to start clipboard service: {ex.Message}");
        }

        BuildAvaloniaApp()
            .WithDeveloperTools()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
