# BT Battery Indicator

A small, free Windows 10 (1809+) / Windows 11 tray app that shows the battery level of your paired Bluetooth devices.
Everything stays on your PC: no account, no ads, no telemetry, no network access.

- Battery % for Bluetooth mice, keyboards, headphones, earbuds, controllers and phones
- Left, right and case levels for AirPods and Beats earbuds, read from the battery signal they broadcast
- Battery level for Logitech wireless mice and keyboards on a Lightspeed, Unifying or Bolt USB receiver
- Pin devices to show them in a strip on the taskbar with a battery gauge and percentage, or as numbered tray icons if you prefer (yellow under 50%, red under 20%)
- Hover any tray icon to see every connected device
- Low-battery notification at a level you choose (10–50%), and an optional notification when a pinned device disconnects
- A small popup that drops down from the top of the screen when a Bluetooth device connects, showing its battery (can be switched off)
- 7-day battery history chart with a time-left estimate
- Rename or hide devices; light and dark theme; optional start with Windows

Other devices that connect through a 2.4 GHz USB dongle are not supported, and a device that does not report a level
shows "No battery info".

## Screenshots

All screenshots use made-up demo devices.

| Device list | Settings |
| --- | --- |
| ![Device list, dark theme](docs/screenshots/flyout-dark.png) | ![Settings panel, dark theme](docs/screenshots/flyout-settings-dark.png) |

| Light theme | Battery history |
| --- | --- |
| ![Device list, light theme](docs/screenshots/flyout-light.png) | ![7-day battery history chart](docs/screenshots/history-dark.png) |

Pinned devices on the taskbar (dark and light taskbar, and earbuds with one bud charging in its case):

![Taskbar strip, dark](docs/screenshots/strip-dark.png)
![Taskbar strip, light](docs/screenshots/strip-light.png)
![Taskbar strip with separate earbud levels](docs/screenshots/strip-pods3-dark.png)

Popup when a device connects:

![Connect popup for earbuds](docs/screenshots/popup-earbuds.png)
![Connect popup for a keyboard](docs/screenshots/popup-keyboard.png)

## Install (one command)

Unzip `BTBatteryIndicator-win-x64.zip`, click the File Explorer address bar in that folder, type `powershell`, press Enter, then run:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

It installs for your user only (no administrator prompt), adds Start Menu and desktop shortcuts, and starts the app.
After that, start it any time from the Start Menu (type "BT Battery") or the desktop shortcut. Windows 11 hides new tray icons under
the `^` arrow near the clock; drag the icon out to keep it visible.

The app is not code-signed, so Windows SmartScreen may show "Windows protected your PC" the first time.
That warning means "unknown publisher", not "malware found". Choose **More info > Run anyway** only if you trust where
you got the zip.

Uninstall: `powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall` (add `-RemoveData` to delete settings and history).

## Use

- **Left-click** the tray icon: open the panel. **Right-click**: refresh or exit.
- **Pin** button on a row: that device appears on the taskbar and moves with it when the taskbar auto-hides.
- **Click a pinned device that is not connected** (on the taskbar strip): asks Windows to connect it. Works for headphones, earbuds and speakers.
- **Click a row** or the **…** button: battery history, rename, hide.
- **Gear** button: low-battery notification level, accent colour, taskbar strip or tray icons, Logitech reading, a keyboard shortcut to open the panel, hidden devices.

## Build from source (one command)

Needs the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`). Then, in the project folder:

```powershell
.\build.ps1 package
```

This produces `dist\BTBatteryIndicator-win-x64.zip`, and if the app is already installed on that PC it updates and restarts
the installed copy. `.\build.ps1 install` builds and installs in one go. `.\build.ps1` on its own builds and runs the automated tests;
`.\build.ps1 demo` starts the app with made-up devices so you can try everything without real hardware.

## How it works

The app reads the battery value Windows already keeps for each paired Bluetooth device (the same number shown in
Windows Settings). It only reads; it never connects to, pairs, or changes a device. Apple earbuds are the exception to
"Windows already has it": they broadcast their levels instead, so while they are connected the app listens for that
broadcast for a few seconds at a time. Listening is receive-only.

Logitech receiver devices are the one case where the app sends anything: every two minutes it asks the device for its
battery level using Logitech's own message format. It never changes a device setting, and the gear panel has a switch
to turn this off. Settings and history are plain
JSON files in `%LOCALAPPDATA%\BTBatteryIndicator`.

## License

MIT. See `LICENSE`.
