# Windows Repair Assistant

A free, lightweight Windows 10/11 utility that runs Microsoft's built-in repair tools in the right
order and explains the result in plain English.

**Double-click → approve administrator permission → Start Repair → wait → view results → restart now or later.**

It runs, strictly one after another:

| # | Command | Purpose |
|---|---------|---------|
| 1 | `DISM /Online /Cleanup-Image /CheckHealth` | Has Windows already recorded component store damage? |
| 2 | `DISM /Online /Cleanup-Image /ScanHealth` | Thorough scan of the component store |
| 3 | `DISM /Online /Cleanup-Image /RestoreHealth` | Repair the component store (may download from Windows Update) |
| 4 | `sfc /scannow` | Scan and repair protected system files |

No ads, accounts, telemetry, network calls of its own, database or background services.

## Project layout

```
src/WindowsRepairAssistant.Core/   Logic, no UI: process runner, output decoding/parsing,
                                   result analysis, step orchestration, summary, logging
src/WindowsRepairAssistant/        WPF app (MVVM): Welcome → Progress → Results screens
tests/WindowsRepairAssistant.Tests xUnit tests (+ opt-in real-tool integration tests)
scripts/publish.cmd                Test + publish the release exe
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
```

Simulated sessions are labelled `[SIMULATION ...]` in the window title and in the log. The code is
compiled out of Release builds entirely (`#if DEBUG`).

### Real-tool integration tests (read-only, opt-in)

Run from an **elevated** prompt. These run `DISM /CheckHealth` and `sfc /verifyonly`, which never
modify the system:

```cmd
set WRA_RUN_REAL_TOOLS=1
dotnet test --filter Category=RealTools
```

## Publishing a release

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

### Before public distribution: code signing

The exe is **unsigned**. On other PCs, the UAC prompt will show "Unknown publisher" and SmartScreen
will warn on first download, which undermines trust in a tool that asks for admin rights. Sign it
before release (e.g. Azure Trusted Signing, roughly USD 10/month) with `signtool sign /fd SHA256 /tr <timestamp-url> /td SHA256 ...`.

## Logs

One plain-text log per session in `C:\ProgramData\WindowsRepairAssistant\Logs\`
(`repair-YYYYMMDD-HHMMSS.log`; newest 50 kept). The app has **View log** and **Open log folder**
buttons. Logs never leave the computer. Each contains the date/time, Windows version, every
command, its output (progress throttled to every 10%), exit codes, status, errors and total
duration. A session that never wrote its end marker is reported on the next launch as
"did not finish normally".
