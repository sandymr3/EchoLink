using System;
using System.Diagnostics;
using System.IO;

namespace EchoLink.Services;

public class TailscaleService
{
    private Process? _daemonProcess;
    private bool _stopping;
    private readonly LoggingService _log = LoggingService.Instance;

    public void StartDaemon()
    {
        // 1. Locate the bundled binary dynamically (name differs by OS)
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string binaryName = OperatingSystem.IsWindows() ? "tailscaled.exe" : "tailscaled";
        string binaryPath = Path.Combine(appDir, "Binaries", binaryName);

        if (!File.Exists(binaryPath))
        {
            _log.Error($"[Tailscale] Binary not found at: {binaryPath}");
            return;
        }

        _log.Info($"[Tailscale] Found binary at: {binaryPath}");

        // 2. Set up a folder for Tailscale to save its data safely in the user's home folder
        string userConfigDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string tailscaleDir = Path.Combine(userConfigDir, "EchoLink", "Tailscale");
        Directory.CreateDirectory(tailscaleDir);

        string stateFile = Path.Combine(tailscaleDir, "tailscaled.state");

        // 3. Build arguments — Windows uses a named pipe (no --socket flag needed),
        //    Linux uses a Unix socket. --tun=userspace-networking avoids needing
        //    kernel drivers or root/admin on both platforms.
        string arguments = OperatingSystem.IsWindows()
            ? $"--state=\"{stateFile}\" --tun=userspace-networking --socks5-server=localhost:1055"
            : $"--state=\"{stateFile}\" --socket=\"{Path.Combine(tailscaleDir, "tailscaled.sock")}\" --tun=userspace-networking --socks5-server=localhost:1055";

        // 4. Configure it to run invisibly in the background
        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // 4. Launch it
        try
        {
            _daemonProcess = Process.Start(startInfo)!;
            _log.Info($"[Tailscale] Daemon started (PID {_daemonProcess.Id})");

            // Pipe daemon output into the in-app log so it's visible in the debug console
            _daemonProcess.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _log.Debug($"[tailscaled] {e.Data}");
            };
            _daemonProcess.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _log.Warning($"[tailscaled] {e.Data}");
            };
            _daemonProcess.BeginOutputReadLine();
            _daemonProcess.BeginErrorReadLine();

            // Log if the process exits unexpectedly (ignore intentional kills)
            _daemonProcess.EnableRaisingEvents = true;
            _daemonProcess.Exited += (_, _) =>
            {
                if (!_stopping)
                    _log.Error($"[Tailscale] Daemon exited unexpectedly (code {_daemonProcess.ExitCode})");
            };
        }
        catch (Exception ex)
        {
            _log.Error($"[Tailscale] Failed to start daemon: {ex.Message}");
        }
    }

    public void StopDaemon()
    {
        if (_daemonProcess != null && !_daemonProcess.HasExited)
        {
            _stopping = true;
            _log.Info("[Tailscale] Stopping daemon...");
            _daemonProcess.Kill();
            _daemonProcess.Dispose();
            _log.Info("[Tailscale] Daemon stopped.");
        }
    }
}