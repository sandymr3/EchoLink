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
    }

    private void OnRunMacroClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MacroButton macro } &&
            DataContext is MacrosViewModel vm)
        {
            vm.ExecuteMacroCommand.Execute(macro);
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
