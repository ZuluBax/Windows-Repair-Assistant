using System.Text;

namespace WindowsRepairAssistant.Core;

public interface ISessionLog : IDisposable
{
    string? FilePath { get; }

    void SessionStarted(SessionContext context, DateTimeOffset startedAt, int stepCount);

    void StepStarted(int index, int total, RepairStepDefinition step, DateTimeOffset at);

    void StepOutput(string line, double? percent);

    /// <param name="run">Null when the step was skipped or never produced a process result.</param>
    void StepFinished(StepResult result, ProcessRunResult? run);

    void Error(string message, Exception? exception = null);

    void SessionCompleted(RepairReport report);
}

/// <summary>Used when the log file could not be created, so a logging problem never blocks a repair.</summary>
public sealed class NullSessionLog : ISessionLog
{
    public static readonly NullSessionLog Instance = new();

    public string? FilePath => null;

    public void SessionStarted(SessionContext context, DateTimeOffset startedAt, int stepCount) { }

    public void StepStarted(int index, int total, RepairStepDefinition step, DateTimeOffset at) { }

    public void StepOutput(string line, double? percent) { }

    public void StepFinished(StepResult result, ProcessRunResult? run) { }

    public void Error(string message, Exception? exception = null) { }

    public void SessionCompleted(RepairReport report) { }

    public void Dispose() { }
}

/// <summary>
/// Plain-text session log, flushed after every write so a crash or power loss still leaves a useful partial log.
/// </summary>
public sealed class FileSessionLog : ISessionLog
{
    /// <summary>Written last; its absence means the session ended unexpectedly.</summary>
    public const string EndMarker = "=== END OF REPAIR SESSION ===";

    private const string Rule = "------------------------------------------------------------------------";
    private const int ProgressLogIntervalPercent = 10;

    private readonly Lock _gate = new();
    private readonly StreamWriter _writer;
    private readonly TimeProvider _time;
    private int _lastLoggedProgressBucket = -1;
    private bool _disposed;

    public FileSessionLog(string filePath, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _time = time;
        FilePath = filePath;

        // CreateNew: never follow or overwrite something already at this path. This runs elevated in a
        // folder standard users can also see, so we refuse to reuse a pre-planted file or link.
        var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
    }

    public string? FilePath { get; }

    public void SessionStarted(SessionContext context, DateTimeOffset startedAt, int stepCount)
    {
        Write(sb => sb
            .AppendLine("========================================================================")
            .AppendLine($" Windows Repair Assistant {context.AppVersion} - repair session log")
            .AppendLine("========================================================================")
            .AppendLine($"Started:          {startedAt:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"Windows version:  {context.WindowsVersion}")
            .AppendLine($"Steps planned:    {stepCount}")
            .AppendLine("Privacy:          This log is stored only on this computer. Nothing is sent anywhere.")
            .AppendLine());
    }

    public void StepStarted(int index, int total, RepairStepDefinition step, DateTimeOffset at)
    {
        Write(sb => sb
            .AppendLine(Rule)
            .AppendLine($"STEP {index + 1} of {total}: {step.DisplayName} - {step.Title}")
            .AppendLine($"Command: {step.CommandLine}")
            .AppendLine($"Started: {at:yyyy-MM-dd HH:mm:ss}")
            .AppendLine(Rule));
        lock (_gate)
        {
            _lastLoggedProgressBucket = -1;
        }
    }

    public void StepOutput(string line, double? percent)
    {
        lock (_gate)
        {
            if (percent is double p)
            {
                // DISM redraws its progress bar hundreds of times; keep the log readable.
                int bucket = (int)(p / ProgressLogIntervalPercent);
                if (bucket == _lastLoggedProgressBucket)
                {
                    return;
                }

                _lastLoggedProgressBucket = bucket;
            }
        }

        Write(sb => sb.AppendLine($"[{_time.GetLocalNow():HH:mm:ss}] {line}"));
    }

    public void StepFinished(StepResult result, ProcessRunResult? run)
    {
        Write(sb =>
        {
            sb.AppendLine();
            if (run is not null)
            {
                if (!string.IsNullOrWhiteSpace(run.StandardError))
                {
                    sb.AppendLine("Error output:").AppendLine(run.StandardError.TrimEnd());
                }

                if (run.StartError is not null)
                {
                    sb.AppendLine($"Start error: {run.StartError}");
                }
            }

            string exit = result.ExitCode is int code ? $"{code} (0x{unchecked((uint)code):X8})" : "n/a";
            sb.AppendLine($"Exit code: {exit}")
              .AppendLine($"Duration:  {FormatDuration(result.Duration)}")
              .AppendLine($"Status:    {result.Status.ToDisplayText()}")
              .AppendLine($"Finding:   {result.Finding}")
              .AppendLine($"Result:    {result.Message}")
              .AppendLine();
        });
    }

    public void Error(string message, Exception? exception = null)
    {
        Write(sb =>
        {
            sb.AppendLine($"[{_time.GetLocalNow():HH:mm:ss}] ERROR: {message}");
            if (exception is not null)
            {
                sb.AppendLine(exception.ToString());
            }
        });
    }

    public void SessionCompleted(RepairReport report)
    {
        Write(sb =>
        {
            sb.AppendLine(Rule).AppendLine("SUMMARY").AppendLine(Rule);
            foreach (StepResult step in report.Steps)
            {
                sb.AppendLine($"{step.Step.DisplayName,-20} {step.Status.ToDisplayText()}");
            }

            sb.AppendLine()
              .AppendLine($"Outcome: {report.Summary.Headline}");
            foreach (string detail in report.Summary.Details)
            {
                sb.AppendLine($" - {detail}");
            }

            sb.AppendLine($"Restart recommended: {(report.Summary.RestartRecommended ? "yes" : "no")}")
              .AppendLine($"Finished: {report.FinishedAt:yyyy-MM-dd HH:mm:ss zzz}")
              .AppendLine($"Total duration: {FormatDuration(report.Duration)}")
              .AppendLine()
              .AppendLine(EndMarker);
        });
    }

    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
        }
    }

    private void Write(Action<StringBuilder> build)
    {
        var sb = new StringBuilder();
        build(sb);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _writer.Write(sb.ToString());
            }
            catch (IOException)
            {
                // Disk full / removed: losing log lines must never abort a repair in progress.
                // The results screen still shows the outcome.
            }
        }
    }
}
