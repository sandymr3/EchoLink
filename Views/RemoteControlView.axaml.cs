using Avalonia.Controls;
using Avalonia.Input;
using EchoLink.ViewModels;

namespace EchoLink.Views;

public partial class RemoteControlView : UserControl
{
    public RemoteControlView()
    {
        InitializeComponent();

        var trackpad = this.FindControl<Border>("TrackpadArea");
        if (trackpad is null) return;

        trackpad.PointerPressed  += OnPointerPressed;
        trackpad.PointerMoved    += OnPointerMoved;
        trackpad.PointerReleased += OnPointerReleased;
    }

    private RemoteControlViewModel? ViewModel => DataContext as RemoteControlViewModel;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(sender as Control);
        ViewModel?.OnPointerPressed(pos.X, pos.Y);
        (sender as Border)?.Focus();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var props = e.GetCurrentPoint(sender as Control).Properties;
        if (!props.IsLeftButtonPressed) return;

        var pos = e.GetPosition(sender as Control);
        ViewModel?.OnPointerMoved(pos.X, pos.Y);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        ViewModel?.OnPointerReleased();
    }
}
