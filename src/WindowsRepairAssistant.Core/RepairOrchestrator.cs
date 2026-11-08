namespace WindowsRepairAssistant.Core;

/// <summary>Receives live updates. Called from background threads; implementations must marshal to the UI.</summary>
public interface IRepairObserver
{
    void StepStarted(int index, RepairStepDefinition step);

    /// <param name="percent">Real percentage reported by the tool, or null if the line carried none.</param>
    void StepOutput(int index, string line, double? percent);

    void StepFinished(int index, StepResult result);
}

public sealed record SessionContext(string AppVersion, string WindowsVersion);

public sealed record RepairReport(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<StepResult> Steps,
    RepairSummary Summary)
{
    public TimeSpan Duration => FinishedAt - StartedAt;
}

/// <summary>
/// Runs the repair steps strictly one after another and decides which later steps to skip.
/// </summary>
public sealed class RepairOrchestrator(
    IProcessRunner runner,
    IReadOnlyList<RepairStepDefinition> plan,
    ISessionLog log,
    SessionContext context,
    TimeProvider time)
{
    private int _isRunning;

    public async Task<RepairReport> RunAsync(IRepairObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        // Second line of defence behind the disabled Start button: two concurrent DISM sessions
        // against the same online image fail unpredictably.
        if (Interlocked.Exchange(ref _isRunning, 1) == 1)
        {
            throw new InvalidOperationException("A repair session is already running.");
        }

        try
        {
            DateTimeOffset startedAt = time.GetLocalNow();
            log.SessionStarted(context, startedAt, plan.Count);

            var results = new List<StepResult>(plan.Count);
            for (int index = 0; index < plan.Count; index++)
            {
                StepResult result = await RunStepAsync(index, plan[index], results, observer).ConfigureAwait(false);
                results.Add(result);
                Notify(() => observer.StepFinished(index, result));
            }

            var report = new RepairReport(startedAt, time.GetLocalNow(), results, SummaryBuilder.Build(results));
            log.SessionCompleted(report);
            return report;
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
        }
    }

    private async Task<StepResult> RunStepAsync(int index, RepairStepDefinition step, IReadOnlyList<StepResult> previous, IRepairObserver observer)
    {
        StepResult? skipped = GetSkip(step, previous);
        if (skipped is not null)
        {
            log.StepStarted(index, plan.Count, step, time.GetLocalNow());
            log.StepFinished(skipped, null);
            return skipped;
        }

        Notify(() => observer.StepStarted(index, step));
        log.StepStarted(index, plan.Count, step, time.GetLocalNow());

        long startTimestamp = time.GetTimestamp();
        ProcessRunResult? run = null;
        try
        {
            run = await runner.RunAsync(step.ExecutablePath, step.Arguments, line =>
            {
                double? percent = ProgressParser.TryParsePercent(line);
                log.StepOutput(line, percent);
                Notify(() => observer.StepOutput(index, line, percent));
            }).ConfigureAwait(false);

            StepAnalysis analysis = ApplyContext(step, OutputAnalyzer.Analyze(step.Kind, run), previous);
            var result = new StepResult(step, analysis.Status, analysis.Finding, analysis.Message, run.ExitCode, run.Duration);
            log.StepFinished(result, run);
            return result;
        }
        catch (Exception ex)
        {
            // Any failure here is reported as this step failing; the sequence continues so the
            // user still gets the remaining independent checks (notably SFC).
            log.Error($"Unexpected error while running {step.DisplayName}.", ex);
            var result = new StepResult(step, StepStatus.Failed, StepFinding.ToolError,
                "An unexpected error occurred while running this step. See the log for details.",
                run?.ExitCode, time.GetElapsedTime(startTimestamp));
            log.StepFinished(result, run);
            return result;
        }
    }

    internal static StepResult? GetSkip(RepairStepDefinition step, IReadOnlyList<StepResult> previous)
    {
        if (!step.IsDism)
        {
            // SFC uses a different mechanism and can still find and fix problems when DISM cannot.
            return null;
        }

        StepResult? failedDism = previous.FirstOrDefault(r => r.Step.IsDism && r.Status == StepStatus.Failed);
        if (failedDism is not null)
        {
            return StepResult.Skipped(step, $"Skipped because {failedDism.Step.DisplayName} failed. The remaining DISM steps would fail for the same reason.");
        }

        if (step.Kind != RepairStepKind.DismRestoreHealth)
        {
            return null;
        }

        if (previous.Any(r => r.Finding == StepFinding.CorruptionNotRepairable))
        {
            return StepResult.Skipped(step, "Skipped because Windows reported that the damage cannot be repaired by DISM.");
        }

        // Only ScanHealth's explicit verdict counts: CheckHealth just reads a recorded flag, and
        // unrecognised output (e.g. non-English Windows) proves nothing, so repair still runs then.
        bool scanConfirmedClean = previous.Any(r => r.Step.Kind == RepairStepKind.DismScanHealth && r.Finding == StepFinding.NoCorruption);
        if (scanConfirmedClean && !previous.Any(r => r.Finding == StepFinding.CorruptionRepairable))
        {
            return StepResult.Skipped(step, "Not needed. The scan found no damage in the Windows component store, so there was nothing to repair.", StepFinding.NoRepairNeeded);
        }

        return null;
    }

    /// <summary>
    /// RestoreHealth reports the same success message whether it fixed something or had nothing to fix,
    /// so only claim a repair when an earlier scan actually found damage.
    /// </summary>
    internal static StepAnalysis ApplyContext(RepairStepDefinition step, StepAnalysis analysis, IReadOnlyList<StepResult> previous)
    {
        bool repairedKnownDamage = step.Kind == RepairStepKind.DismRestoreHealth
            && analysis.Status == StepStatus.Succeeded
            && previous.Any(r => r.Finding == StepFinding.CorruptionRepairable);

        return repairedKnownDamage
            ? analysis with { Finding = StepFinding.Repaired, Message = "Damage in the Windows component store was repaired." }
            : analysis;
    }

    private void Notify(Action notify)
    {
        // An observer (UI) failure must never interrupt draining a child process's output pipe,
        // which would stall DISM/SFC mid-repair.
        try
        {
            notify();
        }
        catch (Exception ex)
        {
            log.Error("Progress display update failed.", ex);
        }
    }
}
