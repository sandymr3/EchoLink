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

namespace EchoLink;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Start the tailscale daemon
        TailscaleService.Instance.StartDaemon();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();

            // Show a temporary empty window while we check auth state
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            // Hook cleanup
            desktop.Exit += (_, _) => TailscaleService.Instance.StopDaemon();

            // Check auth state asynchronously, then show the right window
            _ = ShowStartupWindowAsync(desktop);
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            DisableAvaloniaDataAnnotationValidation();
            singleView.MainView = new Views.MainView
            {
                DataContext = new MainWindowViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task ShowStartupWindowAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Wait for tailscaled to be ready (up to 5 seconds)
        string state = "Unknown";
        // Phase 1: Wait for daemon to respond with any known state (up to 5s)
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(500);
            state = await TailscaleService.Instance.GetBackendStateAsync();
            if (state == "Running")
                break;
            if (state is "NeedsLogin" or "Stopped")
                break; // move to phase 2
        }

        // Phase 2: If we got NeedsLogin, the daemon may still be restoring
        // saved credentials from the state file (needs a network round-trip
        // to the coordination server). Give it a few more seconds.
        if (state == "NeedsLogin")
        {
            for (int i = 0; i < 8; i++) // up to 4 more seconds
            {
                await Task.Delay(500);
                state = await TailscaleService.Instance.GetBackendStateAsync();
                if (state == "Running")
                    break;
            }
        }

        if (state == "Running")
        {
            // Already authenticated — go straight to main window
            var mainWindow = new MainWindow { DataContext = new MainWindowViewModel() };
            desktop.MainWindow = mainWindow;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;
            mainWindow.Show();
        }
        else
        {
            // Need to login
            var loginWindow = new LoginWindow { DataContext = new LoginViewModel() };
            desktop.MainWindow = loginWindow;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnLastWindowClose;
            loginWindow.Show();
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