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

    public record ExecuteResult(bool Success, string? Error);

    /// <summary>
    /// Probes the remote OS and, if it matches <paramref name="targetOs"/>,
    /// fires <paramref name="command"/> on the same open SSH session.
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
                var privateKeyFile  = new PrivateKeyFile(privateKeyPath);
                var connectionInfo  = new ConnectionInfo(
                    target.IpAddress, sshPort, username,
                    ProxyTypes.Socks5, "127.0.0.1", Socks5Port, "", "",
                    new PrivateKeyAuthenticationMethod(username, privateKeyFile));

                connectionInfo.Timeout = TimeSpan.FromSeconds(12);

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
                    bool wantsWindows = targetOs.Equals("Windows", StringComparison.OrdinalIgnoreCase);
                    bool wantsLinux   = targetOs.Equals("Linux",   StringComparison.OrdinalIgnoreCase);
                    bool isWindows    = detectedOs.Equals("Windows", StringComparison.OrdinalIgnoreCase);
                    bool isLinux      = detectedOs.Equals("Linux",   StringComparison.OrdinalIgnoreCase);

                    bool mismatch = (wantsWindows && !isWindows) || (wantsLinux && !isLinux);

                    if (mismatch)
                    {
                        client.Disconnect();
                        return new ExecuteResult(false,
                            $"This macro targets \"{targetOs}\" but {target.Name} is running " +
                            $"{detectedOs}. Create a specific macro for {detectedOs} to continue.");
                    }
                }

                // ── Fire the command, wrapped in the correct shell ─────────────
                // ROOT CAUSE FIX 1:
                // SshClient.CreateCommand() sends directly to the exec channel —
                // it does NOT go through a shell. Commands like 'start', 'loginctl',
                // 'echo', etc. are shell built-ins or need PATH resolution via the
                // shell. Without wrapping we get silent failures.
                //
                // ROOT CAUSE FIX 2 (Windows GUI apps):
                // SSH sessions on Windows run in a non-interactive context.
                // GUI apps launched directly from SSH are invisible (Session isolation).
                // We use 'cmd /c start /B' which tells Windows to detach the process
                // into the interactive user session before the SSH channel closes.
                string wrappedCommand = BuildShellCommand(command, detectedOs);
                _log.Info($"[MacroPreFlight] Executing on {target.Name}: {wrappedCommand}");

                using var cmd = client.CreateCommand(wrappedCommand);
                cmd.CommandTimeout = TimeSpan.FromSeconds(15);
                string stdout = cmd.Execute();
                string stderr = cmd.Error;
                int    exitCode = cmd.ExitStatus ?? 0;

                // Log output for diagnostics (visible in Debug Console)
                if (!string.IsNullOrWhiteSpace(stdout))
                    _log.Debug($"[MacroExec] stdout: {stdout.Trim()}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    _log.Warning($"[MacroExec] stderr: {stderr.Trim()}");
                _log.Info($"[MacroExec] Exit code: {exitCode}");

                // Non-zero exit code means the shell ran but the command itself errored.
                // For GUI apps 'start' returns 0 immediately regardless.
                // We treat exit 0 OR exit 1-from-start as success because 'start /B'
                // can return 1 when the process is background-detached but working.
                bool commandOk = exitCode == 0 ||
                                 (detectedOs.Equals("Windows", StringComparison.OrdinalIgnoreCase) && exitCode <= 1);

                client.Disconnect();

                if (!commandOk)
                {
                    string errDetail = string.IsNullOrWhiteSpace(stderr) ? $"exit code {exitCode}" : stderr.Trim();
                    return new ExecuteResult(false, $"Command failed on {target.Name}: {errDetail}");
                }

                return new ExecuteResult(true, null);
            }
            catch (Exception ex)
            {
                try { client?.Disconnect(); } catch { }
                _log.Error($"[MacroPreFlight] SSH error for {target.Name}: {ex.Message}");
                return new ExecuteResult(false, $"SSH connection failed: {ex.Message}");
            }
            finally
            {
                client?.Dispose();
            }
        });
    }

    /// <summary>
    /// Wraps the user-supplied command in the correct system shell so that:
    /// - Shell built-ins work (start, loginctl, echo, source …)
    /// - PATH is resolved correctly
    /// - GUI apps on Windows are detached into the interactive session
    /// </summary>
    private static string BuildShellCommand(string command, string detectedOs)
    {
        if (detectedOs.Equals("Windows", StringComparison.OrdinalIgnoreCase))
        {
            // Use cmd.exe so built-ins (start, echo, del, …) work.
            // 'start /B' detaches GUI processes into the user's interactive desktop
            // session, making them visible even when the SSH channel closes.
            // We check if the command already uses 'start' to avoid double-wrapping.
            if (command.TrimStart().StartsWith("start ", StringComparison.OrdinalIgnoreCase) ||
                command.TrimStart().StartsWith("start\"", StringComparison.OrdinalIgnoreCase))
            {
                // Already uses start — just wrap in cmd /c
                return $"cmd /c {command}";
            }

            // Wrap non-start commands: use 'start /B ""' to launch GUI-friendly
            return $"cmd /c start /B \"\" {command}";
        }
        else
        {
            // Linux / macOS — wrap in bash so aliases, built-ins, $PATH, pipes work
            // Escape any single quotes in the command before embedding in shell
            string escaped = command.Replace("'", "'\\''");
            return $"bash -c '{escaped}'";
        }
    }

    /// <summary>
    /// Runs uname first; if that fails or returns no output, tries the Windows
    /// 'ver' command. Returns "Linux", "Windows", "macOS", or "Unknown".
    /// </summary>
    private async Task<string> DetectRemoteOsAsync(SshClient client)
    {
        // Step 1 – 'uname -s' (Linux, macOS, BSD)
        try
        {
            using var uname = client.CreateCommand("uname -s");
            uname.CommandTimeout = TimeSpan.FromSeconds(5);
            string output = uname.Execute().Trim();
            _log.Debug($"[MacroPreFlight] uname output: '{output}' / exit: {uname.ExitStatus}");

            if (!string.IsNullOrWhiteSpace(output) && (uname.ExitStatus ?? 0) == 0)
            {
                if (output.Contains("Linux",   StringComparison.OrdinalIgnoreCase)) return "Linux";
                if (output.Contains("Darwin",  StringComparison.OrdinalIgnoreCase)) return "macOS";
                if (output.Contains("FreeBSD", StringComparison.OrdinalIgnoreCase)) return "Linux";
                return output;
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"[MacroPreFlight] uname failed: {ex.Message}");
        }

        // Step 2 – 'cmd.exe /c ver' (Windows OpenSSH default shell is cmd)
        try
        {
            using var ver = client.CreateCommand("cmd.exe /c ver");
            ver.CommandTimeout = TimeSpan.FromSeconds(5);
            string output = ver.Execute().Trim();
            _log.Debug($"[MacroPreFlight] ver output: '{output}' / exit: {ver.ExitStatus}");

            if (!string.IsNullOrWhiteSpace(output) &&
                output.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                return "Windows";
        }
        catch (Exception ex)
        {
            _log.Debug($"[MacroPreFlight] ver failed: {ex.Message}");
        }

        // Step 3 – PowerShell (last resort)
        try
        {
            using var ps = client.CreateCommand("powershell -NoProfile -Command \"$env:OS\"");
            ps.CommandTimeout = TimeSpan.FromSeconds(5);
            string output = ps.Execute().Trim();
            _log.Debug($"[MacroPreFlight] powershell output: '{output}'");
            if (output.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                return "Windows";
        }
        catch (Exception ex)
        {
            _log.Debug($"[MacroPreFlight] powershell probe failed: {ex.Message}");
        }

        _log.Warning("[MacroPreFlight] Could not detect remote OS — treating as Unknown.");
        return "Unknown";
    }

    /// <summary>Local execution when the target is the current device.</summary>
    private static Task RunLocallyAsync(string command) =>
        Task.Run(() =>
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c {command}")
                    { UseShellExecute = true, CreateNoWindow = false });
            else
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("bash", $"-c '{command}'")
                    { UseShellExecute = false });
        });
}
