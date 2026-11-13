# Testing

Last full verification: 2026-11-07, on **Windows 11 Pro build 26300.9818 (x64) only**. Scenarios 14-17 (optional extras): 2026-11-13, same machine.
Windows 10 has **not** been tested yet.

## How each scenario is verified

| # | Scenario | Status | How |
|---|----------|--------|-----|
| 1 | Normal launch and UAC elevation | Partly verified | `requireAdministrator` manifest confirmed embedded in the published exe. The UAC prompt itself was not observed (the dev shell was already elevated). **Manual check needed.** |
| 2 | User declines UAC | Not tested | By design Windows does not start the app at all, so there is no app code involved. **Manual check needed.** |
| 3 | All commands complete successfully | Verified (logic, UI); partly real | Unit tests and the `clean` simulation. Real `DISM /CheckHealth` and real `sfc /verifyonly` ran through the production runner. A full real repair run has **not** been done. |
| 4 | DISM detects no corruption | Verified | Real `DISM /CheckHealth` on the dev machine → "No component store corruption detected" correctly classified. Unit tests, including RestoreHealth being skipped after a clean ScanHealth. |
| 5 | DISM detects and repairs corruption | Verified (logic, UI) | Unit tests and the `repaired` simulation. Not reproducible on a healthy PC. |
| 6 | DISM fails | Verified (logic, UI) | Unit tests (error codes, could-not-start, exceptions) and the `dism-fail` simulation: remaining DISM steps Skipped, SFC still runs, outcome "could not be fully completed". |
| 7 | SFC finds and repairs files | Verified (logic, UI) | Unit tests and the `repaired` simulation. |
| 8 | SFC finds files it cannot repair | Verified (logic, UI) | Unit tests (including SFC's mid-sentence line wrapping) and the `sfc-partial` simulation. |
| 9 | Close during a repair | Verified | UI Automation: window close blocked, explanation shown, process kept running, results delivered. |
| 10 | Restart confirm / cancel | Verified (simulated restart) | UI Automation: "No" leaves Windows running; "Yes" calls the restart path (simulated). The real `shutdown.exe /r` call was **not** executed. **Manual check needed.** |
| 11 | Restart later | Verified | UI Automation: choices hidden, explanation shown, Close exits the app. |
| 12 | Windows 10 and Windows 11 | Windows 11 only | **Windows 10 not tested.** |
| 13 | Update check (opt-in) | Logic verified | Unit tests with a fake network: version comparison, malformed and hostile responses, offline, timeout, exact request contents, saved choice. The prompt, notice and footer link have **not** been seen running, and a real "update available" response needs a published stable release. **Manual check needed.** |

| 14 | Temp file cleanup (opt-in) | Logic verified on the real file system; not run on real Temp folders | Real-file-system tests in a scratch folder: nested files and folders deleted, root kept, read-only files deleted, files held open left alone, files listed for a pending restart left alone, a **junction** inside Temp removed without touching its target, a **hard link** never deleting the outside file, a root that is itself a link refused. Path checks: only local folders named Temp/Tmp, never a drive root; another user's TEMP setting only accepted inside their own profile. Real-machine read-only test resolves `%LOCALAPPDATA%\Temp` and `C:\Windows\Temp`. **Not yet run against real Temp folders. The "different administrator approved the prompt" path is untested. Manual check needed.** |
| 15 | Windows Update check (opt-in) | Partly real | Unit tests with a fake agent: important listed before optional, up-to-date message, offline/disabled/busy errors explained, nothing installed by the check. **Real search verified** on the dev machine (17 s, found the pending monthly security update; the "maximum download size" Windows reports was 97 GB, so it is shown in the log only, not on screen). No optional or driver updates were on offer, so the optional classification (`BrowseOnly`) is **unverified on real data**. |
| 16 | Installing chosen updates | Logic only | Unit tests: only ticked updates installed, restart flagged when Windows needs one, failures explained and kept on offer, updates that may ask questions never installed unattended. **A real install has not been run. Run one on a VM (scenario checklist step 10).** |
| 17 | Welcome screen tick-boxes | Verified (non-elevated) | UI Automation on the Debug build launched without elevation: both tick-boxes found by name, unticked plan has 4 steps, ticking both gives 6 with cleanup first and the update check last. The running/results screens for the extras need an elevated simulated run (`--simulate=clean`, `--simulate=no-internet`). **Manual check needed.** |

Also verified: single-instance guard (second launch shows "already open"); the published Release
exe launches and ignores `--simulate`; the log folder ACL is restricted to SYSTEM/Administrators
(Users read-only); the real SFC output (UTF-16) decodes cleanly with 176 genuine progress updates.

## Automated tests

```cmd
dotnet test                                                  :: 145 tests (incl. real-file-system cleanup tests), about 10 s
set WRA_RUN_REAL_TOOLS=1 && dotnet test --filter Category=RealTools   :: elevated, about 1-15 min, read-only
```

## Manual release checklist (run on a clean Windows 10 VM and a Windows 11 VM)

1. Copy only `WindowsRepairAssistant.exe` to the VM (no .NET installed) and double-click it. The UAC prompt appears.
2. Click **No** on UAC. Nothing starts and no error dialog appears.
3. Launch again and click **Yes**. The welcome screen appears.
4. **Start repair**. Steps progress one at a time, DISM/SFC show real percentages, and CheckHealth shows the moving (indeterminate) bar.
5. Try closing the window mid-repair. It is blocked with an explanation.
6. Wait for the results. Check that the wording matches the log (**View log**).
7. **Restart computer now** → **No**: nothing happens. Then **Yes**: the PC restarts.
8. Relaunch after the restart and run again → **Restart later** → **Close**.
   - Update prompt: with no `C:\ProgramData\WindowsRepairAssistant\update-checks.txt`, the first launch asks once, No is the default, and later launches don't ask again. In a Debug build, `--simulate-update=9.9.9` with checks on shows the notice. **Copy link** pastes into a browser, and the **Update checks** link switches On/Off and survives a relaunch. Offline, nothing appears and Start repair still works.
9. Disconnect the network and run again on a VM with injected corruption, if available, to see the "no-internet" path for real.
10. Optional extras: tick both and **Start repair**.
    - Note the size of `%TEMP%` and `C:\Windows\Temp` first, and keep a file open (e.g. a document in Notepad saved in `%TEMP%`). Afterwards, the open file is still there, the results show plausible totals, and the log has no file names.
    - The Windows updates section lists the updates Settings > Windows Update shows (including optional ones), none ticked. Tick one, **Install selected updates**, confirm. It installs, the results say so, and a restart is recommended if Windows needs one. Check in Settings that it is installed.
    - Repeat offline: the check fails with the internet message and the repair verdict is unaffected.
    - On a VM with a standard user, approve the admin prompt with a *different* admin account: the standard user's Temp folder is cleaned, not the admin's.

## Not yet covered

- Non-English Windows. Result text matching is English-only; other languages fall back to exit codes and an honest "could not be confirmed" message. Verify on a German or French VM.
- ARM64 Windows. The x64 build should run under emulation, but this is untested.
- A real corrupted system (scenarios 5, 7 and 8 against real output). Corruption can be reproduced on a throwaway VM.
