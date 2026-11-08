using System.Windows;
using System.Windows.Threading;
using WindowsRepairAssistant.Core;
using WindowsRepairAssistant.Services;
using WindowsRepairAssistant.ViewModels;
using WindowsRepairAssistant.Views;

namespace WindowsRepairAssistant;

public partial class App : Application
{
    // Per-session name: one copy per signed-in user is enough. DISM/SFC conflicts across sessions
    // are caught separately by SystemInfo.FindRunningRepairTool.
    private const string SingleInstanceMutexName = @"Local\ShakaTech.WindowsRepairAssistant.SingleInstance";

    private Mutex? _singleInstanceMutex;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        LogStore logStore = LogStore.CreateDefault();
        var crashReporter = new CrashReporter(logStore);
        DispatcherUnhandledException += (_, args) => OnUnhandledUiException(crashReporter, args);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => crashReporter.Write(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            crashReporter.Write(args.Exception);
            args.SetObserved();
        };

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            MessageBox.Show(
                "Windows Repair Assistant is already open. Please use the window that is already running.",
                "Windows Repair Assistant", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        IProcessRunner processRunner = new SystemProcessRunner();
        Func<Task<RestartRequestResult>> requestRestart = WindowsRestart.RequestRestartAsync;
        IUpdateChecker updateChecker = new GitHubUpdateChecker();
        string versionLabel = AppInfo.Version;
        string? simulationTitle = null;
#if DEBUG
        string? scenario = e.Args.FirstOrDefault(a => a.StartsWith("--simulate=", StringComparison.Ordinal))?["--simulate=".Length..];
        if (scenario is not null && SimulatedProcessRunner.Scenarios.Contains(scenario))
        {
            processRunner = new SimulatedProcessRunner(scenario);
            requestRestart = SimulatedProcessRunner.SimulatedRestartAsync;
            // Simulated sessions share the real log folder, so they must be unmistakable there.
            versionLabel += $" [SIMULATION '{scenario}' - no real commands were run]";
            simulationTitle = $"Windows Repair Assistant [SIMULATION: {scenario}]";
        }

        string? simulatedUpdate = e.Args.FirstOrDefault(a => a.StartsWith("--simulate-update=", StringComparison.Ordinal))?["--simulate-update=".Length..];
        if (simulatedUpdate is not null)
        {
            updateChecker = new SimulatedUpdateChecker(simulatedUpdate);
        }
#endif

        _viewModel = new MainViewModel(
            new MessageBoxDialogService(), new ShellLauncher(), logStore, Dispatcher, processRunner, requestRestart, versionLabel,
            updateChecker, new UpdatePreferenceStore(logStore));
        var window = new MainWindow(_viewModel);
        if (simulationTitle is not null)
        {
            window.Title = simulationTitle;
        }

        MainWindow = window;
        window.Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Ask Windows not to sign out or shut down mid-repair. Windows may still force it, in which
        // case the unfinished log is detected on next launch.
        if (_viewModel?.IsRunning == true)
        {
            e.Cancel = true;
        }

        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private static void OnUnhandledUiException(CrashReporter crashReporter, DispatcherUnhandledExceptionEventArgs args)
    {
        string? crashLog = crashReporter.Write(args.Exception);

        // Keep the app alive: if a repair is running, DISM/SFC continue regardless, and the user is
        // better served by a window that still shows progress than by one that vanished.
        args.Handled = true;
        string where = crashLog is null ? "" : $"\n\nDetails were saved to:\n{crashLog}";
        MessageBox.Show(
            $"Something unexpected went wrong in Windows Repair Assistant.{where}",
            "Windows Repair Assistant", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
