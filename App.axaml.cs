using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using EchoLink.ViewModels;
using EchoLink.Views;
using EchoLink.Services; // Ensure this is here to access TailscaleService

namespace EchoLink;

public partial class App : Application
{
    // 1. Declare the service at the class level so it stays alive
    private TailscaleService? _tailscaleService;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 2. Instantiate and start the background daemon
        _tailscaleService = new TailscaleService();
        _tailscaleService.StartDaemon();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };

            // 3. Hook into the desktop exit event to kill the daemon when the app closes
            desktop.Exit += (sender, args) =>
            {
                _tailscaleService?.StopDaemon();
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            DisableAvaloniaDataAnnotationValidation();
            singleView.MainView = new Views.MainView
            {
                DataContext = new MainWindowViewModel(),
            };
            
            // Note: Mobile/Android lifecycle handling for VpnService will be different later
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}