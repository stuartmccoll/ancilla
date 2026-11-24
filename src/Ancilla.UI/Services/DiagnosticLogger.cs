using Ancilla.Core;
using Microsoft.Extensions.Logging;

namespace Ancilla.UI.Services;

/// <summary>
/// Writes errors logged through <see cref="ILogger"/> to the <see cref="DiagnosticLog"/>, with their
/// stack traces, redacted. That is where Blazor reports an exception a page didn't catch (the
/// "An unhandled error has occurred" bar), so those end up in the log files a bug report can quote.
/// Both hosts' logger factories pick it up from <see cref="AncillaServices.AddAncilla"/>.
/// </summary>
public sealed class DiagnosticLoggerProvider(DiagnosticLog log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(log, categoryName);

    public void Dispose() { }

    private sealed class Logger(DiagnosticLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            log.Error($"{logLevel} in {category}: {formatter(state, exception)}", exception);
        }
    }
}
