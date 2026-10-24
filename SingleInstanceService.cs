using AlexisScreenPro.Core;
using AlexisScreenPro.Interfaces;

namespace AlexisScreenPro.Services;

/// <summary>
/// Named-mutex single-instance guard. A second launch signals the first process,
/// which brings its hidden dashboard to the foreground, then exits immediately.
/// </summary>
public sealed class SingleInstanceService : DisposableBase
{
    private readonly ILogService _log;
    private Mutex? _mutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private bool _ownsMutex;

    public SingleInstanceService(ILogService log)
    {
        _log = log;
    }

    public bool TryAcquire(Action activationRequested)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(activationRequested);

        try
        {
            _mutex = new Mutex(initiallyOwned: true, AppConstants.SingleInstanceMutexName, out var createdNew);
            _activationEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                AppConstants.ActivationEventName);

            if (!createdNew)
            {
                _activationEvent.Set();
                _mutex.Dispose();
                _mutex = null;
                _activationEvent.Dispose();
                _activationEvent = null;
                return false;
            }

            _ownsMutex = true;
            _listenerCts = new CancellationTokenSource();
            var token = _listenerCts.Token;
            var activation = _activationEvent;
            _listenerTask = Task.Run(
                () => ListenForActivationAsync(activation, activationRequested, token),
                CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            _log.Warn($"Single-instance guard unavailable: {ex.Message}");
            return true;
        }
    }

    private async Task ListenForActivationAsync(
        EventWaitHandle activationEvent,
        Action activationRequested,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var signaled = await Task.Run(
                    () => WaitHandle.WaitAny([activationEvent, cancellationToken.WaitHandle], 1000),
                    CancellationToken.None).ConfigureAwait(false);

                if (signaled == 0)
                {
                    try
                    {
                        activationRequested();
                    }
                    catch (Exception ex)
                    {
                        _log.Error("Activation request could not be dispatched.", ex);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Normal shutdown race.
        }
        catch (Exception ex)
        {
            _log.Error("Single-instance listener stopped unexpectedly.", ex);
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
                _listenerCts?.Cancel();
                _activationEvent?.Set();
                _listenerTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is AggregateException or ObjectDisposedException)
            {
                // Shutdown best effort.
            }
            finally
            {
                _listenerCts?.Dispose();
                _activationEvent?.Dispose();
                if (_mutex is not null)
                {
                    try
                    {
                        if (_ownsMutex)
                        {
                            _mutex.ReleaseMutex();
                        }
                    }
                    catch (ApplicationException)
                    {
                        // Mutex was not owned by this thread; safe to continue.
                    }

                    _mutex.Dispose();
                }
            }
        }

        base.Dispose(disposing);
    }
}
