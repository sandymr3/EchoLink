using System;
using SharpHook;
using SharpHook.Native;

namespace EchoLink.Services;

public class KeyboardControlService
{
    // Singleton pattern ensures we only ever create one EventSimulator
    public static KeyboardControlService Instance { get; } = new();
    
    private readonly EventSimulator _simulator;

    private KeyboardControlService()
    {
        // Initialize the simulator once
        _simulator = new EventSimulator();
    }

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
