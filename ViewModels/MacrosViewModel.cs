using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;
using Renci.SshNet;

namespace EchoLink.ViewModels;

public partial class MacrosViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;
    private const int Socks5Port = 1055;

    // ── Macro collections ────────────────────────────────────────────────
    public ObservableCollection<MacroButton> Macros         { get; } = new();
    /// <summary>OS-filtered view of Macros, driven by the selected ExecuteTarget.</summary>
    public ObservableCollection<MacroButton> FilteredMacros { get; } = new();
    public bool HasNoMacros => FilteredMacros.Count == 0;

    // ── Inline editor state ──────────────────────────────────────────────
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName    = "New Macro";
    [ObservableProperty] private string _editIcon    = "⚡";
    [ObservableProperty] private string _editCommand = string.Empty;
    [ObservableProperty] private string _editTargetOs = "All";
    [ObservableProperty] private bool _editSyncToMesh;

    // ── Execution state ──────────────────────────────────────────────────
    [ObservableProperty] private Device? _executeTarget;
    [ObservableProperty] private string _statusText = "Load macros or add new ones below";
    [ObservableProperty] private bool _isSyncing;

    public ObservableCollection<Device> OnlineDevices { get; } = new();
    public IReadOnlyList<string> OsOptions { get; } = ["All", "Windows", "Linux"];

    public MacrosViewModel()
    {
        FilteredMacros.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoMacros));
        MacroService.Instance.MacrosChanged += OnExternalMacrosChanged;
        LoadMacros();
        _ = LoadDevicesAsync();
    }

    // ── Loading ──────────────────────────────────────────────────────────

    private void LoadMacros()
    {
        Macros.Clear();
        foreach (var m in MacroService.Instance.Load())
            Macros.Add(m);
        RefreshFilter();
    }

    private void OnExternalMacrosChanged()
    {
        // Fired by FileSystemWatcher (background thread) – dispatch to UI
        Avalonia.Threading.Dispatcher.UIThread.Post(LoadMacros);
        _log.Info("[Macros] macros.json changed externally — reloaded.");
    }

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        try
        {
            var (_, devices) = await TailscaleService.Instance.GetNetworkStatusAsync();
            OnlineDevices.Clear();
            foreach (var d in devices)
                if (d.IsOnline)
                    OnlineDevices.Add(d);

            // Pre-select self so "Run Locally" is the default
            ExecuteTarget = OnlineDevices.FirstOrDefault(d => d.IsSelf);
        }
        catch (Exception ex)
        {
            _log.Error($"[Macros] Load devices failed: {ex.Message}");
        }
    }

    // ── OS Filtering ─────────────────────────────────────────────────────

    partial void OnExecuteTargetChanged(Device? value) => RefreshFilter();

    private void RefreshFilter()
    {
        FilteredMacros.Clear();
        string? targetOs = ExecuteTarget?.Os; // e.g. "windows", "linux", "darwin"

        foreach (var m in Macros)
        {
            if (m.TargetOs is null)
            {
                // No filter – runs anywhere
                FilteredMacros.Add(m);
                continue;
            }

            if (targetOs is null)
            {
                // No target selected – show everything
                FilteredMacros.Add(m);
                continue;
            }

            bool targetIsWindows = targetOs.Contains("windows", StringComparison.OrdinalIgnoreCase);
            bool targetIsLinux   = targetOs.Contains("linux",   StringComparison.OrdinalIgnoreCase);

            bool macroMatchesTarget =
                (m.TargetOs.Equals("Windows", StringComparison.OrdinalIgnoreCase) && targetIsWindows) ||
                (m.TargetOs.Equals("Linux",   StringComparison.OrdinalIgnoreCase) && targetIsLinux);

            if (macroMatchesTarget)
                FilteredMacros.Add(m);
        }
    }

    // ── Inline editor ────────────────────────────────────────────────────

    [RelayCommand]
    private void AddMacro()
    {
        EditName = "New Macro";
        EditIcon = "⚡";
        EditCommand = string.Empty;
        EditTargetOs = "All";
        EditSyncToMesh = false;
        IsEditing = true;
    }

    [RelayCommand]
    private void SaveMacro()
    {
        if (string.IsNullOrWhiteSpace(EditCommand))
        {
            StatusText = "⚠ Command cannot be empty.";
            return;
        }

        var macro = new MacroButton
        {
            Name       = EditName.Trim(),
            Icon       = EditIcon.Trim(),
            Command    = EditCommand.Trim(),
            TargetOs   = EditTargetOs == "All" ? null : EditTargetOs,
            SyncToMesh = EditSyncToMesh
        };

        Macros.Add(macro);
        MacroService.Instance.Save([.. Macros]);
        RefreshFilter();
        IsEditing = false;
        StatusText = $"✔ '{macro.Name}' saved.";
        _log.Info($"[Macros] Added macro '{macro.Name}'");

        // Auto-broadcast to mesh if the user opted in
        if (macro.SyncToMesh)
            _ = SyncToMeshAsync();
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void DeleteMacro(MacroButton macro)
    {
        Macros.Remove(macro);
        MacroService.Instance.Save([.. Macros]);
        RefreshFilter();
        StatusText = $"Deleted '{macro.Name}'";
        _log.Info($"[Macros] Deleted macro '{macro.Name}'");
    }

    // ── Execution ────────────────────────────────────────────────────────

    [RelayCommand]
    private void ExecuteMacroAsync(MacroButton macro)
    {
        if (macro is null) return;

        // Immediate visual feedback
        StatusText = $"⚡ '{macro.Name}' sent → {ExecuteTarget?.Name ?? "this device"}";
        FlashMacro(macro);

        // Fire the actual command as a background task (true fire-and-forget)
        _ = DispatchCommandAsync(macro);
    }

    private async Task DispatchCommandAsync(MacroButton macro)
    {
        try
        {
            if (ExecuteTarget is null || ExecuteTarget.IsSelf)
                await RunLocallyAsync(macro.Command);
            else
                await FireRemoteCommandAsync(macro.Command, ExecuteTarget);

            _log.Info($"[Macros] Fired '{macro.Name}' → {ExecuteTarget?.Name ?? "local"}");
        }
        catch (Exception ex)
        {
            _log.Error($"[Macros] Execute '{macro.Name}' failed: {ex.Message}");
            // Update status on UI thread; don't surface a crash dialog for bad commands
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => StatusText = $"⚠ '{macro.Name}': {ex.Message}");
        }
    }

    private static void FlashMacro(MacroButton macro)
    {
        macro.IsFlashing = true;
        _ = Task.Delay(1500).ContinueWith(_ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => macro.IsFlashing = false));
    }

    private static Task RunLocallyAsync(string command) =>
        Task.Run(() =>
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c {command}")
                    { UseShellExecute = true, CreateNoWindow = false });
            else
                Process.Start(new ProcessStartInfo("bash", $"-c \"{command}\"")
                    { UseShellExecute = false });
        });

    private async Task FireRemoteCommandAsync(string command, Device target)
    {
        var settings = SettingsService.Instance.Load();
        if (!settings.PeerUsernames.TryGetValue(target.IpAddress, out var username))
            username = Environment.UserName;

        var pairingService = new SshPairingService(TailscaleService.Instance);
        await pairingService.EnsureKeyPairAsync();

        int sshPort = target.Os?.Contains("android", StringComparison.OrdinalIgnoreCase) == true ? 2222 : 22;

        await Task.Run(() =>
        {
            var privateKeyFile = new PrivateKeyFile(pairingService.PrivateKeyPath);
            var connectionInfo = new ConnectionInfo(
                target.IpAddress, sshPort, username,
                ProxyTypes.Socks5, "127.0.0.1", Socks5Port, "", "",
                new PrivateKeyAuthenticationMethod(username, privateKeyFile));

            using var client = new SshClient(connectionInfo);
            client.Connect();
            // Send the command; do not read stdout/stderr (fire-and-forget at the channel level)
            using var cmd = client.CreateCommand(command);
            cmd.Execute();
            client.Disconnect();
        });
    }

    // ── Mesh sync ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SyncToMeshAsync()
    {
        IsSyncing = true;
        StatusText = "Syncing macros to mesh…";
        try
        {
            await MacroService.Instance.SyncToMeshAsync([.. Macros], OnlineDevices);
            int peerCount = OnlineDevices.Count(d => d.IsOnline && !d.IsSelf);
            StatusText = $"✔ Macros synced to {peerCount} peer{(peerCount == 1 ? "" : "s")}";
        }
        catch (Exception ex)
        {
            StatusText = $"❌ Sync failed: {ex.Message}";
            _log.Error($"[Macros] Sync failed: {ex.Message}");
        }
        finally
        {
            IsSyncing = false;
        }
    }
}
