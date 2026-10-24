using AlexisScreenPro.Core;

namespace AlexisScreenPro.Interfaces;

public enum TrayNotificationKind
{
    Info,
    Success,
    Warning,
    Error
}

public interface ITrayService
{
    void Initialize();

    void SetRecordingState(RecordingState state);

    void SetElapsed(TimeSpan elapsed);

    void ShowNotification(string title, string message, TrayNotificationKind kind = TrayNotificationKind.Info);
}
