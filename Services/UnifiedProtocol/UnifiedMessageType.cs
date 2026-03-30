namespace EchoLink.Services.UnifiedProtocol;

/// <summary>
/// Message types for unified protocol (port 55555)
/// Format: [Type:1 byte][Length:4 bytes big-endian][Payload:N bytes]
/// </summary>
public enum UnifiedMessageType : byte
{
    // === Remote Control (0x01-0x05) ===
    MouseMove = 0x01,
    MouseClick = 0x02,
    KeyPress = 0x03,
    Scroll = 0x04,
    SystemAction = 0x05,
    
    // === Audio Streaming (0x06, 0x0E-0x0F) ===
    AudioFrame = 0x06,
    AudioPreflightRequest = 0x0E,
    AudioPreflightResponse = 0x0F,
    
    // === System Monitor (0x07-0x08) ===
    MonitorRequest = 0x07,
    MonitorResponse = 0x08,
    
    // === Clipboard Sync (0x09) ===
    ClipboardSync = 0x09,
    
    // === Macros (0x0A, 0x10) ===
    MacroExecute = 0x0A,
    
    // === File Browser (0x0B-0x0C) ===
    FileBrowserRequest = 0x0B,
    FileBrowserResponse = 0x0C,

    // === Clipboard ACK (0x0D) ===
    ClipboardAck = 0x0D,

    // === Keyboard Event (0x11) ===
    KeyboardEvent = 0x11,

    // === Macro Result (0x10) ===
    MacroResult = 0x10,

    // === Keepalive (0xFF) ===
    PingPong = 0xFF
}
