# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).

## What is signed

Only `WindowsRepairAssistant.exe` release builds published on this repository's
[Releases page](https://github.com/ZuluBax/Windows-Repair-Assistant/releases).

Every signed binary is built automatically from the source code in this repository by the
[release workflow](.github/workflows/release.yml) on GitHub-hosted runners. Nothing is built or
signed on a personal computer. The signing request is verified by SignPath against the GitHub
build that produced it, and the exe's product name and version must match the release.

## Team roles

| Role | Members |
|------|---------|
| Committers and reviewers | [ZuluBax](https://github.com/ZuluBax) |
| Approvers | [ZuluBax](https://github.com/ZuluBax) |

- **Committers** may change the source code. Changes from anyone else (pull requests) are reviewed
  by a reviewer before they are merged.
- **Approvers** manually approve every signing request in SignPath before a release is signed.
- All team members use multi-factor authentication for GitHub and SignPath.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it.

Windows Repair Assistant has no telemetry, analytics, accounts or update checks. It runs
Microsoft's built-in Windows tools (DISM and SFC) when the user clicks **Start Repair**. During that
repair, DISM may download replacement Windows system files from Microsoft's Windows Update service.
This is part of Windows itself and is governed by the
[Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement).

Repair logs are stored only on the computer the program runs on
(`C:\ProgramData\WindowsRepairAssistant\Logs`). Only the 5 most recent repair logs and 5 most recent
crash logs are kept, and they are never sent anywhere.

## System changes

The program makes no changes until the user clicks **Start Repair**. The start screen explains what
the repair does, and the computer is only restarted after the user explicitly confirms a warning
dialog. The program is a single executable with no installer; removing it is a matter of deleting
the file (and, optionally, the log folder above).
