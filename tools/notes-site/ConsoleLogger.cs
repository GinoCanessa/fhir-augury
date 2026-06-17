using Microsoft.Extensions.Logging;

namespace FhirAugury.Tools.NotesSite;

/// <summary>
/// Minimal <see cref="ILogger"/> that writes warnings and errors to stderr.
/// Avoids pulling in a console-logging package; info/debug are dropped to keep
/// the tool's stdout reserved for progress lines.
/// </summary>
internal sealed class ConsoleLogger : ILogger
{
    public static readonly ConsoleLogger Instance = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        Console.Error.WriteLine($"[{logLevel}] {formatter(state, exception)}");
    }
}
