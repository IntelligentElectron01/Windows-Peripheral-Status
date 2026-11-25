# BT Battery Indicator — Spec

Free, local Windows 10 (1809+) / 11 tray app showing battery levels of paired Bluetooth devices.
Functional clean-room recreation; original name, icon, and code.

Status key: `TODO` not built · `BUILT` coded, not verified · `AUTO` passes automated test (`.\build.ps1`) · `PASS` also verified by hand on real hardware · `FAIL`

| ID | Requirement | Pass/fail check | Status |
|----|-------------|-----------------|--------|
| R1 | Battery % for paired Bluetooth devices; "No battery info" when not reported; UI says 2.4 GHz dongles unsupported | Paired devices show the same % as Windows Settings > Bluetooth; a device with no level shows "No battery info"; dongle note visible in flyout | AUTO. Live read-only scan returned 5 paired devices with correct types; one was connected and reported a level. Not yet compared by eye with Windows Settings (manual check 1). |
| R2 | Pin a device → its own tray icon with %; several pins → several icons | Pin 2 devices: 2 tray icons each showing that device's number; unpin removes it | BUILT. Icon artwork checked from rendered images; live tray not yet seen (manual check 2). |
| R3 | Tray icon yellow < 50%, red < 20% | Unit test at 100/50/49/20/19/0; visual check with simulated levels | AUTO; visual = manual check 2. |
| R4 | Hovering a tray icon lists every connected device and level | Tooltip lists all connected devices, one per line | AUTO (text and 127-character Windows limit); hover = manual check 2. |
| R5 | Low-battery notification for all connected devices at user threshold (10–50%), once per crossing | Falls to threshold → 1 notification; stays below → 0 more; recharges then falls → 1 more | AUTO; real toast = manual check 3. |
| R6 | Optional notification when a pinned device disconnects | Toggle on + pinned device disconnects → 1 notification; toggle off → none | AUTO; real toast = manual check 3. |
| R7 | 7-day battery history chart with time-left estimate | Samples ≤ 7 days kept, older pruned; estimate when draining, "not enough data" otherwise | AUTO; chart checked from rendered images. |
| R8 | Rename and hide devices | New name shown everywhere and after restart; hidden device leaves list/tooltip and can be unhidden | AUTO; manual check 4. |
| R9 | Optional start with Windows, quiet in tray, no admin | Toggle writes/removes a per-user Run entry; after sign-in app is in tray with no window; no UAC prompt | AUTO against a throwaway registry key. The real toggle and a real sign-in are untested (manual check 5). |
| R10 | Follows light/dark theme; flyout layout per brief; Windows 11 look | Switching Windows theme changes flyout live; flyout has header + last-synced + refresh, rows (icon, name, %, bar, pin), two toggles, Exit | BUILT. Both themes checked from rendered images; live switching and on-screen position unverified (manual check 6). Windows 10 look untested. |
| R11 | Fully local: no account, ads, telemetry, network | No networking APIs in source (automated); no connections while running | AUTO. Packaged app observed with 0 TCP connections and 0 UDP endpoints. |
| R12 | Settings/history persist; survives Bluetooth off, disconnects, sleep/resume | Restart keeps settings + history; corrupt files recover to defaults; Bluetooth off shows a clear state, no crash | AUTO for persistence and corrupt files. Bluetooth-off and sleep/resume are coded but unverified (manual check 7). |

## Manual checks (the only things that need a person)
1. Open the flyout; compare each connected device's % with Windows Settings > Bluetooth & devices.
2. Pin two devices: two numbered tray icons appear; hover one and see all connected devices. Run `.\build.ps1 demo`, right-click its tray icon > "Demo: drain all by 10%" a few times: icons go yellow below 50 and red below 20.
3. Still in demo: keep draining until a device reaches 20%: exactly one notification per device. Switch on "Notify on disconnect", pin Demo Keyboard, choose "Demo: connect/disconnect keyboard": one notification.
4. Rename a device (… > Rename), hide another (… > Hide), exit and restart: both stick; un-hide from the gear panel.
5. Switch on "Start with Windows", sign out and in: icon appears, no window, no admin prompt.
6. Switch Windows between light and dark (Settings > Personalisation > Colours) with the flyout open.
7. Turn Bluetooth off and on; put the PC to sleep and wake it: app keeps running and recovers within about 30 seconds.

## User choices (2026-11-25)
- Name: **BT Battery Indicator**.
- Accent colour: a setting; default blue, alternative "Windows accent".
- Private GitHub repository.
- Real test hardware: mouse/keyboard (Low Energy) and headphones/earbuds (classic Bluetooth).

## Known limits
- 2.4 GHz USB-dongle devices; per-earbud and charging-case levels (vendor-specific, not exposed by Windows).
- Classic (hands-free) devices report in coarse steps, often 10 or 20 points; that is all the device sends.
- A device that only reports while in use shows "Not connected" otherwise.
- The flyout may sit slightly off on setups with several monitors at different scaling.
- Unsigned build: SmartScreen may warn on first run.
