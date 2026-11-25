# BT Battery Indicator

A small, free Windows 10 (1809+) / Windows 11 tray app that shows the battery level of your paired Bluetooth devices.
Everything stays on your PC: no account, no ads, no telemetry, no network access.

- Battery % for Bluetooth mice, keyboards, headphones, earbuds, controllers and phones
- Left, right and case levels for AirPods and Beats earbuds, read from the battery signal they broadcast
- Pin devices to show them in a strip on the taskbar with a battery gauge and percentage, or as numbered tray icons if you prefer (yellow under 50%, red under 20%)
- Hover any tray icon to see every connected device
- Low-battery notification at a level you choose (10–50%), and an optional notification when a pinned device disconnects
- 7-day battery history chart with a time-left estimate
- Rename or hide devices; light and dark theme; optional start with Windows

Bluetooth only. Devices that connect through a 2.4 GHz USB dongle are not supported, and a device that does not
report a level to Windows shows "No battery info".

## Install (one command)

Unzip `BTBatteryIndicator-win-x64.zip`, click the File Explorer address bar in that folder, type `powershell`, press Enter, then run:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

It installs for your user only (no administrator prompt) and starts the app. Windows 11 hides new tray icons under
the `^` arrow near the clock; drag the icon out to keep it visible.

The app is not code-signed, so Windows SmartScreen may show "Windows protected your PC" the first time.
That warning means "unknown publisher", not "malware found". Choose **More info > Run anyway** only if you trust where
you got the zip.

Uninstall: `powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall` (add `-RemoveData` to delete settings and history).

## Use

- **Left-click** the tray icon: open the panel. **Right-click**: refresh or exit.
- **Pin** button on a row: that device appears on the taskbar. The strip hides while the taskbar is hidden or an app is full-screen.
- **Click a row** or the **…** button: battery history, rename, hide.
- **Gear** button: low-battery notification level, accent colour, taskbar strip or tray icons, hidden devices.

## Build from source (one command)

Needs the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`). Then, in the project folder:

```powershell
.\build.ps1 package
```

This produces `dist\BTBatteryIndicator-win-x64.zip`. `.\build.ps1` on its own builds and runs the automated tests;
`.\build.ps1 demo` starts the app with made-up devices so you can try everything without real hardware.

## How it works

The app reads the battery value Windows already keeps for each paired Bluetooth device (the same number shown in
Windows Settings). It only reads; it never connects to, pairs, or changes a device. Apple earbuds are the exception to
"Windows already has it": they broadcast their levels instead, so while they are connected the app listens for that
broadcast for a few seconds at a time. Listening is receive-only. Settings and history are plain
JSON files in `%LOCALAPPDATA%\BTBatteryIndicator`.

## License

MIT. See `LICENSE`.
