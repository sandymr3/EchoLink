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

    /// <summary>
    /// Raised on the UI thread when authentication completes successfully.
    /// </summary>
    public event Action? LoginSucceeded;

    [RelayCommand]
    private async Task LoginWithGoogleAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusText = "Starting login...";

        _loginCts = new CancellationTokenSource();
        var ct = _loginCts.Token;

        try
        {
            // Reset the stderr-based Running flag so we can detect a fresh
            // transition after this login attempt.
            TailscaleService.Instance.ResetRunningState();

            // "tailscale up --login-server=..." handles both auth AND bringing
            // the VPN up.  Unlike "tailscale login", "up" stays connected to
            // the daemon until it reaches Running state, then exits with code 0.
            //
            // If auth is needed, it prints the same auth URL and waits for the
            // user to complete browser authentication.
            //
            // When "up" exits with code 0, the daemon IS in Running state.
            await TailscaleService.Instance.LoginAsync(authUrl =>
            {
                _log.Info($"[Login] Auth URL received: {authUrl}");
                OpenBrowser(authUrl);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    StatusText = "Browser opened — complete Google sign-in...");
            }, ct);

            // "tailscale up" exited with code 0 → daemon reached Running state.
            // The daemon may briefly lose Running after the CLI disconnects, but
            // MainWindow's periodic status checks will re-trigger the profile
            // switch and bring it back to Running.
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
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            try { System.Diagnostics.Process.Start("xdg-open", url); } catch { /* ignore */ }
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        _loginCts?.Cancel();
        _log.Info("[Login] Login cancelled by user.");
    }
}
