using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class FileTransferViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;
    private readonly SftpService _sftp = new();

    // ── Upload state ────────────────────────────────────────────────────
    [ObservableProperty] private Device? _selectedTarget;
    [ObservableProperty] private string _selectedFileName = string.Empty;
    [ObservableProperty] private double _uploadProgress;
    [ObservableProperty] private bool _isUploading;
    [ObservableProperty] private string _statusText = "Drop a file or click to browse";
    [ObservableProperty] private bool _isDropZoneActive;
    [ObservableProperty] private bool _hasFileSelected;
    [ObservableProperty] private Avalonia.Platform.Storage.IStorageFile? _selectedStorageFile;

    // ── Browse / download state ─────────────────────────────────────────
    [ObservableProperty] private bool _isBrowsing;
    [ObservableProperty] private bool _isLoadingDirectory;
    [ObservableProperty] private string _currentRemotePath = "/";
    [ObservableProperty] private RemoteFileEntry? _pendingDownload;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _downloadStatusText = string.Empty;

    private CancellationTokenSource? _uploadCts;
    private CancellationTokenSource? _downloadCts;

    public ObservableCollection<Device> OnlineDevices { get; } = new();
    public ObservableCollection<RemoteFileEntry> RemoteFiles { get; } = new();

    public FileTransferViewModel()
    {
        _ = LoadDevicesAsync();
    }

    partial void OnSelectedTargetChanged(Device? value)
    {
        // Reset browse state when device changes
        IsBrowsing = false;
        RemoteFiles.Clear();
        CurrentRemotePath = "/";
    }

    // ── Device loading ───────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadDevicesAsync()
    {
        try
        {
            var (_, devices) = await TailscaleService.Instance.GetNetworkStatusAsync();
            OnlineDevices.Clear();
            foreach (var d in devices)
                if (d.IsOnline && !d.IsSelf)
                    OnlineDevices.Add(d);
        }
        catch (Exception ex)
        {
            _log.Error($"[FileTransfer] Load devices failed: {ex.Message}");
        }
    }

    // ── Remote file browser ──────────────────────────────────────────────

    [RelayCommand]
    private async Task BrowseRemoteAsync()
    {
        if (SelectedTarget is null) return;
        IsBrowsing = true;
        await LoadDirectoryAsync(CurrentRemotePath);
    }

    [RelayCommand]
    private async Task NavigateDirAsync(RemoteFileEntry entry)
    {
        if (!entry.IsDirectory) return;
        await LoadDirectoryAsync(entry.FullPath);
    }

    [RelayCommand]
    private async Task GoUpDirectoryAsync()
    {
        var trimmed = CurrentRemotePath.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        var parent = idx > 0 ? trimmed[..idx] : "/";
        await LoadDirectoryAsync(parent);
    }

    private async Task LoadDirectoryAsync(string path)
    {
        if (SelectedTarget is null) return;

        IsLoadingDirectory = true;
        RemoteFiles.Clear();
        StatusText = $"Listing {path}…";

        try
        {
            var pairingService = new SshPairingService(TailscaleService.Instance);
            await pairingService.EnsureKeyPairAsync();

            if (!IsTargetPaired(SelectedTarget))
            {
                StatusText = "❌ Not paired — click 🔗 Pair on the Dashboard first";
                IsLoadingDirectory = false;
                return;
            }

            string username = GetTargetUsername(SelectedTarget);
            int sshPort = IsAndroid(SelectedTarget) ? 2222 : 22;

            var entries = await _sftp.ListDirectoryAsync(
                SelectedTarget.IpAddress, username, pairingService.PrivateKeyPath,
                path, sshPort);

            CurrentRemotePath = path;
            RemoteFiles.Clear();
            foreach (var e in entries)
                RemoteFiles.Add(e);

            StatusText = $"{entries.Count} items in {path}";
        }
        catch (Renci.SshNet.Common.SftpPermissionDeniedException)
        {
            StatusText = $"❌ Permission denied — cannot read {path}";
            _log.Error($"[SFTP] Permission denied listing: {path}");
        }
        catch (Exception ex)
        {
            StatusText = $"❌ {ex.Message}";
            _log.Error($"[SFTP] ListDirectory failed: {ex.Message}");
        }
        finally
        {
            IsLoadingDirectory = false;
        }
    }

    // ── Download flow ────────────────────────────────────────────────────

    [RelayCommand]
    private void InitiateDownload(RemoteFileEntry entry)
    {
        if (entry.IsDirectory) return;
        PendingDownload = entry;
    }

    [RelayCommand]
    private void CancelPendingDownload()
    {
        PendingDownload = null;
    }

    [RelayCommand]
    private async Task ConfirmDownloadAsync()
    {
        if (PendingDownload is null || SelectedTarget is null) return;

        var entry = PendingDownload;
        PendingDownload = null;

        var downloadsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloadsDir);
        string localPath = Path.Combine(downloadsDir, entry.Name);

        IsDownloading = true;
        DownloadProgress = 0;
        DownloadStatusText = $"Connecting to {SelectedTarget.Name}…";
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        try
        {
            var pairingService = new SshPairingService(TailscaleService.Instance);
            await pairingService.EnsureKeyPairAsync();
            string username = GetTargetUsername(SelectedTarget);
            int sshPort = IsAndroid(SelectedTarget) ? 2222 : 22;

            await _sftp.DownloadFileAsync(
                SelectedTarget.IpAddress, username, pairingService.PrivateKeyPath,
                entry.FullPath, localPath,
                (downloaded, total) =>
                {
                    DownloadProgress = total > 0 ? (double)downloaded / total * 100.0 : 0;
                    DownloadStatusText = $"Downloading {entry.Name}… {DownloadProgress:F1}%";
                },
                sshPort, ct);

            DownloadStatusText = $"✔ Saved to {localPath}";
            _log.Info($"[SFTP] Downloaded {entry.Name} → {localPath}");
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText = "❌ Download cancelled.";
        }
        catch (Renci.SshNet.Common.SftpPermissionDeniedException ex)
        {
            // Remote folder/file access denied (e.g. root-owned path)
            DownloadStatusText = $"❌ Permission denied on remote: {ex.Message}";
            _log.Error($"[SFTP] Permission denied: {ex.Message}");
        }
        catch (IOException ex)
        {
            // File lock collision or out-of-disk-space — message is user-friendly from SftpService
            DownloadStatusText = $"❌ {ex.Message}";
            _log.Error($"[SFTP] IO error: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Network drop, SSH timeout, etc.
            DownloadStatusText = $"❌ Transfer failed: {ex.Message}";
            _log.Error($"[SFTP] Download failed: {ex.Message}");
        }
        finally
        {
            IsDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    private void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    // ── Upload flow (existing) ───────────────────────────────────────────

    public void SetFile(Avalonia.Platform.Storage.IStorageFile file)
    {
        SelectedStorageFile = file;
        SelectedFileName = file.Name;
        HasFileSelected = true;
        StatusText = "File ready to send. Click Send.";
        _log.Info($"File selected: {file.Name}");
    }

    public void SetFile(string filePath)
    {
        SelectedFileName = Path.GetFileName(filePath);
        HasFileSelected = true;
        StatusText = "File ready to send. Click Send.";
        _log.Info($"File selected (path): {SelectedFileName}");
    }

    [RelayCommand]
    public async Task SendFileAsync()
    {
        if (SelectedTarget is null) { StatusText = "Please select a target device first."; return; }
        if (SelectedStorageFile is null) { StatusText = "Please select a file first."; return; }
        await PerformSftpUploadAsync(SelectedStorageFile);
    }

    private async Task PerformSftpUploadAsync(Avalonia.Platform.Storage.IStorageFile file)
    {
        if (SelectedTarget is null) return;

        IsUploading = true;
        UploadProgress = 0;
        var fileName = file.Name;

        _uploadCts = new CancellationTokenSource();
        var ct = _uploadCts.Token;
        StatusText = $"Connecting to {SelectedTarget.Name}…";

        try
        {
            var pairingService = new SshPairingService(TailscaleService.Instance);
            await pairingService.EnsureKeyPairAsync();

            var pairingResult = await pairingService.RequestPairingAsync(
                SelectedTarget.IpAddress, Environment.MachineName, Environment.UserName);

            string targetUsername = pairingResult.TargetUsername ?? "root";
            if (pairingResult.Accepted && !string.IsNullOrWhiteSpace(pairingResult.TargetUsername))
            {
                var settings = SettingsService.Instance.Load();
                settings.PeerUsernames[SelectedTarget.IpAddress] = pairingResult.TargetUsername;
                SettingsService.Instance.Save(settings);
            }

            if (!pairingResult.Accepted)
                _log.Warning("[SFTP] Pairing rejected or timed out.");

            int sshPort = IsAndroid(SelectedTarget) ? 2222 : 22;

            using var fileStream = await file.OpenReadAsync();
            await _sftp.UploadStreamAsync(
                SelectedTarget.IpAddress, targetUsername, pairingService.PrivateKeyPath,
                fileStream, fileName, fileName,
                (uploaded, total) =>
                {
                    UploadProgress = total == 0 ? 0 : (double)uploaded / total * 100;
                    StatusText = $"Uploading {fileName}… {UploadProgress:F1}%";
                }, sshPort, ct);

            StatusText = $"✔ '{fileName}' sent to {SelectedTarget.Name}";
            _log.Info($"[SFTP] Upload complete: {fileName}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "❌ Upload cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"❌ Failed: {ex.Message}";
            _log.Error($"[SFTP] Upload error: {ex.Message}");
        }
        finally
        {
            IsUploading = false;
            _uploadCts?.Dispose();
            _uploadCts = null;
        }
    }

    [RelayCommand]
    private void CancelUpload()
    {
        if (IsUploading && _uploadCts != null)
            _uploadCts.Cancel();
        else
        {
            IsUploading = false;
            StatusText = "Upload cancelled.";
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static bool IsAndroid(Device d) =>
        d.Os?.Contains("android", StringComparison.OrdinalIgnoreCase) == true ||
        d.Name?.Contains("android", StringComparison.OrdinalIgnoreCase) == true;

    private static string GetTargetUsername(Device d)
    {
        var settings = SettingsService.Instance.Load();
        return settings.PeerUsernames.TryGetValue(d.IpAddress, out var u) && !string.IsNullOrWhiteSpace(u)
            ? u
            : Environment.UserName;
    }

    private static bool IsTargetPaired(Device d)
    {
        var settings = SettingsService.Instance.Load();
        return settings.PeerUsernames.TryGetValue(d.IpAddress, out var u) && !string.IsNullOrWhiteSpace(u);
    }
}
