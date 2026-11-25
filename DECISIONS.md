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
- **Controls styled by hand instead of WPF's built-in Fluent theme** — the accent-colour setting must recolour every control, and the built-in theme always uses the Windows accent. (Supersedes the first line's reason; .NET 10 is still the choice for its support window.)
- **Tray icons use the .NET `NotifyIcon` wrapper rather than raw Win32 calls** — same Windows feature underneath, far less code to get wrong (it already handles Explorer restarts).
- **No test framework; tests are a small console program** — a framework would be a third-party download.
- **A device paired over both classic and Low Energy (e.g. a phone) is shown once** — matched by name; the Low Energy value is preferred because it is exact while hands-free is coarse.
- **Polling every 30 seconds** — simple and reliable across sleep/Bluetooth toggles; the read is a few milliseconds.
- **Low-battery alert fires at or below the threshold and re-arms 2 points above it** — stops repeat alerts when a level wobbles at the edge.
- **`--demo` mode with made-up devices and its own data folder** — lets colours and notifications be tested in a minute instead of waiting for real batteries to drain.
- **Pinned devices show in a strip on the taskbar by default (user request); per-device tray icons remain as a setting** — Windows 11 has no supported way to add content to the taskbar, so the strip is an always-on-top window that follows the taskbar. It reads the taskbar's position only and changes nothing in Windows.
- **Strip follows the taskbar through a Windows "this window moved" notification plus a 2-second safety timer** — polling five times a second kept the CPU awake for nothing.
- **Quick scans read only battery and connected state; names and types are cached and re-read every 15th scan** — cut a scan from 45 ms to 3 ms of CPU.
- **Software rendering, non-concurrent GC, Windows language tables instead of ICU, and handing unused memory back after the UI closes** — together took idle RAM in use from 116 MB to about 25 MB. Cost: the flyout takes a fraction of a second longer to open after a long idle.
- **Not done, needs the user's call:** a build that relies on an installed .NET runtime (154 MB on disk becomes about 1 MB, but friends must install .NET first), and a native rewrite without WPF (about 5 MB RAM and 2 MB on disk, but the interface would be rebuilt from scratch and new build tools downloaded).
- **Apple earbud levels come from listening to their Bluetooth broadcast (user approved 2026-11-25)** — Windows holds no level for them. Receive-only: nothing is sent and no connection is made. Matched to the paired device by Apple's product number, then by signal strength.
- **New capability: passive Bluetooth Low Energy scanning** — only while a connected Apple audio device has no level from Windows; 5 seconds per scan until a level is known, then 5 seconds every 2 minutes.
- **The listener calls Windows' built-in advertisement watcher directly instead of using Microsoft's Windows SDK helper package** — that package is an extra download under Microsoft's Windows SDK licence (not an open licence) and adds about 25 MB to the app. Cost: about 250 lines of low-level code that must match Windows' interface layout; checked live against real broadcasts.
- **Earbud icons are drawn in code from basic shapes (head, ear tip, stem; a case with a lid seam)** — Apple's own symbols are Apple-licensed and cannot be shipped, and Windows' icon font has no earbud glyph.
- **Earbud display rules: pair when within 10 points, single bud when one is worn, separate when they differ; the single number used for alerts and history is the worn bud, otherwise the lower bud** — mirrors how Apple devices present it, and the low-battery alert follows the bud that will die first.
- **Accent colour is a setting (default blue `#2F7FDB`, or Windows accent)** — user request.
