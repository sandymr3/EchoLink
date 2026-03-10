using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;

    // ── EchoBoard™ — Clipboard Sync Engine ──────────────────────────────
    [ObservableProperty] private bool _mirrorClipEnabled = true;
    [ObservableProperty] private bool _ghostPasteEnabled = true;
    [ObservableProperty] private bool _snapShareEnabled = true;
    [ObservableProperty] private int _clipboardHistoryLimit = 50;

    // ── General ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _launchOnStartup;
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private bool _showNotifications = true;

    // ── Hotkeys ─────────────────────────────────────────────────────────
    public ObservableCollection<HotkeyBinding> Hotkeys { get; } = [];

    // ── Status ──────────────────────────────────────────────────────────
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _showSaved;

    /// <summary>
    /// Fired when settings change so other ViewModels can react.
    /// </summary>
    public event Action? SettingsChanged;

    public SettingsViewModel()
    {
        LoadSettings();
    }

    private void LoadSettings()
    {
        var data = _settings.Load();

        MirrorClipEnabled = data.MirrorClipEnabled;
        GhostPasteEnabled = data.GhostPasteEnabled;
        SnapShareEnabled = data.SnapShareEnabled;
        ClipboardHistoryLimit = data.ClipboardHistoryLimit;

        LaunchOnStartup = data.LaunchOnStartup;
        MinimizeToTray = data.MinimizeToTray;
        ShowNotifications = data.ShowNotifications;

        // Build hotkey list from saved data, falling back to defaults
        Hotkeys.Clear();
        var defaults = GetDefaultHotkeys();
        foreach (var def in defaults)
        {
            var saved = data.Hotkeys.Find(h => h.ActionName == def.ActionName);
            if (saved is not null)
            {
                def.KeyGesture = saved.KeyGesture;
                def.IsEnabled = saved.IsEnabled;
            }
            Hotkeys.Add(def);
        }

        _log.Debug("Settings loaded.");
    }

    private static List<HotkeyBinding> GetDefaultHotkeys() =>
    [
        new("EchoShot",      "EchoShot — Push Clipboard",         "Ctrl+Shift+C"),
        new("SnapPull",      "SnapPull — Pull Latest Clip",       "Ctrl+Shift+V"),
        new("SyncToggle",    "Toggle MirrorClip Sync",            "Ctrl+Shift+S"),
        new("QuickTransfer", "QuickTransfer — Send File",         "Ctrl+Shift+F"),
        new("CommandDeck",   "CommandDeck — Dashboard",           "Ctrl+Shift+D"),
        new("WipeBoard",     "WipeBoard — Clear Clip History",    "Ctrl+Shift+X"),
        new("GhostLock",     "GhostLock — Lock Remote Screen",    "Ctrl+Shift+L"),
        new("PulsePing",     "PulsePing — Refresh Network",       "Ctrl+Shift+R"),
    ];

    [RelayCommand]
    private void SaveSettings()
    {
        var data = new SettingsData
        {
            MirrorClipEnabled = MirrorClipEnabled,
            GhostPasteEnabled = GhostPasteEnabled,
            SnapShareEnabled = SnapShareEnabled,
            ClipboardHistoryLimit = ClipboardHistoryLimit,

            LaunchOnStartup = LaunchOnStartup,
            MinimizeToTray = MinimizeToTray,
            ShowNotifications = ShowNotifications,

            Hotkeys = Hotkeys.Select(h => new HotkeyData
            {
                ActionName = h.ActionName,
                KeyGesture = h.KeyGesture,
                IsEnabled = h.IsEnabled
            }).ToList()
        };

        _settings.Save(data);
        StatusText = "Settings saved";
        ShowSaved = true;
        SettingsChanged?.Invoke();
        _log.Info("Settings saved successfully.");

        _ = HideSavedBadgeAsync();
    }

    private async Task HideSavedBadgeAsync()
    {
        await Task.Delay(2000);
        ShowSaved = false;
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        MirrorClipEnabled = true;
        GhostPasteEnabled = true;
        SnapShareEnabled = true;
        ClipboardHistoryLimit = 50;

        LaunchOnStartup = false;
        MinimizeToTray = true;
        ShowNotifications = true;

        Hotkeys.Clear();
        foreach (var h in GetDefaultHotkeys())
            Hotkeys.Add(h);

        StatusText = "Defaults restored — click Save to apply";
        _log.Info("Settings reset to defaults.");
    }
}
