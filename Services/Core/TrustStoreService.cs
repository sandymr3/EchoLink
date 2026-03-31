using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using EchoLink.Models;

namespace EchoLink.Services;

/// <summary>
/// Manages the local trust anchor (ApprovedGuests).
/// This serves as the single source of truth for explicitly paired guest devices.
/// 
/// Uses NodeId as the primary key (persistent identity) instead of IP address (ephemeral).
/// </summary>
public class TrustStoreService
{
    private static readonly Lazy<TrustStoreService> _instance = new(() => new TrustStoreService());
    public static TrustStoreService Instance => _instance.Value;

    private readonly string _storePath;
    private readonly string _sshDir;
    private readonly object _lock = new();
    private readonly LoggingService _log = LoggingService.Instance;

    private ConcurrentDictionary<string, ApprovedGuest> _guests = new();

    private TrustStoreService()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoLink");
        Directory.CreateDirectory(appData);
        _storePath = Path.Combine(appData, "ApprovedGuests.json");

        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _sshDir = Path.Combine(homeDir, ".ssh");

        Load();
    }

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_storePath))
                {
                    var json = File.ReadAllText(_storePath);
                    var list = JsonSerializer.Deserialize<List<ApprovedGuest>>(json);
                    if (list != null)
                    {
                        foreach (var guest in list)
                        {
                            _guests[guest.NodeId] = guest;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[TrustStore] Failed to load guests: {ex.Message}");
            }
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                var list = _guests.Values.ToList();
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });

                string tmpPath = _storePath + ".tmp";
                File.WriteAllText(tmpPath, json);

                // Atomic replace (Move)
                File.Move(tmpPath, _storePath, overwrite: true);
                _log.Debug("[TrustStore] Saved successfully.");
            }
            catch (Exception ex)
            {
                _log.Error($"[TrustStore] Failed to save guests: {ex.Message}");
            }
        }
    }

    public List<ApprovedGuest> GetAllGuests()
    {
        return _guests.Values.ToList();
    }

    /// <summary>
    /// Checks if a device is approved/trusted by its NodeId.
    /// </summary>
    public bool IsGuestApproved(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return false;
        return _guests.ContainsKey(nodeId);
    }

    public ApprovedGuest? GetGuest(string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return null;
        _guests.TryGetValue(nodeId, out var guest);
        return guest;
    }

    /// <summary>
    /// Adds a guest to the trust store by NodeId.
    /// </summary>
    public void AddGuest(string nodeId, string publicKey, string name)
    {
        if (string.IsNullOrEmpty(nodeId))
        {
            _log.Warning("[TrustStore] AddGuest called with empty NodeId - ignoring");
            return;
        }

        var guest = new ApprovedGuest
        {
            NodeId = nodeId,
            PublicKey = publicKey,
            Name = name,
            AddedAt = DateTime.UtcNow
        };

        _guests[nodeId] = guest;
        Save();
        _log.Info($"[TrustStore] Added guest {name} (NodeId: {nodeId})");
    }

    /// <summary>
    /// Removes a guest from the trust store by NodeId.
    /// Also removes the SSH key from authorized_keys.
    /// </summary>
    public async Task RemoveGuestAsync(string nodeId)
    {
        if (_guests.TryRemove(nodeId, out var removedGuest))
        {
            Save();
            _log.Info($"[TrustStore] Removed guest {removedGuest.Name} (NodeId: {nodeId})");

            if (!string.IsNullOrEmpty(removedGuest.PublicKey))
            {
                await RemoveFromAuthorizedKeysAsync(removedGuest.PublicKey);
                _log.Info($"[TrustStore] Purged SSH key for {nodeId}");
            }
        }
    }

    private async Task RemoveFromAuthorizedKeysAsync(string publicKey)
    {
        string authKeysPath = Path.Combine(_sshDir, "authorized_keys");
        if (!File.Exists(authKeysPath)) return;

        try
        {
            var lines = await File.ReadAllLinesAsync(authKeysPath);
            var newLines = lines.Where(line => !line.Trim().Contains(publicKey.Trim())).ToList();

            if (lines.Length != newLines.Count)
            {
                // Write to temp file and move to be safe
                string tmpPath = authKeysPath + ".tmp";
                await File.WriteAllLinesAsync(tmpPath, newLines);
                File.Move(tmpPath, authKeysPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[TrustStore] Failed to remove SSH key: {ex.Message}");
        }
    }
}
