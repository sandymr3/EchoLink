using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using EchoLink.Models;

namespace EchoLink.Services;

public class MacroService
{
    public static readonly MacroService Instance = new();

    private readonly LoggingService _log = LoggingService.Instance;

    // Primary storage: %LOCALAPPDATA%/EchoLink/Macros/macros.json
    private readonly string _macrosDir;
    private readonly string _macrosPath;

    // SFTP inbox: ~/EchoLink/Inbox/Macros/macros.json  (written by remote peers)
    private readonly string _inboxDir;
    private readonly string _inboxPath;

    private FileSystemWatcher? _mainWatcher;
    private FileSystemWatcher? _inboxWatcher;
    private Timer? _debounceTimer;
    private readonly object _debounceLock = new();

    public event Action? MacrosChanged;

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    private MacroService()
    {
        _macrosDir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EchoLink", "Macros");
        _macrosPath = Path.Combine(_macrosDir, "macros.json");

        _inboxDir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "EchoLink", "Inbox", "Macros");
        _inboxPath = Path.Combine(_inboxDir, "macros.json");

        Directory.CreateDirectory(_macrosDir);
        Directory.CreateDirectory(_inboxDir);

        StartWatchers();
    }

    // ── Public API ────────────────────────────────────────────────────────

    public List<MacroButton> Load()
    {
        if (!File.Exists(_macrosPath))
            return WriteAndReturnDefaults();

        return ReadWithRetry(_macrosPath);
    }

    public void Save(List<MacroButton> macros)
    {
        try
        {
            File.WriteAllText(_macrosPath, JsonSerializer.Serialize(macros, _jsonOpts));
        }
        catch (Exception ex)
        {
            _log.Error($"[Macros] Save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies macros.json to EchoLink/Inbox/Macros/macros.json on all paired online peers via SFTP.
    /// Failures for individual peers are caught and logged; other peers still receive the sync.
    /// </summary>
    public async Task SyncToMeshAsync(List<MacroButton> macros, IEnumerable<Device> peers)
    {
        var settings       = SettingsService.Instance.Load();
        var pairingService = new SshPairingService(TailscaleService.Instance);
        await pairingService.EnsureKeyPairAsync();

        var json  = JsonSerializer.Serialize(macros, _jsonOpts);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var sftp  = new SftpService();

        var tasks = peers
            .Where(p => p.IsOnline && !p.IsSelf)
            .Select(peer => SyncToPeerAsync(peer, bytes, sftp, pairingService, settings));

        await Task.WhenAll(tasks);
    }

    // ── Internal helpers ──────────────────────────────────────────────────

    private async Task SyncToPeerAsync(
        Device peer,
        byte[] payload,
        SftpService sftp,
        SshPairingService pairingService,
        SettingsData settings)
    {
        if (!settings.PeerUsernames.TryGetValue(peer.IpAddress, out var username))
            return;

        try
        {
            using var ms = new MemoryStream(payload);
            int port = peer.Os?.Contains("android", StringComparison.OrdinalIgnoreCase) == true ? 2222 : 22;
            await sftp.UploadStreamAsync(
                peer.IpAddress, username, pairingService.PrivateKeyPath,
                ms, "macros.json", "EchoLink/Inbox/Macros/macros.json",
                (_, _) => { }, port);
            _log.Info($"[Macros] Synced macros to {peer.Name}");
        }
        catch (Exception ex)
        {
            // Gracefully skip offline/unreachable peers
            _log.Warning($"[Macros] Sync to {peer.Name} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads macros.json with up to 3 retries to handle transient file-lock collisions
    /// (e.g., SFTP upload still writing while the watcher fires).
    /// </summary>
    private List<MacroButton> ReadWithRetry(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return JsonSerializer.Deserialize<List<MacroButton>>(stream) ?? [];
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                _log.Error($"[Macros] Load failed (attempt {attempt + 1}): {ex.Message}");
                return [];
            }
        }
        return [];
    }

    private List<MacroButton> WriteAndReturnDefaults()
    {
        var defaults = new List<MacroButton>
        {
            new() { Name = "Lock Screen",   Icon = "🔒", Command = "rundll32.exe user32.dll,LockWorkStation",        TargetOs = "Windows" },
            new() { Name = "Restart",       Icon = "🔄", Command = "shutdown /r /t 0",                               TargetOs = "Windows" },
            new() { Name = "Sleep",         Icon = "💤", Command = "rundll32.exe powrprof.dll,SetSuspendState 0,1,0", TargetOs = "Windows" },
            new() { Name = "Lock Screen",   Icon = "🔒", Command = "loginctl lock-session",                          TargetOs = "Linux"   },
            new() { Name = "Update System", Icon = "⬆️",  Command = "sudo apt-get update && sudo apt-get upgrade -y", TargetOs = "Linux"   },
            new() { Name = "Hello Mesh",    Icon = "👋", Command = "echo 'EchoLink macro works!'",                   TargetOs = null      },
        };
        Save(defaults);
        _log.Info("[Macros] Created default macros.json");
        return defaults;
    }

    // ── File watchers with debounce ───────────────────────────────────────

    private void StartWatchers()
    {
        _mainWatcher = CreateWatcher(_macrosDir, OnMainFileChanged);

        // Only watch inbox if the directory is different from main
        if (!_inboxDir.Equals(_macrosDir, StringComparison.OrdinalIgnoreCase))
            _inboxWatcher = CreateWatcher(_inboxDir, OnInboxFileChanged);
    }

    private static FileSystemWatcher CreateWatcher(string dir, FileSystemEventHandler handler)
    {
        var w = new FileSystemWatcher(dir, "macros.json")
        {
            NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        w.Changed += handler;
        w.Created += handler;
        return w;
    }

    private void OnMainFileChanged(object _, FileSystemEventArgs e) =>
        ScheduleDebounce(() => MacrosChanged?.Invoke());

    private void OnInboxFileChanged(object _, FileSystemEventArgs e) =>
        ScheduleDebounce(() =>
        {
            try
            {
                // Give SFTP time to fully release its file lock
                File.Copy(_inboxPath, _macrosPath, overwrite: true);
                _log.Info("[Macros] Imported macros from Inbox.");
                MacrosChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _log.Warning($"[Macros] Inbox import failed: {ex.Message}");
            }
        });

    private void ScheduleDebounce(Action callback)
    {
        lock (_debounceLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => callback(), null, 300, Timeout.Infinite);
        }
    }
}
