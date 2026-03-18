using Avalonia.Controls;
using Avalonia.Interactivity;
using EchoLink.Models;
using EchoLink.ViewModels;

namespace EchoLink.Views;

public partial class MacrosView : UserControl
{
    public MacrosView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    /// <summary>
    /// Wire ViewModel callbacks once we have access to the parent Window.
    /// </summary>
    private void OnAttachedToVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is not MacrosViewModel vm) return;

        var parentWindow = TopLevel.GetTopLevel(this) as Window;

        // ── Target-device selection modal ────────────────────────────────
        vm.ShowTargetModalAsync = async (macro) =>
        {
            var dialogVm = new TargetSelectDialogViewModel(
                macro.Name,
                vm.OnlineDevices,
                () => { /* cancel handled by dialog close */ });

            var dialog = new TargetSelectDialog { DataContext = dialogVm };
            var result = await dialog.ShowDialog<Device?>(parentWindow);
            return result;
        };

        // ── OS-mismatch error dialog ─────────────────────────────────────
        vm.ShowErrorDialogAsync = async (title, message) =>
        {
            var dlg = new Window
            {
                Title           = title,
                Width           = 440,
                Height          = 220,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize       = false,
                Background      = Avalonia.Media.Brush.Parse("#0D0D0D"),
                SystemDecorations = Avalonia.Controls.SystemDecorations.BorderOnly,
            };

            var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 16 };

            panel.Children.Add(new TextBlock
            {
                Text       = "⚠ " + title,
                FontSize   = 16,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                Foreground = Avalonia.Media.Brush.Parse("#FFA726"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });

            panel.Children.Add(new TextBlock
            {
                Text         = message,
                FontSize     = 13,
                Foreground   = Avalonia.Media.Brush.Parse("#B3B3B3"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });

            var okBtn = new Button
            {
                Content             = "Got it",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Padding             = new Avalonia.Thickness(20, 8),
                Background          = Avalonia.Media.Brush.Parse("#1A2A1A"),
                Foreground          = Avalonia.Media.Brush.Parse("#00E676"),
                CornerRadius        = new Avalonia.CornerRadius(6)
            };
            okBtn.Click += (_, _) => dlg.Close();
            panel.Children.Add(okBtn);

            dlg.Content = panel;
            await dlg.ShowDialog(parentWindow!);
        };

        // ── Toast notification ────────────────────────────────────────────
        vm.ShowToast = (message, icon) =>
        {
            ToastWindow.Show(parentWindow, message, icon);
        };
    }

    private void OnRunMacroClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MacroButton macro } &&
            DataContext is MacrosViewModel vm)
        {
            _ = vm.ExecuteMacroCommand.ExecuteAsync(macro);
        }
    }

    private void OnDeleteMacroClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MacroButton macro } &&
            DataContext is MacrosViewModel vm)
        {
            vm.DeleteMacroCommand.Execute(macro);
        }
    }
}
