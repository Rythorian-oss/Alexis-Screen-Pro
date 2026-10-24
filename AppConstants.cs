namespace AlexisScreenPro.Core;

/// <summary>
/// Product-wide immutable constants. Keeping these in one place avoids drift
/// between the tray, the widget and the capture engine.
/// </summary>
public static class AppConstants
{
    public const string ProductName = "Alexis Screen Pro";
    public const string ShortName = "Alexis";
    public const string Version = "1.0.0";
    public const string OutputFolderName = "ASP";
    public const string RecordingExtension = ".mp4";
    public const string LogFileName = "alexis-screen-pro.log";
    public const string CaptureLogFileName = "capture-engine.log";
    public const string SettingsFolderName = "AlexisScreenPro";
    public const string SettingsFileName = "settings.json";
    public const string SingleInstanceMutexName = @"Local\AlexisScreenPro.SingleInstance.7CBF8C4F";
    public const string ActivationEventName = @"Local\AlexisScreenPro.Activate.7CBF8C4F";
    public const string StartupRegistryValueName = "AlexisScreenPro";

    public const int DefaultFrameRate = 30;
    public const int DefaultBitrateMbps = 8;
    public const int DefaultAutoStopMinutes = 0;
    public const int MaxLogBytes = 2 * 1024 * 1024;

    public static readonly int[] FrameRateChoices = [30, 60];
    public static readonly int[] BitrateChoicesMbps = [4, 8, 12, 20];
    public static readonly int[] AutoStopChoicesMinutes = [0, 1, 3, 5, 10, 15, 30, 60];

    public const double WidgetWidth = 460;
    public const double WidgetHeight = 760;
    public const double SettingsWidth = 820;
    public const double SettingsHeight = 720;
}
