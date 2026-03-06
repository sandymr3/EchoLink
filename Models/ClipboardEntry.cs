namespace EchoLink.Models;

public record ClipboardEntry(
    string Content,
    string SourceDevice,
    DateTime Timestamp)
{
    public string TimeAgo
    {
        get
        {
            var diff = DateTime.Now - Timestamp;
            if (diff.TotalSeconds < 60)  return $"{(int)diff.TotalSeconds}s ago";
            if (diff.TotalMinutes < 60)  return $"{(int)diff.TotalMinutes}m ago";
            return $"{(int)diff.TotalHours}h ago";
        }
    }

    public string Preview => Content.Length > 80
        ? string.Concat(Content.AsSpan(0, 77), "...")
        : Content;
}
