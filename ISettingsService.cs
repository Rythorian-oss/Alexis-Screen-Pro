using AlexisScreenPro.Models;

namespace AlexisScreenPro.Interfaces;

public interface ISettingsService
{
    AppSettings Current { get; }

    event Action<AppSettings>? SettingsChanged;

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default,
        bool notifyListeners = true);
}
