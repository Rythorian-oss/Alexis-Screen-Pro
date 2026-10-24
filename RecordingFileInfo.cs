namespace AlexisScreenPro.Models;

public sealed record RecordingFileInfo(
    string FilePath,
    string FileName,
    long SizeBytes,
    DateTime CreatedUtc);
