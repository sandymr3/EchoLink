using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class MacrosViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;
    private readonly OsProbeService _probe = new();

    // ── Macro collection (ALL macros – no OS filter on the grid) ─────────
    public ObservableCollection<MacroButton> Macros { get; } = new();
    public bool HasNoMacros => Macros.Count == 0;

    // ── Inline editor state ──────────────────────────────────────────────
    [ObservableProperty] private bool   _isEditing;
    [ObservableProperty] private string _editName      = "New Macro";
    [ObservableProperty] private string _editIcon      = "⚡";
    [ObservableProperty] private string _editCommand   = string.Empty;
    [ObservableProperty] private string _editTargetOs  = "All";
    [ObservableProperty] private bool   _editSyncToMesh;

    // ── Execution state ──────────────────────────────────────────────────
    [ObservableProperty] private string _statusText  = "Load macros or add new ones below";
    [ObservableProperty] private bool   _isSyncing;
    [ObservableProperty] private bool   _isExecuting;

    public ObservableCollection<Device> OnlineDevices { get; } = new();
    public IReadOnlyList<string> OsOptions { get; } = ["All", "Windows", "Linux"];

    // ── UI callback injected by the View ─────────────────────────────────
    /// <summary>
    /// Set by MacrosView.axaml.cs; opens the target-device modal and returns
    /// the device the user picked, or null if cancelled.
    /// </summary>
    public Func<MacroButton, Task<Device?>>? ShowTargetModalAsync { get; set; }

    /// <summary>
    /// Set by MacrosView.axaml.cs; opens a blocking error dialog (like a message box).
    /// </summary>
    public Func<string, string, Task>? ShowErrorDialogAsync { get; set; }

    /// <summary>
    /// Set by MacrosView.axaml.cs; shows a brief toast notification.
    /// </summary>
    public Action<string, string>? ShowToast { get; set; }

    public MacrosViewModel()
    {
        Macros.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoMacros));
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
    }

    private void OnExternalMacrosChanged()
    {
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

            // Include self + all online peers
            foreach (var d in devices)
                if (d.IsOnline)
                    OnlineDevices.Add(d);
        }
        catch (Exception ex)
        {
            _log.Error($"[Macros] Load devices failed: {ex.Message}");
        }
    }

    // ── Inline editor ────────────────────────────────────────────────────

    [RelayCommand]
    private void AddMacro()
    {
        EditName      = "New Macro";
        EditIcon      = "⚡";
        EditCommand   = string.Empty;
        EditTargetOs  = "All";
        EditSyncToMesh = false;
        IsEditing     = true;
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
        IsEditing  = false;
        StatusText = $"✔ '{macro.Name}' saved.";
        _log.Info($"[Macros] Added macro '{macro.Name}'");

        // Always broadcast to mesh (Global Macro Command Center: all peers share the same list)
        _ = SyncToMeshAsync();
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void DeleteMacro(MacroButton macro)
    {
        Macros.Remove(macro);
        MacroService.Instance.Save([.. Macros]);
        StatusText = $"Deleted '{macro.Name}'";
        _log.Info($"[Macros] Deleted macro '{macro.Name}'");

        // Propagate the deletion to all peers
        _ = SyncToMeshAsync();
    }

    // ── Execution ────────────────────────────────────────────────────────

    /// <summary>
    /// Called when the user clicks ▶ Run on any macro tile.
    /// Step 1: show target-selection modal (online devices only).
    /// Step 2: pre-flight OS check.
    /// Step 3: execute or reject.
    /// </summary>
    [RelayCommand]
    private async Task ExecuteMacroAsync(MacroButton macro)
    {
        if (macro is null || IsExecuting) return;
        if (ShowTargetModalAsync is null)
        {
            _log.Warning("[Macros] ShowTargetModalAsync not wired up.");
            return;
        }

        // ── Step 1: refresh device list, then show modal ──────────────────
        await LoadDevicesAsync();

        Device? target = await ShowTargetModalAsync(macro);
        if (target is null)
        {
            StatusText = "Cancelled.";
            return;
        }

        // ── Step 2 & 3: pre-flight + execute ─────────────────────────────
        IsExecuting = true;
        FlashMacro(macro);
        StatusText = $"⚡ Pre-flight check → {target.Name}…";

        try
        {
            if (target.IsSelf)
            {
                // Local: no SSH, no OS check
                await RunLocallyAsync(macro.Command);
                string msg = $"Command sent to {target.Name}";
                StatusText = $"✔ {msg}";
                ShowToast?.Invoke(msg, "⚡");
                _log.Info($"[Macros] Fired '{macro.Name}' locally.");
                return;
            }

            // Remote: load credentials
            var settings = SettingsService.Instance.Load();
            if (!settings.PeerUsernames.TryGetValue(target.IpAddress, out var username))
                username = Environment.UserName;

            var pairSvc = new SshPairingService(TailscaleService.Instance);
            await pairSvc.EnsureKeyPairAsync();

            var result = await _probe.ProbeAndExecuteAsync(
                target, macro.Command, macro.TargetOs, username, pairSvc.PrivateKeyPath);

            if (result.Success)
            {
                string msg = $"Command sent to {target.Name}";
                StatusText = $"✔ {msg}";
                ShowToast?.Invoke(msg, "⚡");
                _log.Info($"[Macros] Fired '{macro.Name}' → {target.Name}");
            }
            else
            {
                string err = result.Error ?? "Unknown error";
                StatusText = $"⚠ {err}";
                if (ShowErrorDialogAsync is not null)
                    await ShowErrorDialogAsync("OS Mismatch – Cannot Execute", err);
                else
                    _log.Warning($"[Macros] Blocked: {err}");
            }
        }
        catch (Exception ex)
        {
            string err = $"'{macro.Name}': {ex.Message}";
            StatusText = $"❌ {err}";
            _log.Error($"[Macros] Execute failed: {ex.Message}");
        }
        finally
        {
            IsExecuting = false;
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
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c {command}")
                    { UseShellExecute = true, CreateNoWindow = false });
            else
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("bash", $"-c \"{command}\"")
                    { UseShellExecute = false });
        });

    // ── Mesh sync ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SyncToMeshAsync()
    {
        IsSyncing  = true;
        StatusText = "Syncing macros to mesh…";
        try
        {
            // Refresh device list to catch newly connected peers
            await LoadDevicesAsync();
            await MacroService.Instance.SyncToMeshAsync([.. Macros], OnlineDevices);
            int peerCount = OnlineDevices.Count(d => d.IsOnline && !d.IsSelf);
            StatusText = $"✔ Macros synced to {peerCount} peer{(peerCount == 1 ? "" : "s")}";
            ShowToast?.Invoke($"Macros synced to {peerCount} peer{(peerCount == 1 ? "" : "s")}", "⇄");
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
