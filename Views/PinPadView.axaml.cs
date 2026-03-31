using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace EchoLink.Views;

public partial class PinPadView : Window
{
    private const string FilledDotColor = "#00C853";
    private const string EmptyDotColor = "#2A2A2A";

    private string _pin = string.Empty;

    public PinPadView()
        : this("Unlock EchoLink", "Enter your 4-digit PIN")
    {
    }

    public PinPadView(string title, string subtitle)
    {
        InitializeComponent();
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
        UpdateDots();

        KeyDown += OnPinPadKeyDown;
    }

    private void OnPinPadKeyDown(object? sender, KeyEventArgs e)
    {
        if (TryMapDigit(e.Key, out char digit))
        {
            AppendDigit(digit);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back)
        {
            OnBackspaceClick(null, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            OnCancelClick(null, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.Enter || e.Key == Key.Return) && _pin.Length == 4)
        {
            Close(_pin);
            e.Handled = true;
        }
    }

    private void OnDigitClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string digit } || _pin.Length >= 4)
        {
            return;
        }

        AppendDigit(digit[0]);
    }

    private void OnBackspaceClick(object? sender, RoutedEventArgs e)
    {
        if (_pin.Length == 0)
        {
            return;
        }

        _pin = _pin[..^1];
        UpdateDots();
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        _pin = string.Empty;
        UpdateDots();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void AppendDigit(char digit)
    {
        if (_pin.Length >= 4 || !char.IsAsciiDigit(digit))
        {
            return;
        }

        _pin += digit;
        UpdateDots();

        if (_pin.Length == 4)
        {
            Close(_pin);
        }
    }

    private static bool TryMapDigit(Key key, out char digit)
    {
        digit = '\0';

        if (key >= Key.D0 && key <= Key.D9)
        {
            digit = (char)('0' + (key - Key.D0));
            return true;
        }

        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            digit = (char)('0' + (key - Key.NumPad0));
            return true;
        }

        return false;
    }

    private void UpdateDots()
    {
        Dot1.Fill = Brush.Parse(_pin.Length >= 1 ? FilledDotColor : EmptyDotColor);
        Dot2.Fill = Brush.Parse(_pin.Length >= 2 ? FilledDotColor : EmptyDotColor);
        Dot3.Fill = Brush.Parse(_pin.Length >= 3 ? FilledDotColor : EmptyDotColor);
        Dot4.Fill = Brush.Parse(_pin.Length >= 4 ? FilledDotColor : EmptyDotColor);
    }
}
