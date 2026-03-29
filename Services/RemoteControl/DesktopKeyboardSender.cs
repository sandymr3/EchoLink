using SharpHook;
using SharpHook.Native;
using System;
using System.Threading.Tasks;
using EchoLink.Services;

namespace EchoLink.Services.RemoteControl
{
    /// <summary>
    /// PC-side keyboard sender that captures global keystrokes via SharpHook
    /// and routes them to Android when routing is enabled.
    /// 
    /// SharpHook v5.3.0 Compatible Implementation:
    /// - Manual modifier tracking (e.Modifiers was removed)
    /// - Uses Dispose() instead of removed Stop() method
    /// - Fire-and-forget TCP to avoid blocking input queue
    /// </summary>
    public class DesktopKeyboardSender : IDisposable
    {
        private readonly TaskPoolGlobalHook _globalHook;
        private bool _isRoutingToPhone = false;
        
        // Track modifier keys manually since e.Modifiers was removed in v5.3.0
        private bool _isCtrlDown = false;
        private bool _isAltDown = false;
        private bool _isShiftDown = false;

        /// <summary>
        /// Event fired when routing state changes.
        /// Subscribe to update UI indicators.
        /// </summary>
        public event Action<bool>? RoutingStateChanged;

        /// <summary>
        /// Returns true if keystrokes are currently being routed to phone.
        /// </summary>
        public bool IsRoutingActive => _isRoutingToPhone;

        public DesktopKeyboardSender()
        {
            _globalHook = new TaskPoolGlobalHook();
            
            // Hook up events
            _globalHook.KeyPressed += OnGlobalKeyPressed;
            _globalHook.KeyReleased += OnGlobalKeyReleased;
        }

        /// <summary>
        /// Start the global hook. Call once at application startup.
        /// Uses RunAsync() to avoid blocking the main thread.
        /// </summary>
        public void Start()
        {
            _ = _globalHook.RunAsync();
        }

        /// <summary>
        /// Toggle routing state. Called by Ctrl+Alt+K hotkey or UI button.
        /// </summary>
        public void ToggleRouting()
        {
            _isRoutingToPhone = !_isRoutingToPhone;
            RoutingStateChanged?.Invoke(_isRoutingToPhone);
        }

        /// <summary>
        /// Handle key release events to track modifier state.
        /// </summary>
        private void OnGlobalKeyReleased(object? sender, KeyboardHookEventArgs e)
        {
            // Track modifier states
            if (e.Data.KeyCode == KeyCode.VcLeftControl || e.Data.KeyCode == KeyCode.VcRightControl)
                _isCtrlDown = false;

            if (e.Data.KeyCode == KeyCode.VcLeftAlt || e.Data.KeyCode == KeyCode.VcRightAlt)
                _isAltDown = false;

            // Track Shift key
            if (e.Data.KeyCode == KeyCode.VcLeftShift || e.Data.KeyCode == KeyCode.VcRightShift)
                _isShiftDown = false;
        }

        /// <summary>
        /// Handle key press events from SharpHook.
        /// MUST execute in under 5ms to avoid keyboard lag.
        /// </summary>
        private void OnGlobalKeyPressed(object? sender, KeyboardHookEventArgs e)
        {
            // Track modifier states
            if (e.Data.KeyCode == KeyCode.VcLeftControl || e.Data.KeyCode == KeyCode.VcRightControl)
                _isCtrlDown = true;

            if (e.Data.KeyCode == KeyCode.VcLeftAlt || e.Data.KeyCode == KeyCode.VcRightAlt)
                _isAltDown = true;

            // Track Shift key
            if (e.Data.KeyCode == KeyCode.VcLeftShift || e.Data.KeyCode == KeyCode.VcRightShift)
                _isShiftDown = true;

            // 1. The Toggle Mechanism (Ctrl + Alt + K)
            if (e.Data.KeyCode == KeyCode.VcK && _isCtrlDown && _isAltDown)
            {
                ToggleRouting();
                
                // Suppress the K key so it doesn't type anywhere
                e.SuppressEvent = true; 
                return;
            }

            // 2. The Routing Mechanism
            if (_isRoutingToPhone)
            {
                // STOP the keystroke from reaching the PC OS
                e.SuppressEvent = true; 

                // Convert and Send via KeyboardControlService
                byte[]? payload = ConvertToPayload(e.Data.KeyCode); 
                if (payload != null)
                {
                     // Fire and forget so we don't block the global hook
                     // This keeps the handler under 5ms to avoid keyboard lag
                     _ = SendToAndroidAsync(payload);
                }
            }
        }

        /// <summary>
        /// Send payload to Android device.
        /// </summary>
        private static async Task SendToAndroidAsync(byte[] payload)
        {
            try
            {
                if (UnifiedProtocol.UnifiedProtocolClient.Instance.IsConnected)
                {
                    await UnifiedProtocol.UnifiedProtocolClient.Instance.SendMessageAsync(
                        UnifiedProtocol.UnifiedMessageType.KeyboardEvent, 
                        payload,
                        CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Debug($"[DesktopKeyboardSender] Send failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Convert SharpHook KeyCode to Unified Protocol payload.
        ///
        /// Message Format:
        /// - Type 0 (Control): [Type:1][KeyCode:2] = 3 bytes
        /// - Type 1 (Text): [Type:1][UTF-8 Bytes:N] = N+1 bytes
        /// </summary>
        private byte[]? ConvertToPayload(KeyCode keyCode)
        {
            // First check if it's a control key (Backspace, Enter, etc.)
            short? vkCode = MapSharpHookToVirtualKey(keyCode);

            if (vkCode.HasValue)
            {
                byte[] payload = new byte[3];
                payload[0] = 0; // Type 0 = Control Key
                Array.Copy(BitConverter.GetBytes(vkCode.Value), 0, payload, 1, 2);
                return payload;
            }

            // If not a control key, convert printable character to string
            string? charToSend = KeyCodeToChar(keyCode);

            if (!string.IsNullOrEmpty(charToSend))
            {
                byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(charToSend);
                byte[] payload = new byte[1 + textBytes.Length];
                payload[0] = 1; // Type 1 = Text String
                Array.Copy(textBytes, 0, payload, 1, textBytes.Length);
                return payload;
            }

            return null;
        }

        /// <summary>
        /// Map SharpHook KeyCode to Virtual Key code for special keys.
        /// </summary>
        private static short? MapSharpHookToVirtualKey(KeyCode keyCode)
        {
            return keyCode switch
            {
                KeyCode.VcBackspace => 8,
                KeyCode.VcTab => 9,     // Added Tab support
                KeyCode.VcEnter => 13,
                KeyCode.VcEscape => 27,
                KeyCode.VcLeft => 37,
                KeyCode.VcUp => 38,
                KeyCode.VcRight => 39,
                KeyCode.VcDown => 40,
                _ => null
            };
        }

        /// <summary>
        /// Convert SharpHook KeyCode to character for printable keys.
        /// Uses shift state and OS Caps Lock to determine case.
        /// Note: SharpHook v5.3.0 uses basic KeyCode enums.
        /// </summary>
        private string? KeyCodeToChar(KeyCode keyCode)
        {
            // Check OS Caps Lock state safely
            bool isCaps = false;
            try { isCaps = Console.CapsLock; } catch { }

            // Standard XOR logic: Shift reverses CapsLock
            bool isUpper = (_isShiftDown && !isCaps) || (!_isShiftDown && isCaps);

            // Handle Space first (most common character!)
            if (keyCode == KeyCode.VcSpace)
            {
                return " ";
            }

            // 1. Handle Alphabet (A-Z)
            if (keyCode >= KeyCode.VcA && keyCode <= KeyCode.VcZ)
            {
                string c = keyCode.ToString().Replace("Vc", "").ToLower();
                return isUpper ? c.ToUpper() : c;
            }

            // 2. Handle Numbers & Symbols based on Shift state
            if (!_isShiftDown)
            {
                return keyCode switch
                {
                    KeyCode.Vc1 => "1", KeyCode.Vc2 => "2", KeyCode.Vc3 => "3",
                    KeyCode.Vc4 => "4", KeyCode.Vc5 => "5", KeyCode.Vc6 => "6",
                    KeyCode.Vc7 => "7", KeyCode.Vc8 => "8", KeyCode.Vc9 => "9",
                    KeyCode.Vc0 => "0",
                    KeyCode.VcComma => ",",
                    KeyCode.VcPeriod => ".",
                    KeyCode.VcSlash => "/",
                    KeyCode.VcSemicolon => ";",
                    KeyCode.VcQuote => "'",
                    KeyCode.VcOpenBracket => "[",
                    KeyCode.VcCloseBracket => "]",
                    KeyCode.VcMinus => "-",
                    KeyCode.VcEquals => "=",
                    KeyCode.VcBackslash => "\\",
                    _ => null
                };
            }
            else
            {
                // Shift is held down! Return the shifted symbols.
                return keyCode switch
                {
                    KeyCode.Vc1 => "!", KeyCode.Vc2 => "@", KeyCode.Vc3 => "#",
                    KeyCode.Vc4 => "$", KeyCode.Vc5 => "%", KeyCode.Vc6 => "^",
                    KeyCode.Vc7 => "&", KeyCode.Vc8 => "*", KeyCode.Vc9 => "(",
                    KeyCode.Vc0 => ")",
                    KeyCode.VcComma => "<",
                    KeyCode.VcPeriod => ">",
                    KeyCode.VcSlash => "?",
                    KeyCode.VcSemicolon => ":",
                    KeyCode.VcQuote => "\"",
                    KeyCode.VcOpenBracket => "{",
                    KeyCode.VcCloseBracket => "}",
                    KeyCode.VcMinus => "_",
                    KeyCode.VcEquals => "+",
                    KeyCode.VcBackslash => "|",
                    _ => null
                };
            }
        }

        public void Dispose()
        {
            _globalHook?.Dispose();
        }
    }
}
