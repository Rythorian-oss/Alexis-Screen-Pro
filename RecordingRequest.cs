namespace AlexisScreenPro.Models;

/// <summary>
/// Immutable snapshot of one recording request. The view model creates it on the
/// UI thread; the capture service owns it thereafter.
/// </summary>
public sealed record RecordingRequest
{
    public required string OutputPath { get; init; }

    public bool CaptureAudio { get; init; } = true;

    public bool CaptureSystemAudio { get; init; } = true;

    public bool CaptureMicrophone { get; init; }

    public string? SystemAudioDeviceName { get; init; }

    public string? MicrophoneDeviceName { get; init; }

    public bool CaptureCursor { get; init; } = true;

    public int FrameRate { get; init; } = 30;

    public int BitrateBitsPerSecond { get; init; } = 8_000_000;

    public bool UseHardwareEncoding { get; init; } = true;

    /// <summary>Null or zero means the recording runs until stopped manually.</summary>
    public TimeSpan? AutoStopAfter { get; init; }
}
