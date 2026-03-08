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
        var loginWindow = new LoginWindow
        {
            DataContext = new LoginViewModel()
        };
        loginWindow.Show();
        Close();
    }
}