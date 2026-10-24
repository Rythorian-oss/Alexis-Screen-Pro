using System.Collections.ObjectModel;
using System.Windows.Threading;
using AlexisScreenPro.Core;
using AlexisScreenPro.Interfaces;
using AlexisScreenPro.Models;

namespace AlexisScreenPro.ViewModels;

/// <summary>
/// The widget's presentation brain. It owns no capture resources; it translates
/// recording progress into bindable state, persists preference changes with a
/// short debounce, and keeps the tray synchronized.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IRecordingService _recordingService;
    private readonly IFileManagementService _fileService;
    private readonly ISettingsService _settingsService;
    private readonly ITrayService _trayService;
    private readonly ILogService _log;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _preferenceSaveGate = new(1, 1);

    private AppSettings _settingsSnapshot;
    private RecordingState _state = RecordingState.Idle;
    private TimeSpan _elapsed;
    private TimeSpan? _remaining;
    private double _autoStopFraction;
    private string _encodingHint = "Hardware encoder";
    private AutoStopOption _selectedAutoStopOption;
    private RecentRecordingItem? _lastRecordingItem;
    private bool _isToastVisible;
    private bool _isToastError;
    private bool _isToastWarning;
    private string _toastTitle = string.Empty;
    private string _toastMessage = string.Empty;
    private bool _initialized;
    private DispatcherTimer? _toastTimer;
    private CancellationTokenSource? _preferenceSaveCts;

    public MainViewModel(
        IRecordingService recordingService,
        IFileManagementService fileService,
        ISettingsService settingsService,
        ITrayService trayService,
        ILogService log)
    {
        _recordingService = recordingService ?? throw new ArgumentNullException(nameof(recordingService));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _trayService = trayService ?? throw new ArgumentNullException(nameof(trayService));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _dispatcher = Dispatcher.CurrentDispatcher;

        _settingsSnapshot = _settingsService.Current.Clone();
        _selectedAutoStopOption = AutoStopOption.FromMinutes(_settingsSnapshot.AutoStopMinutes);

        AutoStopOptions = AutoStopOption.All;
        RecentRecordings = [];

        ToggleRecordingCommand = new AsyncRelayCommand(
            ToggleRecordingAsync,
            () => State is RecordingState.Idle or RecordingState.Recording,
            HandleCommandException);

        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);

        OpenRecordingCommand = new RelayCommand<string>(
            OpenRecording,
            path => !string.IsNullOrWhiteSpace(path));

        _recordingService.StateChanged += RecordingService_OnStateChanged;
        _recordingService.RecordingCompleted += RecordingService_OnRecordingCompleted;
        _recordingService.RecordingFailed += RecordingService_OnRecordingFailed;
        _recordingService.WarningRaised += RecordingService_OnWarningRaised;
        _settingsService.SettingsChanged += SettingsService_OnSettingsChanged;
    }

    public IReadOnlyList<AutoStopOption> AutoStopOptions { get; }

    public ObservableCollection<RecentRecordingItem> RecentRecordings { get; }

    public AsyncRelayCommand ToggleRecordingCommand { get; }

    public RelayCommand OpenOutputFolderCommand { get; }

    public RelayCommand<string> OpenRecordingCommand { get; }

    public RecordingState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            OnPropertyChanged();
            NotifyStateDependents();
        }
    }

    public bool IsRecording => State is RecordingState.Recording or RecordingState.Stopping;

    public bool IsActivelyRecording => State == RecordingState.Recording;

    public bool IsBusy => State is RecordingState.Starting or RecordingState.Stopping;

    public bool CanToggleRecording => State is RecordingState.Idle or RecordingState.Recording;

    public bool CanEditCaptureOptions => State == RecordingState.Idle;

    public bool IsIdle => State == RecordingState.Idle;

    public string RecordButtonLabel => State switch
    {
        RecordingState.Starting => "Arming capture…",
        RecordingState.Recording => "Stop & save",
        RecordingState.Stopping => "Finalizing…",
        _ => "Start recording"
    };

    public string StatusBadge => State switch
    {
        RecordingState.Starting => "ARMING",
        RecordingState.Recording => "LIVE",
        RecordingState.Stopping => "SAVING",
        _ => "READY"
    };

    public string FooterStatus => State switch
    {
        RecordingState.Starting => "ARMING CAPTURE",
        RecordingState.Recording => "RECORDING",
        RecordingState.Stopping => "FINALIZING",
        _ => "SYSTEM READY"
    };

    public string StatusTitle => State switch
    {
        RecordingState.Starting => "Warming up the studio.",
        RecordingState.Recording => "Recording your screen.",
        RecordingState.Stopping => "Saving your capture.",
        _ => "Ready when you are."
    };

    public string StatusSubtitle => State switch
    {
        RecordingState.Starting => "Preparing the capture engine and audio graph…",
        RecordingState.Recording => $"Full screen · {FrameRate} FPS · {AudioSummary}",
        RecordingState.Stopping => "Sealing the MP4 container and writing the file…",
        _ => $"Full screen · {FrameRate} FPS · {AudioSummary}"
    };

    public string EncodingHint => State switch
    {
        RecordingState.Recording when _encodingHint.Contains("Software", StringComparison.OrdinalIgnoreCase) => "SOFTWARE",
        RecordingState.Recording => "HARDWARE",
        _ => "AUTO"
    };

    public string ElapsedText => FormatClock(_elapsed);

    public string TimerStatusText
    {
        get
        {
            if (State == RecordingState.Recording && _remaining is not null)
            {
                return $"{FormatClock(_remaining.Value)} until auto-stop";
            }

            return SelectedAutoStopOption.Minutes == 0
                ? "No auto-stop"
                : $"Stops after {SelectedAutoStopOption.ShortLabel}";
        }
    }

    public bool IsTimerWarning =>
        State == RecordingState.Recording
        && _remaining is not null
        && _remaining <= TimeSpan.FromSeconds(30);

    public bool HasAutoStop => SelectedAutoStopOption.Minutes > 0;

    public double AutoStopFraction => _autoStopFraction;

    public AutoStopOption SelectedAutoStopOption
    {
        get => _selectedAutoStopOption;
        set
        {
            if (value is null || value == _selectedAutoStopOption)
            {
                return;
            }

            _selectedAutoStopOption = value;
            _settingsSnapshot.AutoStopMinutes = value.Minutes;
            OnPropertyChanged();
            OnPropertiesChanged(nameof(TimerStatusText), nameof(HasAutoStop));
            QueuePreferenceSave();
        }
    }

    public bool CaptureAudio
    {
        get => _settingsSnapshot.CaptureAudio;
        set
        {
            if (_settingsSnapshot.CaptureAudio == value)
            {
                return;
            }

            _settingsSnapshot.CaptureAudio = value;
            if (value && !_settingsSnapshot.CaptureSystemAudio && !_settingsSnapshot.CaptureMicrophone)
            {
                _settingsSnapshot.CaptureSystemAudio = true;
            }

            OnPropertiesChanged(
                nameof(CaptureAudio),
                nameof(CaptureSystemAudio),
                nameof(CaptureMicrophone),
                nameof(AudioSummary),
                nameof(StatusSubtitle),
                nameof(SessionDetail));
            QueuePreferenceSave();
        }
    }

    public bool CaptureSystemAudio
    {
        get => _settingsSnapshot.CaptureSystemAudio;
        set
        {
            if (_settingsSnapshot.CaptureSystemAudio == value)
            {
                return;
            }

            _settingsSnapshot.CaptureSystemAudio = value;
            if (value)
            {
                _settingsSnapshot.CaptureAudio = true;
            }
            else if (!_settingsSnapshot.CaptureMicrophone)
            {
                _settingsSnapshot.CaptureAudio = false;
            }

            NotifyAudioDependents();
            QueuePreferenceSave();
        }
    }

    public bool CaptureMicrophone
    {
        get => _settingsSnapshot.CaptureMicrophone;
        set
        {
            if (_settingsSnapshot.CaptureMicrophone == value)
            {
                return;
            }

            _settingsSnapshot.CaptureMicrophone = value;
            if (value)
            {
                _settingsSnapshot.CaptureAudio = true;
            }
            else if (!_settingsSnapshot.CaptureSystemAudio)
            {
                _settingsSnapshot.CaptureAudio = false;
            }

            NotifyAudioDependents();
            QueuePreferenceSave();
        }
    }

    public string AudioSummary
    {
        get
        {
            if (!CaptureAudio)
            {
                return "Audio muted";
            }

            return (CaptureSystemAudio, CaptureMicrophone) switch
            {
                (true, true) => "System + microphone",
                (true, false) => "System audio",
                (false, true) => "Microphone",
                _ => "Audio muted"
            };
        }
    }

    public string SessionDetail => $"{FrameRate} FPS · {BitrateMbps} Mbps · {AudioSummary}";

    public int FrameRate => _settingsSnapshot.FrameRate;

    public int BitrateMbps => _settingsSnapshot.BitrateMbps;

    public string OutputDirectory => _fileService.OutputDirectory;

    public string OutputFolderDisplay => $@"Desktop\{AppConstants.OutputFolderName}";

    public RecentRecordingItem? LastRecordingItem
    {
        get => _lastRecordingItem;
        private set => SetProperty(ref _lastRecordingItem, value);
    }

    public bool HasRecentRecordings => RecentRecordings.Count > 0;

    public string RecentCountText => RecentRecordings.Count == 0
        ? "No captures yet"
        : $"{RecentRecordings.Count} recent capture{(RecentRecordings.Count == 1 ? string.Empty : "s")}";

    public bool IsToastVisible
    {
        get => _isToastVisible;
        private set => SetProperty(ref _isToastVisible, value);
    }

    public bool IsToastError
    {
        get => _isToastError;
        private set => SetProperty(ref _isToastError, value);
    }

    public bool IsToastWarning
    {
        get => _isToastWarning;
        private set => SetProperty(ref _isToastWarning, value);
    }

    public string ToastTitle
    {
        get => _toastTitle;
        private set => SetProperty(ref _toastTitle, value);
    }

    public string ToastMessage
    {
        get => _toastMessage;
        private set => SetProperty(ref _toastMessage, value);
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _trayService.SetRecordingState(State);
        await RefreshRecentRecordingsAsync().ConfigureAwait(false);
    }

    public async Task FlushPreferencesAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _preferenceSaveCts, null)?.Cancel();
            await _preferenceSaveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _settingsService
                    .SaveAsync(_settingsSnapshot.Clone(), notifyListeners: false)
                    .ConfigureAwait(false);
            }
            finally
            {
                _preferenceSaveGate.Release();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Preferences could not be flushed on shutdown: {ex.Message}");
        }
    }

    private async Task ToggleRecordingAsync()
    {
        if (State == RecordingState.Idle)
        {
            await StartRecordingAsync().ConfigureAwait(true);
        }
        else if (State == RecordingState.Recording)
        {
            await StopRecordingAsync().ConfigureAwait(true);
        }
    }

    private async Task StartRecordingAsync()
    {
        try
        {
            _elapsed = TimeSpan.Zero;
            _remaining = null;
            _autoStopFraction = 0;
            _encodingHint = "Hardware encoder";
            NotifyTimerDependents();
            OnPropertyChanged(nameof(ElapsedText));

            var outputPath = _fileService.CreateRecordingFilePath();
            var autoStop = SelectedAutoStopOption.Minutes > 0
                ? TimeSpan.FromMinutes(SelectedAutoStopOption.Minutes)
                : (TimeSpan?)null;

            var request = new RecordingRequest
            {
                OutputPath = outputPath,
                CaptureAudio = CaptureAudio,
                CaptureSystemAudio = CaptureSystemAudio,
                CaptureMicrophone = CaptureMicrophone,
                SystemAudioDeviceName = _settingsSnapshot.SystemAudioDeviceName,
                MicrophoneDeviceName = _settingsSnapshot.MicrophoneDeviceName,
                CaptureCursor = _settingsSnapshot.CaptureCursor,
                FrameRate = _settingsSnapshot.FrameRate,
                BitrateBitsPerSecond = _settingsSnapshot.BitrateMbps * 1_000_000,
                UseHardwareEncoding = _settingsSnapshot.UseHardwareEncoding,
                AutoStopAfter = autoStop
            };

            var progress = new Progress<RecordingProgress>(OnRecordingProgress);
            await _recordingService.StartAsync(request, progress).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Error("Record command failed to start.", ex);
            ShowToast("Capture could not start", ex.Message, isError: true);
        }
    }

    private async Task StopRecordingAsync()
    {
        try
        {
            await _recordingService.StopAsync(RecordingStopReason.Manual).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Error("Record command failed to stop.", ex);
            ShowToast("Capture could not stop cleanly", ex.Message, isError: true);
        }
    }

    private void OnRecordingProgress(RecordingProgress progress)
    {
        if (IsDisposed || State == RecordingState.Idle)
        {
            return;
        }

        _elapsed = progress.Elapsed;
        _remaining = progress.TimeRemaining;
        _autoStopFraction = progress.AutoStopFraction ?? 0;
        if (!string.IsNullOrWhiteSpace(progress.StatusHint))
        {
            _encodingHint = progress.StatusHint;
        }

        OnPropertyChanged(nameof(ElapsedText));
        OnPropertiesChanged(
            nameof(TimerStatusText),
            nameof(IsTimerWarning),
            nameof(AutoStopFraction),
            nameof(EncodingHint));

        _trayService.SetElapsed(progress.Elapsed);
    }

    private void RecordingService_OnStateChanged(object? sender, RecordingStateChangedEventArgs e)
    {
        Dispatch(() =>
        {
            State = e.State;
            _trayService.SetRecordingState(e.State);

            if (e.State == RecordingState.Starting)
            {
                _elapsed = TimeSpan.Zero;
                _remaining = null;
                _autoStopFraction = 0;
                OnPropertiesChanged(nameof(ElapsedText), nameof(TimerStatusText), nameof(AutoStopFraction));
            }
            else if (e.State == RecordingState.Stopping)
            {
                _remaining = null;
                OnPropertiesChanged(nameof(TimerStatusText), nameof(IsTimerWarning));
            }
            else if (e.State == RecordingState.Idle)
            {
                _elapsed = TimeSpan.Zero;
                _remaining = null;
                _autoStopFraction = 0;
                OnPropertiesChanged(
                    nameof(ElapsedText),
                    nameof(TimerStatusText),
                    nameof(IsTimerWarning),
                    nameof(AutoStopFraction));
            }
        });
    }

    private void RecordingService_OnRecordingCompleted(object? sender, RecordingCompletedEventArgs e)
    {
        Dispatch(() =>
        {
            var result = e.Result;
            var duration = RecentRecordingItem.FormatDuration(result.Duration);
            var size = RecentRecordingItem.FormatSize(result.SizeBytes);
            var reason = result.StopReason == RecordingStopReason.AutoStopTimer
                ? "Auto-stop timer reached. "
                : string.Empty;

            ShowToast("Capture saved", $"{reason}{duration} · {size} · Desktop\\ASP", isError: false);
            _log.Info($"Recording saved by view model: {result.FilePath}");

            _ = RefreshRecentRecordingsAsync();

            if (_settingsSnapshot.ShowCompletionNotifications)
            {
                _trayService.ShowNotification(
                    "Capture saved",
                    $"{duration} · {size} · saved to Desktop\\ASP",
                    TrayNotificationKind.Success);
            }
        });
    }

    private void RecordingService_OnRecordingFailed(object? sender, RecordingFailedEventArgs e)
    {
        Dispatch(() =>
        {
            ShowToast("Capture failed", Shorten(e.ErrorMessage, 180), isError: true);
            _trayService.ShowNotification(
                "Capture failed",
                Shorten(e.ErrorMessage, 140),
                TrayNotificationKind.Error);
        });
    }

    private void RecordingService_OnWarningRaised(object? sender, RecordingWarningEventArgs e)
    {
        Dispatch(() =>
        {
            ShowToast("Capture notice", e.Message, isError: false, isWarning: true);
            _trayService.ShowNotification(
                "Capture notice",
                Shorten(e.Message, 140),
                TrayNotificationKind.Warning);
        });
    }

    private async Task RefreshRecentRecordingsAsync()
    {
        try
        {
            var recent = await _fileService.GetRecentRecordingsAsync(2).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                if (IsDisposed)
                {
                    return;
                }

                RecentRecordings.Clear();
                foreach (var item in recent)
                {
                    RecentRecordings.Add(new RecentRecordingItem(item));
                }

                LastRecordingItem = RecentRecordings.FirstOrDefault();
                OnPropertiesChanged(nameof(HasRecentRecordings), nameof(RecentCountText));
            });
        }
        catch (Exception ex)
        {
            _log.Warn($"Recent captures could not be refreshed: {ex.Message}");
        }
    }

    private void OpenOutputFolder()
    {
        if (_fileService.TryOpenOutputDirectory(out var error))
        {
            return;
        }

        ShowToast("Folder could not open", error ?? "Unknown Explorer error.", isError: true);
        _log.Warn($"Output folder could not open: {error}");
    }

    private void OpenRecording(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_fileService.TryOpenFile(path, out var error))
        {
            return;
        }

        ShowToast("Recording could not open", error ?? "The file is unavailable.", isError: true);
        _log.Warn($"Recording could not open: {error}");
    }

    private void SettingsService_OnSettingsChanged(AppSettings settings)
    {
        Dispatch(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            _settingsSnapshot = settings.Clone();
            var option = AutoStopOption.FromMinutes(_settingsSnapshot.AutoStopMinutes);
            var optionChanged = _selectedAutoStopOption != option;
            _selectedAutoStopOption = option;

            OnPropertiesChanged(
                nameof(CaptureAudio),
                nameof(CaptureSystemAudio),
                nameof(CaptureMicrophone),
                nameof(SelectedAutoStopOption),
                nameof(FrameRate),
                nameof(BitrateMbps),
                nameof(AudioSummary),
                nameof(StatusSubtitle),
                nameof(SessionDetail),
                nameof(TimerStatusText),
                nameof(HasAutoStop));

            if (optionChanged)
            {
                OnPropertyChanged(nameof(SelectedAutoStopOption));
            }
        });
    }

    private void QueuePreferenceSave()
    {
        if (IsDisposed)
        {
            return;
        }

        var snapshot = _settingsSnapshot.Clone();
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _preferenceSaveCts, cts)?.Cancel();

        _ = SavePreferencesDebouncedAsync(snapshot, cts);
    }

    private async Task SavePreferencesDebouncedAsync(AppSettings snapshot, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(250, cts.Token).ConfigureAwait(false);
            await _preferenceSaveGate.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                await _settingsService
                    .SaveAsync(snapshot, cts.Token, notifyListeners: false)
                    .ConfigureAwait(false);
            }
            finally
            {
                _preferenceSaveGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer preference edit superseded this snapshot.
        }
        catch (Exception ex)
        {
            _log.Warn($"A preference change could not be saved: {ex.Message}");
        }
        finally
        {
            // Only clear the field if this operation is still the newest one.
            Interlocked.CompareExchange(ref _preferenceSaveCts, null, cts);
            cts.Dispose();
        }
    }

    private void ShowToast(string title, string message, bool isError, bool isWarning = false)
    {
        if (IsDisposed)
        {
            return;
        }

        ToastTitle = title;
        ToastMessage = message;
        IsToastError = isError;
        IsToastWarning = !isError && isWarning;
        IsToastVisible = true;

        _toastTimer ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _toastTimer.Stop();
        _toastTimer.Interval = isError
            ? TimeSpan.FromSeconds(7)
            : isWarning
                ? TimeSpan.FromSeconds(6)
                : TimeSpan.FromSeconds(4.5);
        _toastTimer.Tick -= ToastTimer_OnTick;
        _toastTimer.Tick += ToastTimer_OnTick;
        _toastTimer.Start();
    }

    private void ToastTimer_OnTick(object? sender, EventArgs e)
    {
        _toastTimer?.Stop();
        IsToastVisible = false;
    }

    private void HandleCommandException(Exception exception)
    {
        _log.Error("An async command failed.", exception);
        Dispatch(() => ShowToast("Something went wrong", Shorten(exception.Message, 180), isError: true));
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.DataBind, action);
        }
    }

    private void NotifyStateDependents()
    {
        OnPropertiesChanged(
            nameof(IsRecording),
            nameof(IsActivelyRecording),
            nameof(IsBusy),
            nameof(CanToggleRecording),
            nameof(CanEditCaptureOptions),
            nameof(IsIdle),
            nameof(RecordButtonLabel),
            nameof(StatusBadge),
            nameof(FooterStatus),
            nameof(StatusTitle),
            nameof(StatusSubtitle),
            nameof(EncodingHint),
            nameof(TimerStatusText),
            nameof(IsTimerWarning));

        ToggleRecordingCommand.NotifyCanExecuteChanged();
    }

    private void NotifyTimerDependents()
    {
        OnPropertiesChanged(
            nameof(TimerStatusText),
            nameof(IsTimerWarning),
            nameof(HasAutoStop),
            nameof(AutoStopFraction));
    }

    private void NotifyAudioDependents()
    {
        OnPropertiesChanged(
            nameof(CaptureAudio),
            nameof(CaptureSystemAudio),
            nameof(CaptureMicrophone),
            nameof(AudioSummary),
            nameof(StatusSubtitle),
            nameof(SessionDetail));
    }

    private static string FormatClock(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }

    private static string Shorten(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown error.";
        }

        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= maxLength
            ? singleLine
            : singleLine[..maxLength] + "…";
    }

    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            _recordingService.StateChanged -= RecordingService_OnStateChanged;
            _recordingService.RecordingCompleted -= RecordingService_OnRecordingCompleted;
            _recordingService.RecordingFailed -= RecordingService_OnRecordingFailed;
            _recordingService.WarningRaised -= RecordingService_OnWarningRaised;
            _settingsService.SettingsChanged -= SettingsService_OnSettingsChanged;

            _toastTimer?.Stop();
            _toastTimer = null;
            Interlocked.Exchange(ref _preferenceSaveCts, null)?.Cancel();
            _preferenceSaveGate.Dispose();
        }

        base.Dispose(disposing);
    }
}
