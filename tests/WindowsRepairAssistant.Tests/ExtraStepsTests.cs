using WindowsRepairAssistant.Core;

namespace WindowsRepairAssistant.Tests;

/// <summary>The optional temp-cleanup and Windows Update steps around the DISM/SFC repair.</summary>
public class ExtraStepsTests
{
    private static readonly StepAnalysis CleanupOk = new(StepStatus.Succeeded, StepFinding.None, "Deleted 3 files and freed 2.0 MB.");

    private static FakeProcessRunner HealthyRunner() => new FakeProcessRunner()
        .Returns(RepairStepKind.DismCheckHealth, SampleOutput.DismClean)
        .Returns(RepairStepKind.DismScanHealth, SampleOutput.DismClean)
        .Returns(RepairStepKind.SfcScanNow, SampleOutput.SfcClean);

    private static Task<RepairReport> Run(IReadOnlyList<RepairStepDefinition> plan, FakeProcessRunner? runner = null, RecordingObserver? observer = null) =>
        new RepairOrchestrator(runner ?? HealthyRunner(), plan, NullSessionLog.Instance, new SessionContext("test", "test"), TimeProvider.System)
            .RunAsync(observer ?? new RecordingObserver());

    [Fact]
    public void Cleanup_runs_first_and_update_check_last()
    {
        var plan = RepairPlan.Create(@"C:\Windows\System32", new FakeInProcessStep(() => CleanupOk), new FakeInProcessStep(() => CleanupOk));

        Assert.Equal(
            [RepairStepKind.CleanTempFiles, RepairStepKind.DismCheckHealth, RepairStepKind.DismScanHealth, RepairStepKind.DismRestoreHealth,
             RepairStepKind.SfcScanNow, RepairStepKind.CheckWindowsUpdates],
            plan.Select(s => s.Kind));
        Assert.False(plan[0].IsDism);
        Assert.False(plan[0].IsRepairTool);
        Assert.False(plan[^1].IsDism);
        Assert.StartsWith("(built into Windows Repair Assistant)", plan[0].CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_plan_is_unchanged_when_extras_are_not_chosen()
    {
        Assert.Equal(
            [RepairStepKind.DismCheckHealth, RepairStepKind.DismScanHealth, RepairStepKind.DismRestoreHealth, RepairStepKind.SfcScanNow],
            RepairPlan.Create(@"C:\Windows\System32").Select(s => s.Kind));
    }

    [Fact]
    public async Task In_process_steps_run_without_launching_any_program()
    {
        var cleanup = new FakeInProcessStep(() => CleanupOk);
        var runner = HealthyRunner();
        var observer = new RecordingObserver();

        RepairReport report = await Run(RepairPlan.Create(@"C:\Windows\System32", cleanup), runner, observer);

        Assert.Equal(1, cleanup.Runs);
        Assert.Equal(3, runner.Invocations.Count); // CheckHealth, ScanHealth, SFC: the cleanup launched nothing
        Assert.Equal("start:CleanTempFiles", observer.Events[0]);
        Assert.Equal(StepStatus.Succeeded, report.Steps[0].Status);
        Assert.Equal("Deleted 3 files and freed 2.0 MB.", report.Steps[0].Message);
    }

    [Fact]
    public async Task Healthy_repair_with_cleanup_says_no_repairs_were_made_and_reports_the_cleanup()
    {
        RepairReport report = await Run(RepairPlan.Create(@"C:\Windows\System32", new FakeInProcessStep(() => CleanupOk)));

        Assert.Equal(OverallOutcome.NoProblemsFound, report.Summary.Outcome);
        Assert.Contains("No repairs were made to Windows.", report.Summary.Details);
        Assert.DoesNotContain("No changes were made to your system.", report.Summary.Details);
        Assert.Contains("Temporary files: Deleted 3 files and freed 2.0 MB.", report.Summary.Details);
    }

    [Fact]
    public async Task Failing_extras_do_not_change_the_repair_verdict()
    {
        var crashingCleanup = new FakeInProcessStep(() => throw new InvalidOperationException("boom"));
        var failedCheck = new FakeInProcessStep(() => new StepAnalysis(StepStatus.Failed, StepFinding.ToolError, "The check could not be completed. No internet."));

        RepairReport report = await Run(RepairPlan.Create(@"C:\Windows\System32", crashingCleanup, failedCheck));

        Assert.Equal(StepStatus.Failed, report.Steps[0].Status);
        Assert.Equal(StepStatus.Failed, report.Steps[^1].Status);
        Assert.Equal(StepStatus.Succeeded, report.Steps[4].Status); // SFC still ran
        Assert.Equal(OverallOutcome.NoProblemsFound, report.Summary.Outcome);
        // The cleanup may have deleted files before it crashed, so "no changes" cannot be promised.
        Assert.DoesNotContain("No changes were made to your system.", report.Summary.Details);
        Assert.Contains("No repairs were made to Windows.", report.Summary.Details);
        Assert.Contains("Windows Update: The check could not be completed. No internet.", report.Summary.Details);
    }

    [Fact]
    public async Task Update_check_lists_important_updates_first_and_installs_nothing()
    {
        var agent = new FakeWindowsUpdateAgent
        {
            SearchResult = () => [FakeWindowsUpdateAgent.Update("b-driver", optional: true), FakeWindowsUpdateAgent.Update("a-security")],
        };
        var service = new WindowsUpdateService(agent);

        StepAnalysis result = await service.RunAsync(_ => { });

        Assert.Equal(StepStatus.Succeeded, result.Status);
        Assert.Equal(["a-security", "b-driver"], service.AvailableUpdates!.Select(u => u.Id));
        Assert.StartsWith("2 updates are available (1 important, 1 optional). Nothing has been installed.", result.Message, StringComparison.Ordinal);
        Assert.Empty(agent.Installed);
    }

    [Fact]
    public async Task Up_to_date_computer_is_reported_plainly()
    {
        var service = new WindowsUpdateService(new FakeWindowsUpdateAgent());

        StepAnalysis result = await service.RunAsync(_ => { });

        Assert.Equal("Windows is up to date. No updates are available.", result.Message);
        Assert.Empty(service.AvailableUpdates!);
    }

    [Fact]
    public async Task Offline_check_fails_with_an_internet_hint_and_offers_no_list()
    {
        var agent = new FakeWindowsUpdateAgent { SearchResult = () => throw new WindowsUpdateException("x", unchecked((int)0x8024402C)) };
        var service = new WindowsUpdateService(agent);

        StepAnalysis result = await service.RunAsync(_ => { });

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Equal("The check could not be completed. Windows could not connect to Windows Update. Check that you are connected to the internet, then try again.", result.Message);
        Assert.Null(service.AvailableUpdates);
    }

    [Theory]
    [InlineData(0x80070422u, "The Windows Update service is turned off on this computer.")]
    [InlineData(0x80240016u, "Windows Update is busy with another installation, or is waiting for a restart. Restart your computer, then try again.")]
    [InlineData(0x80241234u, "Windows Update reported error 0x80241234.")]
    public void Error_codes_are_explained(uint hResult, string expected)
    {
        Assert.Equal(expected, WindowsUpdateService.DescribeError(unchecked((int)hResult)));
    }

    [Fact]
    public async Task Only_the_chosen_updates_are_installed_and_restart_is_requested_when_needed()
    {
        var agent = new FakeWindowsUpdateAgent
        {
            SearchResult = () => [FakeWindowsUpdateAgent.Update("one"), FakeWindowsUpdateAgent.Update("two"), FakeWindowsUpdateAgent.Update("three", optional: true)],
        };
        agent.InstallResults["three"] = () => new UpdateInstallOutcome(true, true, null, null);
        var service = new WindowsUpdateService(agent);
        await service.RunAsync(_ => { });
        WindowsUpdateInfo[] chosen = [.. service.AvailableUpdates!.Where(u => u.Id != "two")];

        RepairReport report = await Run(service.CreateInstallPlan(chosen));

        Assert.Equal(["one", "three"], agent.Installed);
        Assert.Equal(["Update 1 of 2", "Update 2 of 2"], report.Steps.Select(s => s.Step.DisplayName));
        Assert.Equal("Title one", report.Steps[0].Step.Title);
        Assert.Equal(OverallOutcome.UpdatesInstalled, report.Summary.Outcome);
        Assert.Equal("All the selected updates were installed.", report.Summary.Headline);
        Assert.True(report.Summary.RestartRecommended);
        Assert.Equal(["two"], service.AvailableUpdates!.Select(u => u.Id));
    }

    [Fact]
    public async Task A_failed_install_is_explained_and_the_update_stays_on_offer()
    {
        var agent = new FakeWindowsUpdateAgent { SearchResult = () => [FakeWindowsUpdateAgent.Update("good"), FakeWindowsUpdateAgent.Update("bad")] };
        agent.InstallResults["bad"] = () => new UpdateInstallOutcome(false, false, unchecked((int)0x80072EE7), "The download did not complete.");
        var service = new WindowsUpdateService(agent);
        await service.RunAsync(_ => { });

        RepairReport report = await Run(service.CreateInstallPlan(service.AvailableUpdates!));

        Assert.Equal(OverallOutcome.UpdatesPartlyInstalled, report.Summary.Outcome);
        Assert.Equal("1 of 2 updates were installed. The others could not be installed.", report.Summary.Headline);
        Assert.Contains(report.Summary.Details, d => d.StartsWith("Title bad: Windows could not connect", StringComparison.Ordinal));
        Assert.False(report.Summary.RestartRecommended);
        Assert.Equal(["bad"], service.AvailableUpdates!.Select(u => u.Id));
    }

    [Fact]
    public async Task Updates_that_may_ask_questions_are_never_installed_unattended()
    {
        var agent = new FakeWindowsUpdateAgent { SearchResult = () => [FakeWindowsUpdateAgent.Update("interactive", needsInput: true)] };
        var service = new WindowsUpdateService(agent);
        await service.RunAsync(_ => { });

        RepairReport report = await Run(service.CreateInstallPlan(service.AvailableUpdates!));

        Assert.Empty(agent.Installed);
        Assert.Equal(StepStatus.Skipped, report.Steps[0].Status);
        Assert.Equal(OverallOutcome.UpdatesNotInstalled, report.Summary.Outcome);
    }
}
