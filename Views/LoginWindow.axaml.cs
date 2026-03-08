using Avalonia.Controls;
using EchoLink.ViewModels;

namespace EchoLink.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is LoginViewModel vm)
        {
            vm.LoginSucceeded += OnLoginSucceeded;
        }
    }

    private void OnLoginSucceeded()
    {
        var mainWindow = new MainWindow
        {
            DataContext = new MainWindowViewModel()
        };
        mainWindow.Show();
        Close();
    }
}
