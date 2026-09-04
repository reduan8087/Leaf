---
name: leaf-installer
description: >-
  Build the Leaf Inno Setup installer (installer/Leaf.iss -> dist/Leaf-Setup-x64.exe) from the Native AOT publish output,
  and manage the .pdf file association (ProgId Leaf.Document, HKCU Software\Classes, Applications\Leaf.exe,
  RegisteredApplications/Capabilities, SHChangeNotify, ms-settings default-apps deep link). Use when asked to build,
  package or install the installer, set up Inno Setup, edit Leaf.iss, fix Open-with / Default apps behaviour, or clean up
  registrations. Side effects: writes the registry and runs installers, so only run when explicitly asked.
disable-model-invocation: true
argument-hint: "[build|install|uninstall|assoc-check]"
allowed-tools: Bash(pwsh -NoProfile -File scripts/make-installer.ps1 *) Bash(pwsh -File scripts/make-installer.ps1 *)
metadata:
  owner: leaf
---

# Leaf installer

Inno Setup 6 is installed per-user: `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`.

```
pwsh -NoProfile -File scripts/make-installer.ps1            # publish (AOT) + compile Leaf.iss -> dist/Leaf-Setup-x64.exe
pwsh -NoProfile -File scripts/make-installer.ps1 -SkipPublish
```

## Registration model (per-user, no admin)
| Key (HKCU) | Purpose |
|---|---|
| `Software\Classes\Leaf.Document` (+ `DefaultIcon`, `shell\open\command`, `FriendlyTypeName`, `AppUserModelID`) | The ProgId Windows associates with `.pdf` |
| `Software\Classes\.pdf\OpenWithProgids\Leaf.Document` | Lists Leaf in Explorer "Open with" without stealing the default |
| `Software\Classes\Applications\Leaf.exe` (`FriendlyAppName`, `SupportedTypes`, `shell\open\command`) | Open-with by executable |
| `Software\Leaf\Capabilities` (`ApplicationName`, `ApplicationDescription`, `ApplicationIcon`, `FileAssociations\.pdf`) | Default-programs capabilities |
| `Software\RegisteredApplications\Leaf = Software\Leaf\Capabilities` | Makes Leaf appear in Settings > Default apps |

Windows 11 does not let installers set the default silently (UserChoice hash). After registering, open
`ms-settings:defaultapps?registeredAppUser=Leaf` (per-user install) or `registeredAppMachine=Leaf` (elevated install);
the page shows a one-click "Set default" button. The app's first-run InfoBar and the installer's final task both use it.

## Checks
- `assoc-check`: read `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice\ProgId`
  (`Leaf.Document` when Leaf is the default) and confirm the keys above exist.
- After changing `Leaf.iss`, install, then run: `Get-ItemProperty 'HKCU:\Software\Classes\Leaf.Document\shell\open\command'`.
- Uninstall is in Settings > Apps or `%LOCALAPPDATA%\Programs\Leaf\unins000.exe`; it must remove every key above (`uninsdeletekey`).

Unsigned binaries trigger SmartScreen ("More info" > "Run anyway") on first launch of the installer; that is expected for the MVP.
