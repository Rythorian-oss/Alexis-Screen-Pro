using System.Windows;
using System.Windows.Threading;
using AlexisScreenPro.Core;
using AlexisScreenPro.Interfaces;
using AlexisScreenPro.Services;
using AlexisScreenPro.ViewModels;
using AlexisScreenPro.Views;

namespace AlexisScreenPro;

public partial class App : Application
{
    public App()
    {
        // Register before XAML initialization so even a broken resource
        // dictionary can be captured and diagnosed.
        RegisterGlobalExceptionHandlers();
    }

    private FileManagementService? _files;
    private LogService? _log;
    private SettingsService? _settings;
    private ScreenRecorderService? _recorder;
    private TrayService? _tray;
    private StartupService? _startup;
    private SingleInstanceService? _singleInstance;
    private MainViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private bool _shutdownStarted;
    private bool _servicesDisposed;
    private bool _pendingActivation;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            await InitializeAsync(e.Args).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            try
            {
                _log?.Error("Alexis Screen Pro failed to start.", ex);
            }
            catch
            {
                // Logging must not obscure the startup failure.
            }

            MessageBox.Show(
                $"Alexis Screen Pro could not start.\n\n{ex.Message}\n\n" +
                $"A diagnostic log is available at:\n{AppPaths.LogFilePath}",
                "Alexis Screen Pro",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            await ShutdownAsync(exitCode: 1).ConfigureAwait(true);
        }
    }

    private async Task InitializeAsync(string[] arguments)
    {
        _files = new FileManagementService();
        _files.Initialize();

        // The logger is constructed first, but deliberately stays silent until
        // the single-instance guard succeeds. A secondary process therefore
        // never interleaves bytes into the primary process's log.
        _log = new LogService(_files.LogFilePath);

        _singleInstance = new SingleInstanceService(_log);
        if (!_singleInstance.TryAcquire(OnSecondInstanceRequested))
        {
            await ShutdownAsync(exitCode: 0).ConfigureAwait(true);
            return;
        }

        _log.Info("============================================================");
        _log.Info($"Alexis Screen Pro {AppConstants.Version} starting on {Environment.OSVersion}.");
        _log.Info($"Recordings folder: {_files.OutputDirectory}");

        _settings = new SettingsService(_log);
        await _settings.LoadAsync().ConfigureAwait(true);

        _startup = new StartupService(_log);
        _recorder = new ScreenRecorderService(_log, _files);

        _tray = new TrayService(
            _log,
            ShowDashboard,
            ToggleRecordingFromTray,
            ShowSettings,
            OpenRecordingsFromTray,
            () => _ = ShutdownAsync());

        _mainViewModel = new MainViewModel(_recorder, _files, _settings, _tray, _log);

        _tray.Initialize();
        await _mainViewModel.InitializeAsync().ConfigureAwait(true);

        _mainWindow = new MainWindow(_mainViewModel, _tray, _log);
        _mainWindow.SettingsRequested += (_, _) => ShowSettings();

        var launchedAtLogin = arguments.Any(argument =>
            string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));

        var startHidden = launchedAtLogin || _settings.Current.StartMinimized;
        if (startHidden)
        {
            _log.Info("Starting minimized to the notification area.");
            _tray.ShowNotification(
                AppConstants.ProductName,
                _settings.Current.StartMinimized
                    ? "Running quietly in the tray. Click the cobalt icon to open the widget."
                    : "Started with Windows. Use the tray icon whenever you are ready to capture.",
                TrayNotificationKind.Info);
        }
        else
        {
            ShowDashboard();
        }

        if (_pendingActivation)
        {
            _pendingActivation = false;
            ShowDashboard();
        }

        _log.Info("Startup complete.");
    }

    private void OnSecondInstanceRequested()
    {
        _log?.Info("Activation requested by a second instance.");
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                if (_mainWindow is null)
                {
                    _pendingActivation = true;
                }
                else
                {
                    ShowDashboard();
                }
            }));
    }

    private void ShowDashboard()
    {
        if (_mainWindow is null)
        {
            _pendingActivation = true;
            return;
        }

        _log?.Info("Show dashboard requested.");
        _mainWindow.ShowAndActivate();
    }

    private void ToggleRecordingFromTray()
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(() =>
            {
                if (_mainViewModel?.ToggleRecordingCommand.CanExecute(null) == true)
                {
                    _mainViewModel.ToggleRecordingCommand.Execute(null);
                }
            }));
    }

    private void ShowSettings()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ShowSettings));
            return;
        }

        if (_settings is null || _recorder is null || _startup is null || _files is null || _log is null)
        {
            return;
        }

        if (_settingsWindow is not null)
        {
            if (_settingsWindow.IsVisible)
            {
                if (!_settingsWindow.IsActive)
                {
                    _settingsWindow.Activate();
                }

                return;
            }

            _settingsWindow = null;
        }

        var viewModel = new SettingsViewModel(_settings, _recorder, _startup, _files, _log);
        var window = new SettingsWindow(viewModel);

        if (_mainWindow is { IsVisible: true })
        {
            window.Owner = _mainWindow;
        }

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, window))
            {
                _settingsWindow = null;
            }
        };

        _settingsWindow = window;
        window.Show();
        window.Activate();
        _log.Info("Studio settings opened.");
    }

    private void OpenRecordingsFromTray()
    {
        if (_files is null)
        {
            return;
        }

        if (!_files.TryOpenOutputDirectory(out var error))
        {
            _log?.Warn($"Recordings folder could not be opened: {error}");
            _tray?.ShowNotification(
                "Folder could not open",
                error ?? "Windows Explorer returned an error.",
                TrayNotificationKind.Warning);
        }
    }

    private async Task ShutdownAsync(int exitCode = 0)
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        _log?.Info("Shutdown requested.");

        try
        {
            if (_recorder?.IsBusy == true)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(28));
                    await _recorder.StopAsync(RecordingStopReason.ApplicationExit, timeout.Token)
                        .ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    _log?.Warn("Recording shutdown timed out; the application will exit anyway.");
                }
                catch (Exception ex)
                {
                    _log?.Error("Recording could not be finalized during shutdown.", ex);
                }
            }

            if (_mainViewModel is not null)
            {
                await _mainViewModel.FlushPreferencesAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            if (_settingsWindow is not null)
            {
                try
                {
                    _settingsWindow.Close();
                }
                catch (Exception ex)
                {
                    _log?.Warn($"Settings window close failed: {ex.Message}");
                }

                _settingsWindow = null;
            }

            if (_mainWindow is not null)
            {
                try
                {
                    _mainWindow.PrepareForClose();
                    _mainWindow.Close();
                }
                catch (Exception ex)
                {
                    _log?.Warn($"Widget close failed: {ex.Message}");
                }

                _mainWindow = null;
            }

            DisposeServices();
            Shutdown(exitCode);
        }
    }

    private void DisposeServices()
    {
        if (_servicesDisposed)
        {
            return;
        }

        _servicesDisposed = true;

        SafeDispose(_mainViewModel, nameof(MainViewModel));
        _mainViewModel = null;

        SafeDispose(_tray, nameof(TrayService));
        _tray = null;

        SafeDispose(_recorder, nameof(ScreenRecorderService));
        _recorder = null;

        SafeDispose(_singleInstance, nameof(SingleInstanceService));
        _singleInstance = null;

        SafeDispose(_startup, nameof(StartupService));
        _startup = null;

        SafeDispose(_settings, nameof(SettingsService));
        _settings = null;

        SafeDispose(_files, nameof(FileManagementService));
        _files = null;

        try
        {
            _log?.Info("Alexis Screen Pro stopped.");
            _log?.Dispose();
        }
        catch
        {
            // Nothing else can be done during teardown.
        }
        finally
        {
            _log = null;
        }
    }

    private void SafeDispose(IDisposable? resource, string name)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            resource.Dispose();
        }
        catch (Exception ex)
        {
            try
            {
                _log?.Warn($"{name} disposal failed: {ex.Message}");
            }
            catch
            {
                // Ignore logging failures during teardown.
            }
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                if (_log is not null)
                {
                    _log.Error("Unhandled dispatcher exception.", args.Exception);
                }
                else
                {
                    TryAppendFatalLog("Unhandled dispatcher exception before logger startup.", args.Exception);
                }
            }
            catch
            {
                // Never let logging mask a UI exception.
            }

            if (args.Exception is OutOfMemoryException)
            {
                args.Handled = false;
                return;
            }

            args.Handled = true;
            try
            {
                _tray?.ShowNotification(
                    "Something went wrong",
                    "Alexis Screen Pro recovered from an unexpected error. Details are in the ASP log.",
                    TrayNotificationKind.Error);
            }
            catch
            {
                // Notification is best-effort.
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                var exception = args.ExceptionObject as Exception;
                var message = $"Unhandled AppDomain exception (terminating={args.IsTerminating}).";
                if (_log is not null)
                {
                    _log.Error(message, exception);
                }
                else
                {
                    TryAppendFatalLog(message, exception);
                }
            }
            catch
            {
                // Process is already going down.
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try
            {
                _log?.Error("Unobserved task exception.", args.Exception);
            }
            catch
            {
                // Ignore.
            }

            args.SetObserved();
        };
    }

    /// <summary>
    /// Last-resort synchronous log used before LogService exists or after the
    /// process is already terminating.
    /// </summary>
    private static void TryAppendFatalLog(string message, Exception? exception)
    {
        try
        {
            AppPaths.EnsureOutputDirectory();
            var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [FATAL] {message}";
            if (exception is not null)
            {
                text += Environment.NewLine + exception;
            }

            File.AppendAllText(AppPaths.LogFilePath, text + Environment.NewLine);
        }
        catch
        {
            // If even the ASP folder is unavailable, there is nowhere to log.
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        try
        {
            if (_recorder?.IsBusy == true)
            {
                _recorder.StopAsync(RecordingStopReason.ApplicationExit)
                    .Wait(TimeSpan.FromSeconds(8));
            }

            _mainViewModel?.FlushPreferencesAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _log?.Warn($"Session-ending cleanup was incomplete: {ex.Message}");
        }

        _mainWindow?.PrepareForClose();
        DisposeServices();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_recorder?.IsBusy == true)
            {
                _recorder.StopAsync(RecordingStopReason.ApplicationExit)
                    .Wait(TimeSpan.FromSeconds(6));
            }
        }
        catch (Exception ex)
        {
            _log?.Warn($"Last-chance recording cleanup failed: {ex.Message}");
        }

        _mainWindow?.PrepareForClose();
        DisposeServices();
        base.OnExit(e);
    }
}
