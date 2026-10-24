using AlexisScreenPro.Models;

namespace AlexisScreenPro.Interfaces;

public interface IFileManagementService
{
    string OutputDirectory { get; }

    string LogFilePath { get; }

    void Initialize();

    /// <summary>Creates a collision-free .mp4 path in the ASP folder.</summary>
    string CreateRecordingFilePath(string? suffix = null);

    Task<IReadOnlyList<RecordingFileInfo>> GetRecentRecordingsAsync(
        int maximumCount = 4,
        CancellationToken cancellationToken = default);

    long GetFileSizeSafe(string filePath);

    bool TryOpenOutputDirectory(out string? error);

    bool TryOpenFile(string filePath, out string? error);

    void TryDeletePartialFile(string? filePath);
}
