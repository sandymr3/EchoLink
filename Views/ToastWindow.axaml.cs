using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace EchoLink.Views;

public partial class ToastWindow : Window
{
    private ToastWindow() => InitializeComponent();

    /// <summary>
    /// Shows a brief toast notification anchored to the bottom-right of <paramref name="owner"/>.
    /// Auto-dismisses after <paramref name="durationMs"/> milliseconds.
    /// </summary>
    public static void Show(
        Window? owner,
        string message,
        string icon = "✅",
        int durationMs = 2500)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var toast = new ToastWindow();
            toast.IconText.Text    = icon;
            toast.MessageText.Text = message;

            if (owner != null)
            {
                // Position bottom-right of owner, 16 px inset
                var ownerBounds = owner.Bounds;
                double x = ownerBounds.X + ownerBounds.Width  - toast.Width  - 16;
                double y = ownerBounds.Y + ownerBounds.Height - toast.Height - 48;
                toast.Position = new PixelPoint((int)x, (int)y);
            }

            toast.Show(owner);

            // Auto-dismiss
            _ = Task.Delay(durationMs).ContinueWith(_ =>
                Dispatcher.UIThread.Post(() =>
                {
                    try { toast.Close(); } catch { }
                }));
        });
    }
}
