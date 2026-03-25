using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace EchoLink.Services.Platform;

public class LinuxAdminService
{
    public static LinuxAdminService Instance { get; } = new();

    private LinuxAdminService() { }

    /// <summary>
    /// Executes a command as root. 
    /// Automatically prompts the user via their native Linux GUI (PolicyKit).
    /// </summary>
    public async Task<bool> ExecuteAsRootAsync(string command, string arguments)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("[AdminService] ExecuteAsRootAsync is only supported on Linux.");
            return false;
        }

        try
        {
            // 1. We wrap the command in bash to ensure arguments parse correctly
            string bashArgs = $"-c \"{command} {arguments}\"";

            var psi = new ProcessStartInfo
            {
                // pkexec is the Linux standard for GUI apps requesting root
                FileName = "pkexec", 
                Arguments = $"bash {bashArgs}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            
            Console.WriteLine($"[AdminService] Requesting root for: {command} {arguments}");
            process.Start();

            // 2. Wait for the user to type their password in the Linux popup
            await process.WaitForExitAsync();

            // 3. Read output for logging
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();

            if (process.ExitCode == 0)
            {
                Console.WriteLine("[AdminService] Success!");
                return true;
            }
            else
            {
                Console.WriteLine($"[AdminService] Failed (Code {process.ExitCode}): {error}");
                // ExitCode 126 or 127 usually means the user clicked "Cancel" on the popup
                return false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AdminService] Crash executing root command: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fallback method if pkexec fails. Requires passing the raw password 
    /// collected from an Avalonia UI TextBox.
    /// </summary>
    public async Task<bool> ExecuteWithRawPasswordAsync(string command, string arguments, string rawPassword)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("[AdminService] ExecuteWithRawPasswordAsync is only supported on Linux.");
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sudo",
                // -S tells sudo to read the password from StandardInput
                Arguments = $"-S {command} {arguments}",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Pipe the password straight into sudo's invisible prompt
            await process.StandardInput.WriteLineAsync(rawPassword);
            process.StandardInput.Close();

            await process.WaitForExitAsync();

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AdminService] Sudo -S failed: {ex.Message}");
            return false;
        }
    }
}
