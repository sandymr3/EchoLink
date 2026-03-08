using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty] private ViewModelBase _currentPage;
    [ObservableProperty] private string _currentPageTitle = "Dashboard";
    [ObservableProperty] private bool _isSidebarOpen = true;

    public DashboardViewModel     Dashboard     { get; } = new();
    public FileTransferViewModel  FileTransfer  { get; } = new();
    public ClipboardViewModel     Clipboard     { get; } = new();
    public RemoteControlViewModel RemoteControl { get; } = new();
    public DebugConsoleViewModel  DebugConsole  { get; } = new();

    /// <summary>
    /// Raised when logout completes so the hosting window can switch to LoginWindow.
    /// </summary>
    public event System.Action? LoggedOut;

    public MainWindowViewModel()
    {
        _currentPage = Dashboard;
    }

    [RelayCommand] private void NavigateDashboard()     => Navigate(Dashboard,     "Dashboard");
    [RelayCommand] private void NavigateFileTransfer()  => Navigate(FileTransfer,  "File Transfer");
    [RelayCommand] private void NavigateClipboard()     => Navigate(Clipboard,     "Clipboard Hub");
    [RelayCommand] private void NavigateRemoteControl() => Navigate(RemoteControl, "Remote Control");
    [RelayCommand] private void NavigateDebugConsole()  => Navigate(DebugConsole,  "Debug Console");

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    [RelayCommand]
    private async System.Threading.Tasks.Task LogoutAsync()
    {
        await TailscaleService.Instance.LogoutAsync();
        LoggedOut?.Invoke();
    }

    private void Navigate(ViewModelBase vm, string title)
    {
        CurrentPage      = vm;
        CurrentPageTitle = title;
    }
}
