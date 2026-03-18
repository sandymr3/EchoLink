using System.Text;
using System.Threading.Tasks;
using Renci.SshNet;
using EchoLink.Models;

namespace EchoLink.Services;

/// <summary>
/// Pre-flight OS probing: opens a single SSH session, detects the remote OS by running
/// a hidden shell command, validates it against the macro's TargetOs tag, then either
/// fires the command on the same session or aborts with a descriptive reason.
/// </summary>
public class OsProbeService
{
    private const int Socks5Port = 1055;
    private readonly LoggingService _log = LoggingService.Instance;

    /// <summary>
    /// Result of a pre-flight + execution cycle.
    /// </summary>
    public record ExecuteResult(bool Success, string? Error);

    /// <summary>
    /// Probes the remote OS and, if it matches <paramref name="targetOs"/>,
    /// fires <paramref name="command"/> on the same open SSH session.
    /// Returns a result describing success or the mismatch reason.
    ///
    /// Pass <c>null</c> for <paramref name="targetOs"/> to skip the OS check and always execute.
    /// </summary>
    public async Task<ExecuteResult> ProbeAndExecuteAsync(
        Device target,
        string command,
        string? targetOs,
        string username,
        string privateKeyPath)
    {
        if (target.IsSelf)
        {
            // Local execution — no SSH needed
            await RunLocallyAsync(command);
            return new ExecuteResult(true, null);
        }

        int sshPort = target.Os?.Contains("android", StringComparison.OrdinalIgnoreCase) == true ? 2222 : 22;

        return await Task.Run(async () =>
        {
            SshClient? client = null;
            try
            {
                var privateKeyFile = new PrivateKeyFile(privateKeyPath);
                var connectionInfo = new ConnectionInfo(
                    target.IpAddress, sshPort, username,
                    ProxyTypes.Socks5, "127.0.0.1", Socks5Port, "", "",
                    new PrivateKeyAuthenticationMethod(username, privateKeyFile));

                // Short timeout: we want the whole probe+execute < 1 second ideally
                connectionInfo.Timeout = TimeSpan.FromSeconds(8);

                client = new SshClient(connectionInfo);
                client.Connect();

                // ── Pre-flight: detect remote OS ──────────────────────────────
                string detectedOs = await DetectRemoteOsAsync(client);
                _log.Info($"[MacroPreFlight] Remote OS detected as '{detectedOs}' for {target.Name}");

                // ── Validate ──────────────────────────────────────────────────
                if (!string.IsNullOrEmpty(targetOs) &&
                    !targetOs.Equals("Any", StringComparison.OrdinalIgnoreCase) &&
                    !targetOs.Equals("All", StringComparison.OrdinalIgnoreCase))
                {
                    bool targetWantsWindows = targetOs.Equals("Windows", StringComparison.OrdinalIgnoreCase);
                    bool targetWantsLinux   = targetOs.Equals("Linux", StringComparison.OrdinalIgnoreCase);

                    bool remoteIsWindows = detectedOs.Equals("Windows", StringComparison.OrdinalIgnoreCase);
                    bool remoteIsLinux   = detectedOs.Equals("Linux", StringComparison.OrdinalIgnoreCase);

                    bool mismatch = (targetWantsWindows && !remoteIsWindows) ||
                                    (targetWantsLinux   && !remoteIsLinux);

                    if (mismatch)
                    {
                        client.Disconnect();
                        return new ExecuteResult(false,
                            $"This macro targets \"{targetOs}\" but {target.Name} is running {detectedOs}. " +
                            $"Create a specific macro for {detectedOs} to continue.");
                    }
                }

                // ── Fire the command on the same session ──────────────────────
                using var cmd = client.CreateCommand(command);
                cmd.CommandTimeout = TimeSpan.FromSeconds(10);
                cmd.Execute();

                client.Disconnect();
                return new ExecuteResult(true, null);
            }
            catch (Exception ex)
            {
                try { client?.Disconnect(); } catch { }
                _log.Error($"[MacroPreFlight] SSH error for {target.Name}: {ex.Message}");
                return new ExecuteResult(false, $"SSH error: {ex.Message}");
            }
            finally
            {
                client?.Dispose();
            }
        });
    }

    /// <summary>
    /// Runs <c>uname</c> first; if that fails or returns no output, tries the Windows
    /// <c>ver</c> command.  Returns "Linux", "Windows", "macOS", or "Unknown".
    /// </summary>
    private static async Task<string> DetectRemoteOsAsync(SshClient client)
    {
        // Step 1 – try 'uname -s' (works on Linux, macOS, BSD)
        try
        {
            using var uname = client.CreateCommand("uname -s");
            uname.CommandTimeout = TimeSpan.FromSeconds(4);
            string output = uname.Execute().Trim();

            if (!string.IsNullOrWhiteSpace(output))
            {
                if (output.Contains("Linux",   StringComparison.OrdinalIgnoreCase)) return "Linux";
                if (output.Contains("Darwin",  StringComparison.OrdinalIgnoreCase)) return "macOS";
                if (output.Contains("FreeBSD", StringComparison.OrdinalIgnoreCase)) return "Linux"; // treat BSD like Linux
                return output; // return whatever we got
            }
        }
        catch { /* fall through to Windows check */ }

        // Step 2 – 'uname' not found → must be Windows (cmd.exe /c ver)
        try
        {
            using var ver = client.CreateCommand("cmd.exe /c ver");
            ver.CommandTimeout = TimeSpan.FromSeconds(4);
            string output = ver.Execute().Trim();

            if (!string.IsNullOrWhiteSpace(output) &&
                output.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                return "Windows";
        }
        catch { /* fall through */ }

        // Step 3 – try PowerShell as last resort
        try
        {
            using var ps = client.CreateCommand("powershell -NoProfile -Command \"$env:OS\"");
            ps.CommandTimeout = TimeSpan.FromSeconds(4);
            string output = ps.Execute().Trim();
            if (output.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                return "Windows";
        }
        catch { }

        return "Unknown";
    }

    private static Task RunLocallyAsync(string command) =>
        Task.Run(() =>
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c {command}")
                    { UseShellExecute = true, CreateNoWindow = false });
            else
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("bash", $"-c \"{command}\"")
                    { UseShellExecute = false });
        });
}
