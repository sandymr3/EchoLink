using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;
using EchoLink.Services.UnifiedProtocol;

namespace EchoLink.ViewModels;

public partial class MacrosViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;
    private string? _editingMacroId;
    private MacroButton? _pendingDeleteMacro;

    // ── Macro collection (ALL macros – no OS filter on the grid) ─────────
    public ObservableCollection<MacroButton> Macros { get; } = new();
    public bool HasNoMacros => Macros.Count == 0;

    // ── Inline editor state ──────────────────────────────────────────────
    [ObservableProperty] private bool   _isEditing;
    [ObservableProperty] private string _editName      = "New Macro";
    [ObservableProperty] private string _editIcon      = "⚡";
    [ObservableProperty] private string _editCommand   = string.Empty;
    [ObservableProperty] private string _editTargetOs  = "All";
    [ObservableProperty] private bool   _editRequiresUi;
    [ObservableProperty] private bool   _editSyncToMesh;

    // ── Execution state ──────────────────────────────────────────────────
    [ObservableProperty] private string _statusText  = "Load macros or add new ones below";
    [ObservableProperty] private bool   _isSyncing;
    [ObservableProperty] private bool   _isExecuting;

    public ObservableCollection<Device> OnlineDevices { get; } = new();
    public IReadOnlyList<string> OsOptions { get; } = ["All", "Windows", "Linux"];

    // ── Dialog / Overlay State ───────────────────────────────────────────
    [ObservableProperty] private bool _isTargetDialogVisible;
    [ObservableProperty] private string _dialogMacroName = string.Empty;
    public ObservableCollection<Device> TargetDialogDevices { get; } = new();
    private TaskCompletionSource<Device?>? _targetSelectionTcs;

    [ObservableProperty] private bool _isErrorDialogVisible;
    [ObservableProperty] private string _errorDialogTitle = string.Empty;
    [ObservableProperty] private string _errorDialogMessage = string.Empty;

    [ObservableProperty] private bool _isOutputDialogVisible;
    [ObservableProperty] private string _outputDialogTitle = string.Empty;
    [ObservableProperty] private string _outputDialogMessage = string.Empty;

    [ObservableProperty] private bool _isToastVisible;
    [ObservableProperty] private string _toastMessage = string.Empty;
    [ObservableProperty] private string _toastIcon = string.Empty;

    [ObservableProperty] private bool _isMacroDetailsVisible;
    [ObservableProperty] private MacroButton? _selectedMacroDetails;

    [ObservableProperty] private bool _isDeleteConfirmVisible;
    [ObservableProperty] private string _deleteConfirmMessage = string.Empty;

    public MacrosViewModel()
    {
        Macros.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoMacros));
        MacroService.Instance.MacrosChanged += OnExternalMacrosChanged;

        LoadMacros();
        _ = LoadDevicesAsync();
        
        // Subscribe to device discovery events - just update UI from cached data
        DeviceDiscoveryService.Instance.DeviceListChanged += () =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => LoadDevicesAsync());
        };
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
            // Get feature target devices from DeviceDiscoveryService (already filtered and cached)
            // Dashboard controls the actual RefreshAsync call
            var devices = DeviceDiscoveryService.Instance.GetFeatureTargetDevices();
            
            var allTargets = new List<Device>();

            // Include self + all online eligible peers
            var selfDevice = DeviceDiscoveryService.Instance.GetSelfDevice();
            if (selfDevice != null)
                allTargets.Add(selfDevice);
                
            allTargets.AddRange(devices);

            UpdateDeviceCollection(OnlineDevices, allTargets);
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
        _editingMacroId = null;
        EditName      = "New Macro";
        EditIcon      = "⚡";
        EditCommand   = string.Empty;
        EditTargetOs  = "All";
        EditRequiresUi = false;
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

        MacroButton macro;
        bool updated = false;
        if (!string.IsNullOrWhiteSpace(_editingMacroId))
        {
            var existing = Macros.FirstOrDefault(m =>
                m.Id.Equals(_editingMacroId, StringComparison.Ordinal));
            if (existing != null)
            {
                macro = existing;
                updated = true;
            }
            else
            {
                macro = new MacroButton();
            }
        }
        else
        {
            macro = new MacroButton();
        }

        macro.Name       = EditName.Trim();
        macro.Icon       = EditIcon.Trim();
        macro.Command    = EditCommand.Trim();
        macro.TargetOs   = EditTargetOs == "All" ? null : EditTargetOs;
        macro.RequiresUI = EditRequiresUi;
        macro.SyncToMesh = EditSyncToMesh;

        if (!updated)
        {
            Macros.Add(macro);
        }

        MacroService.Instance.Save([.. Macros]);
        _editingMacroId = null;
        IsEditing  = false;
        StatusText = updated ? $"✔ '{macro.Name}' updated." : $"✔ '{macro.Name}' saved.";
        _log.Info(updated
            ? $"[Macros] Updated macro '{macro.Name}'"
            : $"[Macros] Added macro '{macro.Name}'");

        // Always broadcast to mesh (Global Macro Command Center: all peers share the same list)
        _ = SyncToMeshAsync();
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void OpenMacroDetails(MacroButton macro)
    {
        SelectedMacroDetails = macro;
        IsMacroDetailsVisible = true;
    }

    [RelayCommand]
    private void CloseMacroDetails()
    {
        IsMacroDetailsVisible = false;
        SelectedMacroDetails = null;
    }

    [RelayCommand]
    private void EditSelectedMacro()
    {
        if (SelectedMacroDetails is null)
        {
            return;
        }

        _editingMacroId = SelectedMacroDetails.Id;
        EditName = SelectedMacroDetails.Name;
        EditIcon = SelectedMacroDetails.Icon;
        EditCommand = SelectedMacroDetails.Command;
        EditTargetOs = string.IsNullOrWhiteSpace(SelectedMacroDetails.TargetOs)
            ? "All"
            : SelectedMacroDetails.TargetOs;
        EditRequiresUi = SelectedMacroDetails.RequiresUI;
        EditSyncToMesh = SelectedMacroDetails.SyncToMesh;

        IsMacroDetailsVisible = false;
        IsEditing = true;
    }

    [RelayCommand]
    private void RequestDeleteSelectedMacro()
    {
        if (SelectedMacroDetails is null)
        {
            return;
        }

        _pendingDeleteMacro = SelectedMacroDetails;
        DeleteConfirmMessage =
            $"Are you sure you want to delete '{SelectedMacroDetails.Name}'?";
        IsMacroDetailsVisible = false;
        IsDeleteConfirmVisible = true;
    }

    [RelayCommand]
    private void CancelDeleteMacro()
    {
        _pendingDeleteMacro = null;
        IsDeleteConfirmVisible = false;
        DeleteConfirmMessage = string.Empty;
    }

    [RelayCommand]
    private void ConfirmDeleteMacro()
    {
        if (_pendingDeleteMacro is null)
        {
            IsDeleteConfirmVisible = false;
            return;
        }

        var macro = _pendingDeleteMacro;
        _pendingDeleteMacro = null;
        IsDeleteConfirmVisible = false;
        DeleteConfirmMessage = string.Empty;

        Macros.Remove(macro);
        MacroService.Instance.Save([.. Macros]);
        StatusText = $"Deleted '{macro.Name}'";
        _log.Info($"[Macros] Deleted macro '{macro.Name}'");

        // Propagate the deletion to all peers
        _ = SyncToMeshAsync();
    }

    // ── Dialog Commands ──────────────────────────────────────────────────

    [RelayCommand]
    private void CancelTargetSelection()
    {
        IsTargetDialogVisible = false;
        _targetSelectionTcs?.TrySetResult(null);
    }

    [RelayCommand]
    private void SelectTarget(Device device)
    {
        IsTargetDialogVisible = false;
        _targetSelectionTcs?.TrySetResult(device);
    }

    [RelayCommand]
    private void CloseErrorDialog()
    {
        IsErrorDialogVisible = false;
    }

    [RelayCommand]
    private void CloseOutputDialog()
    {
        IsOutputDialogVisible = false;
    }

    private void ShowToastInternal(string message, string icon)
    {
        ToastMessage = message;
        ToastIcon = icon;
        IsToastVisible = true;
        
        // Auto-hide toast
        _ = Task.Delay(3000).ContinueWith(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsToastVisible = false);
        });
    }

    // ── Execution ────────────────────────────────────────────────────────

    private static string NormalizeOs(string? os)
    {
        if (string.IsNullOrWhiteSpace(os)) return string.Empty;
        if (os.Contains("windows", StringComparison.OrdinalIgnoreCase)) return "Windows";
        if (os.Contains("linux", StringComparison.OrdinalIgnoreCase)) return "Linux";
        if (os.Contains("android", StringComparison.OrdinalIgnoreCase)) return "Android";
        if (os.Contains("mac", StringComparison.OrdinalIgnoreCase) ||
            os.Contains("darwin", StringComparison.OrdinalIgnoreCase)) return "macOS";

        return os.Trim();
    }

    private static bool IsMacroTargetCompatible(string? macroTargetOs, string? deviceOs)
    {
        if (string.IsNullOrWhiteSpace(macroTargetOs) ||
            macroTargetOs.Equals("All", StringComparison.OrdinalIgnoreCase) ||
            macroTargetOs.Equals("Any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return NormalizeOs(macroTargetOs)
            .Equals(NormalizeOs(deviceOs), StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task ExecuteMacroAsync(MacroButton macro)
    {
        if (macro is null || IsExecuting) return;

        // ── Step 1: refresh device list, then show modal ──────────────────
        await LoadDevicesAsync();
        
        TargetDialogDevices.Clear();
        foreach (var d in OnlineDevices) TargetDialogDevices.Add(d);
        
        DialogMacroName = macro.Name;
        _targetSelectionTcs = new TaskCompletionSource<Device?>();
        IsTargetDialogVisible = true;

        Device? target = await _targetSelectionTcs.Task;
        if (target is null)
        {
            StatusText = "Cancelled.";
            return;
        }

        // ── Step 2: immediate in-memory OS compatibility check ───────────
        if (!IsMacroTargetCompatible(macro.TargetOs, target.Os))
        {
            var macroOs = string.IsNullOrWhiteSpace(macro.TargetOs) ? "Any" : macro.TargetOs;
            var deviceOs = string.IsNullOrWhiteSpace(target.Os) ? "Unknown" : target.Os;
            StatusText = $"⚠ OS mismatch: macro targets {macroOs}, device is {deviceOs}";
            ErrorDialogTitle = "OS Mismatch – Cannot Execute";
            ErrorDialogMessage =
                $"This macro targets '{macroOs}' but {target.Name} is running '{deviceOs}'.";
            IsErrorDialogVisible = true;
            return;
        }

        // ── Step 3: send MacroExecute and wait for MacroResult ───────────
        IsExecuting = true;
        FlashMacro(macro);
        StatusText = $"Executing... {macro.Name} → {target.Name}";

        try
        {
            // Keep caller on Unified Protocol only (no SSH path)
            bool connected = await UnifiedProtocolClient.Instance.ConnectAsync(
                target.IpAddress,
                UnifiedProtocolService.UnifiedPort,
                NetworkService.TailscaleSocks5Port,
                CancellationToken.None);

            if (!connected)
            {
                StatusText = $"❌ Could not connect to {target.Name} over Unified Protocol.";
                ErrorDialogTitle = "Execution Failed";
                ErrorDialogMessage =
                    $"Unable to connect to {target.Name} via Unified Protocol.";
                IsErrorDialogVisible = true;
                return;
            }

            var executionId = Guid.NewGuid();
            var request = new MacroExecutePayload
            {
                ExecutionId = executionId,
                CommandText = macro.Command,
                TargetOs = NormalizeOs(macro.TargetOs),
                RequiresUI = macro.RequiresUI
            };

            var requestJson = JsonSerializer.Serialize(request);
            var requestPayload = Encoding.UTF8.GetBytes(requestJson);
            var resultPayload = await UnifiedProtocolClient.Instance.SendRequestAndWaitForResponseAsync(
                UnifiedMessageType.MacroExecute,
                requestPayload,
                UnifiedMessageType.MacroResult,
                TimeSpan.FromSeconds(15),
                CancellationToken.None);

            if (resultPayload is null)
            {
                StatusText = "❌ Execution timed out after 15 seconds.";
                ErrorDialogTitle = "Execution Timeout";
                ErrorDialogMessage =
                    "No MacroResult was received within 15 seconds. The target may be offline or not yet running the new macro engine.";
                IsErrorDialogVisible = true;
                return;
            }

            var resultJson = Encoding.UTF8.GetString(resultPayload);
            var result = JsonSerializer.Deserialize<MacroResultPayload>(resultJson);
            if (result is null)
            {
                StatusText = "❌ Invalid response from target.";
                ErrorDialogTitle = "Execution Failed";
                ErrorDialogMessage =
                    "The target returned an invalid MacroResult payload.";
                IsErrorDialogVisible = true;
                return;
            }

            if (result.ExitCode != 0)
            {
                string err = string.IsNullOrWhiteSpace(result.OutputText)
                    ? $"Execution failed with exit code {result.ExitCode}."
                    : result.OutputText;
                StatusText = $"⚠ {err}";
                ErrorDialogTitle = "Execution Failed";
                ErrorDialogMessage = err;
                IsErrorDialogVisible = true;
                return;
            }

            if (macro.RequiresUI)
            {
                const string msg = "App opened";
                StatusText = $"✔ {msg}";
                ShowToastInternal(msg, "⚡");
                _log.Info($"[Macros] UI macro '{macro.Name}' launched on {target.Name} (ExecutionId={executionId}).");
            }
            else
            {
                string output = string.IsNullOrWhiteSpace(result.OutputText)
                    ? "(No output)"
                    : result.OutputText;
                StatusText = $"✔ Command finished on {target.Name}";
                OutputDialogTitle = "Command Output";
                OutputDialogMessage = output;
                IsOutputDialogVisible = true;
                _log.Info($"[Macros] Background macro '{macro.Name}' completed on {target.Name} (ExecutionId={executionId}).");
            }
        }
        catch (Exception ex)
        {
            string err = $"'{macro.Name}': {ex.Message}";
            StatusText = $"❌ {err}";
            _log.Error($"[Macros] Execute failed: {ex.Message}");
            ErrorDialogTitle = "Execution Failed";
            ErrorDialogMessage = ex.Message;
            IsErrorDialogVisible = true;
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

            var onlinePeers = OnlineDevices.Where(d => d.IsOnline && !d.IsSelf).ToList();
            if (onlinePeers.Count == 0)
            {
                StatusText = "⚠ No online peers found for mesh sync.";
                return;
            }

            var settings = SettingsService.Instance.Load();
            int pairedPeers = onlinePeers.Count(p =>
                !string.IsNullOrEmpty(p.NodeId) && settings.ApprovedGuests.ContainsKey(p.NodeId));
            if (pairedPeers == 0)
            {
                StatusText =
                    "⚠ No paired peers available for sync yet. Pair at least one peer first.";
                return;
            }

            await MacroService.Instance.SyncToMeshAsync([.. Macros], OnlineDevices);
            StatusText =
                $"✔ Macros synced to {pairedPeers} paired peer{(pairedPeers == 1 ? "" : "s")}";
            ShowToastInternal(
                $"Macros synced to {pairedPeers} paired peer{(pairedPeers == 1 ? "" : "s")}",
                "⇄");
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
