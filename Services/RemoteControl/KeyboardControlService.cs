using System;
using System.Threading;
using System.Threading.Tasks;
using SharpHook;
using SharpHook.Native;

namespace EchoLink.Services;

public class KeyboardControlService
{
    // Singleton pattern ensures we only ever create one EventSimulator
    public static KeyboardControlService Instance { get; } = new();

    /// <summary>
    /// Event fired when PC keystrokes should be sent to Android.
    /// Subscribed to by EchoLinkImeService on Android.
    /// </summary>
    public static event Action<byte[]>? OnRemoteKeyReceived;

    private readonly EventSimulator _simulator;

    private KeyboardControlService()
    {
        // Initialize the simulator once
        _simulator = new EventSimulator();
    }

    /// <summary>
    /// Handle incoming keyboard event from Unified Protocol.
    /// Routes to appropriate handler based on platform:
    /// - Android: Fires OnRemoteKeyReceived for IME (PC→Android)
    /// - Windows/Linux: Uses SharpHook to inject keystrokes (Android→PC)
    /// </summary>
    public async Task HandleKeyboardEventAsync(byte[] payload, CancellationToken ct)
    {
        if (payload == null || payload.Length == 0)
        {
            return;
        }

        if (OperatingSystem.IsAndroid())
        {
            // PC→Android: Fire event for EchoLinkImeService to inject text
            OnRemoteKeyReceived?.Invoke(payload);
        }
        else
        {
            // Android→PC: Use SharpHook to inject keystrokes
            ProcessIncomingKeyboardEvent(payload);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Handle incoming keyboard event from Android (Android → PC direction).
    /// Uses SharpHook to inject keystrokes into the PC OS.
    /// </summary>
    public void ProcessIncomingKeyboardEvent(byte[] payload)
    {
        if (payload == null || payload.Length == 0) return;

        byte keyType = payload[0];

        if (keyType == 0 && payload.Length >= 3) // Control Key (Type 0)
        {
            short vkCode = BitConverter.ToInt16(payload, 1);

            // Map the Windows Virtual Key to SharpHook's cross-platform KeyCode
            KeyCode sharpKeyCode = MapVirtualKeyToSharpHook(vkCode);

            if (sharpKeyCode != KeyCode.Vc0) // Vc0 means unknown/unmapped
            {
                _simulator.SimulateKeyPress(sharpKeyCode);
                _simulator.SimulateKeyRelease(sharpKeyCode);
            }
        }
        else if (keyType == 1 && payload.Length > 1) // Text String (Type 1)
        {
            string textToType = System.Text.Encoding.UTF8.GetString(payload, 1, payload.Length - 1);

            // This natively translates the UTF-8 string into OS keystrokes
            _simulator.SimulateTextEntry(textToType);
        }
    }

    /// <summary>
    /// Send keystrokes from PC to Android (PC → Android direction).
    /// Called by DesktopKeyboardSender when routing is active.
    /// </summary>
    public void SendToAndroid(byte[] payload)
    {
        OnRemoteKeyReceived?.Invoke(payload);
    }

    // A helper to translate standard key codes to SharpHook's enum
    private KeyCode MapVirtualKeyToSharpHook(short vkCode)
    {
        return vkCode switch
        {
            8 => KeyCode.VcBackspace,
            13 => KeyCode.VcEnter,
            27 => KeyCode.VcEscape,
            37 => KeyCode.VcLeft,
            38 => KeyCode.VcUp,
            39 => KeyCode.VcRight,
            40 => KeyCode.VcDown,
            // Add more keys here as needed!
            _ => KeyCode.Vc0
        };
    }
}
