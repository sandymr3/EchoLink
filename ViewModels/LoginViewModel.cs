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
            
            await Task.Delay(1500, ct);

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
            EchoLink.Services.Auth.AuthService authService;

            if (OperatingSystem.IsAndroid())
            {
                // Uses the AndroidBrowser instance registered in MainActivity.cs
                string redirectUri = "https://echo-link.app/oidc/callback";
                
                if (App.AndroidBrowserInstance == null)
                    throw new InvalidOperationException("AndroidBrowserInstance is not registered.");
                    
                authService = new EchoLink.Services.Auth.AuthService(App.AndroidBrowserInstance, redirectUri);
            }
            else
            {
                // Uses the Custom Loopback Listener on Port 5000 for Desktop
                string redirectUri = "http://127.0.0.1:5000/";
                authService = new EchoLink.Services.Auth.AuthService(new EchoLink.Services.Auth.SystemBrowser(5000), redirectUri);
            }

            // 1. Trigger the browser popup / Custom Tab and get the JWT
            var jwt = await authService.LoginAsync();

            if (string.IsNullOrEmpty(jwt))
            {
                StatusText = "Login returned empty token.";
                return;
            }

            StatusText = "Authenticating with EchoLink Control Plane...";
            _log.Info("[Login] Google JWT received successfully.");

            // 2. Exchange JWT with the Go Middleware for a Tailscale Pre-Auth Key
            var preAuthKey = await EchoLink.Services.Auth.MiddlewareClient.Instance.ExchangeJwtForPreAuthKeyAsync(jwt);
            
            if (string.IsNullOrEmpty(preAuthKey))
            {
                StatusText = "Failed to obtain Pre-Auth Key from Middleware.";
                return;
            }

            StatusText = "Connecting to the Tailnet mesh...";
            _log.Info("[Login] Pre-Auth Key obtained, starting Tailscale node...");

            // 3. Start Tailscale using the obtained Pre-Auth Key.
            // Ecosystem node (not ephemeral) for standard login
            await TailscaleService.Instance.StartDaemonAsync(preAuthKey, false, ct);

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
