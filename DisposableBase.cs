namespace AlexisScreenPro.Core;

/// <summary>
/// Implements the conventional .NET dispose pattern. Managed hooks are released
/// deterministically; the finalizer is only a safety net for the native capture
/// handles owned by derived services. Every service calls GC.SuppressFinalize on
/// normal disposal.
/// </summary>
public abstract class DisposableBase : IDisposable
{
    private bool _disposed;

    ~DisposableBase()
    {
        Dispose(disposing: false);
    }

    public bool IsDisposed => _disposed;

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }

    protected void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
