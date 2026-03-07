using System;
using System.Diagnostics;
using System.IO;

namespace EchoLink.Services;

public class TailscaleService
{
    private Process? _daemonProcess;

    public void StartDaemon()
    {
        // 1. Locate the bundled binary dynamically
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string binaryPath = Path.Combine(appDir, "Binaries", "tailscaled");

        if (!File.Exists(binaryPath))
        {
            Console.WriteLine($"[Error] Tailscale binary not found at: {binaryPath}");
            return;
        }

        // 2. Set up a folder for Tailscale to save its data safely in the user's home folder
        string userConfigDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string tailscaleDir = Path.Combine(userConfigDir, "EchoLink", "Tailscale");
        Directory.CreateDirectory(tailscaleDir);

        string stateFile = Path.Combine(tailscaleDir, "tailscaled.state");
        string socketFile = Path.Combine(tailscaleDir, "tailscaled.sock");

        // 3. Configure it to run invisibly in the background
        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            // --tun=userspace-networking is crucial so it runs without root privileges!
            Arguments = $"--state=\"{stateFile}\" --socket=\"{socketFile}\" --tun=userspace-networking --socks5-server=localhost:1055",
            UseShellExecute = false,
            CreateNoWindow = true, 
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // 4. Launch it
        try
        {
            _daemonProcess = Process.Start(startInfo);
            Console.WriteLine("[Success] Tailscale background process started.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Failed to start: {ex.Message}");
        }
    }

    public void StopDaemon()
    {
        if (_daemonProcess != null && !_daemonProcess.HasExited)
        {
            _daemonProcess.Kill();
            _daemonProcess.Dispose();
        }
    }
}