using AlexisScreenPro.Core;
using AlexisScreenPro.Models;

namespace AlexisScreenPro.Interfaces;

public interface IRecordingService
{
    RecordingState State { get; }

    bool IsRecording { get; }

    bool IsBusy { get; }

    event EventHandler<RecordingStateChangedEventArgs>? StateChanged;

    event EventHandler<RecordingCompletedEventArgs>? RecordingCompleted;

    event EventHandler<RecordingFailedEventArgs>? RecordingFailed;

    event EventHandler<RecordingWarningEventArgs>? WarningRaised;

    Task StartAsync(
        RecordingRequest request,
        IProgress<RecordingProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task StopAsync(
        RecordingStopReason reason = RecordingStopReason.Manual,
        CancellationToken cancellationToken = default);

    Task<AudioDeviceCatalog> GetAudioDevicesAsync(CancellationToken cancellationToken = default);
}
