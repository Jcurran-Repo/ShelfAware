namespace ShelfAware.Web.Diagnostics;

/// <summary>Captures every Error/Critical event the app logs — the same events the house
/// catch-log-and-say-so convention already produces — into the sink for the in-app error log.
/// Registered as a logging provider so HANDLED errors are seen too, not just crashes.
///
/// Two hard rules for code on this path: it must never throw (a throwing logger takes the app's
/// own error handling down with it — a failure becomes a counted drop instead), and it must never
/// capture its own pipeline's categories (the writer logs its troubles under
/// ShelfAware.Web.Diagnostics.*, which IsEnabled excludes — the recursion break).</summary>
public sealed class ErrorLogCaptureProvider(ErrorLogSink sink) : ILoggerProvider
{
    /// <summary>Category prefix the capture ignores — everything in this pipeline logs under it.</summary>
    public const string OwnCategoryPrefix = "ShelfAware.Web.Diagnostics";

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(sink, categoryName);

    public void Dispose() { }

    private sealed class CaptureLogger(ErrorLogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Error &&
            !category.StartsWith(OwnCategoryPrefix, StringComparison.Ordinal);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try
            {
                // Structured log states carry the message template under {OriginalFormat} — the
                // stable identity of a log SITE, which is what fingerprinting wants (the rendered
                // message varies per occurrence).
                string? template = null;
                if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
                {
                    template = values.FirstOrDefault(kv => kv.Key == "{OriginalFormat}").Value?.ToString();
                }

                sink.TryPost(new CapturedError(
                    At: DateTimeOffset.Now,
                    Level: logLevel.ToString(),
                    Category: category,
                    ExceptionType: exception?.GetType().FullName,
                    MessageTemplate: template,
                    Message: Truncate(formatter(state, exception), 2000) ?? "",
                    ExceptionDetail: Truncate(exception?.ToString(), 6000)));
            }
            catch
            {
                // A logger cannot log its own failure, and must never throw into the app it
                // serves — the event becomes a counted drop rather than vanishing silently.
                sink.CountDrop();
            }
        }

        private static string? Truncate(string? s, int max) =>
            s is { Length: var len } && len > max ? s[..max] : s;
    }
}
