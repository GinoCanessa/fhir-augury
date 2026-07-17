using Microsoft.Extensions.Logging;

namespace FhirAugury.Tools.DictionaryBuild;

/// <summary>
/// Minimal <see cref="ILogger"/> that writes informational and higher messages
/// to stderr. Verbosity is raised to <see cref="LogLevel.Information"/> (vs the
/// fhir-spec-review tool's Warning floor) so the "loaded N words, M typos"
/// summary from <c>DictionaryDatabase</c> surfaces on a successful rebuild.
/// </summary>
internal sealed class ConsoleLogger : ILogger
{
    public static readonly ConsoleLogger Instance = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        Console.Error.WriteLine($"[{logLevel}] {formatter(state, exception)}");
    }
}
