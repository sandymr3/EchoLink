using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EchoLink.Models;

namespace EchoLink.Services;

public class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    private readonly string _settingsPath;
    private readonly LoggingService _log = LoggingService.Instance;

    private SettingsService()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EchoLink");
        Directory.CreateDirectory(appData);
        _settingsPath = Path.Combine(appData, "settings.json");
    }

    public SettingsData Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var data = JsonSerializer.Deserialize<SettingsData>(json) ?? new SettingsData();
                
                // Migrate legacy settings (PeerUsernames/PeerPublicKeys → ApprovedGuests)
                MigrateLegacySettings(data);
                
                return data;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load settings: {ex.Message}");
        }
        return new SettingsData();
    }

    public void Save(SettingsData data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            
            // Atomic write: write to temp file, then move
            string tempPath = _settingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
            
            _log.Debug("Settings saved.");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save settings: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Migrates legacy PeerUsernames/PeerPublicKeys to new ApprovedGuests format.
    /// This is a one-time migration for backward compatibility.
    /// </summary>
    private void MigrateLegacySettings(SettingsData data)
    {
        // Check if already migrated
        if (data.ApprovedGuests != null && data.ApprovedGuests.Count > 0)
            return;
        
        // Check if there's legacy data to migrate
        if (data.PeerUsernames == null || data.PeerUsernames.Count == 0)
            return;
        
        _log.Info($"[Settings] Migrating {data.PeerUsernames.Count} legacy peer entries to ApprovedGuests...");
        
        // Initialize new collections if null
        if (data.ApprovedGuests == null)
            data.ApprovedGuests = new Dictionary<string, ApprovedGuestInfo>();
        
        if (data.PeerIpAddresses == null)
            data.PeerIpAddresses = new Dictionary<string, string>();
        
        foreach (var kvp in data.PeerUsernames)
        {
            string oldIp = kvp.Key;
            string username = kvp.Value;
            string publicKey = data.PeerPublicKeys?.TryGetValue(oldIp, out var key) == true ? key : "";
            
            // Create a temporary NodeId - will be updated when device is discovered
            // Format: legacy_<ip> to identify migrated entries
            string tempNodeId = $"legacy_{oldIp}";
            
            data.ApprovedGuests[tempNodeId] = new ApprovedGuestInfo
            {
                NodeId = tempNodeId,
                Name = username,
                PublicKey = publicKey,
                LastKnownIp = oldIp,
                AddedAt = DateTime.UtcNow
            };
            
            data.PeerIpAddresses[tempNodeId] = oldIp;
        }
        
        _log.Info($"[Settings] Migration complete. Created {data.ApprovedGuests.Count} ApprovedGuest entries.");
    }
}

public class SettingsData
{
    // ── EchoBoard (Clipboard) ──
    public bool MirrorClipEnabled { get; set; } = true;
    public bool GhostPasteEnabled { get; set; } = true;
    public bool SnapShareEnabled { get; set; } = true;
    public int ClipboardHistoryLimit { get; set; } = 50;
    public bool ClipboardUseTargetSelection { get; set; }
    public List<string> ClipboardShareTargets { get; set; } = [];

    // ── General ──
    public bool LaunchOnStartup { get; set; }
    public bool ShowNotifications { get; set; } = true;

    // ── Hotkeys ──
    public List<HotkeyData> Hotkeys { get; set; } = [];

    // ── Approved Guests (NodeId-based trust) ──
    // NEW: Replaces PeerUsernames/PeerPublicKeys with NodeId-based identity
    public Dictionary<string, ApprovedGuestInfo> ApprovedGuests { get; set; } = new();
    
    // NEW: Runtime IP address tracking (NodeId → Current IP)
    public Dictionary<string, string> PeerIpAddresses { get; set; } = new();

    // ── LEGACY: Kept for migration only, do not use in new code ──
    [Obsolete("Use ApprovedGuests instead")]
    public Dictionary<string, string> PeerUsernames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    
    [Obsolete("Use ApprovedGuests instead")]
    public Dictionary<string, string> PeerPublicKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ── Auth State ──
    public bool IsLoggedIn { get; set; }
    public bool IsAppShieldEnabled { get; set; }
    public bool HasSeenAppShieldOnboarding { get; set; }

    // ── Linux App Shield (PIN) ──
    public string LinuxAppShieldPinSalt { get; set; } = "";
    public string LinuxAppShieldPinHash { get; set; } = "";
    public int LinuxAppShieldPinIterations { get; set; } = 120000;

    // ── Windows App Shield (PIN fallback) ──
    public string WindowsAppShieldPinSalt { get; set; } = "";
    public string WindowsAppShieldPinHash { get; set; } = "";
    public int WindowsAppShieldPinIterations { get; set; } = 120000;
}

/// <summary>
/// Represents an approved guest device in the trust store.
/// Keyed by NodeId (persistent identity) instead of IP address (ephemeral).
/// </summary>
public class ApprovedGuestInfo
{
    public string NodeId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string LastKnownIp { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; }
}

public class HotkeyData
{
    public string ActionName { get; set; } = "";
    public string KeyGesture { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
}
