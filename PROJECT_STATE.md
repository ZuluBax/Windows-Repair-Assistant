# Project state: Windows Repair Assistant

_Last updated: 2026-11-07_

## Status

**v1.0.0 feature-complete; not yet released.** Builds clean (warnings as errors), 44 unit tests
passing, read-only real-tool tests passing, UI flows verified with UI Automation using simulated
runs. The release exe is produced by `scripts\publish.cmd`.

## Blockers before public release

1. **Code signing.** The exe is unsigned, so UAC shows "Unknown publisher" and SmartScreen warns. That's unacceptable for an admin tool.
2. **Windows 10 testing.** Never run on Windows 10. Use the manual checklist in `docs/TESTING.md`.
3. **One real end-to-end repair run** on a VM, including a real restart.

## Next steps (suggested order)

1. Run the manual checklist on Windows 10 and Windows 11 VMs.
2. Set up code signing and add a signing step to `publish.cmd`.
3. ~~Add an app icon~~ Done (`tools/IconGenerator` → `Assets/app.ico`).
4. Add the signing step to `.github/workflows/release.yml` once a certificate exists. (CI and tag-triggered release workflows are already in place; repo: https://github.com/ZuluBax/Windows-Repair-Assistant)
5. Later / v1.1 candidates, only if users ask: CBS.log-based SFC result detection for non-English Windows, dark mode.

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
| "No problems" / "repaired" claimed only on explicit tool messages | The spec says never to claim success because a command finished. SFC's exit code doesn't distinguish clean from corrupt, so unrecognised SFC output is "Completed with issues / could not be confirmed". |
| Output read as raw bytes with encoding detection | `sfc.exe` writes UTF-16 when redirected while DISM writes in the OEM code page. Verified against real output. |
| Split lines on CR as well as LF | DISM/SFC redraw progress with a bare CR; otherwise progress would only appear at the end. |
| Progress bar only shows real percentages | DISM and SFC report them; CheckHealth does not, so the bar is indeterminate there. No invented overall percentage. |
| Absolute `System32` paths, `ArgumentList` | The app runs elevated: no PATH lookup, no string-built command lines. |
| Logs in `%ProgramData%` with a restricted ACL, reparse-point checks, `CreateNew` | Stops a standard user from planting links that redirect an elevated write or delete (a privilege-escalation pattern). |
| Self-contained single file, no trimming | Users need no .NET install; WPF doesn't support trimming. Costs ~62 MB. |
| Icon drawn from our own vector geometry; DIB entries below 256 px, PNG only at 256 px | An icon-font glyph isn't ours to distribute. An all-PNG `.ico` embedded via `<ApplicationIcon>` produced an assembly the runtime rejected ("Bad IL format") and that Malwarebytes quarantined. |
| Simulation mode is `#if DEBUG` only | It's for testing UI flows quickly; it must not ship. Verified absent from the Release build. |

## Known limitations / technical debt

- Result text matching is **English-only** (DISM/SFC give no machine-readable result). Other languages degrade honestly to "could not be confirmed".
- x64 build only; ARM64 untested.
- The `.NET 10 SDK` on the dev machine is a **per-user install** (`%LOCALAPPDATA%\Microsoft\dotnet10`), because the machine-wide winget install needed a UAC approval. Run `winget install Microsoft.DotNet.SDK.10` yourself to get it on PATH. `publish.cmd` finds either. The machine-wide SDK is 8.0, which goes out of support on 2026-11-10.
- On this dev machine, `dotnet` launched from a PowerShell host exits immediately (an environment quirk seen in the AI tooling session), so the publish script is a `.cmd`. It is unlikely to affect a normal terminal.
