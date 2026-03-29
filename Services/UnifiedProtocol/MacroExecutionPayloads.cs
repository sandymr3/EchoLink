namespace EchoLink.Services.UnifiedProtocol;

/// <summary>
/// Payload sent with UnifiedMessageType.MacroExecute.
/// </summary>
public sealed class MacroExecutePayload
{
    public Guid ExecutionId { get; set; }
    public string CommandText { get; set; } = string.Empty;
    public string TargetOs { get; set; } = string.Empty;
    public bool RequiresUI { get; set; }
}

/// <summary>
/// Payload sent with UnifiedMessageType.MacroResult.
/// </summary>
public sealed class MacroResultPayload
{
    public Guid ExecutionId { get; set; }
    public int ExitCode { get; set; }
    public string OutputText { get; set; } = string.Empty;
}
