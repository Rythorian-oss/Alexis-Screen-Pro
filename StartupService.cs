using Microsoft.Win32;
using AlexisScreenPro.Core;
using AlexisScreenPro.Interfaces;

namespace AlexisScreenPro.Services;

/// <summary>
/// Thin, failure-tolerant wrapper around the current user's Run key.
/// </summary>
public sealed class StartupService : DisposableBase, IStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly ILogService _log;

    public StartupService(ILogService log)
    {
        _log = log;
    }

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppConstants.StartupRegistryValueName) is string value
                   && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _log.Warn($"Could not read startup registration: {ex.Message}");
            return false;
        }
    }

    public bool TrySetEnabled(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (key is null)
            {
                error = "Windows startup settings are unavailable for this account.";
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(AppConstants.StartupRegistryValueName, throwOnMissingValue: false);
                _log.Info("Startup registration removed.");
                return true;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || executable.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                error = "Start with Windows must be enabled from the built AlexisScreenPro.exe.";
                return false;
            }

            key.SetValue(
                AppConstants.StartupRegistryValueName,
                $"\"{executable}\" --startup",
                RegistryValueKind.String);

            _log.Info("Startup registration enabled.");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            error = ex.Message;
            _log.Warn($"Could not update startup registration: {ex.Message}");
            return false;
        }
    }

    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}
