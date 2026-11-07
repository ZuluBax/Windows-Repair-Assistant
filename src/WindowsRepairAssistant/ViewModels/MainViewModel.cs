using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using WindowsRepairAssistant.Core;
using WindowsRepairAssistant.Services;

namespace WindowsRepairAssistant.ViewModels;

public enum AppScreen
{
    Welcome,
    Running,
    Results,
}

public sealed class MainViewModel : ObservableObject, IRepairObserver
{
    private const int MaxOutputLines = 500;

    private readonly IReadOnlyList<RepairStepDefinition> _plan;
    private readonly IProcessRunner _processRunner;
    private readonly Func<Task<RestartRequestResult>> _requestRestart;
    private readonly string _versionLabel;
    private readonly IDialogService _dialogs;
    private readonly IShellLauncher _shell;
    private readonly LogStore _logStore;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _elapsedTimer;

    private DateTimeOffset _runStartedAt;
    private bool _lastOutputWasProgress;

    private AppScreen _screen = AppScreen.Welcome;
    private bool _isRunning;
    private bool _isAdministrator = true;
    private string? _startupNotice;
    private bool _startupNoticeIsWarning;

    private string _currentStepNumberText = "";
    private string _currentStepTitle = "";
    private string _currentStepDescription = "";
    private string _currentStatusText = "";
    private string _overallStatusText = "";
    private string _elapsedText = "";
    private bool _isProgressIndeterminate = true;
    private double _progressValue;
    private string _progressText = "";

    private OverallOutcome _outcome;
    private string _resultHeadline = "";
    private IReadOnlyList<string> _resultDetails = [];
    private string _totalDurationText = "";
    private string? _logFilePath;
    private string? _logProblem;
    private bool _restartRecommended;
    private bool _restartDeferred;
    private bool _isRestarting;

    public MainViewModel(
        IDialogService dialogs,
        IShellLauncher shell,
        LogStore logStore,
        Dispatcher dispatcher,
        IProcessRunner processRunner,
        Func<Task<RestartRequestResult>> requestRestart,
        string versionLabel)
    {
        _versionLabel = versionLabel;
        _processRunner = processRunner;
        _requestRestart = requestRestart;
        _dialogs = dialogs;
        _shell = shell;
        _logStore = logStore;
        _dispatcher = dispatcher;
        _plan = RepairPlan.CreateDefault();
        Steps = new ObservableCollection<StepViewModel>(_plan.Select((step, i) => new StepViewModel(step, i + 1)));

        _elapsedTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateElapsed(), dispatcher);
        _elapsedTimer.Stop();

        StartRepairCommand = new AsyncRelayCommand(StartRepairAsync, () => !IsRunning && IsAdministrator);
        RestartNowCommand = new AsyncRelayCommand(RestartNowAsync, () => Screen == AppScreen.Results && !IsRunning && !IsRestarting);
        RestartLaterCommand = new RelayCommand(() => RestartDeferred = true, () => Screen == AppScreen.Results && !IsRestarting);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        ViewLogCommand = new RelayCommand(ViewLog, () => LogFilePath is not null);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty), () => !IsRunning);
    }

    public event EventHandler? CloseRequested;

    public ObservableCollection<StepViewModel> Steps { get; }

    /// <summary>Raw tool output for the "technical details" panel; progress redraws replace the previous line.</summary>
    public ObservableCollection<string> OutputLines { get; } = [];

    public ICommand StartRepairCommand { get; }

    public ICommand RestartNowCommand { get; }

    public ICommand RestartLaterCommand { get; }

    public ICommand OpenLogFolderCommand { get; }

    public ICommand ViewLogCommand { get; }

    public ICommand CloseCommand { get; }

    public string AppVersion => AppInfo.Version;

    public AppScreen Screen { get => _screen; private set => SetProperty(ref _screen, value); }

    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }

    public bool IsAdministrator { get => _isAdministrator; private set => SetProperty(ref _isAdministrator, value); }

    public string? StartupNotice { get => _startupNotice; private set => SetProperty(ref _startupNotice, value); }

    public bool StartupNoticeIsWarning { get => _startupNoticeIsWarning; private set => SetProperty(ref _startupNoticeIsWarning, value); }

    public string CurrentStepNumberText { get => _currentStepNumberText; private set => SetProperty(ref _currentStepNumberText, value); }

    public string CurrentStepTitle { get => _currentStepTitle; private set => SetProperty(ref _currentStepTitle, value); }

    public string CurrentStepDescription { get => _currentStepDescription; private set => SetProperty(ref _currentStepDescription, value); }

    public string CurrentStatusText { get => _currentStatusText; private set => SetProperty(ref _currentStatusText, value); }

    public string OverallStatusText { get => _overallStatusText; private set => SetProperty(ref _overallStatusText, value); }

    public string ElapsedText { get => _elapsedText; private set => SetProperty(ref _elapsedText, value); }

    public bool IsProgressIndeterminate { get => _isProgressIndeterminate; private set => SetProperty(ref _isProgressIndeterminate, value); }

    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }

    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    public OverallOutcome Outcome { get => _outcome; private set => SetProperty(ref _outcome, value); }

    public string ResultHeadline { get => _resultHeadline; private set => SetProperty(ref _resultHeadline, value); }

    public IReadOnlyList<string> ResultDetails { get => _resultDetails; private set => SetProperty(ref _resultDetails, value); }

    public string TotalDurationText { get => _totalDurationText; private set => SetProperty(ref _totalDurationText, value); }

    public string? LogFilePath { get => _logFilePath; private set => SetProperty(ref _logFilePath, value); }

    public string? LogProblem { get => _logProblem; private set => SetProperty(ref _logProblem, value); }

    public bool RestartRecommended { get => _restartRecommended; private set => SetProperty(ref _restartRecommended, value); }

    public bool RestartDeferred
    {
        get => _restartDeferred;
        private set
        {
            if (SetProperty(ref _restartDeferred, value))
            {
                OnPropertyChanged(nameof(ShowRestartChoices));
            }
        }
    }

    public bool IsRestarting
    {
        get => _isRestarting;
        private set
        {
            if (SetProperty(ref _isRestarting, value))
            {
                OnPropertyChanged(nameof(ShowRestartChoices));
            }
        }
    }

    public bool ShowRestartChoices => !RestartDeferred && !IsRestarting;

    public string RestartLaterMessage => RestartRecommended
        ? "Windows will keep running normally. Please restart your computer soon so that all repairs can be completed."
        : "Windows will keep running normally. You can close this window.";

    /// <summary>Checks run once the window is shown.</summary>
    public void Initialize()
    {
        if (!SystemInfo.IsRunningAsAdministrator())
        {
            IsAdministrator = false;
            StartupNoticeIsWarning = true;
            StartupNotice = "Windows Repair Assistant is not running with administrator permission, so it cannot repair Windows. " +
                            "Close it, open it again and choose \"Yes\" when Windows asks for permission.";
            return;
        }

        if (_logStore.FindInterruptedSessionLog() is not null)
        {
            StartupNoticeIsWarning = false;
            StartupNotice = "Your previous repair did not finish normally. The app or the computer may have been closed while it was running. " +
                            "You can safely start a new repair. The unfinished log is in the log folder.";
        }

        // Also on startup (not only after a repair) so crash logs are tidied even if no repair is ever run.
        // Runs after the interrupted-session check; the newest log is never pruned anyway.
        _logStore.PruneOldLogs();
    }

    private async Task StartRepairAsync()
    {
        if (IsRunning)
        {
            return;
        }

        if (!SystemInfo.IsRunningAsAdministrator())
        {
            _dialogs.ShowError("Administrator permission needed",
                "Windows Repair Assistant needs administrator permission to repair Windows. Close the app, open it again and choose \"Yes\" when Windows asks for permission.");
            return;
        }

        string? runningTool = SystemInfo.FindRunningRepairTool();
        if (runningTool is not null)
        {
            _dialogs.ShowError("Another repair is already running",
                $"{runningTool} is already running on this computer, possibly from an earlier repair.\n\nRunning two repairs at once can cause both to fail. Please wait a few minutes for it to finish, then try again.");
            return;
        }

        PrepareForRun();
        ISessionLog log = OpenSessionLog();
        try
        {
            var orchestrator = new RepairOrchestrator(
                _processRunner, _plan, log,
                new SessionContext(_versionLabel, SystemInfo.DescribeWindows()),
                TimeProvider.System);

            // Off the UI thread so nothing the orchestrator does synchronously can freeze the window.
            RepairReport report = await Task.Run(() => orchestrator.RunAsync(this));
            ShowResults(report);
        }
        catch (Exception ex)
        {
            log.Error("The repair session stopped unexpectedly.", ex);
            ShowUnexpectedFailure();
        }
        finally
        {
            log.Dispose();
            _elapsedTimer.Stop();
            IsRunning = false;
            CommandManager.InvalidateRequerySuggested();
        }

        _logStore.PruneOldLogs();
    }

    private void PrepareForRun()
    {
        foreach (StepViewModel step in Steps)
        {
            step.Reset();
        }

        OutputLines.Clear();
        _lastOutputWasProgress = false;
        StartupNotice = null;
        RestartDeferred = false;
        IsRestarting = false;
        LogFilePath = null;
        LogProblem = null;
        CurrentStepNumberText = "";
        CurrentStepTitle = "Preparing…";
        CurrentStepDescription = "";
        CurrentStatusText = "";
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressText = "";
        OverallStatusText = $"0 of {Steps.Count} steps finished";

        IsRunning = true;
        Screen = AppScreen.Running;
        _runStartedAt = DateTimeOffset.Now;
        UpdateElapsed();
        _elapsedTimer.Start();
    }

    private ISessionLog OpenSessionLog()
    {
        try
        {
            FileSessionLog log = _logStore.CreateSessionLog(DateTimeOffset.Now, TimeProvider.System);
            LogFilePath = log.FilePath;
            return log;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A logging problem must not stop the user repairing their PC; we tell them on the results screen.
            LogProblem = $"A log file could not be saved for this repair: {ex.Message}";
            return NullSessionLog.Instance;
        }
    }

    private void ShowResults(RepairReport report)
    {
        for (int i = 0; i < report.Steps.Count && i < Steps.Count; i++)
        {
            Steps[i].Apply(report.Steps[i]);
        }

        Outcome = report.Summary.Outcome;
        ResultHeadline = report.Summary.Headline;
        ResultDetails = report.Summary.Details;
        RestartRecommended = report.Summary.RestartRecommended;
        OnPropertyChanged(nameof(RestartLaterMessage));
        TotalDurationText = $"Total time: {FileSessionLog.FormatDuration(report.Duration)}";
        Screen = AppScreen.Results;
    }

    private void ShowUnexpectedFailure()
    {
        foreach (StepViewModel step in Steps.Where(s => !s.IsFinished))
        {
            step.Status = StepStatus.Skipped;
            step.Message = "Not run because the repair stopped unexpectedly.";
        }

        Outcome = OverallOutcome.Incomplete;
        ResultHeadline = "The repair stopped unexpectedly.";
        ResultDetails = ["Windows Repair Assistant ran into an unexpected problem. No further changes were made. You can close the app and try again. The log file contains technical details."];
        RestartRecommended = false;
        OnPropertyChanged(nameof(RestartLaterMessage));
        TotalDurationText = $"Total time: {FileSessionLog.FormatDuration(DateTimeOffset.Now - _runStartedAt)}";
        Screen = AppScreen.Results;
    }

    private async Task RestartNowAsync()
    {
        // Defensive: the button only exists on the results screen, but never restart mid-repair.
        if (IsRunning || Screen != AppScreen.Results)
        {
            return;
        }

        if (!_dialogs.ConfirmRestart())
        {
            return;
        }

        IsRestarting = true;
        RestartRequestResult result = await _requestRestart();
        if (!result.Succeeded)
        {
            IsRestarting = false;
            _dialogs.ShowError("Restart did not start",
                $"Windows could not be restarted automatically: {result.ErrorMessage}\n\nPlease save your work and restart from the Start menu instead.");
        }
    }

    private void OpenLogFolder()
    {
        string? error = _shell.OpenFolder(_logStore.LogDirectory);
        if (error is not null)
        {
            _dialogs.ShowInformation("Log folder", error);
        }
    }

    private void ViewLog()
    {
        if (LogFilePath is null)
        {
            return;
        }

        string? error = _shell.OpenTextFile(LogFilePath);
        if (error is not null)
        {
            _dialogs.ShowInformation("Log file", error);
        }
    }

    private void UpdateElapsed() =>
        ElapsedText = $"Elapsed time {FileSessionLog.FormatDuration(DateTimeOffset.Now - _runStartedAt)}";

    void IRepairObserver.StepStarted(int index, RepairStepDefinition step) => Post(() =>
    {
        Steps[index].Status = StepStatus.Running;
        CurrentStepNumberText = $"STEP {index + 1} OF {Steps.Count}";
        CurrentStepTitle = step.Title;
        CurrentStepDescription = step.Description;
        CurrentStatusText = $"Running {step.DisplayName}…";
        IsProgressIndeterminate = true;
        ProgressValue = 0;
        ProgressText = "Working… This step may pause for several minutes at a time. That is normal.";
        AppendOutput($"> {step.CommandLine}", isProgress: false);
    });

    void IRepairObserver.StepOutput(int index, string line, double? percent) => Post(() =>
    {
        if (percent is double value)
        {
            IsProgressIndeterminate = false;
            ProgressValue = value;
            ProgressText = $"{value:0.#}% complete (reported by Windows)";
        }
        else
        {
            CurrentStatusText = line;
        }

        AppendOutput(line, percent.HasValue);
    });

    void IRepairObserver.StepFinished(int index, StepResult result) => Post(() =>
    {
        Steps[index].Apply(result);
        OverallStatusText = $"{Steps.Count(s => s.IsFinished)} of {Steps.Count} steps finished";
    });

    private void AppendOutput(string line, bool isProgress)
    {
        if (isProgress && _lastOutputWasProgress && OutputLines.Count > 0)
        {
            OutputLines[^1] = line;
        }
        else
        {
            OutputLines.Add(line);
            if (OutputLines.Count > MaxOutputLines)
            {
                OutputLines.RemoveAt(0);
            }
        }

        _lastOutputWasProgress = isProgress;
    }

    // Same priority for every update so they are applied in the order the tools produced them.
    private void Post(Action action) => _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
}
