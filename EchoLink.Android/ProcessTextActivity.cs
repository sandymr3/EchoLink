using Android.App;
using Android.Content;
using Android.OS;
using System.Threading.Tasks;
using EchoLink.Services;

namespace EchoLink.Android;

/// <summary>
/// Invisible activity that intercepts the "Send to PC" menu action from Android's text selection toolbar.
/// 
/// Workflow:
/// 1. User selects text in any app (Chrome, WhatsApp, etc.)
/// 2. Taps the floating menu (⋮ on phones, may show directly on tablets)
/// 3. Taps "Send to PC" (this activity)
/// 4. This activity extracts the text and broadcasts it via ClipboardSyncService
/// 5. Instantly finishes - user stays in their original app seamlessly
/// 
/// This bypasses Android's background clipboard monitoring restrictions (Android 10+)
/// by requiring explicit user consent via the menu action.
/// </summary>
[Activity(
    Label = "Send to PC",
    Theme = "@style/MyTheme.Translucent",
    Exported = true)]
[IntentFilter(
    new[] { Intent.ActionProcessText },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "text/plain")]
public class ProcessTextActivity : Activity
{
    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Extract the selected text from the intent
        // ActionProcessText provides the text via ExtraProcessText
        string? selectedText = Intent?.GetStringExtra(Intent.ExtraProcessText);
        
        // Fallback to ExtraText if ExtraProcessText is not available
        if (string.IsNullOrEmpty(selectedText))
        {
            selectedText = Intent?.GetStringExtra(Intent.ExtraText);
        }

        if (!string.IsNullOrEmpty(selectedText))
        {
            // Broadcast the text to all connected peers via Unified Protocol
            await ClipboardSyncService.Instance.ManualBroadcastToPeersAsync(selectedText);
        }

        // Instantly close this activity - user stays in their original app
        // No visible UI flash, no disruption to user experience
        Finish();
    }
}
