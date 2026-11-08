# Code signing policy

**Windows Repair Assistant is not code-signed at the moment.** Windows may show "Unknown publisher"
or a SmartScreen warning when you run it.

## What is published

Only pre-releases are available for now, each an unsigned `WindowsRepairAssistant.exe` on this
repository's [Releases page](https://github.com/ZuluBax/Windows-Repair-Assistant/releases). Stable
releases will not be published until they can be signed.

Every published exe is built automatically from the source code in this repository by the
[release workflow](.github/workflows/release.yml) on GitHub-hosted runners, not on a personal
computer. Each release lists the exe's SHA-256 checksum so you can check your download matches it.

## Team roles

| Role | Members |
|------|---------|
| Committers and reviewers | [ZuluBax](https://github.com/ZuluBax) |

**Committers** may change the source code. Changes from anyone else (pull requests) are reviewed by a
reviewer before they are merged.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it.

Full details: [Privacy policy](PRIVACY.md).

## System changes

The program makes no changes until the user clicks **Start Repair**. The start screen explains what
the repair does, and the computer is only restarted after the user explicitly confirms a warning
dialog. The program is a single executable with no installer; removing it is a matter of deleting
the file (and, optionally, the log folder above).
