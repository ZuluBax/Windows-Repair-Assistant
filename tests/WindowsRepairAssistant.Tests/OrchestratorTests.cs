using WindowsRepairAssistant.Core;

namespace WindowsRepairAssistant.Tests;

public class OrchestratorTests
{
    private static readonly IReadOnlyList<RepairStepDefinition> Plan = RepairPlan.Create(@"C:\Windows\System32");

    private static Task<RepairReport> Run(FakeProcessRunner runner, RecordingObserver? observer = null, ISessionLog? log = null) =>
        new RepairOrchestrator(runner, Plan, log ?? NullSessionLog.Instance, new SessionContext("test", "test"), TimeProvider.System)
            .RunAsync(observer ?? new RecordingObserver());

    [Fact]
    public void Plan_uses_absolute_system_paths_and_separate_arguments()
    {
        Assert.All(Plan, s => Assert.True(Path.IsPathFullyQualified(s.ExecutablePath)));
        Assert.Equal(["/Online", "/Cleanup-Image", "/RestoreHealth"], Plan[2].Arguments);
        Assert.Equal(["/scannow"], Plan[3].Arguments);
    }

    [Fact] // Scenario 3 + 4: everything completes, nothing to fix
    public async Task Healthy_system_runs_all_steps_in_order_and_reports_no_problems()
    {
        var runner = new FakeProcessRunner()
            .Returns(RepairStepKind.DismCheckHealth, SampleOutput.DismClean)
            .Returns(RepairStepKind.DismScanHealth, SampleOutput.DismClean)
            .Returns(RepairStepKind.DismRestoreHealth, SampleOutput.DismRestoreOk)
            .Returns(RepairStepKind.SfcScanNow, SampleOutput.SfcClean);

        RepairReport report = await Run(runner);

        Assert.Equal([RepairStepKind.DismCheckHealth, RepairStepKind.DismScanHealth, RepairStepKind.DismRestoreHealth, RepairStepKind.SfcScanNow], runner.Invocations);
        Assert.Equal(1, runner.MaxConcurrent);
        Assert.All(report.Steps, s => Assert.Equal(StepStatus.Succeeded, s.Status));
        Assert.Equal(StepFinding.NoRepairNeeded, report.Steps[2].Finding);
        Assert.Equal(OverallOutcome.NoProblemsFound, report.Summary.Outcome);
        Assert.Equal("Windows system integrity checks did not identify problems requiring repair.", report.Summary.Headline);
        Assert.False(report.Summary.RestartRecommended);
    }

    [Fact] // Scenario 5
    public async Task Dism_corruption_that_is_repaired_is_reported_as_repaired()
    {
        var runner = new FakeProcessRunner()
            .Returns(RepairStepKind.DismCheckHealth, SampleOutput.DismRepairable)
            .Returns(RepairStepKind.DismScanHealth, SampleOutput.DismRepairable)
            .Returns(RepairStepKind.DismRestoreHealth, SampleOutput.DismRestoreOk)
            .Returns(RepairStepKind.SfcScanNow, SampleOutput.SfcClean);

        RepairReport report = await Run(runner);

        Assert.Equal(StepFinding.Repaired, report.Steps[2].Finding);
        Assert.Equal(OverallOutcome.ProblemsRepaired, report.Summary.Outcome);
        Assert.True(report.Summary.RestartRecommended);
    }

    [Fact] // Scenario 6: DISM fails -> remaining DISM skipped, SFC still runs
    public async Task Dism_failure_skips_remaining_dism_steps_but_still_runs_sfc()
    {
        var runner = new FakeProcessRunner()
            .Returns(RepairStepKind.DismCheckHealth, SampleOutput.DismUnknownError)
            .Returns(RepairStepKind.SfcScanNow, SampleOutput.SfcClean);

        RepairReport report = await Run(runner);

        Assert.Equal([RepairStepKind.DismCheckHealth, RepairStepKind.SfcScanNow], runner.Invocations);
        Assert.Equal(StepStatus.Failed, report.Steps[0].Status);
        Assert.Equal(StepStatus.Skipped, report.Steps[1].Status);
        Assert.Equal(StepStatus.Skipped, report.Steps[2].Status);
        Assert.Equal(StepStatus.Succeeded, report.Steps[3].Status);
        Assert.Equal(OverallOutcome.Incomplete, report.Summary.Outcome);
        Assert.DoesNotContain("repaired them", report.Summary.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_failing_after_corruption_found_means_problems_remain()
    {
        var runner = new FakeProcessRunner()
            .Returns(RepairStepKind.DismCheckHealth, SampleOutput.DismRepairable)
            .Returns(RepairStepKind.DismScanHealth, SampleOutput.DismRepairable)
            .Returns(RepairStepKind.DismRestoreHealth, SampleOutput.DismSourceMissing)
            .Returns(RepairStepKind.SfcScanNow, SampleOutput.SfcClean);

        RepairReport report = await Run(runner);

        Assert.Equal(OverallOutcome.ProblemsRemain, report.Summary.Outcome);
        Assert.Contains(report.Summary.Details, d => d.Contains("internet", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Not_repairable_image_skips_restore_health()
    {
        var runner = new FakeProcessRunner()
            .Returns(RepairStepKind.DismCheckHealth, SampleOutput.DismNotRepairable)
            .Returns(RepairStepKind.DismScanHealth, SampleOutput.DismNotRepairable)
            .Returns(RepairStepKind.SfcScanNow, SampleOutput.SfcClean);

        RepairReport report = await Run(runner);

        Assert.DoesNotContain(RepairStepKind.DismRestoreHealth, runner.Invocations);
        Assert.Equal(StepStatus.Skipped, report.Steps[2].Status);
        Assert.Equal(OverallOutcome.ProblemsRemain, report.Summary.Outcome);
    }

    [Fact] // Scenario 7
    public async Task Sfc_repairs_are_communicated()
    {
        var runner = new FakeProcessRunner()
            .Returns(RepairStepKind.DismScanHealth, SampleOutput.DismClean)
            .Returns(RepairStepKind.DismRestoreHealth, SampleOutput.DismRestoreOk)
            .Returns(RepairStepKind.SfcScanNow, SampleOutput.SfcRepaired);

        RepairReport report = await Run(runner);

        Assert.Equal(OverallOutcome.ProblemsRepaired, report.Summary.Outcome);
        Assert.Contains(report.Summary.Details, d => d.Contains("System File Checker found damaged system files and repaired them", StringComparison.Ordinal));
        Assert.True(report.Summary.RestartRecommended);
    }

    [Fact] // Scenario 8
    public async Task Sfc_unfixable_corruption_is_communicated()
    {
        var runner = new FakeProcessRunner()
            .Returns(RepairStepKind.DismScanHealth, SampleOutput.DismClean)
            .Returns(RepairStepKind.DismRestoreHealth, SampleOutput.DismRestoreOk)
            .Returns(RepairStepKind.SfcScanNow, SampleOutput.SfcPartial);

        RepairReport report = await Run(runner);

        Assert.Equal(StepStatus.CompletedWithIssues, report.Steps[3].Status);
        Assert.Equal(OverallOutcome.ProblemsRemain, report.Summary.Outcome);
        Assert.Contains(report.Summary.Details, d => d.Contains("could not repair some of them", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exception_in_one_step_is_contained_and_later_steps_continue()
    {
        var runner = new FakeProcessRunner()
            .Throws(RepairStepKind.DismScanHealth, new InvalidOperationException("boom"))
            .Returns(RepairStepKind.SfcScanNow, SampleOutput.SfcClean);

        RepairReport report = await Run(runner);

        Assert.Equal(StepStatus.Failed, report.Steps[1].Status);
        Assert.Equal(StepStatus.Skipped, report.Steps[2].Status);
        Assert.Equal(StepStatus.Succeeded, report.Steps[3].Status);
    }

    [Fact]
    public async Task Tool_that_cannot_start_is_failed_not_skipped_silently()
    {
        var runner = new FakeProcessRunner()
            .Returns(RepairStepKind.DismCheckHealth, SampleOutput.NotStarted)
            .Returns(RepairStepKind.SfcScanNow, SampleOutput.NotStarted);

        RepairReport report = await Run(runner);

        Assert.Equal(StepFinding.CouldNotStart, report.Steps[0].Finding);
        Assert.Equal(StepFinding.CouldNotStart, report.Steps[3].Finding);
        Assert.Equal(OverallOutcome.Incomplete, report.Summary.Outcome);
    }

    [Fact]
    public async Task Observer_failures_do_not_break_the_repair()
    {
        var runner = new FakeProcessRunner().Returns(RepairStepKind.SfcScanNow, SampleOutput.SfcClean);

        RepairReport report = await Run(runner, new RecordingObserver { ThrowOnOutput = true });

        Assert.Equal(4, report.Steps.Count);
        Assert.Equal(StepStatus.Succeeded, report.Steps[3].Status);
    }

    [Fact]
    public async Task Observer_receives_ordered_events_and_real_percentages()
    {
        var observer = new RecordingObserver();
        var runner = new FakeProcessRunner()
            .Returns(RepairStepKind.DismRestoreHealth, SampleOutput.DismRestoreOk)
            .Returns(RepairStepKind.SfcScanNow, SampleOutput.SfcClean);

        await Run(runner, observer);

        Assert.Equal("start:DismCheckHealth", observer.Events[0]);
        Assert.Equal("finish:SfcScanNow:Succeeded", observer.Events[^1]);
        Assert.Contains(84.6, observer.Percents);
        Assert.Contains(57, observer.Percents);
    }

    [Fact]
    public async Task Concurrent_runs_are_rejected()
    {
        var gate = new TaskCompletionSource<ProcessRunResult>();
        var runner = new FakeProcessRunner().Returns(RepairStepKind.DismCheckHealth, () => gate.Task.GetAwaiter().GetResult());
        var orchestrator = new RepairOrchestrator(runner, Plan, NullSessionLog.Instance, new SessionContext("t", "t"), TimeProvider.System);

        Task<RepairReport> first = Task.Run(() => orchestrator.RunAsync(new RecordingObserver()));
        await WaitUntil(() => runner.Invocations.Count > 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.RunAsync(new RecordingObserver()));

        gate.SetResult(SampleOutput.DismClean());
        await first;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
