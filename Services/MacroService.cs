using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using EchoLink.Models;

namespace EchoLink.Services;

public class MacroService
{
    public static readonly MacroService Instance = new();

    private readonly LoggingService _log = LoggingService.Instance;
    private readonly string _macrosDir;
    private readonly string _macrosPath;
    private FileSystemWatcher? _watcher;

    public event Action? MacrosChanged;

    private MacroService()
    {
        _macrosDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EchoLink", "Macros");
        _macrosPath = Path.Combine(_macrosDir, "macros.json");
        Directory.CreateDirectory(_macrosDir);
        StartWatcher();
    }

    public List<MacroButton> Load()
    {
        if (!File.Exists(_macrosPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<MacroButton>>(File.ReadAllText(_macrosPath)) ?? [];
        }
        catch (Exception ex)
        {
            _log.Error($"[Macros] Load failed: {ex.Message}");
            return [];
        }
    }

    public void Save(List<MacroButton> macros)
    {
        try
        {
            File.WriteAllText(_macrosPath, JsonSerializer.Serialize(macros,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _log.Error($"[Macros] Save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies macros.json to /EchoLink/Inbox/Macros/macros.json on all paired online peers via SFTP.
    /// </summary>
    public async Task SyncToMeshAsync(List<MacroButton> macros, IEnumerable<Device> peers)
    {
        var settings = SettingsService.Instance.Load();
        var pairingService = new SshPairingService(TailscaleService.Instance);
        await pairingService.EnsureKeyPairAsync();

        var json  = JsonSerializer.Serialize(macros, new JsonSerializerOptions { WriteIndented = true });
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var sftp  = new SftpService();

        foreach (var peer in peers.Where(p => p.IsOnline && !p.IsSelf))
        {
            if (!settings.PeerUsernames.TryGetValue(peer.IpAddress, out var username))
                continue;

            try
            {
                using var ms = new MemoryStream(bytes);
                int port = peer.Os?.Contains("android", StringComparison.OrdinalIgnoreCase) == true ? 2222 : 22;
                await sftp.UploadStreamAsync(peer.IpAddress, username, pairingService.PrivateKeyPath,
                    ms, "macros.json", "EchoLink/Inbox/Macros/macros.json",
                    (_, _) => { }, port);
                _log.Info($"[Macros] Synced macros to {peer.Name}");
            }
            catch (Exception ex)
            {
                _log.Warning($"[Macros] Sync to {peer.Name} failed: {ex.Message}");
            }
        }
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(_macrosDir, "macros.json")
        {
            NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += (_, _) => MacrosChanged?.Invoke();
    }
}
