namespace AlexisScreenPro.Core;

public enum RecordingState
{
    Idle,
    Starting,
    Recording,
    Stopping
}

public enum RecordingStopReason
{
    Manual,
    AutoStopTimer,
    ApplicationExit,
    CaptureFailure
}
