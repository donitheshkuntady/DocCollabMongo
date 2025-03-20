using System.Diagnostics.CodeAnalysis;

namespace DocCollabMongoApi.Loggers
{
    public class FileLogger : ILogger
    {
        protected readonly FileLoggerProvider _roundTheCodeLoggerFileProvider;

        public FileLogger([NotNull] FileLoggerProvider roundTheCodeLoggerFileProvider)
        {
            _roundTheCodeLoggerFileProvider = roundTheCodeLoggerFileProvider;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                         _roundTheCodeLoggerFileProvider.Options.FolderPath);

            // Ensure the directory exists
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            var fileName = _roundTheCodeLoggerFileProvider.Options.FilePath.Replace("{date}",
                          DateTimeOffset.UtcNow.ToString("yyyyMMdd"));

            var fullFilePath = Path.Combine(logDirectory, fileName);

            var logRecord = string.Format("{0} [{1}] {2} {3}",
                "[" + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss+00:00") + "]",
                logLevel.ToString(),
                formatter(state, exception),
                exception != null ? exception.StackTrace : "");

            using (var streamWriter = new StreamWriter(fullFilePath, true))
            {
                streamWriter.WriteLine(logRecord);
            }
        }
    }
}
