using Android.App;
using Android.Content;
using Android.InputMethodServices;
using Android.Views.InputMethods;
using Android.Views;
using System.Text;
using EchoLink.Services;
using EchoLink.Services.RemoteControl;

namespace EchoLink.Android;

/// <summary>
/// Headless IME (Input Method Editor) service that receives keystrokes from PC
/// and injects them into the currently focused Android app.
///
/// This is a minimal keyboard with just a "Switch Keyboard" button to allow
/// users to easily switch back to their regular keyboard.
/// Users must select "EchoLink Remote Keyboard" in their Android keyboard settings.
/// </summary>
[Service(
    Label = "EchoLink Remote Keyboard",
    Permission = "android.permission.BIND_INPUT_METHOD",
    Exported = true)]
[IntentFilter(new[] { "android.view.InputMethod" })]
[MetaData("android.view.im", Resource = "@xml/method")]
public class EchoLinkImeService : InputMethodService
{
    private readonly LoggingService _log = LoggingService.Instance;

    public override void OnCreate()
    {
        base.OnCreate();

        // Subscribe to remote key events from PC
        KeyboardControlService.OnRemoteKeyReceived += HandleRemoteInput;

        _log.Info("[EchoLinkIME] Service created - ready to receive PC keystrokes");
    }

    public override void OnDestroy()
    {
        base.OnDestroy();

        // Unsubscribe from events
        KeyboardControlService.OnRemoteKeyReceived -= HandleRemoteInput;

        _log.Info("[EchoLinkIME] Service destroyed");
    }

    /// <summary>
    /// Tell Android we want to show our custom keyboard UI.
    /// </summary>
    public override bool OnEvaluateInputViewShown()
    {
        return true;
    }

    /// <summary>
    /// Build the programmatic keyboard UI with a "Switch Keyboard" button.
    /// </summary>
    public override View? OnCreateInputView()
    {
        // Create a dark background layout
        var layout = new global::Android.Widget.LinearLayout(this)
        {
            Orientation = global::Android.Widget.Orientation.Horizontal
        };
        layout.SetGravity(global::Android.Views.GravityFlags.Center);
        layout.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#121212")); // Dark theme

        // Create the switch button
        var button = new global::Android.Widget.Button(this)
        {
            Text = "⌨️ Switch Keyboard",
            LayoutParameters = new global::Android.Widget.LinearLayout.LayoutParams(
                global::Android.Widget.LinearLayout.LayoutParams.WrapContent,
                global::Android.Widget.LinearLayout.LayoutParams.WrapContent)
        };

        // Styling the button
        button.SetTextColor(global::Android.Graphics.Color.White);
        button.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#2D2D2D"));
        button.SetPadding(40, 20, 40, 20);

        // Wire up the click event to open the native Android picker
        button.Click += (sender, e) =>
        {
            var imm = (InputMethodManager?)GetSystemService(InputMethodService);
            imm?.ShowInputMethodPicker();
        };

        layout.AddView(button);
        return layout;
    }

    /// <summary>
    /// Handle incoming keystrokes from PC via Unified Protocol.
    /// This method is called when KeyboardControlService receives keyboard events.
    /// </summary>
    private void HandleRemoteInput(byte[] payload)
    {
        var inputConnection = CurrentInputConnection;

        if (inputConnection == null || payload == null || payload.Length == 0)
        {
            _log.Debug("[EchoLinkIME] Ignoring - no active input connection or empty payload");
            return;
        }

        byte keyType = payload[0];

        try
        {
            if (keyType == 0 && payload.Length >= 3) // Control Key (Backspace, Enter, etc.)
            {
                short vkCode = BitConverter.ToInt16(payload, 1);
                HandleControlKey(inputConnection, vkCode);
            }
            else if (keyType == 1 && payload.Length > 1) // Text String
            {
                string text = Encoding.UTF8.GetString(payload, 1, payload.Length - 1);
                HandleTextString(inputConnection, text);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[EchoLinkIME] Failed to inject keystroke: {ex.Message}");
        }
    }

    /// <summary>
    /// Handle control keys (Backspace, Enter, Escape, etc.)
    /// </summary>
    private void HandleControlKey(IInputConnection inputConnection, short vkCode)
    {
        _log.Debug($"[EchoLinkIME] Control key: {vkCode}");

        switch (vkCode)
        {
            case 8: // Backspace
                // Delete 1 character before the cursor
                inputConnection.DeleteSurroundingText(1, 0);
                break;

            case 9: // Tab
                SendKeyEvent(inputConnection, global::Android.Views.Keycode.Tab);
                break;

            case 13: // Enter
                SendKeyEvent(inputConnection, global::Android.Views.Keycode.Enter);
                break;

            case 27: // Escape
                SendKeyEvent(inputConnection, global::Android.Views.Keycode.Escape);
                break;

            case 37: // Left Arrow
                SendKeyEvent(inputConnection, global::Android.Views.Keycode.DpadLeft);
                break;

            case 38: // Up Arrow
                SendKeyEvent(inputConnection, global::Android.Views.Keycode.DpadUp);
                break;

            case 39: // Right Arrow
                SendKeyEvent(inputConnection, global::Android.Views.Keycode.DpadRight);
                break;

            case 40: // Down Arrow
                SendKeyEvent(inputConnection, global::Android.Views.Keycode.DpadDown);
                break;

            case 46: // Delete
                // Delete 1 character after the cursor
                inputConnection.DeleteSurroundingText(0, 1);
                break;

            default:
                _log.Debug($"[EchoLinkIME] Unsupported control key: {vkCode}");
                break;
        }
    }

    /// <summary>
    /// Send a key event through the input connection.
    /// </summary>
    private void SendKeyEvent(IInputConnection inputConnection, global::Android.Views.Keycode keycode)
    {
        inputConnection.SendKeyEvent(new KeyEvent(KeyEventActions.Down, keycode));
        inputConnection.SendKeyEvent(new KeyEvent(KeyEventActions.Up, keycode));
    }

    /// <summary>
    /// Handle text strings from PC - commits directly to the focused app.
    /// </summary>
    private void HandleTextString(IInputConnection inputConnection, string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _log.Debug($"[EchoLinkIME] Committing text: {text}");
            // CommitText blasts the text directly into WhatsApp, Chrome, etc.
            inputConnection.CommitText(text, 1);
        }
    }
}
