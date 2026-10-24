namespace AlexisScreenPro.Core;

/// <summary>
/// Canonical file-system locations used by every service. Recordings and logs
/// intentionally live on the current user's Desktop inside the ASP folder.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> DesktopLazy = new(ResolveDesktop, isThreadSafe: true);
    private static readonly Lazy<string> OutputLazy = new(
        () => Path.Combine(DesktopLazy.Value, AppConstants.OutputFolderName),
        isThreadSafe: true);

    public static string Desktop => DesktopLazy.Value;

    public static string OutputDirectory => OutputLazy.Value;

    public static string LogFilePath => Path.Combine(OutputDirectory, AppConstants.LogFileName);

    public static string CaptureLogFilePath => Path.Combine(OutputDirectory, AppConstants.CaptureLogFileName);

    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppConstants.SettingsFolderName);

    public static string SettingsFilePath => Path.Combine(SettingsDirectory, AppConstants.SettingsFileName);

    /// <summary>
    /// Creates the ASP folder (and any parents). Safe to call repeatedly.
    /// </summary>
    public static void EnsureOutputDirectory() => Directory.CreateDirectory(OutputDirectory);

    public static void EnsureSettingsDirectory() => Directory.CreateDirectory(SettingsDirectory);

    private static string ResolveDesktop()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop))
        {
            return desktop;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "Desktop");
    }
}
