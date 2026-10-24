namespace AlexisScreenPro.Models;

public enum AudioDeviceKind
{
    SystemLoopback,
    Microphone
}

public sealed record AudioDeviceInfo(
    string FriendlyName,
    string DeviceName,
    bool IsDefault,
    AudioDeviceKind Kind)
{
    public string DisplayName => IsDefault ? $"{FriendlyName}  ·  default" : FriendlyName;
}

public sealed class AudioDeviceCatalog
{
    public static AudioDeviceCatalog Empty { get; } = new();

    public IReadOnlyList<AudioDeviceInfo> LoopbackDevices { get; init; } = [];

    public IReadOnlyList<AudioDeviceInfo> MicrophoneDevices { get; init; } = [];

    public bool HasLoopback => LoopbackDevices.Count > 0;

    public bool HasMicrophone => MicrophoneDevices.Count > 0;
}
