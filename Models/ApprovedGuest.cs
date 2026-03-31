using System;

namespace EchoLink.Models;

public class ApprovedGuest
{
    public string NodeId { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; }
}
