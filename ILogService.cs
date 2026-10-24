namespace AlexisScreenPro.Interfaces;

public interface ILogService
{
    string LogFilePath { get; }

    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);

    void Debug(string message);
}
