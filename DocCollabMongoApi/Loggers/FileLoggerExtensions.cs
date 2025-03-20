namespace DocCollabMongoApi.Loggers
{
    public static class FileLoggerExtensions
    {
        public static ILoggingBuilder AddCodeFileLogger(this ILoggingBuilder builder, Action<FileLoggerOptions> configure)
        {
            builder.Services.AddSingleton<ILoggerProvider, FileLoggerProvider>();
            builder.Services.Configure(configure);
            return builder;
        }
    }
}
