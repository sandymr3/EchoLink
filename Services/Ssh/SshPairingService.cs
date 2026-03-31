using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EchoLink.Services
{
    public class SshPairingService
    {
        private const int KeyExchangePort = 44444; // Fixed port for key exchange over Tailscale
        private readonly TailscaleService _tailscaleService;
        private CancellationTokenSource? _listeningCts;

        public event Action? PairingCompleted;

        // Echolink specific SSH keys
        private readonly string _sshDir;
        public string PrivateKeyPath { get; }
        public string PublicKeyPath { get; }

        public SshPairingService(TailscaleService tailscaleService)
        {
            _tailscaleService = tailscaleService;

            string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _sshDir = Path.Combine(homeDir, ".ssh");
            
            PrivateKeyPath = Path.Combine(_sshDir, "echolink_ed25519");
            PublicKeyPath = PrivateKeyPath + ".pub";
        }

        public async Task EnsureKeyPairAsync()
        {
            if (!Directory.Exists(_sshDir))
            {
                Directory.CreateDirectory(_sshDir);
            }

            // Secure the .ssh directory itself on Windows so sshd doesn't reject it
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("icacls", $"\"{_sshDir}\" /inheritance:r /q") { CreateNoWindow = true })?.WaitForExit();
                Process.Start(new ProcessStartInfo("icacls", $"\"{_sshDir}\" /grant SYSTEM:(F) /q") { CreateNoWindow = true })?.WaitForExit();
                Process.Start(new ProcessStartInfo("icacls", $"\"{_sshDir}\" /grant \"{Environment.UserName}:(F)\" /q") { CreateNoWindow = true })?.WaitForExit();
            }

            if (!File.Exists(PrivateKeyPath) || !File.Exists(PublicKeyPath))
            {
                if (OperatingSystem.IsAndroid())
                {
                    // Generate RSA key natively for Android since ssh-keygen is missing
                    using var rsa = System.Security.Cryptography.RSA.Create(2048);
                    
                    // Write Private Key in PEM format
                    string privatePem = rsa.ExportRSAPrivateKeyPem();
                    File.WriteAllText(PrivateKeyPath, privatePem);

                    // Write Public Key in OpenSSH format
                    string publicOpenSsh = GenerateOpenSshPublicKey(rsa);
                    File.WriteAllText(PublicKeyPath, publicOpenSsh);
                }
                else
                {
                    // Generate a new ed25519 keypair without password via standard ssh-keygen
                    var psi = new ProcessStartInfo
                    {
                        FileName = "ssh-keygen",
                        Arguments = $"-t ed25519 -f \"{PrivateKeyPath}\" -N \"\" -q",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                    }
                    
                    // On Linux/Mac, ensure private key has strict permissions
                    if (!OperatingSystem.IsWindows())
                    {
                        try { Process.Start(new ProcessStartInfo("chmod", $"600 \"{PrivateKeyPath}\"") { CreateNoWindow = true })?.WaitForExit(); } catch { }
                    }
                }
            }
        }

        private static string GenerateOpenSshPublicKey(System.Security.Cryptography.RSA rsa)
        {
            var parameters = rsa.ExportParameters(false);
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            
            byte[] header = Encoding.ASCII.GetBytes("ssh-rsa");
            bw.Write(IPAddress.HostToNetworkOrder(header.Length));
            bw.Write(header);
            
            bw.Write(IPAddress.HostToNetworkOrder(parameters.Exponent.Length));
            bw.Write(parameters.Exponent);
            
            byte[] modulus = parameters.Modulus;
            if (modulus[0] >= 0x80) {
                bw.Write(IPAddress.HostToNetworkOrder(modulus.Length + 1));
                bw.Write((byte)0);
            } else {
                bw.Write(IPAddress.HostToNetworkOrder(modulus.Length));
            }
            bw.Write(modulus);
            
            return "ssh-rsa " + Convert.ToBase64String(ms.ToArray()) + " echolink-android";
        }

        public async Task<string> GetMyPublicKeyAsync()
        {
            await EnsureKeyPairAsync();
            return await File.ReadAllTextAsync(PublicKeyPath);
        }

        public async Task TrustPublicKeyAsync(string nodeId, string ipAddress, string username, string publicKey)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                LoggingService.Instance.Warning($"[SshPairing] TrustPublicKeyAsync called with empty NodeId - using IP fallback");
                // Fallback to legacy behavior if NodeId is missing
                await TrustPublicKeyLegacyAsync(ipAddress, username, publicKey);
                return;
            }
            
            await AddToAuthorizedKeysAsync(publicKey);

            if (OperatingSystem.IsAndroid())
            {
                TailscaleService.Instance.NativeBridge?.SetTempSshPassword(ipAddress, publicKey);
            }

            var settings = SettingsService.Instance.Load();
            
            // Store in new ApprovedGuests format (NodeId-based)
            if (!settings.ApprovedGuests.ContainsKey(nodeId))
            {
                settings.ApprovedGuests[nodeId] = new ApprovedGuestInfo();
            }
            
            var guest = settings.ApprovedGuests[nodeId];
            guest.NodeId = nodeId;
            guest.Name = username;
            guest.PublicKey = publicKey;
            guest.LastKnownIp = ipAddress;
            guest.AddedAt = DateTime.UtcNow;
            
            // Also track IP address mapping
            settings.PeerIpAddresses[nodeId] = ipAddress;
            
            SettingsService.Instance.Save(settings);
            
            LoggingService.Instance.Info($"[SshPairing] Trusted device {username} (NodeId: {nodeId}, IP: {ipAddress})");
        }
        
        /// <summary>
        /// Legacy method for backward compatibility - do not use in new code.
        /// </summary>
        private async Task TrustPublicKeyLegacyAsync(string ip, string username, string publicKey)
        {
            await AddToAuthorizedKeysAsync(publicKey);

            if (OperatingSystem.IsAndroid())
            {
                TailscaleService.Instance.NativeBridge?.SetTempSshPassword(ip, publicKey);
            }

            var settings = SettingsService.Instance.Load();
            settings.PeerUsernames[ip] = username;
            settings.PeerPublicKeys[ip] = publicKey;
            SettingsService.Instance.Save(settings);
        }

        public async Task UntrustPublicKeyAsync(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                LoggingService.Instance.Warning("[SshPairing] UntrustPublicKeyAsync called with empty NodeId - ignoring");
                return;
            }
            
            var settings = SettingsService.Instance.Load();
            
            // Remove from new ApprovedGuests format
            if (settings.ApprovedGuests.ContainsKey(nodeId))
            {
                var guest = settings.ApprovedGuests[nodeId];
                await RemoveFromAuthorizedKeysAsync(guest.PublicKey);
                settings.ApprovedGuests.Remove(nodeId);
                settings.PeerIpAddresses.Remove(nodeId);
                SettingsService.Instance.Save(settings);
                
                if (OperatingSystem.IsAndroid())
                {
                    TailscaleService.Instance.NativeBridge?.RemoveTempSshPassword(guest.LastKnownIp);
                }
                
                LoggingService.Instance.Info($"[SshPairing] Untrusted device {guest.Name} (NodeId: {nodeId})");
                return;
            }
            
            // Fallback: try legacy IP-based lookup
            if (settings.PeerPublicKeys.TryGetValue(nodeId, out var publicKey))
            {
                await RemoveFromAuthorizedKeysAsync(publicKey);
                settings.PeerPublicKeys.Remove(nodeId);
                settings.PeerUsernames.Remove(nodeId);
                SettingsService.Instance.Save(settings);
                
                if (OperatingSystem.IsAndroid())
                {
                    TailscaleService.Instance.NativeBridge?.RemoveTempSshPassword(nodeId);
                }
                
                LoggingService.Instance.Info($"[SshPairing] Untrusted legacy device (IP: {nodeId})");
            }
        }

        private async Task RemoveFromAuthorizedKeysAsync(string publicKey)
        {
            string authKeysPath = Path.Combine(_sshDir, "authorized_keys");
            if (!File.Exists(authKeysPath)) return;

            var lines = await File.ReadAllLinesAsync(authKeysPath);
            var newLines = lines.Where(line => !line.Trim().Contains(publicKey.Trim())).ToList();

            if (lines.Length != newLines.Count)
            {
                await File.WriteAllLinesAsync(authKeysPath, newLines);
            }
        }

        /// <summary>
        /// Call this on App Startup if we have Tailscale IP available
        /// </summary>
        public void StartListening(Func<string, string, Task<bool>> onKeyReceivedConfirmation)
        {
            _listeningCts?.Cancel();
            _listeningCts = new CancellationTokenSource();
            _ = AcceptConnectionsAsync(onKeyReceivedConfirmation, _listeningCts.Token);
        }

        public void StopListening()
        {
            _listeningCts?.Cancel();
        }

        private async Task AcceptConnectionsAsync(Func<string, string, Task<bool>> onKeyReceivedConfirmation, CancellationToken cancellationToken)
        {
            try
            {
                // Ensure tailscale is up and we have an IP
                var myIp = await _tailscaleService.GetTailscaleIpAsync(cancellationToken);
                if (string.IsNullOrEmpty(myIp)) return;

                // Listen strictly on localhost, as Go forwards mesh traffic to 127.0.0.1:44444
                var listener = new TcpListener(IPAddress.Loopback, KeyExchangePort);
                listener.Start();

                // Stop listener when cancelled
                cancellationToken.Register(() => listener.Stop());

                while (!cancellationToken.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cancellationToken);
                    _ = HandleClientConnectionAsync(client, onKeyReceivedConfirmation);
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }

        private async Task HandleClientConnectionAsync(TcpClient client, Func<string, string, Task<bool>> onKeyReceivedConfirmation)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
            {
                try
                {
                    string? incomingPayload = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(incomingPayload)) return;

                    if (incomingPayload.StartsWith("PAIRING_COMPLETE"))
                    {
                        var handshakeParts = incomingPayload.Split("|||");
                        if (handshakeParts.Length >= 4)
                        {
                            string hHostname = handshakeParts[1];
                            string hIp = handshakeParts[2];
                            string hPubKey = handshakeParts[3];
                            string hNodeId = handshakeParts.Length >= 5 ? handshakeParts[4] : string.Empty;

                            if (!string.IsNullOrEmpty(hNodeId))
                            {
                                TrustStoreService.Instance.AddGuest(hNodeId, hPubKey, hHostname);
                                await TrustPublicKeyAsync(hNodeId, hIp, "echolink-mesh", hPubKey);
                            }
                            else
                            {
                                // Fallback to legacy if NodeId not provided
                                await TrustPublicKeyLegacyAsync(hIp, "echolink-mesh", hPubKey);
                            }
                            
                            LoggingService.Instance.Info($"[Pairing] Bidirectional pairing confirmed with {hHostname} ({hIp})");

                            // Trigger refresh to redraw UI organically
                            _ = DeviceDiscoveryService.Instance.RefreshAsync();
                        }

                        PairingCompleted?.Invoke();
                        return;
                    }

                    // Payload should be: "HOSTNAME|||USERNAME|||IP_ADDRESS|||PUBLIC_KEY|||NODE_ID"
                    var parts = incomingPayload.Split("|||");
                    if (parts.Length < 4) return;

                    string hostname = parts[0];
                    string remoteUsername = parts[1];
                    string remoteIp = parts[2];
                    string publicKey = parts[3].Trim();
                    string remoteNodeId = parts.Length >= 5 ? parts[4] : string.Empty;

                    if (!publicKey.StartsWith("ssh-ed25519") && !publicKey.StartsWith("ssh-rsa"))
                    {
                        await writer.WriteLineAsync("REJECTED: Invalid key format");
                        return;
                    }

                    // 1. Silent Check: Is this key already trusted?
                    bool alreadyPaired = await IsKeyAlreadyAuthorizedAsync(publicKey);

                    bool accepted = alreadyPaired;

                    if (!alreadyPaired)
                    {
                        // 2. Prompt user via ViewModel callback ONLY if we don't already trust this key
                        accepted = await onKeyReceivedConfirmation(hostname, publicKey);
                    }

                    if (accepted)
                    {
                        if (!string.IsNullOrEmpty(remoteNodeId))
                        {
                            TrustStoreService.Instance.AddGuest(remoteNodeId, publicKey, hostname);
                            await TrustPublicKeyAsync(remoteNodeId, remoteIp, remoteUsername, publicKey);
                        }
                        else
                        {
                            // Fallback to legacy if NodeId not provided
                            await TrustPublicKeyLegacyAsync(remoteIp, remoteUsername, publicKey);
                        }

                        // Reply with our OS username and Public Key so the sender can also trust us
                        string myPubKey = await GetMyPublicKeyAsync();
                        var selfDevice = DeviceDiscoveryService.Instance.GetSelfDevice();
                        string myNodeId = selfDevice?.NodeId ?? "Unknown";
                        await writer.WriteLineAsync($"ACCEPTED|||{Environment.UserName}|||{myPubKey}|||{myNodeId}");

                        // Refresh to reflect new guest
                        _ = DeviceDiscoveryService.Instance.RefreshAsync();
                    }
                    else
                    {
                        await writer.WriteLineAsync("REJECTED: User declined");
                    }
                }
                catch (Exception ex)
                {
                    LoggingService.Instance.Error($"Key exchange error: {ex.Message}");
                }
            }
        }

        public async Task<bool> IsKeyAlreadyAuthorizedAsync(string publicKey)
        {
            string authKeysPath = Path.Combine(_sshDir, "authorized_keys");
            if (!File.Exists(authKeysPath)) return false;

            var lines = await File.ReadAllLinesAsync(authKeysPath);
            foreach (var line in lines)
            {
                if (line.Contains(publicKey))
                {
                    return true;
                }
            }
            return false;
        }

        public async Task<(bool Accepted, string? TargetUsername)> RequestPairingAsync(string targetIp, string myHostname, string myUsername)
        {
            string myPubKey = await GetMyPublicKeyAsync();
            string myIp = await _tailscaleService.GetTailscaleIpAsync() ?? "Unknown";
            
            var selfDevice = DeviceDiscoveryService.Instance.GetSelfDevice();
            string myNodeId = selfDevice?.NodeId ?? "Unknown";

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                
                // Route through SOCKS5 proxy since we are running userspace tailscale
                using var client = await NetworkService.Instance.ConnectViaSocks5Async(targetIp, KeyExchangePort, cts.Token);
                
                if (client == null || !client.Connected)
                {
                    LoggingService.Instance.Error($"SOCKS5 proxy rejected connection to {targetIp}:{KeyExchangePort}");
                    return (false, null);
                }

                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8);

                await writer.WriteLineAsync($"{myHostname}|||{myUsername}|||{myIp}|||{myPubKey}|||{myNodeId}");

                string? response = await reader.ReadLineAsync();

                if (response != null && response.StartsWith("ACCEPTED|||"))
                {
                    var responseParts = response.Split("|||");
                    if (responseParts.Length >= 3)
                    {
                        string targetUser = responseParts[1];
                        string targetPubKey = responseParts[2];
                        string targetNodeId = responseParts.Length >= 4 ? responseParts[3] : string.Empty;

                        if (!string.IsNullOrEmpty(targetNodeId))
                        {
                            TrustStoreService.Instance.AddGuest(targetNodeId, targetPubKey, targetUser);
                            await TrustPublicKeyAsync(targetNodeId, targetIp, targetUser, targetPubKey);
                        }
                        else
                        {
                            // Fallback to legacy if NodeId not provided
                            await TrustPublicKeyLegacyAsync(targetIp, targetUser, targetPubKey);
                        }
                        
                        _ = DeviceDiscoveryService.Instance.RefreshAsync();
                        return (true, targetUser);
                    }
                }
                
                if (response == null)
                    LoggingService.Instance.Warning($"[Pairing] Connection closed by remote host {targetIp}");
                else
                    LoggingService.Instance.Warning($"[Pairing] Remote responded: {response}");

                return (false, null);
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Error($"Failed to pair with {targetIp}: {ex.Message}");
                return (false, null);
            }
        }

        public async Task SendPairingCompleteAsync(string targetIp)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var client = await NetworkService.Instance.ConnectViaSocks5Async(targetIp, KeyExchangePort, cts.Token);
                if (client != null)
                {
                    using var stream = client.GetStream();
                    using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                    
                    string myIp = await _tailscaleService.GetTailscaleIpAsync(cts.Token) ?? "Unknown";
                    string myPubKey = await GetMyPublicKeyAsync();
                    var selfDevice = DeviceDiscoveryService.Instance.GetSelfDevice();
                    string myNodeId = selfDevice?.NodeId ?? "Unknown";
                    
                    await writer.WriteLineAsync($"PAIRING_COMPLETE|||{Environment.MachineName}|||{myIp}|||{myPubKey}|||{myNodeId}");
                }
            }
            catch { /* Ignore handshake errors */ }
        }

        private async Task AddToAuthorizedKeysAsync(string publicKey)
        {
            string authKeysPath = Path.Combine(_sshDir, "authorized_keys");

            // Ensure file exists
            if (!File.Exists(authKeysPath))
            {
                await File.WriteAllTextAsync(authKeysPath, "");
            }

            // Always enforce permissions on every key addition to ensure OpenSSH doesn't reject it
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("chmod", $"600 \"{authKeysPath}\""));
            }
            else if (OperatingSystem.IsWindows())
            {
                // Unblock files if created by downloading or bad inheritance
                Process.Start(new ProcessStartInfo("icacls", $"\"{authKeysPath}\" /reset /q") { CreateNoWindow = true })?.WaitForExit();
                Process.Start(new ProcessStartInfo("icacls", $"\"{authKeysPath}\" /inheritance:r /q") { CreateNoWindow = true })?.WaitForExit();
                Process.Start(new ProcessStartInfo("icacls", $"\"{authKeysPath}\" /grant SYSTEM:(F) /q") { CreateNoWindow = true })?.WaitForExit();
                Process.Start(new ProcessStartInfo("icacls", $"\"{authKeysPath}\" /grant \"{Environment.UserName}:(F)\" /q") { CreateNoWindow = true })?.WaitForExit();

                // Windows OpenSSH defaults to C:\ProgramData\ssh\administrators_authorized_keys for
                // admin accounts — ensure sshd_config is patched to use ~/.ssh/authorized_keys instead.
                EnsureSshdConfigPatched();
            }

            // Check if key already exists to prevent duplicates
            var existingKeys = await File.ReadAllLinesAsync(authKeysPath);
            foreach (var key in existingKeys)
            {
                if (key.Trim() == publicKey) return; // Already paired
            }

            // Append with a newline ensuring there's separation
            using (var sw = File.AppendText(authKeysPath))
            {
                await sw.WriteLineAsync();
                await sw.WriteLineAsync(publicKey);
            }
        }

        private static void EnsureSshdConfigPatched()
        {
            string sshdConfig = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ssh", "sshd_config");

            if (!File.Exists(sshdConfig)) return;

            try
            {
                string content = File.ReadAllText(sshdConfig);
                // If already patched (block is commented out), do nothing
                if (!content.Contains("\nMatch Group administrators") &&
                    !content.Contains("\r\nMatch Group administrators"))
                    return;

                content = content
                    .Replace("\r\nMatch Group administrators",
                             "\r\n#Match Group administrators")
                    .Replace("\nMatch Group administrators",
                             "\n#Match Group administrators")
                    .Replace("AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys",
                             "#       AuthorizedKeysFile __PROGRAMDATA__/ssh/administrators_authorized_keys");

                File.WriteAllText(sshdConfig, content);

                // Restart sshd so the config change takes effect
                Process.Start(new ProcessStartInfo("net", "stop sshd") { CreateNoWindow = true })?.WaitForExit(5000);
                Process.Start(new ProcessStartInfo("net", "start sshd") { CreateNoWindow = true })?.WaitForExit(5000);

                LoggingService.Instance.Info("[SshPairing] sshd_config patched — admin authorized_keys redirect removed.");
            }
            catch (Exception ex)
            {
                // Non-fatal: log and continue; user may need to run as admin or patch manually
                LoggingService.Instance.Warning($"[SshPairing] Could not patch sshd_config: {ex.Message}");
            }
        }
    }
}
