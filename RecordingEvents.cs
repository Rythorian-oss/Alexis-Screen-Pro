using AlexisScreenPro.Core;

namespace AlexisScreenPro.Models;

public sealed class RecordingStateChangedEventArgs(RecordingState state) : EventArgs
{
    public RecordingState State { get; } = state;
}

public sealed class RecordingCompletedEventArgs(RecordingResult result) : EventArgs
{
    public RecordingResult Result { get; } = result;
}

public sealed class RecordingFailedEventArgs(string errorMessage, string? partialFilePath) : EventArgs
{
    public string ErrorMessage { get; } = errorMessage;

    public string? PartialFilePath { get; } = partialFilePath;
}

public sealed class RecordingWarningEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}
