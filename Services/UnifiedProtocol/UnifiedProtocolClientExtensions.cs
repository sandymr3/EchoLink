namespace EchoLink.Services.UnifiedProtocol;

/// <summary>
/// Extension methods for UnifiedProtocolClient providing
/// convenient strongly-typed send methods.
/// </summary>
public static class UnifiedProtocolClientExtensions
{
    /// <summary>
    /// Send a mouse move event.
    /// </summary>
    /// <param name="dx">Delta X (signed 16-bit)</param>
    /// <param name="dy">Delta Y (signed 16-bit)</param>
    public static async Task SendMouseMoveAsync(this UnifiedProtocolClient client, short dx, short dy, CancellationToken ct)
    {
        var payload = new byte[4];
        BitConverter.GetBytes(dx).CopyTo(payload, 0);
        BitConverter.GetBytes(dy).CopyTo(payload, 2);
        await client.SendMessageAsync(UnifiedMessageType.MouseMove, payload, ct);
    }

    /// <summary>
    /// Send a mouse click event.
    /// </summary>
    /// <param name="button">0=left, 1=right</param>
    /// <param name="state">0=up, 1=down</param>
    public static async Task SendMouseClickAsync(this UnifiedProtocolClient client, byte button, byte state, CancellationToken ct)
    {
        var payload = new byte[2];
        payload[0] = button;
        payload[1] = state;
        await client.SendMessageAsync(UnifiedMessageType.MouseClick, payload, ct);
    }

    /// <summary>
    /// Send a control key event for the remote keyboard.
    /// </summary>
    /// <param name="keyCode">Virtual key code</param>
    public static async Task SendKeyboardControlKeyAsync(this UnifiedProtocolClient client, short keyCode, CancellationToken ct)
    {
        var payload = new byte[3];
        payload[0] = 0; // Type 0 = Control Key
        BitConverter.GetBytes(keyCode).CopyTo(payload, 1);
        await client.SendMessageAsync(UnifiedMessageType.KeyPress, payload, ct);
    }

    /// <summary>
    /// Send text string for the remote keyboard.
    /// </summary>
    /// <param name="text">Text to simulate typing</param>
    public static async Task SendKeyboardTextAsync(this UnifiedProtocolClient client, string text, CancellationToken ct)
    {
        byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(text);
        var payload = new byte[1 + textBytes.Length];
        payload[0] = 1; // Type 1 = Text String
        Array.Copy(textBytes, 0, payload, 1, textBytes.Length);
        await client.SendMessageAsync(UnifiedMessageType.KeyPress, payload, ct);
    }

    /// <summary>
    /// Send a scroll event.
    /// </summary>
    /// <param name="dx">Horizontal scroll delta</param>
    /// <param name="dy">Vertical scroll delta</param>
    public static async Task SendScrollAsync(this UnifiedProtocolClient client, short dx, short dy, CancellationToken ct)
    {
        var payload = new byte[4];
        BitConverter.GetBytes(dx).CopyTo(payload, 0);
        BitConverter.GetBytes(dy).CopyTo(payload, 2);
        await client.SendMessageAsync(UnifiedMessageType.Scroll, payload, ct);
    }

    /// <summary>
    /// Send a system action command.
    /// </summary>
    /// <param name="actionId">0=Lock, 1=Restart, 2=Shutdown</param>
    public static async Task SendSystemActionAsync(this UnifiedProtocolClient client, byte actionId, CancellationToken ct)
    {
        await client.SendMessageAsync(UnifiedMessageType.SystemAction, new[] { actionId }, ct);
    }

    /// <summary>
    /// Send an audio frame (PCM samples).
    /// </summary>
    /// <param name="samples">PCM audio samples (16-bit)</param>
    public static async Task SendAudioFrameAsync(this UnifiedProtocolClient client, short[] samples, CancellationToken ct)
    {
        var payload = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, payload, 0, samples.Length * 2);
        await client.SendMessageAsync(UnifiedMessageType.AudioFrame, payload, ct);
    }

    /// <summary>
    /// Send a ping message for latency measurement.
    /// </summary>
    /// <param name="timestamp">Unix timestamp in milliseconds</param>
    public static async Task SendPingAsync(this UnifiedProtocolClient client, uint timestamp, CancellationToken ct)
    {
        var payload = new byte[4];
        BitConverter.GetBytes(timestamp).CopyTo(payload, 0);
        await client.SendMessageAsync(UnifiedMessageType.PingPong, payload, ct);
    }
}
