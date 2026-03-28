using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using EchoLink.ViewModels;
using EchoLink.Views;
using EchoLink.Services;
using System;
using System.Threading;

namespace EchoLink;

public partial class App : Application
{
    // Global reference for Android Activity to allow bridge to start/stop services
    public static object? AndroidActivityInstance { get; set; }

    private readonly LoggingService _log = LoggingService.Instance;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        DisableAvaloniaDataAnnotationValidation();

        if (ApplicationLifetime is IActivatableLifetime activatableLifetime)
        {
            activatableLifetime.Activated += async (sender, args) =>
            {
                if (args.Kind == ActivationKind.OpenUri && args is ProtocolActivatedEventArgs protocolArgs)
                {
                    var uri = protocolArgs.Uri;
                    _log.Info($"[DeepLink] Received URI: {uri}");
                    
                    if (uri.Scheme == "echolink" && uri.Host == "login")
                    {
                        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                        var authKey = query["authkey"];
                        
                        if (!string.IsNullOrEmpty(authKey))
                        {
                            _log.Info($"[DeepLink] Captured Pre-Auth Key. Logging in...");
                            await PerformDeepLinkLogin(authKey);
                        }
                    }
                }
            };
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            // Show a visible startup window immediately so desktop mode never starts headless.
            var loadingWindow = new Avalonia.Controls.Window
            {
                Title = "EchoLink",
                Width = 420,
                Height = 240,
                CanResize = false,
                Content = new LoadingView(),
            };
            desktop.MainWindow = loadingWindow;
            loadingWindow.Show();

            // Hook cleanup
            desktop.Exit += async (_, _) =>
            {
                await ClipboardSyncService.Instance.StopAsync();
                await AudioStreamingService.Instance.StopAllAsync();
                EchoLink.Services.UnifiedProtocol.UnifiedProtocolService.Instance.StopServer();
                
                if (TailscaleService.Instance.IsEphemeralSession)
                {
                    await TailscaleService.Instance.LogoutAsync();
                }
                else
                {
                    TailscaleService.Instance.StopDaemon();
                }
            };

            // Check auth state asynchronously, then show the right window
            _ = InitializeAppAsync(desktop);
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // Show loading view immediately to avoid blank white screen
            singleView.MainView = new LoadingView();
            _ = InitializeAppAsync(singleView);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeAppAsync(object lifetime)
    {
        try
        {
            _log.Info("[Startup] Initializing application...");

            var settings = SettingsService.Instance.Load();
            if (settings.IsLoggedIn)
            {
                _log.Info("[Startup] User was logged in. Auto-starting Tailscale daemon...");
                using var daemonCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await TailscaleService.Instance.StartDaemonAsync(null!, false, daemonCts.Token);
                _log.Info("[Startup] Tailscale daemon startup step finished.");
            }

            // Give the service/daemon time to initialize
            _log.Info("[Startup] Waiting briefly for daemon stabilization...");
            await Task.Delay(2000);

            _log.Info("[Startup] Checking connection status...");
            bool running = await TailscaleService.Instance.TryBringUpAsync(TimeSpan.FromSeconds(10));

            using var stateCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            string state = await TailscaleService.Instance.GetBackendStateAsync(stateCts.Token);

            // On Android, "TryBringUpAsync" just waits for the daemon state.
            // If it returns true OR the state is already Running, we are good to go.
            _log.Info($"[Startup] Running={running}, State={state}");

            if (running || state == "Running")
            {
                _log.Info("[Startup] Authenticated. Opening Dashboard.");
                NavigateToMain(lifetime);
            }
            else
            {
                _log.Info("[Startup] Not authenticated or transition needed. Opening Login.");
                NavigateToLogin(lifetime);
            }
        }
        catch (OperationCanceledException)
        {
            _log.Warning("[Startup] Initialization timed out. Opening Login.");
            NavigateToLogin(lifetime);
        }
        catch (Exception ex)
        {
            _log.Error($"[Startup] Initialization failed: {ex}");
            NavigateToLogin(lifetime);
        }
    }

    private void NavigateToMain(object lifetime)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            var vm = new MainWindowViewModel();
            if (lifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var oldMain = desktop.MainWindow;
                var win = new MainWindow { DataContext = vm };
                desktop.MainWindow = win;
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;
                win.Show();
                oldMain?.Close();
            }
            else if (lifetime is ISingleViewApplicationLifetime singleView)
            {
                singleView.MainView = new MainView { DataContext = vm };
            }
        });
    }

    private void NavigateToLogin(object lifetime)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            var vm = new LoginViewModel();
            vm.LoginSucceeded += () => NavigateToMain(lifetime);

            if (lifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var oldMain = desktop.MainWindow;
                var win = new LoginWindow { DataContext = vm };
                desktop.MainWindow = win;
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;
                win.Show();
                oldMain?.Close();
            }
            else if (lifetime is ISingleViewApplicationLifetime singleView)
            {
                singleView.MainView = new LoginView { DataContext = vm };
            }
        });
    }

    public async Task HandleDeepLink(Uri uri)
    {
        _log.Info($"[DeepLink] Manual handler received URI: {uri}");
        if (uri.Scheme == "echolink" && uri.Host == "login")
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var authKey = query["authkey"];
            
            if (!string.IsNullOrEmpty(authKey))
            {
                 _log.Info($"[DeepLink] Captured Pre-Auth Key. Logging in...");
                 await PerformDeepLinkLogin(authKey);
            }
        }
    }

    private async Task PerformDeepLinkLogin(string authKey)
    {
        try
        {
            _log.Info("[DeepLink] Starting Tailscale daemon with key...");
            // Ensure we are on UI thread if needed, but StartDaemonAsync is async.
            // NavigateToMain needs to be on UI thread (it handles that internally).

            await TailscaleService.Instance.StartDaemonAsync(authKey, false);

            var settings = SettingsService.Instance.Load();
            settings.IsLoggedIn = true;
            SettingsService.Instance.Save(settings);

            _log.Info("[DeepLink] Login successful. Navigating to Dashboard.");
            if (ApplicationLifetime != null)
            {
                NavigateToMain(ApplicationLifetime);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[DeepLink] Login failed: {ex.Message}");
        }
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
