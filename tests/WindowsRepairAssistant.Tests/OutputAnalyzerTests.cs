using WindowsRepairAssistant.Core;

namespace WindowsRepairAssistant.Tests;

public class OutputAnalyzerTests
{
    [Fact]
    public void Dism_check_with_no_corruption_succeeds()
    {
        StepAnalysis a = OutputAnalyzer.Analyze(RepairStepKind.DismCheckHealth, SampleOutput.DismClean());
        Assert.Equal(StepStatus.Succeeded, a.Status);
        Assert.Equal(StepFinding.NoCorruption, a.Finding);
    }

    [Fact]
    public void Dism_scan_reporting_repairable_corruption_completes_with_issues()
    {
        StepAnalysis a = OutputAnalyzer.Analyze(RepairStepKind.DismScanHealth, SampleOutput.DismRepairable());
        Assert.Equal(StepStatus.CompletedWithIssues, a.Status);
        Assert.Equal(StepFinding.CorruptionRepairable, a.Finding);
    }

    [Fact]
    public void Not_repairable_is_not_mistaken_for_repairable()
    {
        StepAnalysis a = OutputAnalyzer.Analyze(RepairStepKind.DismCheckHealth, SampleOutput.DismNotRepairable());
        Assert.Equal(StepFinding.CorruptionNotRepairable, a.Finding);
    }

    [Fact]
    public void Restore_missing_source_files_fails_with_internet_guidance()
    {
        StepAnalysis a = OutputAnalyzer.Analyze(RepairStepKind.DismRestoreHealth, SampleOutput.DismSourceMissing());
        Assert.Equal(StepStatus.Failed, a.Status);
        Assert.Equal(StepFinding.SourceFilesUnavailable, a.Finding);
        Assert.Contains("internet", a.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dism_without_elevation_is_reported_as_permission_problem()
    {
        StepAnalysis a = OutputAnalyzer.Analyze(RepairStepKind.DismCheckHealth, SampleOutput.DismNotElevated());
        Assert.Equal(StepStatus.Failed, a.Status);
        Assert.Equal(StepFinding.NotElevated, a.Finding);
    }

    [Fact]
    public void Unknown_dism_error_includes_hex_code()
    {
        StepAnalysis a = OutputAnalyzer.Analyze(RepairStepKind.DismScanHealth, SampleOutput.DismUnknownError());
        Assert.Equal(StepStatus.Failed, a.Status);
        Assert.Contains("0x800F0915", a.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_that_never_started_fails()
    {
        StepAnalysis a = OutputAnalyzer.Analyze(RepairStepKind.SfcScanNow, SampleOutput.NotStarted());
        Assert.Equal(StepStatus.Failed, a.Status);
        Assert.Equal(StepFinding.CouldNotStart, a.Finding);
    }

    [Theory]
    [InlineData(nameof(SampleOutput.SfcClean), StepStatus.Succeeded, StepFinding.NoCorruption)]
    [InlineData(nameof(SampleOutput.SfcRepaired), StepStatus.Succeeded, StepFinding.Repaired)]
    [InlineData(nameof(SampleOutput.SfcPartial), StepStatus.CompletedWithIssues, StepFinding.PartiallyRepaired)]
    [InlineData(nameof(SampleOutput.SfcPendingRestart), StepStatus.CompletedWithIssues, StepFinding.RestartPending)]
    [InlineData(nameof(SampleOutput.SfcCouldNotPerform), StepStatus.Failed, StepFinding.ToolError)]
    public void Sfc_messages_map_to_expected_outcome(string sample, StepStatus status, StepFinding finding)
    {
        var run = (ProcessRunResult)typeof(SampleOutput).GetMethod(sample)!.Invoke(null, null)!;
        StepAnalysis a = OutputAnalyzer.Analyze(RepairStepKind.SfcScanNow, run);
        Assert.Equal(status, a.Status);
        Assert.Equal(finding, a.Finding);
    }

    [Fact]
    public void Unrecognised_sfc_output_is_never_reported_as_success()
    {
        // SFC's exit code is 0 whether or not it found problems, so unknown text must not read as "all good".
        StepAnalysis a = OutputAnalyzer.Analyze(RepairStepKind.SfcScanNow, SampleOutput.SfcLocalised());
        Assert.NotEqual(StepStatus.Succeeded, a.Status);
        Assert.Equal(StepFinding.ResultUnrecognized, a.Finding);
    }
}
