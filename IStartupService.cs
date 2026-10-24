namespace AlexisScreenPro.Interfaces;

public interface IStartupService
{
    bool IsEnabled();

    bool TrySetEnabled(bool enabled, out string? error);
}
