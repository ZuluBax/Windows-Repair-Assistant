# Privacy policy

_Windows Repair Assistant. Effective 8 November 2026._

## Summary

**Windows Repair Assistant does not collect, store on any server, share or sell any information
about you or your computer.** It has no telemetry, no analytics, no advertising and no accounts.
Update checks are optional and only happen if you agree to them. Everything it records stays on your
own computer.

This program will not transfer any information to other networked systems unless specifically
requested by the user or the person installing or operating it.

## What the program does on your computer

When you click **Start Repair**, the program runs Microsoft's built-in Windows tools, one after
another: DISM (`CheckHealth`, `ScanHealth`, `RestoreHealth`) and System File Checker (`sfc /scannow`).
No repair tool runs before you click that button.

## Network connections

- **Update checks happen only if you agree.** The first time the program runs, it asks whether it
  may check for new versions. If you choose **Yes**, it asks GitHub for the latest version number
  each time it opens. GitHub sees your IP address, as any website you visit does, and nothing else
  about you or your computer is sent. The program only tells you a new version exists; it never
  downloads or installs anything itself. You can change your choice at any time with the
  **Update checks** link at the bottom of the window.
- **The program makes no other network connections of its own.**
- **Microsoft's DISM tool may connect to Windows Update** during the repair step
  (`DISM /RestoreHealth`) to download replacement Windows system files. This is part of Windows,
  happens only during a repair you started, and is covered by the
  [Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement).

## Information stored on your computer (logs)

The program saves plain-text logs so you, or someone helping you, can see what a repair did.

| | |
|---|---|
| **Where** | `C:\ProgramData\WindowsRepairAssistant\Logs`, on the computer the program runs on |
| **Repair logs** | Date and time, Windows version and build, the commands run, the output of those commands, exit codes, results and how long each step took |
| **Crash logs** | Written only if the program itself hits an unexpected error: the time, Windows version and technical error details, which may include file paths on your computer |
| **How long** | Only the **5 most recent repair logs** and **5 most recent crash logs** are kept. Older ones are deleted automatically. |
| **Who can access them** | Administrators on that computer can read and delete them; standard users can read them |
| **Sent anywhere?** | **Never.** The program has no way to send them. You choose whether to share a log, for example with a technician. |

Logs do not include your documents, personal files, passwords, browsing history or account details.

Your update check choice is saved in `C:\ProgramData\WindowsRepairAssistant\update-checks.txt`.

**To delete the logs**, open the folder (the app's **Open log folder** button) and delete the files,
or delete the whole `C:\ProgramData\WindowsRepairAssistant` folder. Deleting that folder also removes
your update check choice, so the program will ask again next time. Deleting the program itself is
just a matter of deleting `WindowsRepairAssistant.exe`; there is no installer.

## Third parties

The program contains no third-party code that collects data. When you visit or download from
GitHub, or if you turn on update checks, [GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement)
applies to that connection.

## Children

The program collects no personal information from anyone, including children.

## Changes to this policy

Any change to this policy is made in this file, and its full history is public in this repository.
A change that reduced your privacy would be announced in the release notes of the version that
introduces it.

## Contact

Questions about privacy: open an issue at
https://github.com/ZuluBax/Windows-Repair-Assistant/issues.
