using Avalonia.Controls;
using Avalonia.Interactivity;
using EchoLink.Services;
using EchoLink.ViewModels;

namespace EchoLink.Views;

public partial class MainWindow : Window
{
    private TrayService? _trayService;
    // Flag to tell the window when we ACTUALLY want to quit
    private bool _isForceClosing = false;

    public MainWindow()
    {
        InitializeComponent();

        // Subscribe to closing event
        Closing += Window_Closing;

        // Initialize tray service
        _trayService = new TrayService();
        _trayService.Initialize(this);
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // If we already decided to quit, let the window close normally
        if (_isForceClosing) return;

        // Check if user wants to minimize to tray instead of closing
        var settings = Services.SettingsService.Instance.Load();

        if (settings.MinimizeToTray)
        {
            // 1. INSTANTLY STOP THE WINDOW FROM CLOSING
            e.Cancel = true;

            // 2. Now it is safe to show your popup dialog
            string userChoice = await ShowCloseDialogAsync();

            // 3. Process the choice safely
            if (userChoice == "minimize")
            {
                // Vanish from the screen and taskbar
                this.Hide();
            }
            else if (userChoice == "exit")
            {
                // The user wants to quit. Set the flag and call Close() again.
                _isForceClosing = true;
                
                // Cleanup tray
                _trayService?.Dispose();
                
                // Force the app to shutdown
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            }
            // If "cancel", do nothing. The window stays open because we set e.Cancel = true.
        }
    }

    private async System.Threading.Tasks.Task<string> ShowCloseDialogAsync()
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<string>();

        var dialog = new Window
        {
            Title = "Close EchoLink?",
            Width = 400,
            Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur }
        };

        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 20
        };

        var titleLabel = new TextBlock
        {
            Text = "Close EchoLink?",
            FontSize = 18,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Foreground = Avalonia.Media.Brushes.White,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
        };

        var messageLabel = new TextBlock
        {
            Text = "Do you want to minimize to system tray or exit completely?",
            FontSize = 14,
            Foreground = Avalonia.Media.Brushes.LightGray,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 10, 0, 0)
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Spacing = 10,
            Margin = new Avalonia.Thickness(0, 20, 0, 0)
        };

        var minimizeButton = new Button
        {
            Content = "Minimize to Tray",
            Padding = new Avalonia.Thickness(20, 8),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0078D4")),
            Foreground = Avalonia.Media.Brushes.White,
            CornerRadius = new Avalonia.CornerRadius(4)
        };
        minimizeButton.Click += (s, e) =>
        {
            dialog.Close();
            tcs.SetResult("minimize");
        };

        var exitButton = new Button
        {
            Content = "Exit",
            Padding = new Avalonia.Thickness(20, 8),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D32F2F")),
            Foreground = Avalonia.Media.Brushes.White,
            CornerRadius = new Avalonia.CornerRadius(4)
        };
        exitButton.Click += (s, e) =>
        {
            dialog.Close();
            tcs.SetResult("exit");
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            Padding = new Avalonia.Thickness(20, 8),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3A3A3C")),
            Foreground = Avalonia.Media.Brushes.White,
            CornerRadius = new Avalonia.CornerRadius(4)
        };
        cancelButton.Click += (s, e) =>
        {
            dialog.Close();
            tcs.SetResult("cancel");
        };

        buttonPanel.Children.Add(minimizeButton);
        buttonPanel.Children.Add(exitButton);
        buttonPanel.Children.Add(cancelButton);

        panel.Children.Add(titleLabel);
        panel.Children.Add(messageLabel);
        panel.Children.Add(buttonPanel);

        var border = new Border
        {
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#252526")),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(20),
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3E3E42")),
            BorderThickness = new Avalonia.Thickness(1),
            Child = panel
        };

        dialog.Content = border;
        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MainWindowViewModel vm)
        {
            vm.LoggedOut += OnLoggedOut;
        }
    }

    private void OnLoggedOut()
    {
        var loginVm = new LoginViewModel();
        var loginWindow = new LoginWindow
        {
            DataContext = loginVm
        };

        loginVm.LoginSucceeded += () =>
        {
            var mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel()
            };
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = mainWindow;
            }
            mainWindow.Show();
            loginWindow.Close();
        };

        loginWindow.Show();
        Close();
    }
}
