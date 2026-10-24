using AlexisScreenPro.Core;

namespace AlexisScreenPro.Models;

public sealed record RecordingResult
{
    public required string FilePath { get; init; }

    public required DateTime StartedAtUtc { get; init; }

    public required TimeSpan Duration { get; init; }

    public long SizeBytes { get; init; }

    public RecordingStopReason StopReason { get; init; }

    public bool UsedSoftwareFallback { get; init; }
}
