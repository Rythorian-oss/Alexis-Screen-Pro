using AlexisScreenPro.Models;

namespace AlexisScreenPro.ViewModels;

/// <summary>
/// Presentation wrapper for the most recent MP4 files in Desktop\ASP.
/// </summary>
public sealed class RecentRecordingItem
{
    public RecentRecordingItem(RecordingFileInfo file)
    {
        FilePath = file.FilePath;
        FileName = file.FileName;
        SizeBytes = file.SizeBytes;

        var local = file.CreatedUtc.ToLocalTime();
        var today = DateTime.Now.Date;
        var day = local.Date == today
            ? "Today"
            : local.Date == today.AddDays(-1)
                ? "Yesterday"
                : local.ToString("MMM d");

        Title = $"{day} · {local:HH:mm}";
        Meta = $"{FormatSize(file.SizeBytes)}  ·  MP4";
    }

    public string FilePath { get; }

    public string FileName { get; }

    public long SizeBytes { get; }

    public string Title { get; }

    public string Meta { get; }

    public string ToolTip => FilePath;

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.#} KB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024d * 1024):0.#} MB";
        }

        return $"{bytes / (1024d * 1024 * 1024):0.##} GB";
    }

    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"mm\:ss");
}
