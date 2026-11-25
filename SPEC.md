# BT Battery Indicator — Spec

Free, local Windows 10 (1809+) / 11 tray app showing battery levels of paired Bluetooth devices.
Functional clean-room recreation; original name, icon, and code.

Status key: `TODO` not built · `BUILT` coded, not verified · `AUTO` passes automated test · `PASS` verified on real hardware · `FAIL`

| ID | Requirement | Pass/fail check | Status |
|----|-------------|-----------------|--------|
| R1 | Battery % for paired Bluetooth devices; "No battery info" when not reported; UI says 2.4 GHz dongles unsupported | Paired mouse/keyboard and headphones show the same % as Windows Settings > Bluetooth; a device with no level shows "No battery info"; dongle note visible in flyout | TODO |
| R2 | Pin a device → its own tray icon with %; several pins → several icons | Pin 2 devices: 2 tray icons each showing that device's number; unpin removes it | TODO |
| R3 | Tray icon yellow < 50%, red < 20% | Unit test on colour function at 100/50/49/20/19/0; visual check with simulated levels | TODO |
| R4 | Hovering a tray icon lists every connected device and level | Tooltip lists all connected devices, one per line | TODO |
| R5 | Low-battery notification for all connected devices at user threshold (10–50%), once per crossing | Unit test: level falls through threshold → 1 notification; stays below → 0 more; rises above then falls → 1 more | TODO |
| R6 | Optional notification when a pinned device disconnects | Toggle on + pinned device powered off → 1 notification; toggle off → none | TODO |
| R7 | 7-day battery history chart with time-left estimate | Chart window shows samples ≤ 7 days old; older data pruned; estimate shown when draining, "not enough data" otherwise (unit-tested) | TODO |
| R8 | Rename and hide devices | Renamed device shows new name everywhere and after restart; hidden device leaves list/tooltip and can be unhidden | TODO |
| R9 | Optional start with Windows, quiet in tray, no admin | Toggle writes/removes a per-user Run entry (HKCU); after sign-in app is in tray with no window; no UAC prompt ever | TODO |
| R10 | Follows light/dark theme; flyout layout per brief; Windows 11 Fluent look | Switching Windows theme changes flyout live; flyout has header + last-synced + refresh, device rows (icon, name, %, bar, pin), two toggles, Exit | TODO |
| R11 | Fully local: no account, ads, telemetry, network | Source contains no networking APIs (automated grep test); app makes no connections while running (checked with a read-only connection listing) | TODO |
| R12 | Settings/history persist; survives Bluetooth off, disconnects, sleep/resume | Restart keeps settings + history; Bluetooth off shows a clear empty state, no crash; corrupt settings file recovers to defaults (unit-tested) | TODO |

## User choices (2026-11-25)
- Name: **BT Battery Indicator**.
- Accent colour: user setting — default "tasteful blue", alternative "Windows accent".
- Repo: private, GitHub account `IntelligentElectron01`.
- Real test hardware: mouse/keyboard (BLE) and headphones/earbuds (classic Bluetooth).

## Out of scope
2.4 GHz USB-dongle devices; per-earbud/case levels (vendor-specific); pairing or changing devices.
