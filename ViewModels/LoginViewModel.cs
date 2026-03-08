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
    private CancellationTokenSource? _pollCts;

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
        StatusText = "Opening browser for Google sign-in...";

        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;

        try
        {
            // Fire-and-forget the login process (it opens a browser and blocks
            // until the auth handshake finishes or is cancelled).
            var loginTask = TailscaleService.Instance.LoginAsync(authUrl =>
            {
                _log.Info($"[Login] Auth URL: {authUrl}");
                OpenBrowser(authUrl);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    StatusText = "Waiting for Google authentication...");
            }, ct);

            // Poll backend state while the login process runs
            _ = PollForRunningAsync(ct);

            await loginTask;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Login cancelled.";
        }
        catch (Exception ex)
        {
            _log.Error($"[Login] {ex.Message}");
            StatusText = $"Login failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task PollForRunningAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct);
            var state = await TailscaleService.Instance.GetBackendStateAsync(ct);
            if (state == "Running")
            {
                _log.Info("[Login] BackendState is Running — login succeeded.");
                Avalonia.Threading.Dispatcher.UIThread.Post(() => LoginSucceeded?.Invoke());
                _pollCts?.Cancel();
                return;
            }
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // Fallback: on Linux, xdg-open
            System.Diagnostics.Process.Start("xdg-open", url);
        }
    }

    public void Cancel() => _pollCts?.Cancel();
}
