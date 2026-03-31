using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Services;
using EchoLink.Services.UnifiedProtocol;
using EchoLink.Services.SystemMonitor;
using System.Threading.Tasks;

namespace EchoLink.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty] private ViewModelBase? _currentPage;
    [ObservableProperty] private string _currentPageTitle = "Dashboard";
    [ObservableProperty] private bool _isSidebarOpen = true;

    [ObservableProperty] private bool _isSshInstalling;
    [ObservableProperty] private string _sshStatusText = "";

    [ObservableProperty] private bool _isSshReady;

    [ObservableProperty] private bool _showPairingRequest;
    [ObservableProperty] private string _pairingRequestText = "";

    [ObservableProperty] private bool _isAppLocked;
    [ObservableProperty] private bool _isUnlockInProgress;
    [ObservableProperty] private string _appLockStatusText = "EchoLink is locked. Unlock to continue.";

    [ObservableProperty] private bool _showAppShieldOnboarding;
    [ObservableProperty] private string _appShieldOnboardingStatusText = "";

    private System.Threading.Tasks.TaskCompletionSource<bool>? _pairingTcs;
    private readonly ClipboardSyncService _clipboardSync = ClipboardSyncService.Instance;
    private bool _isSetupInitialized;

    private DashboardViewModel? _dashboard;
    private FileTransferViewModel? _fileTransfer;
    private ClipboardViewModel? _clipboard;
    private RemoteControlViewModel? _remoteControl;
    private DebugConsoleViewModel? _debugConsole;
    private SettingsViewModel? _settings;
    private SystemMonitorViewModel? _systemMonitor;
    private MacrosViewModel? _macros;

    public DashboardViewModel Dashboard => _dashboard ??= new DashboardViewModel();
    public FileTransferViewModel FileTransfer => _fileTransfer ??= new FileTransferViewModel();
    public ClipboardViewModel Clipboard => _clipboard ??= new ClipboardViewModel();
    public RemoteControlViewModel RemoteControl => _remoteControl ??= new RemoteControlViewModel();
    public DebugConsoleViewModel DebugConsole => _debugConsole ??= new DebugConsoleViewModel();
    public SettingsViewModel Settings => _settings ??= new SettingsViewModel();
    public SystemMonitorViewModel SystemMonitor => _systemMonitor ??= new SystemMonitorViewModel();
    public MacrosViewModel Macros => _macros ??= new MacrosViewModel();

    /// <summary>
    /// Raised when logout completes so the hosting window can switch to LoginWindow.
    /// </summary>
    public event System.Action? LoggedOut;

    public MainWindowViewModel()
    {
        CurrentPageTitle = "App Locked";

        _ = InitializeAppShieldStateAsync();
    }

    private async Task InitializeAppShieldStateAsync()
    {
        var saved = SettingsService.Instance.Load();
        bool hasConfiguredPin = await AppShieldService.Instance.IsShieldConfiguredAsync();

        if (!hasConfiguredPin)
        {
            // If shield was previously enabled but pin material is missing, disable it safely.
            if (saved.IsAppShieldEnabled)
            {
                saved.IsAppShieldEnabled = false;
                SettingsService.Instance.Save(saved);
            }

            IsAppLocked = false;
            if (saved.IsLoggedIn)
            {
                ShowAppShieldOnboarding = true;
            }

            await InitializeSetupAsync();
            return;
        }

        IsAppLocked = saved.IsAppShieldEnabled;

        if (!IsAppLocked)
        {
            await InitializeSetupAsync();
        }
        else
        {
            _ = UnlockAppAsync();
        }
    }

    private async System.Threading.Tasks.Task InitializeSetupAsync()
    {
        if (_isSetupInitialized)
        {
            return;
        }

        _isSetupInitialized = true;
        CurrentPage = Dashboard;
        CurrentPageTitle = "Dashboard";

        Settings.SettingsChanged += () => Clipboard.RefreshFromSettings();
        _clipboardSync.ClipboardReceived += Clipboard.OnRemoteClipboardReceived;

        // Expose ports 22 and 44444 to the Tailnet so Windows can receive files in userspace-networking
        // On Android, this triggers the Go SSH/SFTP server to start.
        await TailscaleService.Instance.ExposeLocalPortsAsync();

        if (OperatingSystem.IsAndroid())
        {
            IsSshReady = true; // Go bridge handles SSH internally
        }
        else
        {
            // One-time SSH Setup
            IsSshInstalling = true;
            SshStatusText = "Checking SSH Server...";
            bool isSshInstalled = await SshSetupService.IsSshServerInstalledAsync();
            if (!isSshInstalled)
            {
                SshStatusText = "Installing SSH Server (Please accept UAC prompt if asked)...";
                LoggingService.Instance.Info("SSH Server not found. Attempting to install...");
                isSshInstalled = await SshSetupService.InstallAndStartSshServerAsync();
            }
            IsSshInstalling = false;
            IsSshReady = isSshInstalled;
        }

        // Start listening for key exchanges
        var pairingService = new SshPairingService(TailscaleService.Instance);
        await pairingService.EnsureKeyPairAsync();
        pairingService.StartListening(async (hostname, publicKey) =>
        {
            // Prompt user via UI
            return await PromptUserForPairingAsync(hostname);
        });

        // Start clipboard sync (StartAsync is idempotent and returns if already running)
        await _clipboardSync.StartAsync();
        
        // Initialize unified protocol handlers
        RemoteControlService.Instance.InitializeUnifiedProtocol();
        AudioStreamingService.Instance.InitializeUnifiedProtocol();
        ClipboardSyncService.Instance.InitializeUnifiedProtocol();
        SystemMonitorService.Instance.InitializeUnifiedProtocol();
        MacroService.Instance.InitializeUnifiedProtocol();
        UnifiedProtocolService.Instance.StartServer();
    }

    private async System.Threading.Tasks.Task<bool> PromptUserForPairingAsync(string hostname)
    {
        // Must be on UI thread or simply awaited since it's a notification
        // Reset TCS
        _pairingTcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        
        // Setup UI state
        PairingRequestText = $"{hostname} wants to pair for secure file transfers. Accept?";
        ShowPairingRequest = true;

        // Await user action
        bool result = await _pairingTcs.Task;
        
        // Hide UI
        ShowPairingRequest = false;
        return result;
    }

    [RelayCommand]
    private void AcceptPairing() => _pairingTcs?.TrySetResult(true);

    [RelayCommand]
    private void RejectPairing() => _pairingTcs?.TrySetResult(false);

    [RelayCommand]
    private async Task UnlockAppAsync()
    {
        if (!IsAppLocked || IsUnlockInProgress)
        {
            return;
        }

        IsUnlockInProgress = true;
        AppLockStatusText = "Authenticating...";

        bool unlocked = await AppShieldService.Instance.PromptUnlockAsync("Unlock EchoLink");
        if (!unlocked)
        {
            AppLockStatusText = "Unlock failed or canceled.";
            IsUnlockInProgress = false;
            return;
        }

        IsAppLocked = false;
        AppLockStatusText = string.Empty;
        IsUnlockInProgress = false;
        await InitializeSetupAsync();
    }

    [RelayCommand]
    private async Task SetupAppShieldAsync()
    {
        AppShieldOnboardingStatusText = "Setting up App Shield...";
        await AppShieldService.Instance.SetupShieldAsync();

        bool configured = await AppShieldService.Instance.IsShieldConfiguredAsync();
        var settings = SettingsService.Instance.Load();
        settings.IsAppShieldEnabled = configured;
        settings.HasSeenAppShieldOnboarding = configured;
        SettingsService.Instance.Save(settings);

        if (configured)
        {
            AppShieldOnboardingStatusText = "App Shield enabled.";
            await Task.Delay(500);
            ShowAppShieldOnboarding = false;
        }
        else
        {
            AppShieldOnboardingStatusText = "PIN setup was canceled or failed. Please try again.";
        }
    }

    [RelayCommand]
    private void SkipAppShieldSetup()
    {
        var settings = SettingsService.Instance.Load();
        settings.IsAppShieldEnabled = false;
        settings.HasSeenAppShieldOnboarding = true;
        SettingsService.Instance.Save(settings);
        ShowAppShieldOnboarding = false;
    }

    [RelayCommand] private void NavigateDashboard()      => Navigate(Dashboard, "Dashboard");
    [RelayCommand] private void NavigateFileTransfer()   => Navigate(FileTransfer, "File Transfer");
    [RelayCommand] private void NavigateClipboard()      => Navigate(Clipboard, "Clipboard Hub");
    [RelayCommand] private void NavigateRemoteControl()  => Navigate(RemoteControl, "Remote Control");
    [RelayCommand] private void NavigateDebugConsole()   => Navigate(DebugConsole, "Debug Console");
    [RelayCommand] private void NavigateSettings()       => Navigate(Settings, "Settings");
    [RelayCommand] private void NavigateSystemMonitor()  => Navigate(SystemMonitor, "System Monitor");
    [RelayCommand] private void NavigateMacros()         => Navigate(Macros, "Macro Buttons");

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    [RelayCommand]
    private async System.Threading.Tasks.Task LogoutAsync()
    {
        await _clipboardSync.StopAsync();
        await AudioStreamingService.Instance.StopAllAsync();
        UnifiedProtocolService.Instance.StopServer();
        await TailscaleService.Instance.LogoutAsync();
        LoggedOut?.Invoke();
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ExitAsync()
    {
        // Perform cleanup
        await _clipboardSync.StopAsync();
        await AudioStreamingService.Instance.StopAllAsync();
        UnifiedProtocolService.Instance.StopServer();

        if (TailscaleService.Instance.IsEphemeralSession)
        {
            await TailscaleService.Instance.LogoutAsync();
        }
        else
        {
            TailscaleService.Instance.StopDaemon();
        }

        // Shutdown the application
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void Navigate(ViewModelBase vm, string title)
    {
        if (IsAppLocked || !_isSetupInitialized)
        {
            return;
        }

        CurrentPage      = vm;
        CurrentPageTitle = title;
    }
}
