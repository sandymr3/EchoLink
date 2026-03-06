using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class FileTransferViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;

    [ObservableProperty] private Device? _selectedTarget;
    [ObservableProperty] private string _selectedFileName = string.Empty;
    [ObservableProperty] private double _uploadProgress;
    [ObservableProperty] private bool _isUploading;
    [ObservableProperty] private string _statusText = "Drop a file or click to browse";
    [ObservableProperty] private bool _isDropZoneActive;

    public ObservableCollection<Device> OnlineDevices { get; } =
    [
        new Device { Name = "Gautam-Phone",  IpAddress = "100.64.10.2", IsOnline = true, DeviceType = "Phone" },
        new Device { Name = "Gautam-Laptop", IpAddress = "100.64.10.3", IsOnline = true, DeviceType = "Laptop" },
    ];

    [RelayCommand]
    private async Task BrowseFileAsync()
    {
        _log.Info("Opening file picker...");
        // File dialog will be opened from the code-behind; VM handles the rest via SetFile().
        await Task.CompletedTask;
    }

    public async Task SetFileAndUploadAsync(string filePath)
    {
        SelectedFileName = System.IO.Path.GetFileName(filePath);
        _log.Info($"File selected: {SelectedFileName}");

        if (SelectedTarget is null)
        {
            StatusText = "Please select a target device first.";
            return;
        }

        await SimulateUploadAsync(filePath);
    }

    private async Task SimulateUploadAsync(string filePath)
    {
        IsUploading    = true;
        UploadProgress = 0;
        var fileName   = System.IO.Path.GetFileName(filePath);

        _log.Info($"Starting upload of '{fileName}' → {SelectedTarget?.Name}");

        for (int i = 1; i <= 100; i++)
        {
            UploadProgress = i;
            StatusText     = $"Uploading {fileName}… {i}%";
            await Task.Delay(30);
        }

        StatusText   = $"✔ '{fileName}' sent to {SelectedTarget!.Name}";
        IsUploading  = false;
        _log.Info($"Upload complete: {fileName}");
    }

    [RelayCommand]
    private void CancelUpload()
    {
        IsUploading  = false;
        StatusText   = "Upload cancelled.";
        UploadProgress = 0;
        _log.Warning("Upload cancelled by user.");
    }
}
