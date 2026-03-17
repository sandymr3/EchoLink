using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EchoLink.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace EchoLink.Services;

public class SftpService
{
    private readonly LoggingService _log = LoggingService.Instance;
    private const int Socks5Port = 1055;

    /// <summary>
    /// Creates a ConnectionInfo that tries SOCKS5 proxy first.
    /// Falls back to direct TCP if SOCKS5 is unavailable (system-mode Tailscale).
    /// </summary>
    private ConnectionInfo CreateConnectionInfo(string host, int sshPort, string username, PrivateKeyFile key)
    {
        // Check if the SOCKS5 proxy is actually listening before using it.
        bool socks5Available = IsSocks5Available();
        if (socks5Available)
        {
            _log.Info($"[SFTP] Using SOCKS5 proxy (localhost:{Socks5Port}) → {host}:{sshPort}");
            return new ConnectionInfo(host, sshPort, username,
                ProxyTypes.Socks5, "127.0.0.1", Socks5Port, "", "",
                new PrivateKeyAuthenticationMethod(username, key));
        }
        else
        {
            _log.Info($"[SFTP] SOCKS5 not available — connecting directly to {host}:{sshPort}");
            return new ConnectionInfo(host, sshPort, username,
                new PrivateKeyAuthenticationMethod(username, key));
        }
    }

    private bool IsSocks5Available()
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var result = tcp.BeginConnect("127.0.0.1", Socks5Port, null, null);
            bool connected = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(300));
            if (connected && tcp.Connected) { tcp.EndConnect(result); return true; }
            return false;
        }
        catch { return false; }
    }

    public async Task UploadStreamAsync(
        string host, 
        string username, 
        string privateKeyPath,
        Stream fileStream,
        string fileName,
        string remotePath,
        Action<long, long> progressCallback,
        int sshPort = 22,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var privateKeyFile = new PrivateKeyFile(privateKeyPath);
            var connectionInfo = CreateConnectionInfo(host, sshPort, username, privateKeyFile);
            using var client = new SftpClient(connectionInfo);
            try
            {
                client.Connect();

                // Compute the absolute remote path
                string resolvedRemotePath = remotePath;
                if (!remotePath.StartsWith("/") && !remotePath.Contains(":/"))
                {
                    // For cross-platform support without path corruption
                    string currentDir = client.WorkingDirectory;
                    if (sshPort == 2222 || username.StartsWith("u0_"))
                    {
                        // Android device - force save to public Downloads folder instead of app sandbox
                        resolvedRemotePath = $"/storage/emulated/0/Download/{remotePath}";
                    }
                    else if (currentDir == "/") 
                    {
                        // Some Windows OpenSSH configs drop us into / mapping to C:\
                        resolvedRemotePath = $"/C:/Users/{username}/Downloads/{remotePath}";
                    }
                    else if (currentDir.StartsWith("/"))
                    {
                        resolvedRemotePath = $"{currentDir}/{remotePath}";
                    }
                    else
                    {
                        // In Windows OpenSSH, the home dir usually starts at /C:/Users/User
                        resolvedRemotePath = $"{currentDir}/Downloads/{remotePath}";
                    }
                }

                long totalBytes = fileStream.Length;

                _log.Info($"[SFTP] Starting stream upload to {resolvedRemotePath}: {fileName} ({totalBytes} bytes)");

                client.UploadFile(fileStream, resolvedRemotePath, (uploaded) =>
                {
                    ct.ThrowIfCancellationRequested(); // Automatically abort if cancel button clicked
                    progressCallback(checked((long)uploaded), totalBytes);
                });

                _log.Info("[SFTP] Upload completed successfully.");
            }
            catch (Exception ex)
            {
                _log.Error($"[SFTP] Upload failed: {ex.Message}");
                throw;
            }
            finally
            {
                if (client.IsConnected)
                    client.Disconnect();
            }
        }, ct);
    }

    /// <summary>
    /// Uploads a file to a remote Tailscale node via SFTP, tunneling through our SOCKS5 proxy.
    /// </summary>
    /// <param name="host">The Tailscale IP of the recipient.</param>
    /// <param name="username">The username on the target machine.</param>
    /// <param name="password">The password (or we could extend this for key-based auth).</param>
    /// <param name="localPath">Path to the file on this machine.</param>
    /// <param name="remotePath">Full destination path on the remote machine.</param>
    /// <param name="progressCallback">Callback for (bytesUploaded, totalBytes).</param>
    public async Task UploadFileAsync(
        string host, 
        string username, 
        string privateKeyPath,
        string localPath,
        string remotePath,
        Action<long, long> progressCallback,
        int sshPort = 22,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var privateKeyFile = new PrivateKeyFile(privateKeyPath);
            var connectionInfo = CreateConnectionInfo(host, sshPort, username, privateKeyFile);
            using var client = new SftpClient(connectionInfo);
            try
            {
                _log.Info($"[SFTP] Connecting to {host}...");
                client.Connect();

                // Compute the absolute remote path
                string resolvedRemotePath = remotePath;
                if (!remotePath.StartsWith("/") && !remotePath.Contains(":/"))
                {
                    // For cross-platform support without path corruption
                    string currentDir = client.WorkingDirectory;
                    if (sshPort == 2222 || username.StartsWith("u0_"))
                    {
                        // Android device - force save to public Downloads folder instead of app sandbox
                        resolvedRemotePath = $"/storage/emulated/0/Download/{remotePath}";
                    }
                    else if (currentDir == "/") 
                    {
                        // Some Windows OpenSSH configs drop us into / mapping to C:\
                        resolvedRemotePath = $"/C:/Users/{username}/Downloads/{remotePath}";
                    }
                    else if (currentDir.StartsWith("/"))
                    {
                        resolvedRemotePath = $"{currentDir}/{remotePath}";
                    }
                    else
                    {
                        // In Windows OpenSSH, the home dir usually starts at /C:/Users/User
                        resolvedRemotePath = $"{currentDir}/Downloads/{remotePath}";
                    }
                }

                using var fileStream = File.OpenRead(localPath);
                long totalBytes = fileStream.Length;

                _log.Info($"[SFTP] Starting upload to {resolvedRemotePath}: {Path.GetFileName(localPath)} ({totalBytes} bytes)");

                client.UploadFile(fileStream, resolvedRemotePath, (uploaded) =>
                {
                    ct.ThrowIfCancellationRequested(); // Automatically abort if cancel button clicked
                    progressCallback(checked((long)uploaded), totalBytes);
                });

                _log.Info("[SFTP] Upload completed successfully.");
            }
            catch (Exception ex)
            {
                _log.Error($"[SFTP] Upload failed: {ex.Message}");
                throw;
            }
            finally
            {
                if (client.IsConnected)
                    client.Disconnect();
            }
        }, ct);
    }

    /// <summary>
    /// Lists the contents of a remote directory via SFTP.
    /// </summary>
    public async Task<List<RemoteFileEntry>> ListDirectoryAsync(
        string host,
        string username,
        string privateKeyPath,
        string remotePath,
        int sshPort = 22,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var privateKeyFile = new PrivateKeyFile(privateKeyPath);
            var connectionInfo = CreateConnectionInfo(host, sshPort, username, privateKeyFile);

            using var client = new SftpClient(connectionInfo);
            try
            {
                _log.Info($"[SFTP] Listing {remotePath} on {host}");
                client.Connect();

                var entries = new List<RemoteFileEntry>();
                foreach (var f in client.ListDirectory(remotePath))
                {
                    ct.ThrowIfCancellationRequested();
                    // Skip navigation entries and hidden files (dot-prefixed on Linux)
                    if (f.Name is "." or "..") continue;
                    if (f.Name.StartsWith('.')) continue;
                    entries.Add(new RemoteFileEntry
                    {
                        Name         = f.Name,
                        FullPath     = f.FullName,
                        Size         = f.Attributes.Size,
                        IsDirectory  = f.IsDirectory,
                        LastModified = f.Attributes.LastWriteTime
                    });
                }

                return entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name).ToList();
            }
            catch (Exception ex)
            {
                _log.Error($"[SFTP] ListDirectory failed: {ex.Message}");
                throw;
            }
            finally
            {
                if (client.IsConnected) client.Disconnect();
            }
        }, ct);
    }

    /// <summary>
    /// Downloads a file from a remote Tailscale node via SFTP.
    /// Checks disk space, handles file locks, streams directly to disk, and
    /// cleans up any partial file on failure or cancellation.
    /// </summary>
    public async Task DownloadFileAsync(
        string host,
        string username,
        string privateKeyPath,
        string remotePath,
        string localPath,
        Action<long, long> progressCallback,
        int sshPort = 22,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var privateKeyFile = new PrivateKeyFile(privateKeyPath);
            var connectionInfo = CreateConnectionInfo(host, sshPort, username, privateKeyFile);

            using var client = new SftpClient(connectionInfo);
            FileStream? fileStream = null;
            bool downloadStarted = false;
            try
            {
                client.Connect();

                // ── Determine remote file size ──────────────────────────
                var remoteFile = client.Get(remotePath);
                long totalBytes = remoteFile.Attributes.Size;

                // ── Disk space check ────────────────────────────────────
                // Works on both Windows (C:\) and Linux (/)
                string? root = Path.GetPathRoot(Path.GetFullPath(localPath));
                if (root is not null)
                {
                    var drive = new DriveInfo(root);
                    if (drive.AvailableFreeSpace < totalBytes)
                    {
                        throw new IOException(
                            $"Not enough disk space. " +
                            $"Need {FormatBytes(totalBytes)}, " +
                            $"only {FormatBytes(drive.AvailableFreeSpace)} available on {root}");
                    }
                }

                // ── Open local stream (catches file-lock conflicts) ─────
                try
                {
                    fileStream = new FileStream(
                        localPath, FileMode.Create, FileAccess.Write, FileShare.None);
                }
                catch (IOException ex)
                {
                    throw new IOException(
                        $"'{Path.GetFileName(localPath)}' is already open in another app. " +
                        "Close it and try again.", ex);
                }

                downloadStarted = true;
                _log.Info($"[SFTP] Downloading {remotePath} ({FormatBytes(totalBytes)}) → {localPath}");

                // ── Streaming download with cancellation support ─────────
                client.DownloadFile(remotePath, fileStream, downloaded =>
                {
                    ct.ThrowIfCancellationRequested();
                    progressCallback(checked((long)downloaded), totalBytes);
                });

                _log.Info($"[SFTP] Download complete: {Path.GetFileName(localPath)}");
            }
            catch (Exception ex)
            {
                _log.Error($"[SFTP] Download failed: {ex.Message}");

                // Clean up the partial/corrupt file so the user isn't left with garbage
                fileStream?.Dispose();
                fileStream = null;
                if (downloadStarted)
                {
                    try { if (File.Exists(localPath)) File.Delete(localPath); }
                    catch { /* best effort */ }
                }
                throw;
            }
            finally
            {
                fileStream?.Dispose();
                if (client.IsConnected) client.Disconnect();
            }
        }, ct);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024L         => $"{bytes} B",
        < 1048576L      => $"{bytes / 1024.0:F1} KB",
        < 1073741824L   => $"{bytes / 1048576.0:F1} MB",
        _               => $"{bytes / 1073741824.0:F1} GB"
    };
}
