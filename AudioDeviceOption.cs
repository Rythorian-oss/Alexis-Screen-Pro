namespace AlexisScreenPro.Models;

/// <summary>
/// Display model for the settings device pickers. DeviceName null represents
/// "use the Windows default endpoint".
/// </summary>
public sealed record AudioDeviceOption(string? DeviceName, string DisplayName, bool IsSystemDefault = false)
{
    public override string ToString() => DisplayName;
}
