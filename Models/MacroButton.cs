namespace EchoLink.Models;

public class MacroButton
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Macro";
    public string Icon { get; set; } = "⚡";
    public string Command { get; set; } = string.Empty;
    /// <summary>null = runs on all platforms; "Windows" | "Linux" = OS filter.</summary>
    public string? TargetOs { get; set; }
    public bool SyncToMesh { get; set; }
}
