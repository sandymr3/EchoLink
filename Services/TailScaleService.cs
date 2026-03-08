using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EchoLink.Services;

public class TailscaleService
{
    public static TailscaleService Instance { get; private set; } = new();

    private Process? _daemonProcess;
    private bool _stopping;
    private string _tailscaleDir = "";
    private string _socketPath = "";
    private readonly LoggingService _log = LoggingService.Instance;

    private const string HeadscaleServer = "https://echo-link.app";

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
        _tailscaleDir = Path.Combine(userConfigDir, "EchoLink", "Tailscale");
        Directory.CreateDirectory(_tailscaleDir);

        string stateFile = Path.Combine(_tailscaleDir, "tailscaled.state");

        // 3. Build arguments.
        //    Windows: tailscaled creates a hardcoded protected named pipe;
        //             child processes of this admin app can use it without --socket.
        //    Linux:   use an explicit Unix socket so the CLI can find the daemon.
        //    --tun=userspace-networking avoids needing kernel drivers or root/admin.
        if (!OperatingSystem.IsWindows())
            _socketPath = Path.Combine(_tailscaleDir, "tailscaled.sock");

        string arguments = OperatingSystem.IsWindows()
            ? $"--state=\"{stateFile}\" --tun=userspace-networking --socks5-server=localhost:1055"
            : $"--state=\"{stateFile}\" --socket=\"{_socketPath}\" --tun=userspace-networking --socks5-server=localhost:1055";

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

        // 5. Launch it
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

    // ── CLI helpers ─────────────────────────────────────────────────────────

    private string CliPath()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string name = OperatingSystem.IsWindows() ? "tailscale.exe" : "tailscale";
        return Path.Combine(appDir, "Binaries", name);
    }

    private string PrefixSocketArg(string arguments)
    {
        // On Linux we use an explicit Unix socket; on Windows the CLI connects
        // to the default named pipe automatically (app runs elevated).
        if (!OperatingSystem.IsWindows() && !string.IsNullOrEmpty(_socketPath))
            return $"--socket=\"{_socketPath}\" {arguments}";
        return arguments;
    }

    /// <summary>
    /// Runs the bundled tailscale CLI and captures stdout + stderr.
    /// </summary>
    public async Task<(string Stdout, string Stderr)> RunCliAsync(
        string arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CliPath(),
            Arguments = PrefixSocketArg(arguments),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8
        };

        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutSb.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderrSb.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync(ct);

        return (stdoutSb.ToString(), stderrSb.ToString());
    }

    // ── Auth state ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the Tailscale backend state: "Running", "NeedsLogin", "Stopped",
    /// "NoState", or "Unknown" on error.
    /// </summary>
    public async Task<string> GetBackendStateAsync(CancellationToken ct = default)
    {
        try
        {
            var (stdout, _) = await RunCliAsync("status --json", ct);
            if (string.IsNullOrWhiteSpace(stdout)) return "Unknown";

            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("BackendState", out var state))
                return state.GetString() ?? "Unknown";

            return "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>
    /// Returns the device's Tailscale IP (100.x.x.x / fd7a:...) when connected,
    /// or null when not connected.
    /// </summary>
    public async Task<string?> GetTailscaleIpAsync(CancellationToken ct = default)
    {
        try
        {
            var (stdout, _) = await RunCliAsync("status --json", ct);
            if (string.IsNullOrWhiteSpace(stdout)) return null;

            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("Self", out var self) &&
                self.TryGetProperty("TailscaleIPs", out var ips) &&
                ips.GetArrayLength() > 0)
            {
                // Return the first IPv4 address (100.x.x.x)
                foreach (var ip in ips.EnumerateArray())
                {
                    var s = ip.GetString();
                    if (s != null && !s.Contains(':'))
                        return s;
                }
                return ips[0].GetString();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ── Network status (real device list) ───────────────────────────────────

    /// <summary>
    /// Fetches current Tailscale status and returns the self IP + all devices.
    /// </summary>
    public async Task<(string? SelfIp, System.Collections.Generic.List<Models.Device> Devices)>
        GetNetworkStatusAsync(CancellationToken ct = default)
    {
        var devices = new System.Collections.Generic.List<Models.Device>();
        string? selfIp = null;

        try
        {
            var (stdout, _) = await RunCliAsync("status --json", ct);
            if (string.IsNullOrWhiteSpace(stdout))
                return (null, devices);

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            // Self node
            if (root.TryGetProperty("Self", out var self))
            {
                selfIp = ExtractIpv4(self);
                devices.Add(ParseDevice(self, isSelf: true));
            }

            // Peer nodes
            if (root.TryGetProperty("Peer", out var peers) &&
                peers.ValueKind == JsonValueKind.Object)
            {
                foreach (var peer in peers.EnumerateObject())
                    devices.Add(ParseDevice(peer.Value, isSelf: false));
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[Tailscale] Failed to parse status: {ex.Message}");
        }

        return (selfIp, devices);
    }

    private static string? ExtractIpv4(JsonElement node)
    {
        if (!node.TryGetProperty("TailscaleIPs", out var ips)) return null;
        foreach (var ip in ips.EnumerateArray())
        {
            var s = ip.GetString();
            if (s != null && !s.Contains(':'))
                return s;
        }
        return ips.GetArrayLength() > 0 ? ips[0].GetString() : null;
    }

    private static Models.Device ParseDevice(JsonElement node, bool isSelf)
    {
        string hostName = node.TryGetProperty("HostName", out var hn) ? hn.GetString() ?? "" : "";
        string os = node.TryGetProperty("OS", out var osEl) ? osEl.GetString() ?? "" : "";
        bool online = node.TryGetProperty("Online", out var onEl) && onEl.GetBoolean();
        string ip = ExtractIpv4(node) ?? "";

        string deviceType = os.ToLowerInvariant() switch
        {
            "android" or "ios" => "Phone",
            "darwin" => "Laptop",
            "linux" => "Desktop",
            "windows" => "Desktop",
            _ => "Desktop"
        };

        string lastSeen = "";
        if (node.TryGetProperty("LastSeen", out var ls) && ls.ValueKind == JsonValueKind.String)
        {
            if (DateTime.TryParse(ls.GetString(), out var dt))
                lastSeen = dt.ToLocalTime().ToString("g");
        }

        return new Models.Device
        {
            Name = hostName,
            IpAddress = ip,
            IsOnline = online,
            DeviceType = deviceType,
            Os = os,
            LastSeen = lastSeen,
            IsSelf = isSelf
        };
    }

    // ── Login ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs "tailscale login --login-server=https://echo-link.app".
    /// Invokes <paramref name="onAuthUrl"/> with the Google OAuth URL once
    /// the headscale server returns it (caller should open it in a browser).
    /// Returns when the login handshake finishes (process exits).
    /// </summary>
    public async Task LoginAsync(Action<string> onAuthUrl, CancellationToken ct = default)
    {
        string args = PrefixSocketArg($"login --login-server={HeadscaleServer}");

        var psi = new ProcessStartInfo
        {
            FileName = CliPath(),
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8
        };

        bool urlOpened = false;
        var capturedOutput = new StringBuilder();

        void TryExtractUrl(string? line)
        {
            if (urlOpened || string.IsNullOrWhiteSpace(line)) return;
            // tailscale login outputs something like:
            //   "To authenticate, visit:\n\thttps://echo-link.app/register/XXXX"
            // The actual URL may be on a line by itself or after a tab.
            int idx = line.IndexOf("https://", StringComparison.Ordinal);
            if (idx >= 0)
            {
                urlOpened = true;
                onAuthUrl(line[idx..].Trim());
            }
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) capturedOutput.AppendLine(e.Data);
            _log.Debug($"[tailscale login] {e.Data}");
            TryExtractUrl(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) capturedOutput.AppendLine(e.Data);
            _log.Debug($"[tailscale login stderr] {e.Data}");
            TryExtractUrl(e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        _log.Info("[Tailscale] Login initiated against echo-link.app");

        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            string detail = capturedOutput.ToString().Trim();
            throw new Exception(
                string.IsNullOrWhiteSpace(detail)
                    ? $"tailscale login exited with code {proc.ExitCode}"
                    : detail);
        }

        _log.Info("[Tailscale] Login completed successfully.");
    }

    // ── Logout ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Logs out of the current Tailscale/headscale session.
    /// </summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        _log.Info("[Tailscale] Logging out...");
        var (_, stderr) = await RunCliAsync("logout", ct);
        if (!string.IsNullOrWhiteSpace(stderr))
            _log.Warning($"[tailscale logout] {stderr.Trim()}");
        _log.Info("[Tailscale] Logged out.");
    }
}
