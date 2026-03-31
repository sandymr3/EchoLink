using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EchoLink.Models;

namespace EchoLink.Services;

/// <summary>
/// Centralized device discovery and caching service.
/// Provides consistent device filtering across all features.
///
/// Filtering Rules:
/// - Devices from the same account (matching UserId) are always included → Ecosystem section
/// - Explicitly paired devices (in TrustStore/ApprovedGuests) are included → Guests section
/// - Devices from other accounts that aren't paired are excluded → Dropped as "Alien"
///
/// Note: Device identity is based on NodeId (persistent), not IP address (ephemeral).
/// Offline approved guests are injected by TailScaleService to remain visible.
/// </summary>
public class DeviceDiscoveryService
{
    private static readonly Lazy<DeviceDiscoveryService> _instance = new(() => new DeviceDiscoveryService());
    public static DeviceDiscoveryService Instance => _instance.Value;

    private readonly LoggingService _log = LoggingService.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;

    private List<Device> _cachedDevices = new();
    private string _selfUserId = "";
    private string _selfIpAddress = "";
    private readonly object _lock = new();
    
    // Debouncing to prevent infinite refresh loops
    private DateTime _lastRefreshTime = DateTime.MinValue;
    private readonly TimeSpan _refreshDebounce = TimeSpan.FromSeconds(2);
    private bool _isRefreshing;

    /// <summary>
    /// Event fired when device list changes.
    /// ViewModels can subscribe to this instead of polling.
    /// </summary>
    public event Action? DeviceListChanged;

    /// <summary>
    /// The User ID of the local device (from Tailscale account).
    /// Used for filtering devices belonging to the same account.
    /// </summary>
    public string SelfUserId => _selfUserId;

    /// <summary>
    /// The Tailscale IP address of the local device.
    /// </summary>
    public string SelfIpAddress => _selfIpAddress;

    /// <summary>
    /// Gets all cached devices (includes self, unfiltered).
    /// For most use cases, prefer GetDevicesForFeature() or GetPeerDevices().
    /// </summary>
    public IReadOnlyList<Device> CachedDevices
    {
        get
        {
            lock (_lock)
            {
                return new List<Device>(_cachedDevices).AsReadOnly();
            }
        }
    }

    /// <summary>
    /// Gets the self device from the cache.
    /// </summary>
    public Device? GetSelfDevice()
    {
        lock (_lock)
        {
            return _cachedDevices.FirstOrDefault(d => d.IsSelf);
        }
    }

    /// <summary>
    /// Gets all peer devices (excludes self) that are eligible for features.
    /// In the new architecture, all cached peers are eligible (Ecosystem or explicitly paired Guests).
    /// </summary>
    public List<Device> GetPeerDevices()
    {
        lock (_lock)
        {
            return _cachedDevices
                .Where(d => !d.IsSelf)
                .ToList();
        }
    }

    /// <summary>
    /// Gets online peer devices eligible for features.
    /// Same filtering as GetPeerDevices(), plus IsOnline check.
    /// </summary>
    public List<Device> GetOnlinePeerDevices()
    {
        lock (_lock)
        {
            return _cachedDevices
                .Where(d => !d.IsSelf && d.IsOnline)
                .ToList();
        }
    }

    /// <summary>
    /// Gets all paired devices (excludes self).
    /// This includes both same-account devices and explicitly paired devices from other accounts.
    /// </summary>
    public List<Device> GetPairedDevices()
    {
        lock (_lock)
        {
            return _cachedDevices
                .Where(d => !d.IsSelf)
                .ToList();
        }
    }

    /// <summary>
    /// Refreshes the device list from Tailscale.
    /// Should be called on app startup and when user manually refreshes.
    /// Automatically fires DeviceListChanged event.
    /// 
    /// Debounced to prevent infinite refresh loops (max once per 2 seconds).
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // Debounce: prevent rapid-fire refreshes
        lock (_lock)
        {
            if (_isRefreshing)
            {
                _log.Debug("[DeviceDiscovery] Refresh already in progress, skipping");
                return;
            }
            
            var timeSinceLastRefresh = DateTime.UtcNow - _lastRefreshTime;
            if (timeSinceLastRefresh < _refreshDebounce)
            {
                _log.Debug($"[DeviceDiscovery] Debouncing refresh (only {(int)timeSinceLastRefresh.TotalMilliseconds}ms since last)");
                return;
            }
            
            _isRefreshing = true;
            _lastRefreshTime = DateTime.UtcNow;
        }

        try
        {
            var (selfIp, rawDevices) = await TailscaleService.Instance.GetNetworkStatusAsync(ct: ct);
            
            var processedDevices = new List<Device>();
            string selfUserId = (rawDevices.FirstOrDefault(d => d.IsSelf)?.UserId ?? "").Trim().ToLowerInvariant();
            
            foreach (var device in rawDevices)
            {
                if (device.IsSelf)
                {
                    device.Section = DeviceSection.Ecosystem;
                    device.IsPaired = true; // Implicitly paired
                    processedDevices.Add(device);
                }
                else if (!string.IsNullOrEmpty(selfUserId) && (device.UserId ?? "").Trim().ToLowerInvariant() == selfUserId)
                {
                    device.Section = DeviceSection.Ecosystem;
                    device.IsPaired = true; // Ecosystem devices are implicitly paired
                    processedDevices.Add(device);
                }
                else if (TrustStoreService.Instance.IsGuestApproved(device.NodeId))
                {
                    device.Section = DeviceSection.Guests;
                    device.IsPaired = true; // Explicitly paired guest
                    processedDevices.Add(device);
                    
                    // Update LastKnownIp for approved guests if IP changed
                    UpdateGuestLastKnownIp(device.NodeId, device.IpAddress);
                }
                else
                {
                    // Alien: ignored
                    _log.Debug($"[DeviceDiscovery] Dropped alien device: {device.Name} (NodeId: {device.NodeId})");
                }
            }

            lock (_lock)
            {
                _selfIpAddress = selfIp ?? "";
                _cachedDevices = processedDevices;

                // Capture self user ID for consistent filtering
                _selfUserId = selfUserId;

                // Log device discovery for debugging (only on first refresh or when count changes)
                _log.Info($"[DeviceDiscovery] Refreshed: {processedDevices.Count} total devices, SelfUserId={_selfUserId}, SelfIp={_selfIpAddress}");
                foreach (var device in processedDevices)
                {
                    _log.Debug($"[DeviceDiscovery]   - {device.Name} ({device.IpAddress}): UserId={device.UserId}, Section={device.Section}, IsOnline={device.IsOnline}, IsSelf={device.IsSelf}, NodeId={device.NodeId}");
                }
            }

            DeviceListChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error($"[DeviceDiscovery] Refresh failed: {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _isRefreshing = false;
            }
        }
    }
    
    /// <summary>
    /// Updates the LastKnownIp for an approved guest when their IP changes.
    /// This ensures phantom injection uses the most recent IP.
    /// </summary>
    private void UpdateGuestLastKnownIp(string nodeId, string newIp)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        
        var settings = _settings.Load();
        if (settings.ApprovedGuests.TryGetValue(nodeId, out var guest))
        {
            if (guest.LastKnownIp != newIp && !string.IsNullOrEmpty(newIp))
            {
                _log.Debug($"[DeviceDiscovery] Updated IP for {guest.Name}: {guest.LastKnownIp} → {newIp}");
                guest.LastKnownIp = newIp;
                _settings.Save(settings);
            }
        }
    }

    /// <summary>
    /// Checks if a device is eligible to be shown in feature lists.
    /// In the new architecture, all cached devices except Alien are eligible by definition.
    /// </summary>
    private bool IsEligibleDevice(Device device)
    {
        return device.IsPaired;
    }

    /// <summary>
    /// Gets devices specifically for clipboard sharing.
    /// Same as GetPeerDevices() but can be extended with clipboard-specific logic.
    /// </summary>
    public List<Device> GetClipboardShareDevices()
    {
        return GetPeerDevices();
    }

    /// <summary>
    /// Gets devices for remote control, macros, and system monitor.
    /// Returns both online and offline nodes, sorted securely.
    /// </summary>
    public List<Device> GetFeatureTargetDevices()
    {
        lock (_lock)
        {
            return _cachedDevices
                .Where(d => !d.IsSelf)
                .OrderByDescending(d => d.IsOnline)
                .ThenByDescending(d => d.LastSeen)
                .ToList();
        }
    }
}
