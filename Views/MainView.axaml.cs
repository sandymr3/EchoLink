using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using EchoLink.ViewModels;

namespace EchoLink.Views;

public partial class MainView : UserControl
{
    public MainView()
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
        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            var loginVm = new LoginViewModel();
            // We need to re-hook the login succeeded event
            loginVm.LoginSucceeded += () =>
            {
                var mainVm = new MainWindowViewModel();
                singleView.MainView = new MainView { DataContext = mainVm };
            };
            singleView.MainView = new LoginView { DataContext = loginVm };
        }
    }
}
