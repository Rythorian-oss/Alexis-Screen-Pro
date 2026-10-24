using System.Diagnostics;
using AlexisScreenPro.Core;
using AlexisScreenPro.Interfaces;
using AlexisScreenPro.Models;

namespace AlexisScreenPro.Services;

/// <summary>
/// Owns every file-system interaction for capture output. All path handling is
/// rooted in the fixed Desktop\ASP directory, and file operations are guarded so
/// Explorer or an antivirus scanner can never crash the app.
/// </summary>
public sealed class FileManagementService : DisposableBase, IFileManagementService
{
    public string OutputDirectory => AppPaths.OutputDirectory;

    public string LogFilePath => AppPaths.LogFilePath;

    public void Initialize()
    {
        ThrowIfDisposed();
        AppPaths.EnsureOutputDirectory();
        CleanupAbandonedPartials();
    }

    public string CreateRecordingFilePath(string? suffix = null)
    {
        ThrowIfDisposed();
        AppPaths.EnsureOutputDirectory();

        var cleanSuffix = SanitizeSuffix(suffix);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var baseName = $"AlexisScreenPro_{stamp}{cleanSuffix}";

        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var name = attempt == 0 ? baseName : $"{baseName}_{attempt + 1}";
            var path = Path.Combine(OutputDirectory, name + AppConstants.RecordingExtension);
            if (!File.Exists(path))
            {
                return path;
            }
        }

        return Path.Combine(
            OutputDirectory,
            $"AlexisScreenPro_{stamp}_{Guid.NewGuid():N}{AppConstants.RecordingExtension}");
    }

    public async Task<IReadOnlyList<RecordingFileInfo>> GetRecentRecordingsAsync(
        int maximumCount = 4,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (maximumCount <= 0)
        {
            return [];
        }

        var directory = OutputDirectory;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return await Task.Run<IReadOnlyList<RecordingFileInfo>>(() =>
        {
            var results = new List<RecordingFileInfo>(maximumCount);
            try
            {
                var files = new DirectoryInfo(directory)
                    .EnumerateFiles("*" + AppConstants.RecordingExtension, SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => file.LastWriteTimeUtc);

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (results.Count >= maximumCount)
                    {
                        break;
                    }

                    try
                    {
                        var length = file.Length;

                        // A sub-16 KB MP4 is an encoder fragment, never a capture
                        // the user can play. Filter before taking the top N so an
                        // orphaned fragment cannot consume a recent-list slot.
                        if (length < 16 * 1024)
                        {
                            continue;
                        }

                        results.Add(new RecordingFileInfo(
                            file.FullName,
                            file.Name,
                            length,
                            file.LastWriteTimeUtc));
                    }
                    catch (IOException)
                    {
                        // File vanished between enumeration and inspection.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Ignore files we cannot inspect.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // A transient file-system issue should produce an empty list,
                // not an application failure.
            }

            return results;
        }, cancellationToken).ConfigureAwait(false);
    }

    public long GetFileSizeSafe(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    public bool TryOpenOutputDirectory(out string? error)
    {
        error = null;
        try
        {
            AppPaths.EnsureOutputDirectory();
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{OutputDirectory}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TryOpenFile(string filePath, out string? error)
    {
        error = null;
        if (!IsInsideOutputDirectory(filePath))
        {
            error = "The requested file is outside the Alexis Screen Pro capture folder.";
            return false;
        }

        if (!File.Exists(filePath))
        {
            error = "That recording no longer exists.";
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    public void TryDeletePartialFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !IsInsideOutputDirectory(filePath))
        {
            return;
        }

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked partial file is harmless and can be removed next launch.
            _ = ex;
        }
    }

    /// <summary>
    /// Removes sub-16 KB MP4 fragments that can be left behind when a hardware
    /// encoder rejects its first frame. These files are never valid captures.
    /// </summary>
    private void CleanupAbandonedPartials()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(
                         OutputDirectory,
                         "AlexisScreenPro_*.mp4",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (new FileInfo(file).Length < 16 * 1024)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file still owned by a dying capture process can wait.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Housekeeping must never prevent startup.
        }
    }

    private bool IsInsideOutputDirectory(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetFullPath(OutputDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string SanitizeSuffix(string? suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(suffix
            .Where(character => !invalid.Contains(character) && character is not '.' and not ' ')
            .Take(24)
            .ToArray());

        return string.IsNullOrEmpty(cleaned) ? string.Empty : "_" + cleaned;
    }
}
