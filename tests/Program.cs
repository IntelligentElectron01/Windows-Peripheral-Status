// Automated checks for SPEC.md. No test framework: a failed check prints FAIL and the exit code is 1.
using System.Text.RegularExpressions;
using BTBatteryIndicator.Core;
using Microsoft.Win32;
using Node = BTBatteryIndicator.Core.WindowsDeviceSource.Node;

int passed = 0, failed = 0;
string current = "";

void Section(string name) => current = name;
void Check(bool ok, string what)
{
    if (ok) { passed++; return; }
    failed++;
    Console.WriteLine($"FAIL [{current}] {what}");
}

static string Short(string s) => s.Length > 34 ? s[..34] : s;

if (args.Length > 0 && args[0] == "dump")
{
    // Read-only listing of what Windows reports, with addresses masked.
    foreach (var n in WindowsDeviceSource.ReadNodes().OrderBy(n => n.Address))
        Console.WriteLine($"{n.Address![..2]}…{n.Address[^2..]} present={n.Present,-5} conn={n.Connected?.ToString() ?? "-",-5} " +
                          $"batt={n.Battery?.ToString() ?? "-",-3} cod={n.ClassOfDevice?.ToString("X") ?? "-",-6} class={n.Class,-14} " +
                          $"{Short(Regex.Replace(n.InstanceId, "[0-9A-Fa-f]{12}", "…"))} | {n.Name}");
    Console.WriteLine("--- combined ---");
    foreach (var d in new WindowsDeviceSource().Scan())
        Console.WriteLine($"{d.Name} | {d.Kind} | connected={d.Connected} | {Levels.Text(d.Battery)}{(d.AppleModel is int am ? $" | apple model {am:X4}" : "")}");
    return 0;
}

if (args.Length > 0 && args[0] == "pods")
{
    // Listens (receive-only) for earbud broadcasts for 10 seconds and prints what it decodes.
    Console.WriteLine($"handler id {BlePodsListener.IID_ReceivedHandler}");
    var listener = new BlePodsListener();
    var heard = await listener.ListenAsync(TimeSpan.FromSeconds(10));
    Console.WriteLine($"error: {listener.LastError ?? "none"}; broadcasts decoded: {heard.Count}; callbacks {BlePodsListener.Callbacks}, entries {BlePodsListener.Entries}, apple {BlePodsListener.AppleEntries}, diag {BlePodsListener.Diagnostic}");
    foreach (var g in heard.GroupBy(h => h.Levels).OrderByDescending(g => g.Max(h => h.Rssi)))
        Console.WriteLine($"model {g.Key.Model:X4} rssi {g.Max(h => h.Rssi)} x{g.Count()} | {g.Key.Describe()}");
    return 0;
}

string temp = Path.Combine(Path.GetTempPath(), "BTBI-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var t0 = new DateTime(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);

DeviceReading Dev(string id, int? batt, bool connected = true, string? name = null) =>
    new(id, name ?? "Dev " + id, DeviceKind.Generic, connected, batt);

// ---------------------------------------------------------------------------------------------
Section("R1 device reading");
{
    const string A = "AABBCCDDEEFF", B = "112233445566", C = "0A0B0C0D0E0F";
    Check(WindowsDeviceSource.AddressFromInstanceId($@"BTHENUM\DEV_{A}\7&27B68BF9&0&BLUETOOTHDEVICE_{A}") == A, "classic root address");
    Check(WindowsDeviceSource.AddressFromInstanceId($@"BTHENUM\{{0000111E-0000-1000-8000-00805F9B34FB}}_VID&0002054C_PID&0F8A\7&27B68BF9&0&{A}_C00000000") == A,
        "hands-free node address is the device, not the tail of the service GUID");
    Check(WindowsDeviceSource.AddressFromInstanceId($@"BTHLE\DEV_{B.ToLower()}\7&11014291&0&{B.ToLower()}") == B, "BLE root address, upper-cased");
    Check(WindowsDeviceSource.AddressFromInstanceId($@"HID\{{00001812-0000-1000-8000-00805f9b34fb}}_Dev_VID&02046d_PID&b023_REV&0011_{B}&Col01\9&1&0&0000") == B, "BLE HID child address");
    Check(WindowsDeviceSource.AddressFromInstanceId(@"USB\VID_046D&PID_C52B\5&1234") is null, "USB dongle devices are ignored");
    Check(WindowsDeviceSource.AddressFromInstanceId(@"HID\VID_046D&PID_C52B&MI_00\7&AABBCCDDEEFF&0&0000") is null, "non-Bluetooth HID ignored");

    var nodes = new List<Node>
    {
        new($@"BTHENUM\DEV_{A}\x", A, true, "Headset", "Bluetooth", null, true, 0x240404),
        new($@"BTHENUM\{{0000111E-0000-1000-8000-00805F9B34FB}}_VID\7&{A}", A, true, "Headset Hands-Free", "System", 70, false, null),
        new($@"BTHLE\DEV_{B}\x", B, true, "Travel Mouse", "Bluetooth", 55, true, null),
        new($@"HID\{{00001812-0000-1000-8000-00805f9b34fb}}_{B}&Col01\x", B, true, "HID mouse", "Mouse", null, null, null),
        new($@"BTHENUM\DEV_{C}\x", C, true, "Old Speaker", "Bluetooth", null, false, 0x240414),
        new($@"BTHENUM\{{0000111E-0000-1000-8000-00805F9B34FB}}_VID\7&DDDDDDDDDDDD", "DDDDDDDDDDDD", true, "Orphan service", "System", 40, false, null),
    };
    var devs = WindowsDeviceSource.Combine(nodes);
    Check(devs.Count == 3, "one device per address; a service node without a paired root is not a device");
    var headset = devs.Single(d => d.Id == A);
    Check(headset is { Battery: 70, Connected: true, Kind: DeviceKind.Headphones, Name: "Headset" }, "classic headset: level from hands-free node");
    var mouse = devs.Single(d => d.Id == B);
    Check(mouse is { Battery: 55, Connected: true, Kind: DeviceKind.Mouse }, "BLE mouse: level from root node, kind from child class");
    var speaker = devs.Single(d => d.Id == C);
    Check(speaker is { Battery: null, Connected: false, Kind: DeviceKind.Speaker }, "no level reported -> null");
    Check(Levels.Text(null) == "No battery info" && Levels.Text(76) == "76%", "level text");

    // Dual-mode device with a stale classic value and a live BLE value: the live link wins.
    var dual = WindowsDeviceSource.Combine(new List<Node>
    {
        new($@"BTHENUM\DEV_{A}\x", A, true, "Phone", "Bluetooth", null, false, 0x7A020C),
        new($@"BTHENUM\{{0000111F-0000-1000-8000-00805F9B34FB}}_VID\7&{A}", A, true, "Phone HF", "System", 20, false, null),
        new($@"BTHLE\DEV_{A}\x", A, true, "Phone", "Bluetooth", 18, true, null),
    }).Single();
    Check(dual is { Battery: 18, Connected: true, Kind: DeviceKind.Phone }, "dual-mode device prefers the connected link's level");

    // Same device paired over both transports with different addresses: shown once, exact value preferred.
    var twice = WindowsDeviceSource.Combine(new List<Node>
    {
        new($@"BTHENUM\DEV_{A}\x", A, true, "Phone", "Bluetooth", null, true, 0x7A020C),
        new($@"BTHENUM\{{0000111F-0000-1000-8000-00805F9B34FB}}_VID\7&{A}", A, true, "Phone HF", "System", 40, false, null),
        new($@"BTHLE\DEV_{B}\x", B, true, "Phone", "Bluetooth", 21, true, null),
        new($@"BTHLE\DEV_{C}\x", C, true, "Twin Mouse", "Bluetooth", 50, true, null),
        new($@"BTHLE\DEV_EEEEEEEEEEEE\x", "EEEEEEEEEEEE", true, "Twin Mouse", "Bluetooth", 60, true, null),
    });
    Check(twice.Count(d => d.Name == "Phone") == 1 && twice.Single(d => d.Name == "Phone") is { Id: A, Battery: 21, Kind: DeviceKind.Phone },
        "device paired over both transports is listed once with the exact level");
    Check(twice.Count(d => d.Name == "Twin Mouse") == 2, "two separate devices that share a name stay separate");

    try
    {
        var live = new WindowsDeviceSource().Scan();
        Check(live.All(d => d.Battery is null or (>= 0 and <= 100)), "live read-only scan returns sane levels");
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var reused = new WindowsDeviceSource();
        var firstScan = reused.Scan();   // full scan
        var quick = reused.Scan();       // quick scan using cached names/types
        Check(firstScan.Select(d => (d.Id, d.Name, d.Kind)).SequenceEqual(quick.Select(d => (d.Id, d.Name, d.Kind))),
            "quick scan lists the same devices, names and types as a full scan");
        proc.Refresh(); cpu0 = proc.TotalProcessorTime; sw.Restart();
        for (int i = 0; i < 10; i++) reused.Scan();
        proc.Refresh();
        Console.WriteLine($"info: one quick scan takes {sw.ElapsedMilliseconds / 10.0:0.0} ms wall, {(proc.TotalProcessorTime - cpu0).TotalMilliseconds / 10.0:0.0} ms CPU");
        Console.WriteLine($"info: live scan found {live.Count} paired device(s), {live.Count(d => d.Connected)} connected, {live.Count(d => d.Connected && d.Battery is not null)} reporting a level");
    }
    catch (Exception e) { Check(false, "live scan threw " + e.GetType().Name + ": " + e.Message); }
}

// ---------------------------------------------------------------------------------------------
Section("R1 Apple earbuds");
{
    // type 07, length 19, 01, model bytes, status, bud levels, charge|case, then filler
    byte[] Adv(byte status, byte buds, byte chargeCase, byte m0 = 0x24, byte m1 = 0x20) =>
        [0x07, 0x19, 0x01, m0, m1, status, buds, chargeCase, 0x01, 0x00, 0x00, .. new byte[16]];

    var p = ApplePods.Parse(Adv(0x20, 0x98, 0x55))!;
    Check(p is { Model: 0x2024, Left: 80, Right: 90, Case: 50 }, "levels decoded: left 80, right 90, case 50");
    Check(p is { LeftCharging: true, RightCharging: false, CaseCharging: true }, "charging flags decoded");
    var flipped = ApplePods.Parse(Adv(0x00, 0x98, 0x55))!;
    Check(flipped is { Left: 90, Right: 80, LeftCharging: false, RightCharging: true }, "left/right swap flag honoured");
    var partial = ApplePods.Parse(Adv(0x20, 0x8F, 0x0F))!;
    Check(partial is { Left: null, Right: 80, Case: null } && partial.Lowest == 80, "unreported bud/case -> null; lowest ignores them");
    Check(ApplePods.Parse(Adv(0x20, 0xAA, 0x0A))! is { Left: 100, Right: 100, Case: 100 }, "full = 100");
    Check(ApplePods.Parse([0x10, 0x05, 0x01, 0x18, 0x00, 0x00, 0x00, 0x00]) is null, "other Apple broadcasts (e.g. a phone) are ignored");
    Check(ApplePods.Parse([0x07, 0x19]) is null, "truncated data ignored");
    Check(p.Short() == "L 80% · R 90%" && p.Describe() == "Left 80% (charging) · Right 90% · Case 50% (charging)", "display text");

    Check(WindowsDeviceSource.AppleModelFromInstanceId(@"BTHENUM\{0000111e-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&2024\7&1&0&AABBCCDDEEFF_C00000000") == 0x2024, "Apple product number read from instance ID");
    Check(WindowsDeviceSource.AppleModelFromInstanceId(@"BTHENUM\{0000111e-0000-1000-8000-00805f9b34fb}_VID&0002054C_PID&0F8A\7&1&0&AABBCCDDEEFF") is null, "non-Apple vendor ignored");

    DeviceReading Buds(string id, int model, bool connected = true, int? batt = null) =>
        new(id, "Buds " + id, DeviceKind.Headphones, connected, batt) { AppleModel = model };
    var tr = new PodsTracker();
    Check(tr.Update([(p, -50), (p with { Left = 10, Right = 10 }, -80)], t0), "first broadcast is a change");
    Check(tr.Get(0x2024, t0) is { Left: 80 }, "far-away earbuds of the same model are ignored");
    Check(!tr.Update([(p, -55)], t0.AddSeconds(30)), "same levels again: no change");
    tr.Update([(p with { Left = 30, Right = 40 }, -60), (p with { Left = 70, Right = 70 }, -45)], t0.AddMinutes(1));
    Check(tr.Get(0x2024, t0.AddMinutes(1)) is { Left: 70 }, "several pairs nearby: strongest signal wins");
    tr.Update([(partial, -50)], t0.AddMinutes(2));
    Check(tr.Get(0x2024, t0.AddMinutes(2)) is { Left: 70, Right: 80, Case: 50 }, "missing values keep the last known ones");
    Check(tr.Get(0x2024, t0.AddMinutes(30)) is null, "stale after 20 minutes without a broadcast");

    var list = new List<DeviceReading> { Buds("1", 0x2024), Buds("2", 0x2024, connected: false), Buds("3", 0x200E, batt: 55), Dev("4", null) };
    var rich = tr.Enrich(list, t0.AddMinutes(2));
    Check(rich[0] is { Battery: 70, Pods: not null }, "connected earbuds with no Windows level get the broadcast level (lower bud)");
    Check(rich[1] is { Battery: null, Pods: null }, "disconnected earbuds are left alone");
    Check(rich[2] is { Battery: 55, Pods: null }, "a level from Windows is never overridden");
    Check(rich[3] is { Battery: null, Pods: null }, "non-Apple device untouched");
    Check(PodsTracker.Wanted(list) && !PodsTracker.Wanted([Buds("2", 0x2024, false), Dev("4", null)]), "listening only when a connected Apple device lacks a level");
    var mismatch = tr.Enrich([Buds("9", 0x1234)], t0.AddMinutes(2));
    Check(mismatch[0].Battery == 70, "single candidate with an unknown product number accepts the earbuds that were heard");
    var two = tr.Enrich([Buds("8", 0x1234), Buds("9", 0x5678)], t0.AddMinutes(2));
    Check(two.All(d => d.Battery is null), "two candidates and no exact match: no guessing");
    Check(BlePodsListener.IID_ReceivedHandler == new Guid("90EB4ECA-D465-5EA0-A61C-033C8C5ECEF2"), "callback identifier derived correctly");
}

// ---------------------------------------------------------------------------------------------
Section("R3 colours");
{
    Check(Levels.Band(100) == LevelBand.Normal && Levels.Band(50) == LevelBand.Normal, "50 and above is normal");
    Check(Levels.Band(49) == LevelBand.Yellow && Levels.Band(20) == LevelBand.Yellow, "20-49 is yellow");
    Check(Levels.Band(19) == LevelBand.Red && Levels.Band(0) == LevelBand.Red, "below 20 is red");
    Check(Levels.Band(null) == LevelBand.Unknown, "no level is unknown");
}

// ---------------------------------------------------------------------------------------------
Section("R4 tooltip");
{
    var tip = Tooltip.Build([("Keyboard", 76), ("Mouse", 34), ("Speaker", null)]);
    Check(tip == "Keyboard: 76%\nMouse: 34%\nSpeaker: No battery info", "lists every device on its own line");
    Check(Tooltip.Build([]) == "No Bluetooth devices connected", "empty state");
    var many = Enumerable.Range(1, 9).Select(i => ($"A device with quite a long name number {i}", (int?)(i * 10))).ToList();
    var longTip = Tooltip.Build(many);
    Check(longTip.Length <= 127, "never exceeds the 127-character Windows limit");
    Check(longTip.Split('\n').Length >= 6, "long lists shorten names rather than dropping most devices");
}

// ---------------------------------------------------------------------------------------------
Section("R5 low-battery alert");
{
    var s = new Settings { LowBatteryThreshold = 20 };
    var eng = new AlertEngine();
    int Low(params DeviceReading[] d) => eng.Evaluate(d, s).Count(a => a.Kind == AlertKind.LowBattery);
    Check(Low(Dev("1", 25)) == 0, "above threshold: none");
    Check(Low(Dev("1", 20)) == 1, "reaching threshold: one alert");
    Check(Low(Dev("1", 19)) == 0 && Low(Dev("1", 12)) == 0, "staying below: no repeats");
    Check(Low(Dev("1", 21)) == 0 && Low(Dev("1", 20)) == 0, "wobble at the edge: no repeat");
    Check(Low(Dev("1", 80)) == 0, "recharged: none");
    Check(Low(Dev("1", 18)) == 1, "falls again after recharge: one more");
    Check(Low(Dev("2", 15), Dev("3", 90), Dev("4", null)) == 1, "applies to every connected device, skips ones with no level");
    Check(Low(Dev("5", 5, connected: false)) == 0, "disconnected device's stale level is ignored");

    var off = new Settings { LowBatteryNotify = false };
    Check(new AlertEngine().Evaluate([Dev("1", 5)], off).Count == 0, "switched off: none");
    var hidden = new Settings();
    hidden.For("1").Hidden = true;
    Check(new AlertEngine().Evaluate([Dev("1", 5)], hidden).Count == 0, "hidden device: none");

    var s2 = new Settings { LowBatteryThreshold = 500 };
    s2.Normalize();
    Check(s2.LowBatteryThreshold == 50, "threshold clamped to 50");
    s2.LowBatteryThreshold = 1; s2.Normalize();
    Check(s2.LowBatteryThreshold == 10, "threshold clamped to 10");
}

// ---------------------------------------------------------------------------------------------
Section("R6 disconnect alert");
{
    var s = new Settings { NotifyOnDisconnect = true };
    s.For("1").Pinned = true;
    var eng = new AlertEngine();
    int Disc(params DeviceReading[] d) => eng.Evaluate(d, s).Count(a => a.Kind == AlertKind.Disconnected);
    Check(Disc(Dev("1", 80), Dev("2", 80)) == 0, "connected: none");
    Check(Disc(Dev("1", 80, false), Dev("2", 80, false)) == 1, "pinned device disconnects: one; unpinned: none");
    Check(Disc(Dev("1", 80, false)) == 0, "still disconnected: no repeat");
    Check(Disc(Dev("1", 80)) == 0 && Disc() == 1, "device vanishing (Bluetooth off) counts as a disconnect");
    Check(Disc() == 0, "still gone: no repeat");

    var off = new Settings { NotifyOnDisconnect = false };
    off.For("1").Pinned = true;
    var eng2 = new AlertEngine();
    eng2.Evaluate([Dev("1", 80)], off);
    Check(eng2.Evaluate([Dev("1", 80, false)], off).Count == 0, "toggle off: none");
    var first = new AlertEngine();
    Check(first.Evaluate([Dev("1", 80, false)], s).Count == 0, "already disconnected at startup: none");
}

// ---------------------------------------------------------------------------------------------
Section("R7 history and estimate");
{
    var dir = Path.Combine(temp, "hist");
    var h = new HistoryStore(dir);
    h.Load();
    Check(h.Record("1", 80, t0), "first sample stored");
    Check(!h.Record("1", 80, t0.AddMinutes(5)), "unchanged level within 30 min not stored");
    Check(h.Record("1", 79, t0.AddMinutes(6)), "changed level stored");
    Check(h.Record("1", 79, t0.AddMinutes(40)), "heartbeat sample after 30 min");
    h.Record("old", 50, t0.AddDays(-8));
    h.Record("1", 60, t0.AddDays(-7.5));   // out of order and too old: rejected because time went backwards
    h.Prune(t0.AddHours(1));
    Check(h.Get("old").Count == 0, "samples older than 7 days pruned");
    Check(h.Get("1").All(x => x.T >= t0.AddHours(1) - HistoryStore.Keep), "nothing older than 7 days kept");
    h.SaveIfDirty();
    var h2 = new HistoryStore(dir);
    h2.Load();
    Check(h2.Get("1").Count == 3 && h2.Get("1")[^1].L == 79, "history survives a restart");

    File.WriteAllText(Path.Combine(dir, "history.json"), "{ not json");
    var h3 = new HistoryStore(dir);
    h3.Load();
    Check(h3.Get("1").Count == 0, "corrupt history file -> starts empty, no crash");

    List<Sample> S(params (double hours, int level)[] p) => p.Select(x => new Sample { T = t0.AddHours(x.hours), L = x.level }).ToList();
    var est = Estimator.TimeLeft(S((0, 100), (5, 90), (10, 80)), t0.AddHours(10));
    Check(est is TimeSpan t && Math.Abs(t.TotalHours - 40) < 0.01, "2%/h at 80% -> 40 hours left");
    Check(Estimator.TimeLeft(S((0, 80), (10, 80)), t0.AddHours(10)) is null, "flat level -> no estimate");
    Check(Estimator.TimeLeft(S((0, 80), (0.5, 70)), t0.AddHours(0.5)) is null, "under an hour of data -> no estimate");
    Check(Estimator.TimeLeft(S((0, 80)), t0) is null, "single sample -> no estimate");
    var afterCharge = Estimator.TimeLeft(S((0, 50), (5, 10), (6, 100), (11, 90)), t0.AddHours(11));
    Check(afterCharge is TimeSpan t2 && Math.Abs(t2.TotalHours - 45) < 0.01, "estimate uses only the drain since the last charge");
    Check(Estimator.Describe(null).StartsWith("Not enough data"), "no estimate text");
    Check(Estimator.Describe(TimeSpan.FromHours(40)) == "About 40 hours left", "hours text");
    Check(Estimator.Describe(TimeSpan.FromDays(3.2)) == "About 3 days left", "days text");
}

// ---------------------------------------------------------------------------------------------
Section("R8 rename/hide + R12 persistence");
{
    var dir = Path.Combine(temp, "settings");
    var store = new SettingsStore(dir);
    var s = store.Load();
    Check(s is { LowBatteryThreshold: 20, LowBatteryNotify: true, NotifyOnDisconnect: false, Accent: "Blue" }, "sensible defaults on first run");
    s.For("1").CustomName = "Desk keyboard";
    s.For("2").Hidden = true;
    s.For("1").Pinned = true;
    s.LowBatteryThreshold = 35;
    s.NotifyOnDisconnect = true;
    s.Accent = "System";
    Check(store.Save(s), "settings saved");

    var r = new SettingsStore(dir).Load();
    Check(r.DisplayName("1", "KB-1234") == "Desk keyboard", "rename survives a restart");
    Check(r.DisplayName("9", "Other") == "Other", "unrenamed device keeps its Windows name");
    Check(r.For("2").Hidden && r.For("1").Pinned, "hide and pin survive a restart");
    Check(r is { LowBatteryThreshold: 35, NotifyOnDisconnect: true, Accent: "System" }, "other settings survive a restart");
    r.For("2").Hidden = false;
    Check(!r.For("2").Hidden, "device can be unhidden");

    File.WriteAllText(store.PathOnDisk, "\0\0garbage{{{");
    var bad = new SettingsStore(dir).Load();
    Check(bad.LowBatteryThreshold == 20 && bad.Devices.Count == 0, "corrupt settings file -> defaults, no crash");
    Check(File.Exists(store.PathOnDisk + ".bad"), "corrupt file kept as .bad for recovery");
    File.WriteAllText(store.PathOnDisk, "{\"Accent\":\"Purple\",\"LowBatteryThreshold\":99,\"Devices\":null}");
    var odd = new SettingsStore(dir).Load();
    Check(odd is { Accent: "Blue", LowBatteryThreshold: 50 } && odd.Devices is not null, "out-of-range values repaired");
    Check(new Settings().PinnedDisplay == "Taskbar", "pinned devices show on the taskbar by default");
    var pd = new Settings { PinnedDisplay = "Tray" };
    store.Save(pd);
    Check(new SettingsStore(dir).Load().PinnedDisplay == "Tray", "taskbar/tray choice survives a restart");
    pd.PinnedDisplay = "Elsewhere"; pd.Normalize();
    Check(pd.PinnedDisplay == "Taskbar", "unknown pinned-display value repaired");
}

// ---------------------------------------------------------------------------------------------
Section("R9 start with Windows");
{
    // Uses a throwaway key under HKCU, never the real Run key.
    string key = @"Software\BTBatteryIndicator.Tests\" + Guid.NewGuid().ToString("N");
    var auto = new Autostart(key, "TestValue");
    string exe = @"C:\Some Folder\BTBatteryIndicator.exe";
    try
    {
        Check(!auto.IsEnabled(exe), "off by default");
        Check(auto.Set(true, exe) && auto.IsEnabled(exe), "turning on writes the entry");
        using (var k = Registry.CurrentUser.OpenSubKey(key))
            Check((string?)k?.GetValue("TestValue") == "\"C:\\Some Folder\\BTBatteryIndicator.exe\" --tray", "entry is quoted and starts quietly in the tray");
        Check(!auto.IsEnabled(@"C:\Elsewhere\BTBatteryIndicator.exe"), "entry for a different copy does not count");
        Check(auto.Set(false, exe) && !auto.IsEnabled(exe), "turning off removes the entry");
    }
    finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\BTBatteryIndicator.Tests", throwOnMissingSubKey: false); }
    Check(Autostart.RunKey.StartsWith("Software\\"), "uses the per-user key (no admin needed)");
}

// ---------------------------------------------------------------------------------------------
Section("R11 fully local");
{
    var root = AppContext.BaseDirectory;
    while (root is not null && !File.Exists(Path.Combine(root, "build.ps1"))) root = Path.GetDirectoryName(root);
    if (root is null) Check(false, "could not find the project folder");
    else
    {
        string[] banned = ["System.Net", "HttpClient", "WebClient", "WebRequest", "Socket", "TcpClient", "UdpClient",
                           "http://", "https://", "Windows.Networking", "Process.Start", "wininet", "winhttp", "ws2_32"];
        var files = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\")).ToList();
        Check(files.Count > 8, "source files found");
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            foreach (var b in banned)
                Check(!text.Contains(b, StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(f)} must not contain '{b}'");
        }
        foreach (var proj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).Where(f => !f.Contains(@"\.tools\")))
            Check(!File.ReadAllText(proj).Contains("PackageReference"), $"{Path.GetFileName(proj)} has no third-party packages");
    }
}

try { Directory.Delete(temp, true); } catch { }
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
