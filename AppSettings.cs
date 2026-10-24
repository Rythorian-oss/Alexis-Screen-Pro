using AlexisScreenPro.Core;

namespace AlexisScreenPro.Models;

/// <summary>
/// User preferences persisted as JSON in LocalAppData. The ASP recording folder
/// is intentionally not configurable: every capture belongs on the desktop.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public bool CaptureAudio { get; set; } = true;

    public bool CaptureSystemAudio { get; set; } = true;

    public bool CaptureMicrophone { get; set; }

    public string? SystemAudioDeviceName { get; set; }

    public string? MicrophoneDeviceName { get; set; }

    public int FrameRate { get; set; } = AppConstants.DefaultFrameRate;

    public int BitrateMbps { get; set; } = AppConstants.DefaultBitrateMbps;

    public bool UseHardwareEncoding { get; set; } = true;

    public bool CaptureCursor { get; set; } = true;

    public int AutoStopMinutes { get; set; }

    public bool StartMinimized { get; set; }

    public bool ShowCompletionNotifications { get; set; } = true;

    public bool LaunchOnStartup { get; set; }

    public static AppSettings CreateDefault() => new();

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>
    /// Clamps values loaded from disk so a hand-edited or older settings file
    /// can never crash the capture pipeline.
    /// </summary>
    public void Normalize()
    {
        SchemaVersion = 1;

        if (!AppConstants.FrameRateChoices.Contains(FrameRate))
        {
            FrameRate = AppConstants.DefaultFrameRate;
        }

        if (!AppConstants.BitrateChoicesMbps.Contains(BitrateMbps))
        {
            BitrateMbps = AppConstants.DefaultBitrateMbps;
        }

        if (!AppConstants.AutoStopChoicesMinutes.Contains(AutoStopMinutes))
        {
            AutoStopMinutes = AppConstants.DefaultAutoStopMinutes;
        }

        if (string.IsNullOrWhiteSpace(SystemAudioDeviceName))
        {
            SystemAudioDeviceName = null;
        }

        if (string.IsNullOrWhiteSpace(MicrophoneDeviceName))
        {
            MicrophoneDeviceName = null;
        }

        if (!CaptureSystemAudio && !CaptureMicrophone)
        {
            CaptureSystemAudio = true;
        }
    }
}
