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
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is LoginViewModel vm)
            vm.Cancel();
    }
}
