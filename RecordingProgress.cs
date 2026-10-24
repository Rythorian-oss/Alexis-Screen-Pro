using AlexisScreenPro.Core;

namespace AlexisScreenPro.Models;

/// <summary>
/// One tick from the background clock. Reported through IProgress&lt;T&gt; so the
/// capture loop never touches the dispatcher directly.
/// </summary>
public sealed record RecordingProgress
{
    public required RecordingState State { get; init; }

    public TimeSpan Elapsed { get; init; }

    public TimeSpan? TimeRemaining { get; init; }

    public double? AutoStopFraction { get; init; }

    public string StatusHint { get; init; } = string.Empty;
}
