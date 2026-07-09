using System;
using Microsoft.Extensions.Logging;
using Report_Generator.Services;

public class JobLogLoggerProvider : ILoggerProvider
{
    private readonly ReportJobService _jobs;
    public JobLogLoggerProvider(ReportJobService jobs) => _jobs = jobs;

    public ILogger CreateLogger(string categoryName) => new JobLogLogger(_jobs);
    public void Dispose() { }

    private sealed class JobLogLogger : ILogger
    {
        private readonly ReportJobService _jobs;
        public JobLogLogger(ReportJobService jobs) => _jobs = jobs;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NoopDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var jobId = JobLogScope.CurrentJobId;
            if (jobId == null) return; // logging outside a job — ignore, don't misattribute

            var message = formatter(state, exception);
            if (exception != null) message += $" — {exception.Message}";
            _jobs.AppendLog(jobId.Value, message);
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}