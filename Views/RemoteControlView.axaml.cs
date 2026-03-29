using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EchoLink.ViewModels;

namespace EchoLink.Views;

public partial class RemoteControlView : UserControl
{
    private TextBox? _keyboardTrap;

    public RemoteControlView()
    {
        InitializeComponent();

        // 🚨 ADD THIS: The Avalonia Key Swallow
        // This stops Enter, Space, and Tab from triggering PC buttons
        // when PC keyboard routing is active
        this.AddHandler(InputElement.KeyDownEvent, (sender, e) =>
        {
            var vm = DataContext as RemoteControlViewModel;
            if (vm != null && vm.IsPcKeyboardRoutingEnabled)
            {
                // Tells Avalonia "I handled this, don't pass it to the UI"
                e.Handled = true;
                
                // Move focus to this control to prevent space/enter from triggering buttons
                this.Focus();
            }
        }, RoutingStrategies.Tunnel);

        var trackpad = this.FindControl<Border>("TrackpadArea");
        if (trackpad != null)
        {
            trackpad.PointerPressed  += OnPointerPressed;
            trackpad.PointerMoved    += OnPointerMoved;
            trackpad.PointerReleased += OnPointerReleased;
        }

        _keyboardTrap = this.FindControl<TextBox>("KeyboardTrap");
        if (_keyboardTrap != null)
        {
            _keyboardTrap.TextChanged += KeyboardTrap_TextChanged;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel != null)
        {
            ViewModel.RequestKeyboardReset = () =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_keyboardTrap != null)
                    {
                        _keyboardTrap.Text = " ";
                        _keyboardTrap.CaretIndex = 1;
                    }
                });
            };
        }
    }

    private RemoteControlViewModel? ViewModel => DataContext as RemoteControlViewModel;

    private void KeyboardTrap_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_keyboardTrap != null && ViewModel != null)
        {
            ViewModel.ProcessKeyboardTextChange(_keyboardTrap.Text ?? "");
        }
    }

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
