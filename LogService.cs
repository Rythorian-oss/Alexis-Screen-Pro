using AlexisScreenPro.Core;
using AlexisScreenPro.Interfaces;
using System.Collections.Concurrent;
using System.Text;

namespace AlexisScreenPro.Services;

/// <summary>
/// Non-blocking, append-only file logger. Messages are queued so a slow disk can
/// never stall the capture thread or the UI. The log lives beside the recordings
/// in Desktop\ASP as required.
/// </summary>
public sealed class LogService : DisposableBase, ILogService
{
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 4096);
    private readonly Task _writerTask;

    public LogService(string logFilePath)
    {
        if (string.IsNullOrWhiteSpace(logFilePath))
        {
            throw new ArgumentException("A log path is required.", nameof(logFilePath));
        }

        LogFilePath = logFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
        RotateIfNeeded(logFilePath);
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public string LogFilePath { get; }

    public void Debug(string message) => Enqueue("DEBUG", message, null);

    public void Info(string message) => Enqueue("INFO ", message, null);

    public void Warn(string message) => Enqueue("WARN ", message, null);

    public void Error(string message, Exception? exception = null) => Enqueue("ERROR", message, exception);

    private void Enqueue(string level, string message, Exception? exception)
    {
        if (IsDisposed || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var builder = new StringBuilder(160);
        builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        builder.Append(" [").Append(level).Append("] ").Append(message);

        if (exception is not null)
        {
            builder.AppendLine();
            builder.Append("  ").Append(exception.GetType().FullName).Append(": ").Append(exception.Message);
            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            {
                builder.AppendLine().Append(exception.StackTrace);
            }

            if (exception.InnerException is not null)
            {
                builder.AppendLine().Append("  Inner: ").Append(exception.InnerException.Message);
            }
        }

        _queue.TryAdd(builder.ToString());
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await using var stream = new FileStream(
                LogFilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };

            foreach (var line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // A locked log file must never take the application down.
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Last-resort logging failure: no recursive logging possible.
            System.Diagnostics.Debug.WriteLine($"Alexis Screen Pro logger failed: {ex}");
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < AppConstants.MaxLogBytes)
            {
                return;
            }

            var archive = Path.Combine(
                info.DirectoryName!,
                $"{Path.GetFileNameWithoutExtension(path)}-previous{Path.GetExtension(path)}");

            File.Move(path, archive, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Alexis Screen Pro log rotation skipped: {ex.Message}");
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
                _queue.CompleteAdding();
                if (!_writerTask.Wait(TimeSpan.FromSeconds(3)))
                {
                    System.Diagnostics.Debug.WriteLine("Alexis Screen Pro logger did not flush within 3 seconds.");
                }
            }
            catch (Exception ex) when (ex is AggregateException or ObjectDisposedException)
            {
                System.Diagnostics.Debug.WriteLine($"Alexis Screen Pro logger shutdown: {ex.Message}");
            }
            finally
            {
                _queue.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
