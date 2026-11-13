using Microsoft.Extensions.Logging;

namespace WinMirroring.Screen.Tests;

/// <summary>Writes every log line to a file, for live tests whose output is hard to see otherwise.</summary>
internal sealed class FileLogger<T>(string path) : ILogger<T>
{
    private static readonly object Gate = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Gate)
        {
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {typeof(T).Name}: {formatter(state, exception)}{(exception is null ? string.Empty : " | " + exception)}{Environment.NewLine}");
        }
    }
}
