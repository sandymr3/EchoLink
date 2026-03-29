using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoLink.Services;

namespace EchoLink.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly LoggingService _log = LoggingService.Instance;
    private CancellationTokenSource? _loginCts;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = "Sign in to connect to EchoLink mesh";
    
    [ObservableProperty] private bool _isGuestMode;
    [ObservableProperty] private string _guestPin = "";

    /// <summary>
    /// Raised on the UI thread when authentication completes successfully.
    /// </summary>
    public event Action? LoginSucceeded;

    private async Task<bool> EnsureMeshReadyAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(75);
        var nextBringUpAttempt = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var fatal = TailscaleService.Instance.LastFatalStartupError;
            if (!string.IsNullOrWhiteSpace(fatal))
            {
                StatusText = fatal;
                _log.Error($"[Login] Fatal mesh startup error: {fatal}");
                return false;
            }

            var state = await TailscaleService.Instance.GetBackendStateAsync(ct);
            if (state == "Running")
            {
                return true;
            }

            StatusText = string.IsNullOrWhiteSpace(state) || state == "Unknown"
                ? "Connected account. Waiting for mesh to stabilize..."
                : $"Connected account. Mesh is starting ({state})...";

            // Busy networks can need repeated bring-up nudges while state transitions.
            if (DateTime.UtcNow >= nextBringUpAttempt &&
                (state == "Starting" || state == "NeedsLogin" || state == "NoState" || state == "Unknown"))
            {
                _ = await TailscaleService.Instance.TryBringUpAsync(TimeSpan.FromSeconds(8));
                nextBringUpAttempt = DateTime.UtcNow.AddSeconds(4);
            }

            await Task.Delay(1200, ct);
        }

        return false;
    }

    [RelayCommand]
    private void ToggleGuestMode()
    {
        IsGuestMode = !IsGuestMode;
        StatusText = IsGuestMode ? "Enter PIN to join temporarily" : "Sign in to connect to EchoLink mesh";
        GuestPin = "";
    }

    [RelayCommand]
    private async Task ConnectAsGuestAsync()
    {
        if (IsLoading || string.IsNullOrWhiteSpace(GuestPin)) return;
        
        IsLoading = true;
        StatusText = "Validating PIN...";
        
        _loginCts = new CancellationTokenSource();
        var ct = _loginCts.Token;

        try
        {
            var preAuthKey = await EchoLink.Services.Auth.MiddlewareClient.Instance.ClaimGuestPinAsync(GuestPin);
            
            if (string.IsNullOrEmpty(preAuthKey))
            {
                StatusText = "Invalid or expired PIN.";
                return;
            }

            StatusText = "Connecting to Tailnet as Guest...";
            _log.Info("[Login] Guest PIN accepted. Starting ephemeral Tailscale node...");

            // Start Tailscale as Ephemeral node
            await TailscaleService.Instance.StartDaemonAsync(preAuthKey, true, ct);

            var ready = await EnsureMeshReadyAsync(ct);
            if (!ready)
            {
                if (string.IsNullOrWhiteSpace(TailscaleService.Instance.LastFatalStartupError))
                {
                    StatusText = "Connected account, but mesh is still starting. Please wait a moment and retry.";
                }
                _log.Warning("[Login] Guest daemon did not reach Running state within extended startup window.");
                return;
            }

            _log.Info("[Login] Ephemeral node started.");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => LoginSucceeded?.Invoke());
        }
        catch (OperationCanceledException)
        {
            StatusText = "Connection cancelled.";
        }
        catch (Exception ex)
        {
            _log.Error($"[Login] Guest connection failed: {ex.Message}");
            StatusText = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoginWithGoogleAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusText = "Starting Google login...";

        _loginCts = new CancellationTokenSource();
        var ct = _loginCts.Token;

        try
        {
            var authService = new EchoLink.Services.Auth.AuthService();

            // 1. Trigger the browser login flow
            // On Android: Opens browser and returns null (waiting for deep link)
            // On PC: Opens browser, waits for localhost redirect, returns Auth Key
            var authKey = await authService.LoginAsync();

            if (OperatingSystem.IsAndroid())
            {
                // Android flow continues via Deep Link (App.axaml.cs)
                StatusText = "Please complete login in the browser...";
                IsLoading = false; // We can stop loading indicator or keep it? 
                // Better to stop it so user knows they need to switch apps.
                // But deep link will restart the app or resume it.
                return;
            }

            if (string.IsNullOrEmpty(authKey))
            {
                StatusText = "Login returned empty key.";
                return;
            }

            StatusText = "Connecting to the Tailnet mesh...";
            _log.Info("[Login] Pre-Auth Key obtained, starting Tailscale node...");

            // 2. Start Tailscale using the obtained Pre-Auth Key.
            // Ecosystem node (not ephemeral) for standard login
            await TailscaleService.Instance.StartDaemonAsync(authKey, false, ct);

            // Do not transition to main UI until backend is actually Running.
            // This avoids a false-success path where login moves forward while
            // tailscaled is still in NoState/Starting.
            var ready = await EnsureMeshReadyAsync(ct);
            if (!ready)
            {
                if (string.IsNullOrWhiteSpace(TailscaleService.Instance.LastFatalStartupError))
                {
                    StatusText = "Connected account, but mesh is still starting. Please wait a moment and retry.";
                }
                _log.Warning("[Login] Daemon did not reach Running state within extended startup window.");
                return;
            }

            // Persist the login state
            var settings = SettingsService.Instance.Load();
            settings.IsLoggedIn = true;
            SettingsService.Instance.Save(settings);

            _log.Info("[Login] 'tailscale up' succeeded — transitioning to main window.");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => LoginSucceeded?.Invoke());
        }
        catch (OperationCanceledException)
        {
            StatusText = "Login cancelled.";
        }
        catch (Exception ex)
        {
            _log.Error($"[Login] Unexpected error: {ex.Message}");
            StatusText = $"Login failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            // Primary attempt: Native shell commands are often more reliable for various DEs/environments
            if (OperatingSystem.IsWindows())
            {
                // cmd.exe /c start replaces UseShellExecute=true for published apps
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", $"/c start {url.Replace("&", "^&")}") { CreateNoWindow = true });
                return;
            }
            if (OperatingSystem.IsLinux())
            {
                System.Diagnostics.Process.Start("xdg-open", url);
                return;
            }
            if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", url);
                return;
            }

            // Fallback: Avalonia native launcher
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow);
                if (topLevel?.Launcher != null)
                {
                    _ = topLevel.Launcher.LaunchUriAsync(new Uri(url));
                    return;
                }
            }
            else if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime single && single.MainView != null)
            {
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(single.MainView);
                if (topLevel?.Launcher != null)
                {
                    _ = topLevel.Launcher.LaunchUriAsync(new Uri(url));
                    return;
                }
            }

            // Ultimate fallback
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"[Login] Failed to open browser: {ex.Message}");
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        _loginCts?.Cancel();
        _log.Info("[Login] Login cancelled by user.");
    }
}
