namespace WindowsRepairAssistant.Core;

public enum StepStatus
{
    Pending,
    Running,
    Succeeded,
    CompletedWithIssues,
    Failed,
    Skipped,
}

/// <summary>What a step actually discovered, independent of whether the command itself succeeded.</summary>
public enum StepFinding
{
    None,
    NoCorruption,
    CorruptionRepairable,
    CorruptionNotRepairable,
    Repaired,
    NoRepairNeeded,
    PartiallyRepaired,
    SourceFilesUnavailable,
    RestartPending,
    NotElevated,
    ToolError,
    CouldNotStart,
    ResultUnrecognized,
    UpdateInstalled,
    UpdateInstalledRestartRequired,
}

public sealed record StepAnalysis(StepStatus Status, StepFinding Finding, string Message);

public sealed record StepResult(
    RepairStepDefinition Step,
    StepStatus Status,
    StepFinding Finding,
    string Message,
    int? ExitCode,
    TimeSpan Duration)
{
    public static StepResult Skipped(RepairStepDefinition step, string reason, StepFinding finding = StepFinding.None) =>
        new(step, StepStatus.Skipped, finding, reason, null, TimeSpan.Zero);
}

public static class StepStatusText
{
    public static string ToDisplayText(this StepStatus status) => status switch
    {
        StepStatus.Pending => "Waiting",
        StepStatus.Running => "In progress",
        StepStatus.Succeeded => "Completed successfully",
        StepStatus.CompletedWithIssues => "Completed with issues",
        StepStatus.Failed => "Failed",
        StepStatus.Skipped => "Skipped",
        _ => status.ToString(),
    };
}
