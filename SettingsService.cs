using AlexisScreenPro.Core;
using AlexisScreenPro.Interfaces;
using AlexisScreenPro.Models;
using System.Text.Json;

namespace AlexisScreenPro.Services;

/// <summary>
/// Loads and atomically persists settings. Corrupt JSON falls back to safe
/// defaults rather than preventing the application from starting.
/// </summary>
public sealed class SettingsService : DisposableBase, ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogService _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current = AppSettings.CreateDefault();

    public SettingsService(ILogService log)
    {
        _log = log;
    }

    public AppSettings Current => Volatile.Read(ref _current);

    public event Action<AppSettings>? SettingsChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            AppPaths.EnsureSettingsDirectory();
            if (!File.Exists(AppPaths.SettingsFilePath))
            {
                _log.Info("No settings file found; using first-run defaults.");
                Volatile.Write(ref _current, AppSettings.CreateDefault());
                return;
            }

            var json = await File.ReadAllTextAsync(AppPaths.SettingsFilePath, cancellationToken).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions)
                         ?? AppSettings.CreateDefault();
            loaded.Normalize();
            Volatile.Write(ref _current, loaded);
            _log.Info("Settings loaded.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _log.Error("Settings could not be loaded; defaults are in use.", ex);
            Volatile.Write(ref _current, AppSettings.CreateDefault());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default,
        bool notifyListeners = true)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);

        var snapshot = settings.Clone();
        snapshot.Normalize();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = AppPaths.SettingsFilePath + ".tmp";

        try
        {
            AppPaths.EnsureSettingsDirectory();
            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, AppPaths.SettingsFilePath, overwrite: true);
            Volatile.Write(ref _current, snapshot);
            _log.Info("Settings saved.");
            if (notifyListeners)
            {
                SettingsChanged?.Invoke(snapshot.Clone());
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _log.Error("Settings could not be saved.", ex);
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }

            _gate.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            _gate.Dispose();
        }

        base.Dispose(disposing);
    }
}
