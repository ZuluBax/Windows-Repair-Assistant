# Testing

Last full verification: 2026-11-07, on **Windows 11 Pro build 26300.9818 (x64) only**.
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

Also verified: single-instance guard (second launch shows "already open"); the published Release
exe launches and ignores `--simulate`; the log folder ACL is restricted to SYSTEM/Administrators
(Users read-only); the real SFC output (UTF-16) decodes cleanly with 176 genuine progress updates.

## Automated tests

```cmd
dotnet test                                                  :: 50 unit tests, run in under a second
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
9. Disconnect the network and run again on a VM with injected corruption, if available, to see the "no-internet" path for real.

## Not yet covered

- Non-English Windows. Result text matching is English-only; other languages fall back to exit codes and an honest "could not be confirmed" message. Verify on a German or French VM.
- ARM64 Windows. The x64 build should run under emulation, but this is untested.
- A real corrupted system (scenarios 5, 7 and 8 against real output). Corruption can be reproduced on a throwaway VM.
