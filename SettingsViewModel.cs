using AlexisScreenPro.Core;
using AlexisScreenPro.Interfaces;
using AlexisScreenPro.Models;
using System.Collections.ObjectModel;

namespace AlexisScreenPro.ViewModels;

public sealed class SettingsCloseRequestedEventArgs(bool saved) : EventArgs
{
    public bool Saved { get; } = saved;
}

/// <summary>
/// Editable copy of the persisted settings. Nothing is applied until Save is
/// pressed, so Cancel truly cancels.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IRecordingService _recordingService;
    private readonly IStartupService _startupService;
    private readonly IFileManagementService _fileService;
    private readonly ILogService _log;

    private AppSettings _draft;
    private bool _isSaving;
    private bool _isLoadingDevices;
    private string? _validationMessage;
    private AudioDeviceOption _selectedSystemAudioOption;
    private AudioDeviceOption _selectedMicrophoneOption;

    public SettingsViewModel(
        ISettingsService settingsService,
        IRecordingService recordingService,
        IStartupService startupService,
        IFileManagementService fileService,
        ILogService log)
    {
        _settingsService = settingsService;
        _recordingService = recordingService;
        _startupService = startupService;
        _fileService = fileService;
        _log = log;

        _draft = _settingsService.Current.Clone();
        _draft.LaunchOnStartup = _startupService.IsEnabled();

        FrameRateChoices = new ObservableCollection<int>(AppConstants.FrameRateChoices);
        BitrateChoicesMbps = new ObservableCollection<int>(AppConstants.BitrateChoicesMbps);
        AutoStopOptions = AutoStopOption.All;

        SystemAudioDevices = [];
        MicrophoneDevices = [];

        _selectedSystemAudioOption = new AudioDeviceOption(null, "System default", true);
        _selectedMicrophoneOption = new AudioDeviceOption(null, "Microphone default", true);
        SystemAudioDevices.Add(_selectedSystemAudioOption);
        MicrophoneDevices.Add(_selectedMicrophoneOption);

        SaveCommand = new AsyncRelayCommand(
            SaveAsync,
            () => !IsSaving,
            exception =>
            {
                _log.Error("Settings save command failed.", exception);
                SetValidation($"Settings could not be saved: {exception.Message}");
            });

        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, new SettingsCloseRequestedEventArgs(false)));
        ResetCommand = new RelayCommand(ResetToDefaults);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
    }

    public event EventHandler<SettingsCloseRequestedEventArgs>? CloseRequested;

    public ObservableCollection<int> FrameRateChoices { get; }

    public ObservableCollection<int> BitrateChoicesMbps { get; }

    public IReadOnlyList<AutoStopOption> AutoStopOptions { get; }

    public ObservableCollection<AudioDeviceOption> SystemAudioDevices { get; }

    public ObservableCollection<AudioDeviceOption> MicrophoneDevices { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand ResetCommand { get; }

    public RelayCommand OpenOutputFolderCommand { get; }

    public string OutputDirectory => _fileService.OutputDirectory;

    public string OutputFolderDisplay => $@"Desktop\{AppConstants.OutputFolderName}";

    public string AppVersion => $"Version {AppConstants.Version}";

    public bool CaptureAudio
    {
        get => _draft.CaptureAudio;
        set
        {
            if (_draft.CaptureAudio == value)
            {
                return;
            }

            _draft.CaptureAudio = value;
            if (value && !_draft.CaptureSystemAudio && !_draft.CaptureMicrophone)
            {
                _draft.CaptureSystemAudio = true;
            }

            OnPropertiesChanged(
                nameof(CaptureAudio),
                nameof(CaptureSystemAudio),
                nameof(CaptureMicrophone),
                nameof(CanSelectSystemDevice),
                nameof(CanSelectMicrophoneDevice));
            ClearValidation();
        }
    }

    public bool CaptureSystemAudio
    {
        get => _draft.CaptureSystemAudio;
        set
        {
            if (_draft.CaptureSystemAudio == value)
            {
                return;
            }

            _draft.CaptureSystemAudio = value;
            if (value)
            {
                _draft.CaptureAudio = true;
            }
            else if (!_draft.CaptureMicrophone)
            {
                _draft.CaptureAudio = false;
            }

            OnPropertiesChanged(
                nameof(CaptureAudio),
                nameof(CaptureSystemAudio),
                nameof(CanSelectSystemDevice));
            ClearValidation();
        }
    }

    public bool CaptureMicrophone
    {
        get => _draft.CaptureMicrophone;
        set
        {
            if (_draft.CaptureMicrophone == value)
            {
                return;
            }

            _draft.CaptureMicrophone = value;
            if (value)
            {
                _draft.CaptureAudio = true;
            }
            else if (!_draft.CaptureSystemAudio)
            {
                _draft.CaptureAudio = false;
            }

            OnPropertiesChanged(
                nameof(CaptureAudio),
                nameof(CaptureMicrophone),
                nameof(CanSelectMicrophoneDevice));
            ClearValidation();
        }
    }

    public bool CaptureCursor
    {
        get => _draft.CaptureCursor;
        set
        {
            if (_draft.CaptureCursor == value)
            {
                return;
            }

            _draft.CaptureCursor = value;
            OnPropertyChanged();
        }
    }

    public bool UseHardwareEncoding
    {
        get => _draft.UseHardwareEncoding;
        set
        {
            if (_draft.UseHardwareEncoding == value)
            {
                return;
            }

            _draft.UseHardwareEncoding = value;
            OnPropertyChanged();
        }
    }

    public bool StartMinimized
    {
        get => _draft.StartMinimized;
        set
        {
            if (_draft.StartMinimized == value)
            {
                return;
            }

            _draft.StartMinimized = value;
            OnPropertyChanged();
        }
    }

    public bool ShowCompletionNotifications
    {
        get => _draft.ShowCompletionNotifications;
        set
        {
            if (_draft.ShowCompletionNotifications == value)
            {
                return;
            }

            _draft.ShowCompletionNotifications = value;
            OnPropertyChanged();
        }
    }

    public bool LaunchOnStartup
    {
        get => _draft.LaunchOnStartup;
        set
        {
            if (_draft.LaunchOnStartup == value)
            {
                return;
            }

            _draft.LaunchOnStartup = value;
            OnPropertyChanged();
        }
    }

    public int FrameRate
    {
        get => _draft.FrameRate;
        set
        {
            if (_draft.FrameRate == value)
            {
                return;
            }

            _draft.FrameRate = value;
            OnPropertyChanged();
        }
    }

    public int BitrateMbps
    {
        get => _draft.BitrateMbps;
        set
        {
            if (_draft.BitrateMbps == value)
            {
                return;
            }

            _draft.BitrateMbps = value;
            OnPropertyChanged();
        }
    }

    public AutoStopOption SelectedAutoStopOption
    {
        get => AutoStopOption.FromMinutes(_draft.AutoStopMinutes);
        set
        {
            if (value is null || _draft.AutoStopMinutes == value.Minutes)
            {
                return;
            }

            _draft.AutoStopMinutes = value.Minutes;
            OnPropertyChanged();
        }
    }

    public AudioDeviceOption SelectedSystemAudioOption
    {
        get => _selectedSystemAudioOption;
        set
        {
            if (value is null || !SetProperty(ref _selectedSystemAudioOption, value))
            {
                return;
            }

            _draft.SystemAudioDeviceName = value.DeviceName;
        }
    }

    public AudioDeviceOption SelectedMicrophoneOption
    {
        get => _selectedMicrophoneOption;
        set
        {
            if (value is null || !SetProperty(ref _selectedMicrophoneOption, value))
            {
                return;
            }

            _draft.MicrophoneDeviceName = value.DeviceName;
        }
    }

    public bool CanSelectSystemDevice => CaptureAudio && CaptureSystemAudio;
    public bool CanSelectMicrophoneDevice => CaptureAudio && CaptureMicrophone;

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (!SetProperty(ref _isSaving, value))
            {
                return;
            }

            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsLoadingDevices
    {
        get => _isLoadingDevices;
        private set => SetProperty(ref _isLoadingDevices, value);
    }

    public string? ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (!SetProperty(ref _validationMessage, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasValidationMessage));
            OnPropertyChanged(nameof(HasSystemDevices));
            OnPropertyChanged(nameof(HasMicrophoneDevices));
        }
    }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public bool HasSystemDevices => SystemAudioDevices.Count > 1;
    public bool HasMicrophoneDevices => MicrophoneDevices.Count > 1;

    public string DeviceSummary
    {
        get
        {
            if (IsLoadingDevices)
            {
                return "Scanning Windows audio endpoints…";
            }

            return $"{Math.Max(0, SystemAudioDevices.Count - 1)} outputs · " +
                   $"{Math.Max(0, MicrophoneDevices.Count - 1)} microphones";
        }
    }

    public async Task InitializeAsync()
    {
        if (IsLoadingDevices)
        {
            return;
        }

        IsLoadingDevices = true;
        OnPropertyChanged(nameof(DeviceSummary));

        try
        {
            var catalog = await _recordingService.GetAudioDevicesAsync().ConfigureAwait(true);
            RebuildDeviceLists(catalog);
        }
        catch (Exception ex)
        {
            _log.Warn($"Audio devices could not be enumerated for settings: {ex.Message}");
            SetValidation("Audio devices could not be enumerated. Windows defaults will be used.");
        }
        finally
        {
            IsLoadingDevices = false;
            OnPropertyChanged(nameof(DeviceSummary));
            OnPropertyChanged(nameof(HasSystemDevices));
            OnPropertyChanged(nameof(HasMicrophoneDevices));
        }
    }

    private void RebuildDeviceLists(AudioDeviceCatalog catalog)
    {
        var selectedSystem = _draft.SystemAudioDeviceName;
        var selectedMicrophone = _draft.MicrophoneDeviceName;

        SystemAudioDevices.Clear();
        SystemAudioDevices.Add(new AudioDeviceOption(null, "System default", true));
        foreach (var device in catalog.LoopbackDevices)
        {
            SystemAudioDevices.Add(new AudioDeviceOption(device.DeviceName, device.DisplayName));
        }

        MicrophoneDevices.Clear();
        MicrophoneDevices.Add(new AudioDeviceOption(null, "Microphone default", true));
        foreach (var device in catalog.MicrophoneDevices)
        {
            MicrophoneDevices.Add(new AudioDeviceOption(device.DeviceName, device.DisplayName));
        }

        _selectedSystemAudioOption = SystemAudioDevices.FirstOrDefault(option =>
                                        string.Equals(option.DeviceName, selectedSystem, StringComparison.OrdinalIgnoreCase))
                                    ?? SystemAudioDevices[0];
        _selectedMicrophoneOption = MicrophoneDevices.FirstOrDefault(option =>
                                          string.Equals(option.DeviceName, selectedMicrophone, StringComparison.OrdinalIgnoreCase))
                                      ?? MicrophoneDevices[0];

        OnPropertiesChanged(
            nameof(SelectedSystemAudioOption),
            nameof(SelectedMicrophoneOption),
            nameof(HasSystemDevices),
            nameof(HasMicrophoneDevices));
    }

    private async Task SaveAsync()
    {
        if (IsSaving)
        {
            return;
        }

        ClearValidation();

        if (CaptureAudio && !CaptureSystemAudio && !CaptureMicrophone)
        {
            SetValidation("Choose system audio, a microphone, or turn audio off.");
            return;
        }

        if (FrameRate is < 1 or > 120)
        {
            SetValidation("Frame rate must be between 1 and 120.");
            return;
        }

        if (BitrateMbps is < 1 or > 100)
        {
            SetValidation("Video bitrate must be between 1 and 100 Mbps.");
            return;
        }

        IsSaving = true;
        try
        {
            _draft.Normalize();
            if (_startupService.IsEnabled() != _draft.LaunchOnStartup
                && !_startupService.TrySetEnabled(_draft.LaunchOnStartup, out var startupError))
            {
                SetValidation(startupError ?? "Windows startup could not be updated.");
                return;
            }

            await _settingsService.SaveAsync(_draft.Clone()).ConfigureAwait(true);
            _log.Info("Studio settings applied.");
            CloseRequested?.Invoke(this, new SettingsCloseRequestedEventArgs(true));
        }
        catch (Exception ex)
        {
            _log.Error("Studio settings could not be saved.", ex);
            SetValidation($"Settings could not be saved: {ex.Message}");
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void ResetToDefaults()
    {
        _draft = AppSettings.CreateDefault();
        _draft.LaunchOnStartup = _startupService.IsEnabled();
        _selectedSystemAudioOption = SystemAudioDevices.FirstOrDefault() ?? new AudioDeviceOption(null, "System default", true);
        _selectedMicrophoneOption = MicrophoneDevices.FirstOrDefault() ?? new AudioDeviceOption(null, "Microphone default", true);

        ClearValidation();
        OnPropertiesChanged(
            nameof(CaptureAudio),
            nameof(CaptureSystemAudio),
            nameof(CaptureMicrophone),
            nameof(CaptureCursor),
            nameof(UseHardwareEncoding),
            nameof(StartMinimized),
            nameof(ShowCompletionNotifications),
            nameof(LaunchOnStartup),
            nameof(FrameRate),
            nameof(BitrateMbps),
            nameof(SelectedAutoStopOption),
            nameof(SelectedSystemAudioOption),
            nameof(SelectedMicrophoneOption),
            nameof(CanSelectSystemDevice),
            nameof(CanSelectMicrophoneDevice));
    }

    private void OpenOutputFolder()
    {
        if (_fileService.TryOpenOutputDirectory(out var error))
        {
            return;
        }

        SetValidation(error ?? "The recordings folder could not be opened.");
    }

    private void SetValidation(string message)
    {
        ValidationMessage = message;
    }

    private void ClearValidation()
    {
        if (ValidationMessage is not null)
        {
            ValidationMessage = null;
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
            SaveCommand.NotifyCanExecuteChanged();
        }

        base.Dispose(disposing);
    }
}
