using Avalonia.Controls;
using Avalonia.Interactivity;
using EchoLink.Models;
using EchoLink.ViewModels;

namespace EchoLink.Views;

public partial class TargetSelectDialog : Window
{
    public TargetSelectDialog()
    {
        InitializeComponent();
    }

    private void OnDeviceClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Device device } &&
            DataContext is TargetSelectDialogViewModel vm)
        {
            vm.SelectDevice(device);
            Close(device);
        }
    }
}
