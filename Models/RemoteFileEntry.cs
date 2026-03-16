namespace EchoLink.Models;

public class RemoteFileEntry
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long Size { get; set; }
    public bool IsDirectory { get; set; }
    public DateTime LastModified { get; set; }

    public string SizeDisplay => IsDirectory ? "—" : FormatSize(Size);
    public string LastModifiedDisplay => LastModified.ToString("MMM dd, yyyy  HH:mm");
    public string Icon => IsDirectory ? "📁" : GetFileIcon(Name);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024L              => $"{bytes} B",
        < 1048576L           => $"{bytes / 1024.0:F1} KB",
        < 1073741824L        => $"{bytes / 1048576.0:F1} MB",
        _                    => $"{bytes / 1073741824.0:F1} GB"
    };

    private static string GetFileIcon(string name) =>
        System.IO.Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".pdf"                                                       => "📄",
            ".doc" or ".docx"                                            => "📝",
            ".xls" or ".xlsx" or ".csv"                                  => "📊",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp"  => "🖼",
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm"             => "🎬",
            ".mp3" or ".wav" or ".flac" or ".ogg" or ".aac"             => "🎵",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2"     => "📦",
            ".exe" or ".msi" or ".dmg"                                   => "⚙️",
            ".txt" or ".md" or ".log"                                    => "📃",
            ".cs" or ".py" or ".js" or ".ts" or ".go" or ".rs"
                or ".cpp" or ".h" or ".java" or ".kt"                   => "💻",
            _                                                            => "📄"
        };
}
