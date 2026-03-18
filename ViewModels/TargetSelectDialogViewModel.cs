using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Models;

namespace EchoLink.ViewModels;

public partial class TargetSelectDialogViewModel : ViewModelBase
{
    public string MacroName { get; }
    public ObservableCollection<Device> OnlineDevices { get; }
    public bool HasNoDevices => OnlineDevices.Count == 0;
    public int OnlineCount => OnlineDevices.Count;

    private Device? _selectedDevice;
    private readonly Action _cancelAction;

    public TargetSelectDialogViewModel(string macroName, IEnumerable<Device> onlineDevices, Action cancelAction)
    {
        MacroName = macroName;
        _cancelAction = cancelAction;
        OnlineDevices = new ObservableCollection<Device>(onlineDevices.Where(d => d.IsOnline));
    }

    public void SelectDevice(Device device)
    {
        _selectedDevice = device;
    }

    [RelayCommand]
    private void Cancel() => _cancelAction();
}
