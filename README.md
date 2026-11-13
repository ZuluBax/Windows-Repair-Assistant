# Windows Repair Assistant

A free, lightweight Windows 10/11 utility that runs Microsoft's built-in repair tools in the right
order and explains the result in plain English.

**Double-click → approve administrator permission → Start Repair → wait → view results → restart now or later.**

It runs, strictly one after another:

| # | Command | Purpose |
|---|---------|---------|
| 1 | `DISM /Online /Cleanup-Image /CheckHealth` | Has Windows already recorded component store damage? |
| 2 | `DISM /Online /Cleanup-Image /ScanHealth` | Thorough scan of the component store |
| 3 | `DISM /Online /Cleanup-Image /RestoreHealth` | Repair the component store (may download from Windows Update). Skipped when step 2 confirms there is no damage |
| 4 | `sfc /scannow` | Scan and repair protected system files |

Two **optional extras** can be ticked on the first screen. Both start unticked:

| When | Extra | What it does |
|------|-------|--------------|
| Before step 1 | **Clean up temporary files** | Deletes everything in the signed-in user's Temp folder and `C:\Windows\Temp` that is not in use. Files Windows is waiting to move at the next restart are kept. |
| After step 4 | **Check for Windows updates** | Asks Windows Update which updates (important and optional) are available. The user ticks the ones they want and chooses **Install selected updates**. Nothing is pre-ticked or installed automatically. |

No ads, accounts, telemetry, database or background services. Its only network call of its own is an
optional update check for this app, which it asks about on first run. The optional Windows Update check
uses Windows' own update service.

## Privacy and logs

**Your files, logs and system details never leave your computer.** Windows Repair Assistant does not
collect or upload any of them. There is no telemetry, no analytics, no crash reporting service and no
account. Network activity is limited to Microsoft's own DISM tool, which may download replacement
Windows files from Windows Update during a repair, the app's update check if you agree to it, and
Windows Update itself if you tick **Check for Windows updates**.

- **Temp file cleanup** happens only if you tick it, and only on this computer. No list of deleted files
  is kept; the log records totals only.

- **Update checks are opt-in.** On first run the app asks whether it may ask GitHub for the latest
  version number each time it opens. GitHub sees your IP address and nothing else is sent. The app only
  tells you a new version exists; it never downloads or installs anything. Change your choice with the
  **Update checks** link at the bottom of the window.
- **Logs are saved locally only**, on the computer the app runs on, in
  `C:\ProgramData\WindowsRepairAssistant\Logs`. They are never sent anywhere.
- **Only the 5 most recent repair logs are kept.** Older ones are deleted automatically.
- **Only the 5 most recent crash logs are kept** (written only if the app itself hits an unexpected
  error). Older ones are deleted automatically.
- A log records what the repair did (date and time, Windows version, the commands run, their output
  and results) so you or a technician can see what happened. It contains no personal files, documents,
  passwords or browsing data.
- Logs are plain text. Open them with the **View log** / **Open log folder** buttons. You can
  delete them at any time.

This program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it.

**Read the full [Privacy policy](PRIVACY.md).**

## Code signing policy

**This app is not code-signed at the moment**, so Windows may show "Unknown publisher" or a SmartScreen
warning. Only pre-releases are published until it can be signed.

Published builds are built from this repository's source code by GitHub Actions, and each release
lists the exe's SHA-256 checksum. Committer and reviewer: [ZuluBax](https://github.com/ZuluBax).
Full details: [Code signing policy](CODE_SIGNING_POLICY.md).

## Project layout

```
src/WindowsRepairAssistant.Core/   Logic, no UI: process runner, output decoding/parsing,
                                   result analysis, step orchestration, summary, logging
src/WindowsRepairAssistant/        WPF app (MVVM): Welcome → Progress → Results screens
tests/WindowsRepairAssistant.Tests xUnit tests (+ opt-in real-tool integration tests)
scripts/publish.cmd                Test + publish the release exe
tools/IconGenerator/               Regenerates the app icon: dotnet run --project tools/IconGenerator
docs/TESTING.md                    Test matrix: what is verified, and how
PROJECT_STATE.md                   Status, decisions, known limitations, next steps
```

## Prerequisites (development only)

- .NET 10 SDK: `winget install Microsoft.DotNet.SDK.10`
  (end users need nothing; the release exe is self-contained)

## Build, test, run

```cmd
dotnet build
dotnet test
src\WindowsRepairAssistant\bin\Debug\net10.0-windows\WindowsRepairAssistant.exe
```

The app always asks for administrator permission (UAC) because DISM `/Online` and SFC require it.

### Simulation mode (Debug builds only)

Try every screen in seconds without touching the real system. Restart is simulated too, so it
never reboots your development machine:

```cmd
WindowsRepairAssistant.exe --simulate=clean|repaired|sfc-partial|dism-fail|no-internet
WindowsRepairAssistant.exe --simulate-update=9.9.9
```

`--simulate-update` shows the update notice when update checks are on. The first-run prompt and your
answer are real, because they use the same settings file as a normal run.

With `--simulate`, the optional extras are simulated too. Temp cleanup deletes nothing, and the Windows
Update check offers made-up updates (one fails to install, one needs Settings). `no-internet` also makes
the update check fail.

Simulated sessions are labelled `[SIMULATION ...]` in the window title and in the log. The code is
compiled out of Release builds entirely (`#if DEBUG`).

### Real-tool integration tests (read-only, opt-in)

Run from an **elevated** prompt. These run `DISM /CheckHealth` and `sfc /verifyonly`, work out where
the Temp folders are, and search Windows Update (internet needed). None of them modify the system, and
nothing is deleted or installed:

```cmd
set WRA_RUN_REAL_TOOLS=1
dotnet test --filter Category=RealTools
```

## Publishing a release

### Via GitHub (recommended)

```cmd
git tag v1.0.0
git push origin v1.0.0
```

`.github/workflows/release.yml` then runs the tests, builds the exe with that version stamped in,
and creates a GitHub Release with `WindowsRepairAssistant.exe` and its `.sha256` attached. Tags with
a suffix (`v1.1.0-beta.1`) become pre-releases. `.github/workflows/ci.yml` builds and tests every
push and pull request.

### Locally

```cmd
scripts\publish.cmd
```

This runs the tests, then produces:

```
artifacts\publish\win-x64\WindowsRepairAssistant.exe          (~62 MB, self-contained single file)
artifacts\publish\win-x64\WindowsRepairAssistant.exe.sha256   (checksum to publish alongside)
```

Settings live in `src/WindowsRepairAssistant/Properties/PublishProfiles/win-x64.pubxml`
(self-contained, single-file, compressed, no trimming because WPF doesn't support it).

Equivalent manual command:

```cmd
dotnet publish src\WindowsRepairAssistant\WindowsRepairAssistant.csproj -p:PublishProfile=win-x64
```

### Code signing

Releases are currently unsigned: only pre-release tags can be published, and stable tags are refused.
The release workflow has an optional SignPath signing step that stays off unless SignPath is configured
([docs/SIGNPATH_SETUP.md](docs/SIGNPATH_SETUP.md)). Locally built exes are always unsigned.

## Logs (technical details)

User-facing summary: see [Privacy and logs](#privacy-and-logs).

- One plain-text log per session: `repair-YYYYMMDD-HHMMSS.log`; crash reports: `crash-*.log`.
- Retention: `LogStore.LogsToKeepPerKind` (5), applied per kind at startup and after each repair.
- Contents: date/time, Windows version, every command, its output (progress throttled to every 10%),
  exit codes, status, errors and total duration.
- A session that never wrote its end marker is reported on the next launch as "did not finish normally".
- The folder's permissions are restricted to SYSTEM/Administrators (Users can read) because the app
  writes there with administrator rights.
