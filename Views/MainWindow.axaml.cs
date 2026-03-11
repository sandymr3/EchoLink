using Avalonia.Controls;
using EchoLink.ViewModels;

namespace EchoLink.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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