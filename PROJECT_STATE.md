# Project state: Windows Repair Assistant

_Last updated: 2026-11-13_

## In progress: v1.1 optional extras (branch `feat/temp-cleanup-and-windows-updates`, not yet merged)

Two opt-in tick-boxes on the Welcome screen: **Clean up temporary files** (runs first) and **Check for
Windows updates** (runs last; the user then ticks which updates to install). Both are built, with 145 tests
passing and Debug/Release builds clean. Still to do before merging: an elevated simulated run of the
running/results screens, then a real cleanup and a real update install on a VM
(docs/TESTING.md, scenarios 14-17 and checklist step 10).

## Status

**v1.0.0 feature-complete; stable not yet released.** Unsigned pre-releases `v1.0.0-beta.1` and `-beta.2` are public. Builds clean (warnings as errors), 87 unit tests
passing, read-only real-tool tests passing, UI flows verified with UI Automation using simulated
runs. The release exe is produced by `scripts\publish.cmd`.

## Blockers before public release

1. **Code signing.** SignPath Foundation did not approve the application (November 2026). A signing route is needed: reapply once the project has more release history and reputation, Azure Artifact Signing if eligible, or an OV certificate. The SignPath workflow step is dormant, and stable tags are refused until signing works.
2. **Windows 10 testing.** Never run on Windows 10. Use the manual checklist in `docs/TESTING.md`.
3. **One real end-to-end repair run** on a VM, including a real restart.

## Next steps (suggested order)

1. Run the manual checklist on Windows 10 and Windows 11 VMs.
2. Choose a signing route (see blocker 1). A non-SignPath route also needs the signing step in release.yml changed, and the code signing policy updated to match.
3. ~~Add an app icon~~ Done (`tools/IconGenerator` → `Assets/app.ico`).
4. After the first signed release, upload it to VirusTotal and report any false positives to the flagging vendors. (Repo: https://github.com/ZuluBax/Windows-Repair-Assistant)
5. Run the update check's prompt, notice and footer link for real (they have only been unit-tested; see docs/TESTING.md, scenario 13). If reapplying to SignPath Foundation, check the opt-in check against their privacy terms first.
6. Later / v1.1 candidates: one-click update install (after signing is live); only if users ask: CBS.log-based SFC result detection for non-English Windows, dark mode.

## Architecture (short)

- **Core** (`WindowsRepairAssistant.Core`, no UI) holds the logic:
  `SystemProcessRunner` → `ConsoleOutputReader` (encoding detection, CR/LF splitting) →
  `OutputAnalyzer` (status + finding per step) → `RepairOrchestrator` (sequencing, skip rules) →
  `SummaryBuilder` (plain-English verdict) → `FileSessionLog` / `LogStore`.
- **App** (WPF, MVVM-lite): `MainViewModel` implements `IRepairObserver` and marshals updates to
  the dispatcher. There are three screens in `MainWindow.xaml` and no third-party packages.

## Key decisions (and why)

| Decision | Why |
|----------|-----|
| Steps never cancelled or killed once started | Interrupting DISM/SFC mid-repair can leave the component store worse off. Window close is blocked during a run; if the app dies anyway, the tools keep running and the unfinished log is detected on next launch. |
| A failed DISM step skips the remaining DISM steps, but SFC always runs | DISM steps share the servicing stack and fail the same way; SFC is independent and still useful. |
| RestoreHealth is skipped if Windows says the image is "not repairable" | It cannot succeed; avoids a pointless 20-minute wait. |
| RestoreHealth is skipped if ScanHealth explicitly reports no corruption | There is nothing to repair, and "No damage found" followed by "Repairing" reads as a contradiction. Counts as a clean result, not "incomplete". CheckHealth alone can't trigger the skip (it only reads a recorded flag), and neither can unrecognised output (non-English Windows). |
| "No problems" / "repaired" claimed only on explicit tool messages | The spec says never to claim success because a command finished. SFC's exit code doesn't distinguish clean from corrupt, so unrecognised SFC output is "Completed with issues / could not be confirmed". |
| Output read as raw bytes with encoding detection | `sfc.exe` writes UTF-16 when redirected while DISM writes in the OEM code page. Verified against real output. |
| Split lines on CR as well as LF | DISM/SFC redraw progress with a bare CR; otherwise progress would only appear at the end. |
| Progress bar only shows real percentages | DISM and SFC report them; CheckHealth does not, so the bar is indeterminate there. No invented overall percentage. |
| Absolute `System32` paths, `ArgumentList` | The app runs elevated: no PATH lookup, no string-built command lines. |
| Logs in `%ProgramData%` with a restricted ACL, reparse-point checks, `CreateNew` | Stops a standard user from planting links that redirect an elevated write or delete (a privilege-escalation pattern). |
| Self-contained single file, no trimming | Users need no .NET install; WPF doesn't support trimming. Costs ~62 MB. |
| Icon drawn from our own vector geometry; DIB entries below 256 px, PNG only at 256 px | An icon-font glyph isn't ours to distribute. An all-PNG `.ico` embedded via `<ApplicationIcon>` produced an assembly the runtime rejected ("Bad IL format") and that Malwarebytes quarantined. |
| Update check is opt-in, asked once on first run, and only shows a notice with a link to copy | A check sends the user's IP address to GitHub, so it needs consent to keep the "unless specifically requested" privacy promise. The link is copied rather than opened because a browser launched from this elevated process would run as administrator. The link is a fixed address, and only a strictly validated version number from the response is displayed. |
| No self-install yet | An elevated self-replacing exe needs signed releases to verify against, and SignPath Foundation's certificate may be shared across projects, so a signer check alone may not identify our builds. Revisit for v1.1 once signing is live. |
| Extras are opt-in, unticked by default; cleanup first, update check last | Deleting files and contacting Microsoft are the user's choice. Cleanup first frees space before DISM may download; the update check waits until DISM/SFC have finished with the servicing stack. Extras never affect the repair verdict. |
| Temp cleanup: every item is opened without following links, its real path checked to be inside the folder, then deleted through the same handle | Runs as administrator in folders users can write to (`C:\Windows\Temp`). Stops junction/link swaps redirecting the delete (privilege escalation). A link is only ever removed itself. |
| Temp cleanup: "in use" means opened by anyone (exclusive open), plus files Windows will *move* at restart | The user asked for "everything not in use", with no age limit. Installers stage files in Temp to be moved at restart; deleting them first would silently break that update. "Delete at restart" entries are not protected: the dev PC had 5,726 of them, none in Temp, and the first version wrongly reported them all as "left alone". |
| Temp cleanup: only folders named Temp/Tmp, never a drive root; another user's TEMP setting accepted only inside their profile | A TEMP set to `C:\` must never mean "delete the drive". A standard user controls their own TEMP value but we delete as admin, so it cannot point outside their profile. |
| Temp cleanup targets the user signed in to the session, not the elevating account | If a different admin approves UAC, our own TEMP is the admin's. The session user comes from WTS and their profile/TEMP from the registry. |
| Windows Update via the built-in COM API (`Microsoft.Update.Session`, `dynamic`) | No package needed. Uses whatever source the PC is configured for (Microsoft or WSUS). Searching works without elevation. |
| Optional = `IUpdate.BrowseOnly`; updates that may ask questions are listed but cannot be ticked | Matches Settings' "Optional updates". An unattended install that asks a question would hang. |
| No download size on screen | Windows reports a worst-case `MaxDownloadSize` (97 GB was seen for a monthly cumulative update). It is logged and labelled as such instead. |
| Update install reuses the orchestrator (one step per update) | Gets the progress screen, close guard, log and results for free. |
| Simulation mode is `#if DEBUG` only | It's for testing UI flows quickly; it must not ship. Verified absent from the Release build. |

## Known limitations / technical debt

- Extras: no real optional/driver update was on offer while testing, so the `BrowseOnly` classification is unverified on real data. A real update install has not been run. Using WUA COM objects found by the search on a later (MTA) thread during install is untested on a real PC.
- Temp cleanup counts a file as deleted when Windows accepts the delete request; the actual removal happens as the handle closes, and a failure at that moment would not be reported.
- Result text matching is **English-only** (DISM/SFC give no machine-readable result). Other languages degrade honestly to "could not be confirmed".
- x64 build only; ARM64 untested.
- The `.NET 10 SDK` on the dev machine is a **per-user install** (`%LOCALAPPDATA%\Microsoft\dotnet10`), because the machine-wide winget install needed a UAC approval. Run `winget install Microsoft.DotNet.SDK.10` yourself to get it on PATH. `publish.cmd` finds either. The machine-wide SDK is 8.0, which goes out of support on 2026-11-10.
- On this dev machine, `dotnet` launched from a PowerShell host exits immediately (an environment quirk seen in the AI tooling session), so the publish script is a `.cmd`. It is unlikely to affect a normal terminal.
