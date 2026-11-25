# Decisions

One line each: decision — why.

## 2026-11-25
- **C# + WPF on .NET 10 (LTS, supported to Nov 2028)** — WPF has a built-in Windows 11 Fluent theme with light/dark, so R10 needs no third-party UI library. .NET 8 (already on this PC) went out of support 2026-11-10.
- **Zero third-party packages** — everything is Microsoft .NET + Windows APIs; fewer things to trust (priority 2).
- **Battery read via Windows' own device property (`DEVPKEY_Bluetooth_Battery`), read-only** — it is the same number Windows Settings shows, covers both BLE and classic (hands-free) devices, and never opens a connection to or writes to a device (rule g).
- **Tray icons via Win32 `Shell_NotifyIcon`, icons drawn at runtime** — lets each pinned device show its own number and colour (R2/R3); no icon library needed.
- **Notifications via tray balloon API (shown as Windows toasts)** — works for an unpackaged app without registering anything system-wide.
- **Storage: JSON files in `%LOCALAPPDATA%\BTBatteryIndicator`** — human-readable, per-user, no database dependency.
- **Start with Windows: `HKCU\...\Run` entry** — per-user, no admin (R9). Written only when the user flips the toggle.
- **History chart drawn by hand in WPF** — avoids a charting package.
- **SDK installed as a zip inside the project (`.tools\dotnet`, git-ignored), not system-wide** — no admin prompt, no change to Windows, delete the folder to remove it.
- **Shipping: self-contained single-folder build + `install.ps1`** — a friend needs nothing pre-installed; per-user install, no admin. Unsigned, so SmartScreen may warn (harmless for a build you made yourself; a code-signing certificate costs money).
- **Capabilities used:** read device properties, read/write own files, one HKCU Run value, show notifications. No network, no admin, no Bluetooth radio control.
- **Repo: private GitHub `IntelligentElectron01/BTBatteryIndicator`** — user request; source only, never settings/history data.
- **Accent colour is a setting (default blue `#2F7FDB`, or Windows accent)** — user request.
