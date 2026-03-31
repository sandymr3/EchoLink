using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Collections.Generic;
using EchoLink.Models;

namespace EchoLink.Services;

public class ClipboardSyncService
{
    private static readonly Lazy<ClipboardSyncService> _instance = new(() => new ClipboardSyncService());
    public static ClipboardSyncService Instance => _instance.Value;

    private readonly LoggingService _log = LoggingService.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly ClipboardJournalService _journal = new();

    // Platform clipboard abstraction (headless, no UIThread required)
    private IPlatformClipboard? _nativeClipboard;

    private CancellationTokenSource? _cts;
    private Task? _reliabilityTask;

    private string _lastObservedHash = "";
    private DateTime _suppressLocalUntilUtc = DateTime.MinValue;
    private string _localAccountId = "";
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ClipboardSyncMessage>> _pendingByPeer = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _knownOnlinePeers = new(StringComparer.OrdinalIgnoreCase);

    // Per-peer failure tracking — stops SOCKS5 rejection spam and retries
    private readonly ConcurrentDictionary<string, int> _peerFailCount = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _peerCooldownUntil = new(StringComparer.OrdinalIgnoreCase);

    public event Action<ClipboardEntry>? ClipboardReceived;

    private ClipboardSyncService() { }

    /// <summary>
    /// Set the platform clipboard implementation. Can be called before StartAsync()
    /// to inject a platform-specific clipboard handler (Android, Linux, Windows).
    /// </summary>
    public void SetPlatformClipboard(IPlatformClipboard clipboard)
    {
        _nativeClipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
    }

    // === Unified Protocol Integration ===

    public void InitializeUnifiedProtocol()
    {
        UnifiedProtocol.UnifiedProtocolService.Instance.RegisterHandler(
            UnifiedProtocol.UnifiedMessageType.ClipboardSync,
            async (payload, reply, ct) => await HandleClipboardSyncAsync(payload, ct));
        
        _log.Info("[Clipboard] Unified protocol handler registered");
    }

    // ── Peer-failure helpers ─────────────────────────────────────────────────

    private bool IsOnCooldown(string peerIp)
        => _peerCooldownUntil.TryGetValue(peerIp, out var until) && DateTime.UtcNow < until;

    private void RecordPeerFailure(string peerIp)
    {
        var fails = _peerFailCount.AddOrUpdate(peerIp, 1, (_, v) => v + 1);
        // Exponential back-off: 30 s → 60 s → 120 s → 240 s → 300 s (max)
        var seconds = (int)Math.Min(30 * Math.Pow(2, fails - 1), 300);
        _peerCooldownUntil[peerIp] = DateTime.UtcNow.AddSeconds(seconds);

        // Only log on the first failure and every 5 attempts after that
        if (fails == 1 || fails % 5 == 0)
            _log.Warning($"MirrorClip: cannot reach {peerIp} SSH or 44444 (Pairing) (attempt {fails}). " +
                         $"Retrying in {seconds}s. Ensure EchoLink is running there and 'tailscale serve' " +
                         $"exposed SSH and 44444 on that device.");
    }

    private void RecordPeerSuccess(string peerIp)
    {
        _peerFailCount.TryRemove(peerIp, out _);
        _peerCooldownUntil.TryRemove(peerIp, out _);
    }

    // ─────────────────────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_cts is not null)
            return;

        if (_nativeClipboard is null)
        {
            _log.Warning("[Clipboard] No platform clipboard set. Call SetPlatformClipboard() first.");
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _localAccountId = await TailscaleService.Instance.GetCurrentAccountIdAsync(_cts.Token)
            ?? "unknown-account";
        _log.Info($"MirrorClip: local account ID = {_localAccountId}");

        // Subscribe to platform clipboard change events (event-driven, no polling)
        _nativeClipboard.OnClipboardChanged += HandleLocalClipboardChanged;
        _nativeClipboard.StartMonitoring();

        _reliabilityTask = Task.Run(() => RunReliabilityLoopAsync(_cts.Token), _cts.Token);

        _log.Info("MirrorClip sync engine started (event-driven monitor + reliability loop).");
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            return;

        // Unsubscribe from clipboard change events
        if (_nativeClipboard is not null)
        {
            _nativeClipboard.OnClipboardChanged -= HandleLocalClipboardChanged;
            _nativeClipboard.StopMonitoring();
        }

        _cts.Cancel();
        try
        {
            if (_reliabilityTask is not null)
                await _reliabilityTask;
        }
        catch (OperationCanceledException) { }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _reliabilityTask = null;
        }

        _log.Info("MirrorClip sync engine stopped.");
    }

    public async Task PushCurrentClipboardAsync(CancellationToken ct = default)
    {
        if (_nativeClipboard is null)
        {
            _log.Warning("[Clipboard] No platform clipboard set.");
            return;
        }

        var text = await _nativeClipboard.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            _log.Warning("MirrorClip PushCurrentClipboard: clipboard was empty or unreadable.");
            return;
        }

        _log.Info($"MirrorClip PushCurrentClipboard: broadcasting {text.Length} chars...");
        await BroadcastClipboardAsync(text, ct);
    }

    /// <summary>
    /// Manually broadcasts the provided text to all connected peers.
    /// Used by Android's ProcessTextActivity for user-initiated "Send to PC" action.
    /// 
    /// This bypasses Android's background clipboard monitoring restrictions (Android 10+)
    /// by requiring explicit user consent via the text selection menu.
    /// </summary>
    /// <param name="text">The text to broadcast to peers</param>
    /// <param name="ct">Cancellation token</param>
    public async Task ManualBroadcastToPeersAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _log.Warning("[ManualBroadcast] No text to broadcast");
            return;
        }

        _log.Info($"[ManualBroadcast] User initiated broadcast of {text.Length} chars");

        // Suppress local monitoring to avoid loopback for 2 seconds
        // This prevents the broadcast text from being re-broadcast if the local clipboard changes
        _suppressLocalUntilUtc = DateTime.UtcNow.AddSeconds(2);

        await BroadcastClipboardAsync(text, ct);
    }

    public Task UpdateClipboardShareTargetsAsync(IEnumerable<string> targetIps)
    {
        var settings = _settings.Load();
        settings.ClipboardShareTargets = targetIps
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Select(ip => ip.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.ClipboardUseTargetSelection = true;

        _settings.Save(settings);

        // Drop pending messages for peers no longer selected for clipboard share.
        var selected = settings.ClipboardShareTargets
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var peerIp in _pendingByPeer.Keys.ToArray())
        {
            if (selected.Count > 0 && !selected.Contains(peerIp))
                _pendingByPeer.TryRemove(peerIp, out _);
        }

        _log.Info($"MirrorClip: updated clipboard target list ({settings.ClipboardShareTargets.Count} selected peer(s)).");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Invoked instantly when the platform clipboard content changes.
    /// Replaces the old 900ms polling loop with event-driven monitoring.
    /// </summary>
    private void HandleLocalClipboardChanged(object? sender, string newText)
    {
        if (string.IsNullOrWhiteSpace(newText))
            return;

        var hash = ComputeHash(newText);
        if (DateTime.UtcNow < _suppressLocalUntilUtc)
        {
            _lastObservedHash = hash;
            return;
        }

        if (hash == _lastObservedHash)
            return;

        _lastObservedHash = hash;
        _log.Info($"MirrorClip: clipboard changed (event-driven) ({newText.Length} chars), broadcasting...");
        
        // Fire and forget - don't block the event handler
        _ = Task.Run(async () =>
        {
            try
            {
                await BroadcastClipboardAsync(newText, _cts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Warning($"MirrorClip: failed to broadcast clipboard: {ex.Message}");
            }
        });
    }

    private async Task BroadcastClipboardAsync(string text, CancellationToken ct)
    {
        var settings = _settings.Load();
        var hash = ComputeHash(text);

        var (selfIp, devices) = await TailscaleService.Instance.GetNetworkStatusAsync(ct: ct);
        var sender = selfIp ?? Environment.MachineName;
        var accountId = await TailscaleService.Instance.GetCurrentAccountIdAsync(ct)
            ?? _localAccountId;

        var message = new ClipboardSyncMessage
        {
            Type = "clip",
            EventId = Guid.NewGuid().ToString("N"),
            Sequence = _journal.NextSequence(),
            OriginDeviceId = sender,
            SenderDeviceId = sender,
            SenderAccountId = accountId,
            TimestampUtc = DateTime.UtcNow,
            ContentType = "text/plain",
            ContentText = text,
            ContentHash = hash,
            GhostPaste = settings.GhostPasteEnabled
        };

        await _journal.AppendAsync(message, ct);

        // Fire event to add local clip to UI history
        ClipboardReceived?.Invoke(new ClipboardEntry(
            message.ContentText,
            message.OriginDeviceId + " (me)",
            DateTime.Now));

        var peers = GetEligibleClipboardPeers(devices, settings)
            .Select(d => d.IpAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _log.Info($"MirrorClip broadcast: {peers.Count} online peer(s) found. Self IP={sender}, Account={accountId}");
        if (peers.Count == 0)
            _log.Warning("MirrorClip broadcast: no peers found! Check that other devices are connected and online.");

        foreach (var peerIp in peers)
        {
            if (IsOnCooldown(peerIp))
            {
                QueuePending(peerIp, message); // will retry when cooldown expires
                continue;
            }

            try
            {
                bool acked = await SendClipToPeerAsync(peerIp, message, ct);
                if (acked)
                    RecordPeerSuccess(peerIp);
                else
                {
                    RecordPeerFailure(peerIp);
                    QueuePending(peerIp, message);
                }
            }
            catch (Exception ex)
            {
                _log.Debug($"MirrorClip send failed to {peerIp}: {ex.Message}");
                RecordPeerFailure(peerIp);
                QueuePending(peerIp, message);
            }
        }

        _log.Debug($"MirrorClip broadcast complete ({peers.Count} peers).");
    }

    private async Task HandleClipboardSyncAsync(byte[] payload, CancellationToken ct)
    {
        var json = Encoding.UTF8.GetString(payload);
        ClipboardSyncMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<ClipboardSyncMessage>(json);
        }
        catch
        {
            return;
        }

        if (message is null)
            return;

        if (message.Type == "ack")
            return;

        if (message.Type != "clip" || string.IsNullOrWhiteSpace(message.EventId))
            return;

        if (!await IsSameAccountAsync(message.SenderAccountId, ct))
        {
            _log.Debug($"MirrorClip ignored clip from different account. sender={message.SenderAccountId}, local={_localAccountId}");
            return;
        }

        if (_journal.HasEvent(message.EventId))
        {
            return;
        }

        var settings = _settings.Load();
        if (!settings.MirrorClipEnabled)
        {
            return;
        }

        await _journal.AppendAsync(message, ct);
        
        // Apply remote clipboard using platform clipboard (no UIThread required)
        if (_nativeClipboard is not null)
        {
            _suppressLocalUntilUtc = DateTime.UtcNow.AddSeconds(2);
            _lastObservedHash = ComputeHash(message.ContentText);
            
            try
            {
                await _nativeClipboard.SetTextAsync(message.ContentText);
                _log.Info($"MirrorClip: applied remote clipboard ({message.ContentText.Length} chars) to local device.");
            }
            catch (Exception ex)
            {
                _log.Warning($"MirrorClip: failed to apply remote clipboard: {ex.Message}");
            }
        }

        ClipboardReceived?.Invoke(new ClipboardEntry(
            message.ContentText,
            message.SenderDeviceId,
            DateTime.Now));

        _log.Info($"MirrorClip received clip from {message.SenderDeviceId}.");
    }

    private static string ComputeHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }

    private async Task<bool> IsSameAccountAsync(string senderAccountId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(senderAccountId))
            return false;

        if (string.Equals(senderAccountId, "unknown-account", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(_localAccountId) || _localAccountId == "unknown-account")
        {
            _localAccountId = await TailscaleService.Instance.GetCurrentAccountIdAsync(ct)
                ?? _localAccountId;
        }

        // Only enforce strict matching when both sides have resolved IDs.
        if (string.IsNullOrWhiteSpace(_localAccountId) || _localAccountId == "unknown-account")
            return true;

        return string.Equals(senderAccountId, _localAccountId, StringComparison.Ordinal);
    }

    private async Task<bool> SendClipToPeerAsync(
        string targetIp,
        ClipboardSyncMessage message,
        CancellationToken ct)
    {
        // Peer must be paired - check using new ApprovedGuests (NodeId-based) system
        // Fallback to legacy PeerUsernames for backward compatibility during transition
        var settings = _settings.Load();
        bool isPeerPaired = settings.ApprovedGuests.Values.Any(g => g.LastKnownIp == targetIp) ||
                           settings.PeerUsernames.TryGetValue(targetIp, out _);
        
        if (!isPeerPaired)
        {
            _log.Debug($"MirrorClip Cannot sync to {targetIp} because it is not paired.");
            return false;
        }

        try
        {
            // Connect directly to the Unified Protocol Port (55555) via SOCKS5.
            using var client = await NetworkService.Instance.ConnectViaSocks5Async(
                targetIp,
                UnifiedProtocol.UnifiedProtocolService.UnifiedPort,
                ct);

            if (client == null || !client.Connected)
                return false;

            using var stream = client.GetStream();

            var json = JsonSerializer.Serialize(message);
            var payload = Encoding.UTF8.GetBytes(json);

            // Build header: [Type:1][Length:4 big-endian]
            var header = new byte[5];
            header[0] = (byte)UnifiedProtocol.UnifiedMessageType.ClipboardSync;
            var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));
            Array.Copy(lengthBytes, 0, header, 1, 4);

            await stream.WriteAsync(header, ct);
            if (payload.Length > 0)
            {
                await stream.WriteAsync(payload, ct);
            }

            await stream.FlushAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _log.Debug($"MirrorClip: Failed to send to {targetIp} via Unified Protocol: {ex.Message}");
            return false;
        }
    }

    private void QueuePending(string peerIp, ClipboardSyncMessage message)
    {
        var perPeer = _pendingByPeer.GetOrAdd(peerIp, _ => new ConcurrentDictionary<string, ClipboardSyncMessage>(StringComparer.Ordinal));
        perPeer[message.EventId] = message;
    }

    private async Task RunReliabilityLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (_, devices) = await TailscaleService.Instance.GetNetworkStatusAsync(ct: ct);
                var settings = _settings.Load();
                var onlinePeers = GetEligibleClipboardPeers(devices, settings)
                    .Select(d => d.IpAddress)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var peer in onlinePeers)
                {
                    if (!_knownOnlinePeers.Contains(peer))
                        await ReplayRecentToPeerAsync(peer, ct);

                    await RetryPendingForPeerAsync(peer, ct);
                }

                _knownOnlinePeers = onlinePeers;
                await Task.Delay(5000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warning($"MirrorClip reliability loop error: {ex.Message}");
                await Task.Delay(2000, ct);
            }
        }
    }

    private async Task ReplayRecentToPeerAsync(string peerIp, CancellationToken ct)
    {
        if (IsOnCooldown(peerIp))
            return;

        var recent = await _journal.GetRecentClipMessagesAsync(10, ct);
        if (recent.Count == 0)
            return;

        foreach (var msg in recent)
        {
            try
            {
                bool acked = await SendClipToPeerAsync(peerIp, msg, ct);
                if (acked)
                    RecordPeerSuccess(peerIp);
                else
                {
                    RecordPeerFailure(peerIp);
                    QueuePending(peerIp, msg);
                    break; // stop replaying once peer becomes unreachable
                }
            }
            catch
            {
                RecordPeerFailure(peerIp);
                QueuePending(peerIp, msg);
                break;
            }
        }

        _log.Debug($"MirrorClip replayed {recent.Count} recent clips to {peerIp}.");
    }

    private async Task RetryPendingForPeerAsync(string peerIp, CancellationToken ct)
    {
        if (!_pendingByPeer.TryGetValue(peerIp, out var pending) || pending.Count == 0)
            return;

        if (IsOnCooldown(peerIp))
            return;

        foreach (var pair in pending.ToArray())
        {
            try
            {
                bool acked = await SendClipToPeerAsync(peerIp, pair.Value, ct);
                if (acked)
                {
                    RecordPeerSuccess(peerIp);
                    pending.TryRemove(pair.Key, out _);
                }
                else
                {
                    RecordPeerFailure(peerIp);
                    break; // stop retrying this peer this cycle
                }
            }
            catch
            {
                RecordPeerFailure(peerIp);
                break;
            }
        }
    }

    /// <summary>
    /// Filters the network device list to only those eligible for clipboard sharing
    /// based on online status and user-selection settings.
    /// </summary>
    private static IEnumerable<Device> GetEligibleClipboardPeers(
        IEnumerable<Device> devices,
        SettingsData settings)
    {
        var selected = settings.ClipboardShareTargets
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            if (!device.IsOnline || device.IsSelf || string.IsNullOrWhiteSpace(device.IpAddress))
                continue;

            if (!settings.ClipboardUseTargetSelection)
            {
                yield return device;
                continue;
            }

            if (selected.Contains(device.IpAddress))
                yield return device;
        }
    }


}

