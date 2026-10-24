using System.Diagnostics;
using AlexisScreenPro.Core;
using AlexisScreenPro.Interfaces;
using AlexisScreenPro.Models;
using ScreenRecorderLib;
using SrlLogOptions = ScreenRecorderLib.LogOptions;
using SrlRecordingCompleteEventArgs = ScreenRecorderLib.RecordingCompleteEventArgs;
using SrlRecordingFailedEventArgs = ScreenRecorderLib.RecordingFailedEventArgs;
using SrlRecordingStatusEventArgs = ScreenRecorderLib.RecordingStatusEventArgs;
using AspRecordingFailedEventArgs = AlexisScreenPro.Models.RecordingFailedEventArgs;

namespace AlexisScreenPro.Services;

/// <summary>
/// Full-screen capture engine. ScreenRecorderLib performs Desktop Duplication /
/// Windows Graphics Capture and muxes system + microphone audio into H.264 MP4.
///
/// Threading contract:
///  * StartAsync/StopAsync are serialized by a semaphore and never block the UI.
///  * Native callbacks only mark terminal state and queue cleanup.
///  * Progress is pushed through IProgress&lt;T&gt; from a background clock.
///  * Hardware encoder failures during the first seconds transparently retry once
///    with the Media Foundation software encoder.
/// </summary>
public sealed class ScreenRecorderService : DisposableBase, IRecordingService
{
    private static readonly TimeSpan StopCompletionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HardwareFailureRetryWindow = TimeSpan.FromSeconds(4);

    private readonly ILogService _log;
    private readonly IFileManagementService _files;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();

    private Recorder? _recorder;
    private List<RecordingSourceBase> _recordingSources = [];
    private List<AudioSourceBase> _audioSources = [];
    private RecordingState _state = RecordingState.Idle;
    private RecordingRequest? _request;
    private IProgress<RecordingProgress>? _progress;
    private TaskCompletionSource<bool>? _completionSignal;
    private CancellationTokenSource? _progressCts;
    private Task? _progressTask;
    private DateTime _startedAtUtc;
    private Stopwatch? _clock;
    private string? _activeFilePath;
    private bool _stopRequested;
    private bool _terminalHandled;
    private bool _fallbackInProgress;
    private bool _softwareFallbackAttempted;
    private bool _usingSoftwareFallback;
    private RecordingStopReason _stopReason = RecordingStopReason.Manual;

    public ScreenRecorderService(ILogService log, IFileManagementService files)
    {
        _log = log;
        _files = files;
    }

    public RecordingState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public bool IsRecording => State == RecordingState.Recording;

    public bool IsBusy => State != RecordingState.Idle;

    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;

    public event EventHandler<RecordingCompletedEventArgs>? RecordingCompleted;

    public event EventHandler<AspRecordingFailedEventArgs>? RecordingFailed;

    public event EventHandler<RecordingWarningEventArgs>? WarningRaised;

    public async Task StartAsync(
        RecordingRequest request,
        IProgress<RecordingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            throw new ArgumentException("A recording output path is required.", nameof(request));
        }

        if (request.FrameRate is < 1 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Frame rate must be between 1 and 120.");
        }

        if (request.BitrateBitsPerSecond < 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The video bitrate is too low.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("The recording output directory is invalid.", nameof(request));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != RecordingState.Idle)
            {
                throw new InvalidOperationException("A recording is already in progress.");
            }

            Directory.CreateDirectory(directory);

            lock (_sync)
            {
                _request = request;
                _progress = progress;
                _completionSignal = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _stopRequested = false;
                _terminalHandled = false;
                _fallbackInProgress = false;
                _softwareFallbackAttempted = false;
                _usingSoftwareFallback = false;
                _stopReason = RecordingStopReason.Manual;
                _activeFilePath = request.OutputPath;
                _startedAtUtc = DateTime.UtcNow;
                _clock = Stopwatch.StartNew();
            }

            SetState(RecordingState.Starting);
            _log.Info(
                $"Starting capture: {request.FrameRate} FPS, {request.BitrateBitsPerSecond / 1_000_000} Mbps, " +
                $"hardware={request.UseHardwareEncoding}, audio={request.CaptureAudio}, path={request.OutputPath}");

            await Task.Run(
                () => StartRecorderCore(request, request.UseHardwareEncoding),
                CancellationToken.None).ConfigureAwait(false);
            StartProgressLoop();
        }
        catch (Exception ex)
        {
            _log.Error("Recording could not start.", ex);
            CleanupCurrentRecorder();
            TaskCompletionSource<bool>? signal;
            lock (_sync)
            {
                _terminalHandled = true;
                signal = _completionSignal;
            }

            SetState(RecordingState.Idle);
            signal?.TrySetResult(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(
        RecordingStopReason reason = RecordingStopReason.Manual,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (State == RecordingState.Idle)
        {
            return;
        }

        lock (_sync)
        {
            _stopRequested = true;
            _stopReason = reason;
        }

        Recorder? recorder;
        TaskCompletionSource<bool>? signal;

        // Hold the operation gate only long enough to transition state. Waiting
        // for finalization outside the gate allows a pending software-encoder
        // fallback to complete instead of deadlocking against StopAsync.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == RecordingState.Idle)
            {
                return;
            }

            SetState(RecordingState.Stopping);
            CancelProgressLoop();

            lock (_sync)
            {
                recorder = _recorder;
                signal = _completionSignal;
            }
        }
        finally
        {
            _gate.Release();
        }

        _log.Info($"Stopping capture ({reason}).");
        if (recorder is not null)
        {
            try
            {
                await Task.Run(recorder.Stop, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("The capture engine rejected Stop().", ex);
            }
        }

        if (signal is not null)
        {
            try
            {
                await signal.Task
                    .WaitAsync(StopCompletionTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.Error($"Capture finalization exceeded {StopCompletionTimeout.TotalSeconds:0} seconds.");
                await ForceFailureAsync("The recorder did not finalize in time.").ConfigureAwait(false);
            }
        }
    }

    public async Task<AudioDeviceCatalog> GetAudioDevicesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await Task.Run(() =>
        {
            var loopback = new List<AudioDeviceInfo>();
            var microphones = new List<AudioDeviceInfo>();

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var device in Recorder.GetSystemAudioLoopbackDevices())
                {
                    loopback.Add(new AudioDeviceInfo(
                        device.FriendlyName,
                        device.DeviceName,
                        device.IsDefaultDevice,
                        AudioDeviceKind.SystemLoopback));
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"System loopback devices could not be enumerated: {ex.Message}");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var device in Recorder.GetSystemAudioCaptureDevices())
                {
                    microphones.Add(new AudioDeviceInfo(
                        device.FriendlyName,
                        device.DeviceName,
                        device.IsDefaultDevice,
                        AudioDeviceKind.Microphone));
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Microphone devices could not be enumerated: {ex.Message}");
            }

            return new AudioDeviceCatalog
            {
                LoopbackDevices = loopback,
                MicrophoneDevices = microphones
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    private void StartRecorderCore(RecordingRequest request, bool useHardwareEncoding)
    {
        Recorder? recorder = null;
        var recordingSources = new List<RecordingSourceBase>();
        var audioSources = new List<AudioSourceBase>();

        try
        {
            var options = BuildOptions(request, useHardwareEncoding, recordingSources, audioSources);
            recorder = Recorder.CreateRecorder(options);

            recorder.OnRecordingComplete += Recorder_OnRecordingComplete;
            recorder.OnRecordingFailed += Recorder_OnRecordingFailed;
            recorder.OnStatusChanged += Recorder_OnStatusChanged;

            lock (_sync)
            {
                _recorder = recorder;
                _recordingSources = recordingSources;
                _audioSources = audioSources;
                _clock = Stopwatch.StartNew();
                _startedAtUtc = DateTime.UtcNow;
                _activeFilePath = request.OutputPath;
            }

            recorder.Record(request.OutputPath);
            _log.Info("Capture engine accepted the recording request.");
        }
        catch
        {
            if (recorder is not null)
            {
                DetachRecorderHandlers(recorder);
                try
                {
                    recorder.Dispose();
                }
                catch (Exception disposeException)
                {
                    _log.Warn($"Failed to dispose a partially created recorder: {disposeException.Message}");
                }
            }

            foreach (var source in recordingSources)
            {
                try
                {
                    source.Dispose();
                }
                catch (Exception sourceException)
                {
                    _log.Warn($"Failed to dispose a recording source: {sourceException.Message}");
                }
            }

            foreach (var source in audioSources)
            {
                try
                {
                    source.Dispose();
                }
                catch (Exception sourceException)
                {
                    _log.Warn($"Failed to dispose an audio source: {sourceException.Message}");
                }
            }

            lock (_sync)
            {
                if (ReferenceEquals(_recorder, recorder))
                {
                    _recorder = null;
                    _recordingSources = [];
                    _audioSources = [];
                }
            }

            throw;
        }
    }

    private RecorderOptions BuildOptions(
        RecordingRequest request,
        bool useHardwareEncoding,
        List<RecordingSourceBase> recordingSources,
        List<AudioSourceBase> audioSources)
    {
        var mainDisplay = DisplayRecordingSource.MainMonitor
                          ?? throw new InvalidOperationException("No primary display is available for capture.");

        var display = new DisplayRecordingSource(mainDisplay)
        {
            IsCursorCaptureEnabled = request.CaptureCursor,
            IsBorderRequired = false
        };

        recordingSources.Add(display);

        if (request.CaptureAudio)
        {
            if (request.CaptureSystemAudio)
            {
                var loopback = ResolveLoopbackSource(request.SystemAudioDeviceName);
                if (loopback is not null)
                {
                    loopback.IsAudioCaptureEnabled = true;
                    loopback.Volume = 1f;
                    audioSources.Add(loopback);
                }
                else
                {
                    RaiseWarning("No system audio output is available; the recording will continue without it.");
                }
            }

            if (request.CaptureMicrophone)
            {
                var microphone = ResolveMicrophoneSource(request.MicrophoneDeviceName);
                if (microphone is not null)
                {
                    microphone.IsAudioCaptureEnabled = true;
                    microphone.Volume = 1f;
                    audioSources.Add(microphone);
                }
                else
                {
                    RaiseWarning("No microphone is available; the recording will continue without it.");
                }
            }
        }

        RotateCaptureEngineLog();

        return new RecorderOptions
        {
            SourceOptions = new SourceOptions
            {
                RecordingSources = recordingSources
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = audioSources.Count > 0,
                AudioSources = audioSources,
                Bitrate = AudioBitrate.bitrate_192kbps,
                Channels = AudioChannels.Stereo,
                MasterVolume = 1f,
                IsAudioPacketPreviewEnabled = false
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder
                {
                    EncoderProfile = H264Profile.Main,
                    BitrateMode = H264BitrateControlMode.UnconstrainedVBR
                },
                Framerate = request.FrameRate,
                Bitrate = request.BitrateBitsPerSecond,
                Quality = 82,
                IsHardwareEncodingEnabled = useHardwareEncoding,
                IsMp4FastStartEnabled = true,
                IsFixedFramerate = true,
                IsLowLatencyEnabled = false,
                IsThrottlingDisabled = false,
                IsFragmentedMp4Enabled = false
            },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video
            },
            MouseOptions = new MouseOptions
            {
                IsMousePointerEnabled = request.CaptureCursor,
                IsMouseClicksDetected = false
            },
            LogOptions = new SrlLogOptions
            {
                IsLogEnabled = true,
                LogFilePath = AppPaths.CaptureLogFilePath,
                LogSeverityLevel = LogLevel.Warn
            }
        };
    }

    private LoopbackAudioSource? ResolveLoopbackSource(string? requestedDeviceName)
    {
        try
        {
            var devices = Recorder.GetSystemAudioLoopbackDevices();
            if (!string.IsNullOrWhiteSpace(requestedDeviceName))
            {
                var requested = devices.FirstOrDefault(device =>
                    string.Equals(device.DeviceName, requestedDeviceName, StringComparison.OrdinalIgnoreCase));

                if (requested is not null)
                {
                    return new LoopbackAudioSource(requested.DeviceName);
                }

                RaiseWarning("The selected system audio device is offline; using the Windows default instead.");
            }

            var defaultDevice = devices.FirstOrDefault(device => device.IsDefaultDevice)
                                ?? devices.FirstOrDefault();

            return defaultDevice is not null
                ? new LoopbackAudioSource(defaultDevice.DeviceName)
                : LoopbackAudioSource.Default;
        }
        catch (Exception ex)
        {
            _log.Warn($"System audio could not be resolved: {ex.Message}");
            return null;
        }
    }

    private CaptureAudioSource? ResolveMicrophoneSource(string? requestedDeviceName)
    {
        try
        {
            var devices = Recorder.GetSystemAudioCaptureDevices();
            if (!string.IsNullOrWhiteSpace(requestedDeviceName))
            {
                var requested = devices.FirstOrDefault(device =>
                    string.Equals(device.DeviceName, requestedDeviceName, StringComparison.OrdinalIgnoreCase));

                if (requested is not null)
                {
                    return new CaptureAudioSource(requested.DeviceName);
                }

                RaiseWarning("The selected microphone is offline; using the Windows default instead.");
            }

            var defaultDevice = devices.FirstOrDefault(device => device.IsDefaultDevice)
                                ?? devices.FirstOrDefault();

            return defaultDevice is not null
                ? new CaptureAudioSource(defaultDevice.DeviceName)
                : CaptureAudioSource.Default;
        }
        catch (Exception ex)
        {
            _log.Warn($"Microphone audio could not be resolved: {ex.Message}");
            return null;
        }
    }

    private void StartProgressLoop()
    {
        CancellationTokenSource cts;
        CancellationTokenSource? previousCts;
        Task? previousTask;

        lock (_sync)
        {
            previousCts = _progressCts;
            previousTask = _progressTask;
            previousCts?.Cancel();

            cts = new CancellationTokenSource();
            _progressCts = cts;
        }

        if (previousCts is not null)
        {
            if (previousTask is not null)
            {
                _ = previousTask.ContinueWith(
                    _ => previousCts.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                previousCts.Dispose();
            }
        }

        _progressTask = Task.Run(() => RunProgressLoopAsync(cts.Token), CancellationToken.None);
    }

    private async Task RunProgressLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                RecordingRequest? request;
                Stopwatch? clock;
                lock (_sync)
                {
                    request = _request;
                    clock = _clock;
                }

                if (request is null || clock is null)
                {
                    return;
                }

                var elapsed = clock.Elapsed;
                TimeSpan? remaining = null;
                double? fraction = null;
                var timerExpired = false;

                if (request.AutoStopAfter is { TotalSeconds: > 0 } autoStop)
                {
                    var left = autoStop - elapsed;
                    if (left < TimeSpan.Zero)
                    {
                        left = TimeSpan.Zero;
                    }

                    remaining = left;
                    fraction = Math.Clamp(elapsed.TotalSeconds / autoStop.TotalSeconds, 0d, 1d);
                    timerExpired = left <= TimeSpan.Zero;
                }

                var progress = new RecordingProgress
                {
                    State = State,
                    Elapsed = elapsed,
                    TimeRemaining = remaining,
                    AutoStopFraction = fraction,
                    StatusHint = _usingSoftwareFallback ? "Software encoder" : "Hardware encoder"
                };

                try
                {
                    _progress?.Report(progress);
                }
                catch (Exception ex)
                {
                    _log.Warn($"A progress subscriber failed: {ex.Message}");
                }

                if (timerExpired)
                {
                    _log.Info("Auto-stop timer elapsed; finalizing the recording.");
                    _ = Task.Run(
                        async () =>
                        {
                            try
                            {
                                await StopAsync(RecordingStopReason.AutoStopTimer).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _log.Error("Auto-stop failed.", ex);
                            }
                        },
                        CancellationToken.None);
                    return;
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        catch (Exception ex)
        {
            _log.Error("The recording progress clock stopped unexpectedly.", ex);
        }
    }

    private void Recorder_OnStatusChanged(object? sender, SrlRecordingStatusEventArgs e)
    {
        if (!IsCurrentRecorder(sender))
        {
            return;
        }

        switch (e.Status)
        {
            case RecorderStatus.Recording:
                SetState(RecordingState.Recording);
                break;
            case RecorderStatus.Finishing:
                SetState(RecordingState.Stopping);
                break;
            case RecorderStatus.Idle:
                // Completion/failure callbacks own the final state transition.
                break;
            case RecorderStatus.Paused:
                break;
        }
    }

    private void Recorder_OnRecordingComplete(object? sender, SrlRecordingCompleteEventArgs e)
    {
        if (sender is not Recorder recorder || !IsCurrentRecorder(recorder))
        {
            return;
        }

        TaskCompletionSource<bool>? signal;
        string path;
        DateTime startedAt;
        TimeSpan duration;
        bool softwareFallback;
        RecordingStopReason reason;

        lock (_sync)
        {
            if (_terminalHandled || !ReferenceEquals(_recorder, recorder))
            {
                return;
            }

            _terminalHandled = true;
            signal = _completionSignal;
            path = string.IsNullOrWhiteSpace(e.FilePath)
                ? _activeFilePath ?? string.Empty
                : e.FilePath;
            startedAt = _startedAtUtc;
            duration = _clock?.Elapsed ?? TimeSpan.Zero;
            softwareFallback = _usingSoftwareFallback;
            reason = _stopReason;
        }

        _log.Info($"Capture complete: {path} ({duration:mm\\:ss}).");
        _ = Task.Run(
            () => FinalizeSuccessAsync(recorder, path, startedAt, duration, softwareFallback, reason, signal),
            CancellationToken.None);
    }

    private void Recorder_OnRecordingFailed(object? sender, SrlRecordingFailedEventArgs e)
    {
        if (sender is not Recorder recorder || !IsCurrentRecorder(recorder))
        {
            return;
        }

        var error = string.IsNullOrWhiteSpace(e.Error) ? "The capture engine reported an unknown error." : e.Error;
        bool retry;
        TaskCompletionSource<bool>? signal;

        lock (_sync)
        {
            if (_terminalHandled
                || _fallbackInProgress
                || !ReferenceEquals(_recorder, recorder))
            {
                return;
            }

            retry = ShouldFallBackToSoftware(error);
            if (retry)
            {
                _fallbackInProgress = true;
            }
            else
            {
                _terminalHandled = true;
            }

            signal = _completionSignal;
        }

        _log.Error($"Capture engine failure: {error}");

        if (retry)
        {
            RaiseWarning("The hardware encoder failed; retrying with the software encoder.");
            _ = Task.Run(() => RetryWithSoftwareAsync(recorder, error), CancellationToken.None);
            return;
        }

        _ = Task.Run(
            () => FinalizeFailureAsync(recorder, error, signal),
            CancellationToken.None);
    }

    private bool ShouldFallBackToSoftware(string error)
    {
        if (_stopRequested
            || _softwareFallbackAttempted
            || _request is null
            || !_request.UseHardwareEncoding)
        {
            return false;
        }

        var elapsed = _clock?.Elapsed ?? TimeSpan.Zero;
        if (elapsed > HardwareFailureRetryWindow)
        {
            return false;
        }

        var normalized = error.ToLowerInvariant();
        return normalized.Contains("encoder", StringComparison.Ordinal)
               || normalized.Contains("0xc00d36b5", StringComparison.Ordinal)
               || normalized.Contains("hardware", StringComparison.Ordinal)
               || normalized.Contains("callee", StringComparison.Ordinal);
    }

    private async Task RetryWithSoftwareAsync(Recorder failedRecorder, string hardwareError)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsDisposed)
            {
                return;
            }

            TaskCompletionSource<bool>? signal;
            string? failedPath;
            bool shouldAbort;

            lock (_sync)
            {
                if (!ReferenceEquals(_recorder, failedRecorder))
                {
                    return;
                }

                shouldAbort = _stopRequested;
                signal = _completionSignal;
                failedPath = _activeFilePath;
            }

            if (shouldAbort)
            {
                _ = Task.Run(
                    () => FinalizeFailureAsync(failedRecorder, hardwareError, signal),
                    CancellationToken.None);
                return;
            }

            CleanupRecorder(failedRecorder);

            if (!string.IsNullOrWhiteSpace(failedPath))
            {
                var partialSize = _files.GetFileSizeSafe(failedPath);
                if (partialSize < 16 * 1024)
                {
                    _files.TryDeletePartialFile(failedPath);
                    SchedulePartialCleanup(failedPath);
                }
            }

            var retryPath = _files.CreateRecordingFilePath("software");
            var retryRequest = _request! with
            {
                OutputPath = retryPath,
                UseHardwareEncoding = false,
                AutoStopAfter = _request.AutoStopAfter
            };

            lock (_sync)
            {
                _request = retryRequest;
                _activeFilePath = retryPath;
                _softwareFallbackAttempted = true;
                _usingSoftwareFallback = true;
                _fallbackInProgress = false;
                _terminalHandled = false;
                _clock = Stopwatch.StartNew();
                _startedAtUtc = DateTime.UtcNow;
            }

            _log.Warn("Hardware encoding failed; switching to the software Media Foundation encoder.");
            SetState(RecordingState.Starting);
            ReportTransientStatus("Switching to software encoder…");
            await Task.Run(
                () => StartRecorderCore(retryRequest, useHardwareEncoding: false),
                CancellationToken.None).ConfigureAwait(false);
            StartProgressLoop();
        }
        catch (Exception ex)
        {
            _log.Error("Software encoder fallback failed.", ex);
            TaskCompletionSource<bool>? signal;
            lock (_sync)
            {
                _terminalHandled = true;
                _fallbackInProgress = false;
                signal = _completionSignal;
            }

            var recorder = GetCurrentRecorder();
            _ = Task.Run(
                () => FinalizeFailureAsync(recorder, $"Hardware and software encoders both failed. {ex.Message}", signal),
                CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FinalizeSuccessAsync(
        Recorder recorder,
        string path,
        DateTime startedAt,
        TimeSpan duration,
        bool softwareFallback,
        RecordingStopReason stopReason,
        TaskCompletionSource<bool>? signal)
    {
        try
        {
            await Task.Run(() => CleanupRecorder(recorder), CancellationToken.None).ConfigureAwait(false);
            SetState(RecordingState.Idle);

            var size = _files.GetFileSizeSafe(path);
            var result = new RecordingResult
            {
                FilePath = path,
                StartedAtUtc = startedAt,
                Duration = duration,
                SizeBytes = size,
                StopReason = stopReason,
                UsedSoftwareFallback = softwareFallback
            };

            _log.Info($"Saved recording ({size:N0} bytes): {path}");
            Raise(RecordingCompleted, new RecordingCompletedEventArgs(result), nameof(RecordingCompleted));
            signal?.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _log.Error("Recording finalization failed.", ex);
            SetState(RecordingState.Idle);
            Raise(
                RecordingFailed,
                new AspRecordingFailedEventArgs(ex.Message, path),
                nameof(RecordingFailed));
            signal?.TrySetResult(false);
        }
    }

    private async Task FinalizeFailureAsync(
        Recorder? recorder,
        string error,
        TaskCompletionSource<bool>? signal)
    {
        try
        {
            if (recorder is not null)
            {
                await Task.Run(() => CleanupRecorder(recorder), CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                CleanupCurrentRecorder();
            }

            SetState(RecordingState.Idle);

            string? path;
            lock (_sync)
            {
                path = _activeFilePath;
                _fallbackInProgress = false;
            }

            if (!string.IsNullOrWhiteSpace(path) && _files.GetFileSizeSafe(path) < 16 * 1024)
            {
                _files.TryDeletePartialFile(path);
                SchedulePartialCleanup(path);
            }

            _log.Error($"Recording failed: {error}");
            Raise(RecordingFailed, new AspRecordingFailedEventArgs(error, path), nameof(RecordingFailed));
            signal?.TrySetResult(false);
        }
        catch (Exception ex)
        {
            _log.Error("Failure cleanup raised another exception.", ex);
            SetState(RecordingState.Idle);
            signal?.TrySetResult(false);
        }
    }

    private async Task ForceFailureAsync(string error)
    {
        TaskCompletionSource<bool>? signal;
        Recorder? recorder;
        lock (_sync)
        {
            if (_terminalHandled)
            {
                return;
            }

            _terminalHandled = true;
            signal = _completionSignal;
            recorder = _recorder;
        }

        _log.Error(error);
        await FinalizeFailureAsync(recorder, error, signal).ConfigureAwait(false);
    }

    private void SchedulePartialCleanup(string path)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    // Media Foundation can keep the broken first file open for a
                    // short time after the encoder callback returns. Retry a few
                    // times instead of leaving a fragment behind until next launch.
                    for (var attempt = 0; attempt < 5; attempt++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                        if (!File.Exists(path))
                        {
                            return;
                        }

                        _files.TryDeletePartialFile(path);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"Deferred partial-file cleanup failed: {ex.Message}");
                }
            },
            CancellationToken.None);
    }

    private void CleanupCurrentRecorder()
    {
        Recorder? recorder;
        lock (_sync)
        {
            recorder = _recorder;
        }

        if (recorder is not null)
        {
            CleanupRecorder(recorder);
        }
    }

    private void CleanupRecorder(Recorder recorder)
    {
        DetachRecorderHandlers(recorder);

        List<RecordingSourceBase> recordingSources;
        List<AudioSourceBase> audioSources;
        lock (_sync)
        {
            _clock?.Stop();
            _progressCts?.Cancel();
            recordingSources = _recordingSources;
            _recordingSources = [];
            audioSources = _audioSources;
            _audioSources = [];

            if (ReferenceEquals(_recorder, recorder))
            {
                _recorder = null;
            }
        }

        try
        {
            recorder.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn($"Recorder disposal raised: {ex.Message}");
        }

        foreach (var source in recordingSources)
        {
            try
            {
                source.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn($"Recording source disposal raised: {ex.Message}");
            }
        }

        foreach (var source in audioSources)
        {
            try
            {
                source.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn($"Audio source disposal raised: {ex.Message}");
            }
        }
    }

    private void DetachRecorderHandlers(Recorder recorder)
    {
        try
        {
            recorder.OnRecordingComplete -= Recorder_OnRecordingComplete;
            recorder.OnRecordingFailed -= Recorder_OnRecordingFailed;
            recorder.OnStatusChanged -= Recorder_OnStatusChanged;
        }
        catch (Exception ex)
        {
            _log.Warn($"Recorder events could not be detached: {ex.Message}");
        }
    }

    private bool IsCurrentRecorder(object? sender)
    {
        if (sender is not Recorder recorder)
        {
            return false;
        }

        lock (_sync)
        {
            return ReferenceEquals(_recorder, recorder);
        }
    }

    private Recorder? GetCurrentRecorder()
    {
        lock (_sync)
        {
            return _recorder;
        }
    }

    private void SetState(RecordingState newState)
    {
        lock (_sync)
        {
            if (_state == newState)
            {
                return;
            }

            _state = newState;
        }

        Raise(StateChanged, new RecordingStateChangedEventArgs(newState), nameof(StateChanged));
    }

    private void ReportTransientStatus(string hint)
    {
        var progress = new RecordingProgress
        {
            State = State,
            Elapsed = _clock?.Elapsed ?? TimeSpan.Zero,
            StatusHint = hint
        };

        try
        {
            _progress?.Report(progress);
        }
        catch (Exception ex)
        {
            _log.Warn($"A progress subscriber failed: {ex.Message}");
        }
    }

    private void RaiseWarning(string message)
    {
        _log.Warn(message);
        Raise(WarningRaised, new RecordingWarningEventArgs(message), nameof(WarningRaised));
    }

    private void Raise<T>(EventHandler<T>? handler, T args, string eventName)
        where T : EventArgs
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, args);
        }
        catch (Exception ex)
        {
            _log.Error($"A {eventName} subscriber threw an exception.", ex);
        }
    }

    private void CancelProgressLoop()
    {
        lock (_sync)
        {
            _progressCts?.Cancel();
        }
    }

    private static void RotateCaptureEngineLog()
    {
        try
        {
            var info = new FileInfo(AppPaths.CaptureLogFilePath);
            if (!info.Exists || info.Length < 8 * 1024 * 1024)
            {
                return;
            }

            var archive = Path.Combine(
                info.DirectoryName!,
                $"{Path.GetFileNameWithoutExtension(info.Name)}-previous{info.Extension}");
            File.Move(info.FullName, archive, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Capture engine log rotation skipped: {ex.Message}");
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
            try
            {
                CancelProgressLoop();

                Recorder? recorder;
                lock (_sync)
                {
                    recorder = _recorder;
                    _terminalHandled = true;
                }

                if (recorder is not null)
                {
                    try
                    {
                        recorder.Stop();
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"Recorder stop during dispose failed: {ex.Message}");
                    }

                    CleanupRecorder(recorder);
                }

                TaskCompletionSource<bool>? signal;
                CancellationTokenSource? progressCts;
                Task? progressTask;
                lock (_sync)
                {
                    signal = _completionSignal;
                    progressCts = _progressCts;
                    progressTask = _progressTask;
                }

                progressCts?.Cancel();
                try
                {
                    progressTask?.Wait(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex) when (ex is AggregateException or ObjectDisposedException)
                {
                    // Progress task is already unwinding.
                }

                signal?.TrySetResult(false);
                progressCts?.Dispose();
                _gate.Dispose();
            }
            catch (Exception ex)
            {
                _log.Error("Recorder service disposal failed.", ex);
            }
        }

        base.Dispose(disposing);
    }
}
