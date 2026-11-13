using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace WindowsRepairAssistant.Core;

/// <summary>An update Windows Update offers for this computer.</summary>
/// <param name="DownloadSizeBytes">Windows Update's worst-case figure, which can be far above the real download. Log only.</param>
/// <param name="IsOptional">Shown by Windows under "Optional updates" (drivers, previews): Windows does not install these by itself.</param>
/// <param name="NeedsUserInput">The installer may ask questions, which would hang with nobody to answer them.</param>
public sealed record WindowsUpdateInfo(string Id, string Title, long DownloadSizeBytes, bool IsOptional, bool IsDriver, bool NeedsUserInput);

public sealed record UpdateInstallOutcome(bool Succeeded, bool RebootRequired, int? HResult, string? Detail);

/// <summary>A Windows Update failure, with the error code where Windows gave one.</summary>
public sealed class WindowsUpdateException(string message, int? hResult = null, Exception? inner = null) : Exception(message, inner)
{
    public int? ErrorCode { get; } = hResult;
}

/// <summary>Thin wrapper over the Windows Update Agent, so everything that decides anything can be tested without it.</summary>
public interface IWindowsUpdateAgent
{
    /// <exception cref="WindowsUpdateException">The search failed.</exception>
    IReadOnlyList<WindowsUpdateInfo> Search();

    /// <summary>Installs one update returned by the latest <see cref="Search"/>.</summary>
    /// <exception cref="WindowsUpdateException">Windows Update could not be used.</exception>
    UpdateInstallOutcome DownloadAndInstall(string updateId, Action<string> onProgress);
}

/// <summary>
/// The "check for Windows updates" step, plus installing the updates the user then ticks.
/// Nothing is ever installed unless the user picked it.
/// </summary>
public sealed class WindowsUpdateService(IWindowsUpdateAgent agent) : IInProcessStep
{
    private readonly Lock _gate = new();
    private List<WindowsUpdateInfo>? _available;

    /// <summary>Updates found by the last successful check, minus any installed since. Null if no check succeeded.</summary>
    public IReadOnlyList<WindowsUpdateInfo>? AvailableUpdates
    {
        get
        {
            lock (_gate)
            {
                return _available?.ToList();
            }
        }
    }

    /// <summary>The check step.</summary>
    public async Task<StepAnalysis> RunAsync(Action<string> onOutputLine)
    {
        ArgumentNullException.ThrowIfNull(onOutputLine);
        lock (_gate)
        {
            _available = null;
        }

        onOutputLine("Asking Windows Update which updates are available. This can take a few minutes…");
        IReadOnlyList<WindowsUpdateInfo> found;
        try
        {
            // The agent's calls block, sometimes for minutes.
            found = await Task.Run(agent.Search).ConfigureAwait(false);
        }
        catch (WindowsUpdateException ex)
        {
            onOutputLine($"Search failed: {ex.Message}{FormatCode(ex.ErrorCode)}");
            return new StepAnalysis(StepStatus.Failed, StepFinding.ToolError, $"The check could not be completed. {Describe(ex)}");
        }

        List<WindowsUpdateInfo> sorted = found
            .OrderBy(u => u.IsOptional)
            .ThenBy(u => u.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (WindowsUpdateInfo update in sorted)
        {
            // Labelled as Windows' own worst-case figure: the real download is often far smaller.
            onOutputLine($"Found: {update.Title} ({(update.IsOptional ? "optional" : "important")}; " +
                         $"maximum download size reported by Windows Update: {TempCleanupStep.FormatSize(update.DownloadSizeBytes)})");
        }

        lock (_gate)
        {
            _available = sorted;
        }

        if (sorted.Count == 0)
        {
            return new StepAnalysis(StepStatus.Succeeded, StepFinding.None, "Windows is up to date. No updates are available.");
        }

        int optional = sorted.Count(u => u.IsOptional);
        string counts = string.Create(CultureInfo.InvariantCulture,
            $"{sorted.Count} {(sorted.Count == 1 ? "update is" : "updates are")} available ({sorted.Count - optional} important, {optional} optional).");
        return new StepAnalysis(StepStatus.Succeeded, StepFinding.None,
            counts + " Nothing has been installed. Choose which ones to install in the Windows updates section below.");
    }

    /// <summary>One step per update, so each gets its own progress, result and log entry.</summary>
    public IReadOnlyList<RepairStepDefinition> CreateInstallPlan(IReadOnlyList<WindowsUpdateInfo> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return selected.Select((update, i) => RepairStepDefinition.ForInProcess(
                RepairStepKind.InstallWindowsUpdate,
                string.Create(CultureInfo.InvariantCulture, $"Update {i + 1} of {selected.Count}"),
                update.Title,
                "Downloads and installs this update. Large updates can take 30 minutes or more.",
                new InstallStep(this, update)))
            .ToList();
    }

    private async Task<StepAnalysis> InstallAsync(WindowsUpdateInfo update, Action<string> output)
    {
        if (update.NeedsUserInput)
        {
            return new StepAnalysis(StepStatus.Skipped, StepFinding.None,
                "This update may ask you questions while it installs, so please install it from Settings > Windows Update.");
        }

        UpdateInstallOutcome outcome;
        try
        {
            outcome = await Task.Run(() => agent.DownloadAndInstall(update.Id, output)).ConfigureAwait(false);
        }
        catch (WindowsUpdateException ex)
        {
            output($"Install failed: {ex.Message}{FormatCode(ex.ErrorCode)}");
            return new StepAnalysis(StepStatus.Failed, StepFinding.ToolError, Describe(ex));
        }

        if (!outcome.Succeeded)
        {
            output($"Install failed{FormatCode(outcome.HResult)}. {outcome.Detail}");
            string reason = outcome.HResult is int hr ? DescribeError(hr) : outcome.Detail ?? "Windows Update did not say why.";
            return new StepAnalysis(StepStatus.Failed, StepFinding.ToolError, reason);
        }

        lock (_gate)
        {
            _available?.RemoveAll(u => u.Id == update.Id);
        }

        output(outcome.RebootRequired ? "Installed. Windows needs a restart to finish." : "Installed.");
        return outcome.RebootRequired
            ? new StepAnalysis(StepStatus.Succeeded, StepFinding.UpdateInstalledRestartRequired, "Installed. A restart is needed to finish installing it.")
            : new StepAnalysis(StepStatus.Succeeded, StepFinding.UpdateInstalled, "Installed.");
    }

    private static string Describe(WindowsUpdateException ex) =>
        ex.ErrorCode is int hr ? DescribeError(hr) : ex.Message;

    /// <summary>Plain-English text for the Windows Update errors people actually hit.</summary>
    public static string DescribeError(int hResult) => unchecked((uint)hResult) switch
    {
        0x8024402C or 0x80072EE7 or 0x80072EFD or 0x80072EE2 or 0x8024401C or 0x80244022 or 0x8024402F or 0x80072F8F =>
            "Windows could not connect to Windows Update. Check that you are connected to the internet, then try again.",
        0x80070422 => "The Windows Update service is turned off on this computer.",
        0x8024001E => "The Windows Update service stopped while working. Restarting your computer usually fixes this.",
        0x80240016 => "Windows Update is busy with another installation, or is waiting for a restart. Restart your computer, then try again.",
        0x8024002E => "Windows Update is managed by your organisation, or turned off by a setting on this computer.",
        0x80070005 => "Windows Update did not allow this. Close the app, open it again and choose \"Yes\" when Windows asks for permission.",
        _ => string.Create(CultureInfo.InvariantCulture, $"Windows Update reported error 0x{unchecked((uint)hResult):X8}."),
    };

    private static string FormatCode(int? hResult) =>
        hResult is int hr ? string.Create(CultureInfo.InvariantCulture, $" (0x{unchecked((uint)hr):X8})") : "";

    private sealed class InstallStep(WindowsUpdateService service, WindowsUpdateInfo update) : IInProcessStep
    {
        public Task<StepAnalysis> RunAsync(Action<string> onOutputLine) => service.InstallAsync(update, onOutputLine);
    }
}

/// <summary>
/// The real Windows Update Agent, used through its COM automation interface (built into Windows; no
/// extra package). It uses whatever source the PC is set up for: Microsoft, or an organisation's server.
/// </summary>
public sealed class WuaWindowsUpdateAgent : IWindowsUpdateAgent
{
    private const string ClientId = "Windows Repair Assistant";
    private const int UpdateTypeDriver = 2;
    private const int ResultSucceeded = 2;
    private const int ResultSucceededWithErrors = 3;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, object> _lastSearch = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WindowsUpdateInfo> Search() => Call(() =>
    {
        dynamic session = CreateSession();
        dynamic searcher = session.CreateUpdateSearcher();
        // Everything not yet installed that the user hasn't hidden, drivers included: optional updates
        // are exactly what the user asked to see.
        dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");
        int code = (int)result.ResultCode;
        if (code is not (ResultSucceeded or ResultSucceededWithErrors))
        {
            throw new WindowsUpdateException("The search did not complete.");
        }

        var found = new List<WindowsUpdateInfo>();
        lock (_gate)
        {
            _lastSearch.Clear();
            foreach (dynamic update in result.Updates)
            {
                string id = (string)update.Identity.UpdateID;
                _lastSearch[id] = update;
                found.Add(new WindowsUpdateInfo(
                    id,
                    (string)update.Title,
                    Convert.ToInt64((object)update.MaxDownloadSize, CultureInfo.InvariantCulture),
                    IsOptional: (bool)update.BrowseOnly,
                    IsDriver: (int)update.Type == UpdateTypeDriver,
                    NeedsUserInput: (bool)update.InstallationBehavior.CanRequestUserInput));
            }
        }

        return found;
    });

    public UpdateInstallOutcome DownloadAndInstall(string updateId, Action<string> onProgress) => Call(() =>
    {
        object? found;
        lock (_gate)
        {
            _lastSearch.TryGetValue(updateId, out found);
        }

        if (found is null)
        {
            throw new WindowsUpdateException("This update is no longer offered. Run the check again.");
        }

        dynamic update = found;
        dynamic session = CreateSession();
        dynamic installer = session.CreateUpdateInstaller();
        if ((bool)installer.IsBusy)
        {
            return new UpdateInstallOutcome(false, false, unchecked((int)0x80240016), null);
        }

        // The user explicitly chose this update; Settings > Windows Update accepts licence terms the same way.
        if (!(bool)update.EulaAccepted)
        {
            update.AcceptEula();
        }

        dynamic collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", throwOnError: true)!)!;
        collection.Add(update);

        if (!(bool)update.IsDownloaded)
        {
            onProgress("Downloading…");
            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = collection;
            dynamic download = downloader.Download();
            if ((int)download.ResultCode is not (ResultSucceeded or ResultSucceededWithErrors))
            {
                return new UpdateInstallOutcome(false, false, (int)download.HResult, "The download did not complete.");
            }
        }

        onProgress("Installing… Please don't turn off your computer.");
        installer.Updates = collection;
        dynamic install = installer.Install();
        int code = (int)install.ResultCode;
        bool succeeded = code == ResultSucceeded;
        return new UpdateInstallOutcome(succeeded, (bool)install.RebootRequired, succeeded ? null : (int)install.HResult,
            succeeded ? null : "The installation did not complete.");
    });

    private static dynamic CreateSession()
    {
        Type type = Type.GetTypeFromProgID("Microsoft.Update.Session")
                    ?? throw new WindowsUpdateException("Windows Update is not available on this computer.");
        dynamic session = Activator.CreateInstance(type)!;
        session.ClientApplicationID = ClientId;
        return session;
    }

    private static T Call<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (COMException ex)
        {
            throw new WindowsUpdateException(ex.Message, ex.HResult, ex);
        }
        catch (Exception ex) when (ex is RuntimeBinderException or InvalidCastException or UnauthorizedAccessException)
        {
            throw new WindowsUpdateException("Windows Update gave a response this app does not understand.", null, ex);
        }
    }
}
