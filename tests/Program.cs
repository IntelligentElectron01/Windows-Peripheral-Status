// Automated checks for docs/SPEC.md. No test framework: a failed check prints FAIL and the exit code is 1.
using System.Text.RegularExpressions;
using WirelessDeviceMonitor.Core;
using Microsoft.Win32;
using Node = WirelessDeviceMonitor.Core.WindowsDeviceSource.Node;

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
    await listener.ListenAsync(TimeSpan.FromSeconds(2)); // warm-up so one-off start-up cost is not counted
    var me = System.Diagnostics.Process.GetCurrentProcess(); var c0 = me.TotalProcessorTime;
    var heard = await listener.ListenAsync(TimeSpan.FromSeconds(5));
    me.Refresh(); Console.WriteLine($"cpu for one 5 s listen: {(me.TotalProcessorTime - c0).TotalMilliseconds:0} ms");
    Console.WriteLine($"error: {listener.LastError ?? "none"}; broadcasts decoded: {heard.Count}; callbacks {BlePodsListener.Callbacks}, entries {BlePodsListener.Entries}, apple {BlePodsListener.AppleEntries}, diag {BlePodsListener.Diagnostic}");
    foreach (var g in heard.GroupBy(h => h.Levels).OrderByDescending(g => g.Max(h => h.Rssi)))
        Console.WriteLine($"model {g.Key.Model:X4} rssi {g.Max(h => h.Rssi)} x{g.Count()} | {g.Key.Describe()} | view {g.Key.View} -> {g.Key.Short()}");
    return 0;
}

if (args.Length > 0 && args[0] == "links")
{
    // Lists, per paired device, how many Windows audio connections it has (what click-to-connect would use). Sends nothing.
    foreach (var d in new WindowsDeviceSource().Scan())
    {
        Console.WriteLine($"links:{d.Kind} connected={d.Connected} audio links={BluetoothConnector.AudioLinks(d.Id).Count}");
        foreach (var line in BluetoothConnector.Describe(d.Id)) Console.WriteLine("   " + (line.Length > 150 ? line[^150..] : line));
    }
    return 0;
}

if (args.Length > 1 && args[0] == "switch")
{
    // Developer check, only run with the user's say-so: "switch off" disconnects the first connected headphones,
    // "switch on" connects the first not-connected ones, then watches what Windows reports for 15 seconds.
    bool off = args[1] == "off";
    var target = new WindowsDeviceSource().Scan().FirstOrDefault(d => d.Kind == DeviceKind.Headphones && d.Connected == off
                                                                      && BluetoothConnector.AudioLinks(d.Id).Count > 0);
    if (target is null) { Console.WriteLine("no matching headphones"); return 1; }
    string detail;
    var outcome = off ? BluetoothConnector.Disconnect(target.Id, out detail) : BluetoothConnector.Connect(target.Id, out detail);
    Console.WriteLine($"{(off ? "disconnect" : "connect")}: {outcome} | {detail}");
    var clock = System.Diagnostics.Stopwatch.StartNew();
    bool? last = null;
    while (clock.Elapsed.TotalSeconds < 15)
    {
        await Task.Delay(200);
        bool? now = new WindowsDeviceSource().Scan().FirstOrDefault(d => d.Id == target.Id)?.Connected;
        if (now != last) Console.WriteLine($"{clock.Elapsed.TotalSeconds:0.0}s connected={now}");
        last = now;
    }
    return 0;
}

if (args.Length > 0 && args[0] == "outputs")
{
    // Lists which paired devices have a working sound output and which one Windows is playing through. Changes nothing.
    string? playingNow = BluetoothConnector.CurrentOutput();
    Console.WriteLine($"current output known: {playingNow is not null}");
    var described = BluetoothConnector.DescribeOutput();
    Console.WriteLine($"current output: kind {described?.Kind}, name length {described?.Name.Length}, built-in or Bluetooth name shown: {(described?.Kind is BluetoothConnector.OutputKind.BuiltIn or BluetoothConnector.OutputKind.Speakers ? described.Name : "(not printed)")}");
    var mic = BluetoothConnector.DescribeInput();
    Console.WriteLine($"current input: kind {mic?.Kind}, known: {BluetoothConnector.CurrentInput() is not null}");
    if (args.Length > 1 && args[1] == "flip")
    {
        // Developer check, only run with the user's say-so: moves the sound to another working output for a few
        // seconds and back again, so the "Output set" pill can be watched.
        var other = BluetoothConnector.WorkingOutputs().FirstOrDefault(o => o != playingNow);
        if (playingNow is null || other is null) { Console.WriteLine("flip: no second working output"); return 1; }
        await Task.Delay(3000);
        Console.WriteLine($"flip: away {BluetoothConnector.SetOutput(other)} -> kind {BluetoothConnector.DescribeOutput()?.Kind}");
        await Task.Delay(5000);
        Console.WriteLine($"flip: back {BluetoothConnector.SetOutput(playingNow)} -> kind {BluetoothConnector.DescribeOutput()?.Kind}, restored {BluetoothConnector.CurrentOutput() == playingNow}");
        return 0;
    }
    if (args.Length > 1 && args[1] == "watch")
    {
        // Waits 40 seconds and reports every change of sound output it is told about. Changes nothing itself.
        Console.WriteLine($"watching: {BluetoothConnector.WatchOutput(input => Console.WriteLine($"changed -> {(input ? "input" : "output")} kind {(input ? BluetoothConnector.DescribeInput() : BluetoothConnector.DescribeOutput())?.Kind}"))}");
        await Task.Delay(40000);
        return 0;
    }
    foreach (var d in new WindowsDeviceSource().Scan())
    {
        string? mine = BluetoothConnector.OutputOf(d.Id);
        Console.WriteLine($"outputs: {d.Kind} connected={d.Connected} has output={mine is not null} is current={mine is not null && mine == playingNow}");
    }
    return 0;
}

if (args.Length > 0 && args[0] == "alloc")
{
    // How much memory one round of each periodic job asks for (it becomes garbage straight afterwards). Reads only.
    static double Mb(Action job, int times)
    {
        job();   // first run loads code; not counted
        long before = GC.GetTotalAllocatedBytes(true);
        for (int i = 0; i < times; i++) job();
        return (GC.GetTotalAllocatedBytes(true) - before) / 1048576.0 / times;
    }
    var windows = new WindowsDeviceSource();
    Console.WriteLine($"alloc: Windows scan {Mb(() => windows.Scan(), 10):0.000} MB each");
    var receiver = new LogitechSource();
    Console.WriteLine($"alloc: Logitech scan {Mb(() => receiver.Scan(), 3):0.000} MB each");
    var pods = new BlePodsListener();
    long b0 = GC.GetTotalAllocatedBytes(true);
    await pods.ListenAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine($"alloc: one 5 s earbud listen {(GC.GetTotalAllocatedBytes(true) - b0) / 1048576.0:0.000} MB (first run, includes loading)");
    b0 = GC.GetTotalAllocatedBytes(true);
    await pods.ListenAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine($"alloc: second 5 s earbud listen {(GC.GetTotalAllocatedBytes(true) - b0) / 1048576.0:0.000} MB");
    return 0;
}

if (args.Length > 0 && args[0] == "logi")
{
    // Asks Logitech wireless devices for their name and battery level and prints what came back.
    var logi = new LogitechSource();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var found = logi.Scan();
    Console.WriteLine($"poll took {sw.ElapsedMilliseconds} ms; devices: {found.Count}");
    foreach (var line in logi.Trace) Console.WriteLine("trace: " + line);
    var me2 = System.Diagnostics.Process.GetCurrentProcess(); var c2 = me2.TotalProcessorTime;
    for (int i = 0; i < 5; i++) new LogitechSource().Scan();
    me2.Refresh(); Console.WriteLine($"cpu per cold poll: {(me2.TotalProcessorTime - c2).TotalMilliseconds / 5:0} ms");
    foreach (var d in found) Console.WriteLine($"device: {d.Name} | {d.Kind} | connected={d.Connected} | {Levels.Text(d.Battery)} | charging={d.Charging} | id {d.Id}");
    return 0;
}

string temp = Path.Combine(Path.GetTempPath(), "WDM-tests-" + Guid.NewGuid().ToString("N"));
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
    Check(partial is { Left: null, Right: 80, Case: null } && partial.Primary == 80, "unreported bud/case -> null; lowest ignores them");
    Check(ApplePods.Parse(Adv(0x20, 0xAA, 0x0A))! is { Left: 100, Right: 100, Case: 100 }, "full = 100");
    Check(ApplePods.Parse([0x10, 0x05, 0x01, 0x18, 0x00, 0x00, 0x00, 0x00]) is null, "other Apple broadcasts (e.g. a phone) are ignored");
    Check(ApplePods.Parse([0x07, 0x19]) is null, "truncated data ignored");
    Check(p.Describe() == "Left 80% (charging) · Right 90% · Case 50% (charging)", "detail text");

    // How earbuds are presented (modelled on how Apple devices show them).
    PodLevels P(int? l, int? r, bool le = false, bool re = false, int? c = null, bool lc = false) =>
        new(0x2024, l, r, c, LeftCharging: lc, LeftInEar: le, RightInEar: re);
    Check(P(80, 80, true, true) is { View: PodView.Pair, Primary: 80 } && P(80, 80, true, true).Short() == "80%", "both worn, same level -> one pair icon");
    Check(P(70, 80, true, true) is { View: PodView.Pair, Primary: 70 }, "within 10 points still counts as a pair, showing the lower level");
    Check(P(90, 40, true, true) is { View: PodView.Separate, Primary: 40 } && P(90, 40, true, true).Short() == "L 90% · R 40%", "levels far apart -> left and right shown separately");
    Check(P(60, 60, false, true) is { View: PodView.Right, Primary: 60 } && P(60, 60, false, true).Short() == "Right 60%", "only the right bud worn -> right bud alone");
    Check(P(30, 90, true, false) is { View: PodView.Left, Primary: 30 }, "only the left bud worn -> left bud alone");
    Check(P(null, 80, false, true) is { View: PodView.Right, Primary: 80 }, "only one bud reporting -> that bud");
    Check(P(50, null, false, true) is { View: PodView.Left, Primary: 50 }, "worn bud has no level -> fall back to the one that reports");
    Check(P(80, 80) is { View: PodView.Pair }, "neither worn (both in case) -> pair");
    Check(P(null, null) is { View: PodView.None, Primary: null }, "nothing reported -> nothing shown");
    Check(P(100, 50, false, true, 70, true).ShowCase && !P(80, 80, true, true, 70).ShowCase, "case shown only while a bud is charging in it");
    var worn = ApplePods.Parse(Adv(0x2B, 0x98, 0x05))!;
    Check(worn is { LeftInEar: true, RightInEar: true }, "in-ear flags decoded");
    var oneWorn = ApplePods.Parse(Adv(0x09, 0x8F, 0x0F))!;
    Check(oneWorn is { LeftInEar: true, RightInEar: false, Left: 80, Right: null }, "in-ear flags follow the left/right swap");

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
Section("R1 Logitech receiver devices");
{
    var req = Hidpp.BuildRequest(1, 0x06, 0, 0x10, 0x01);
    Check(req.Length == 20 && req[0] == 0x11 && req[1] == 1 && req[2] == 0x06 && req[3] == Hidpp.SoftwareId && req[4] == 0x10 && req[5] == 0x01, "request layout");
    Check(Hidpp.BuildRequest(1, 0x06, 1)[3] == (0x10 | Hidpp.SoftwareId), "function number goes in the upper half of byte 3");
    byte[] Rep(params byte[] b) => [.. b, .. new byte[20 - b.Length]];
    Check(Hidpp.Match(req, Rep(0x11, 1, 0x06, Hidpp.SoftwareId, 0x0F, 0xA0)) == Hidpp.Reply.Ok, "matching reply accepted");
    Check(Hidpp.Match(req, Rep(0x11, 1, 0x06, 0x01, 0x0F)) == Hidpp.Reply.NotOurs, "reply tagged for other software ignored");
    Check(Hidpp.Match(req, Rep(0x11, 2, 0x06, Hidpp.SoftwareId)) == Hidpp.Reply.NotOurs, "reply from a different device ignored");
    Check(Hidpp.Match(req, Rep(0x11, 1, 0xFF, 0x06, Hidpp.SoftwareId, 0x05)) == Hidpp.Reply.Error, "error reply recognised");
    Check(Hidpp.Match(req, [0x11, 1]) == Hidpp.Reply.NotOurs, "truncated report ignored");

    Check(Hidpp.PercentFromMillivolts(4200) == 100 && Hidpp.PercentFromMillivolts(4186) == 100, "full voltage = 100%");
    Check(Hidpp.PercentFromMillivolts(3811) == 50 && Hidpp.PercentFromMillivolts(3717) == 20, "curve points");
    Check(Hidpp.PercentFromMillivolts(3835) is > 50 and < 60, "between points is interpolated");
    Check(Hidpp.PercentFromMillivolts(3400) == 0, "below the curve = 0%");
    Check(Enumerable.Range(3400, 900).Select(Hidpp.PercentFromMillivolts).Zip(Enumerable.Range(3401, 900).Select(Hidpp.PercentFromMillivolts)).All(p => p.Second >= p.First), "curve never goes down as voltage rises");
    Check(Hidpp.DecodeVoltage([0x0F, 0xA0, 0x00]) is { Percent: > 80 and < 90, Charging: false }, "4000 mV decoded, not charging");
    Check(Hidpp.DecodeVoltage([0x10, 0x5A, 0x80]) is { Percent: 100, Charging: true }, "charging flag decoded");
    Check(Hidpp.DecodeVoltage([0x00, 0x00, 0x00]) is null && Hidpp.DecodeVoltage([0x0F]) is null, "nonsense voltage rejected");
    Check(Hidpp.DecodeUnified([73, 0x04, 0x00]) is { Percent: 73, Charging: false }, "percentage feature decoded");
    Check(Hidpp.DecodeUnified([0, 0x02, 0x01]) is { Percent: 20, Charging: true }, "coarse level used when no percentage is given");
    Check(Hidpp.DecodeUnified([0, 0x00, 0x00]) is null, "no information -> null");
    Check(Hidpp.DecodeStatus([55, 50, 0]) is { Percent: 55, Charging: false } && Hidpp.DecodeStatus([55, 50, 1]) is { Charging: true }, "older battery feature decoded");
    Check(Hidpp.KindFromType(0) == DeviceKind.Keyboard && Hidpp.KindFromType(3) == DeviceKind.Mouse, "device type mapped");

    Check(new LogitechSource(() => false).Scan().Count == 0, "switched off: sends nothing and lists nothing");
    var ok = new DemoDeviceSource();
    Check(new CompositeSource(ok, new Throwing()).Scan().Count == ok.Scan().Count, "one reader failing does not hide the other's devices");
    bool threw = false;
    try { new CompositeSource(new Throwing()).Scan(); } catch (InvalidOperationException) { threw = true; }
    Check(threw, "every reader failing is reported as an error");
    Check(new Settings().LogitechEnabled, "Logitech reading on by default");

    // A receiver mouse that goes quiet (asleep) must not raise "disconnected" alerts even when pinned.
    var ns = new Settings { NotifyOnDisconnect = true };
    ns.For("M").Pinned = true;
    var quiet = new AlertEngine();
    var awake = new DeviceReading("M", "Mouse", DeviceKind.Mouse, true, 90) { SleepsWhenIdle = true };
    quiet.Evaluate([awake], ns);
    Check(quiet.Evaluate([awake with { Connected = false }], ns).Count == 0, "sleeping receiver device: no disconnect alert");
    quiet.Evaluate([awake], ns);
    Check(quiet.Evaluate([], ns).Count == 0, "receiver device missing from a scan: no disconnect alert");
    Check(quiet.Evaluate([awake with { Battery = 15 }], ns).Count(a => a.Kind == AlertKind.LowBattery) == 1, "low-battery alert still works for receiver devices");
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
Section("R15 shortcut to open");
{
    Check(Hotkey.Parse("Ctrl+Alt+B") is { Ctrl: true, Alt: true, Shift: false, Win: false, Key: "B" }, "typical shortcut parsed");
    Check(Hotkey.Parse(" alt + shift + F9 ")?.ToString() == "Alt+Shift+F9", "spacing and case tidied into one saved form");
    Check(Hotkey.Parse("Win+D1")?.Display() == "Win+1" && Hotkey.Parse("Ctrl+OemComma")?.Display() == "Ctrl+,", "friendly names for number and punctuation keys");
    Check(Hotkey.Parse("B") is null && Hotkey.Parse("Shift+B") is null, "must include Ctrl, Alt or Win so typing is never swallowed");
    Check(Hotkey.Parse("Ctrl+Alt") is null && Hotkey.Parse("Ctrl+LeftShift") is null, "modifiers alone are not a shortcut");
    Check(Hotkey.Parse("Ctrl+A+B") is null && Hotkey.Parse("") is null && Hotkey.Parse(null) is null, "nonsense and empty rejected");
    Check(new Settings().Hotkey == "", "no shortcut by default");
    var hs = new Settings { Hotkey = "control+alt+b" }; hs.Normalize();
    Check(hs.Hotkey == "Ctrl+Alt+b" || hs.Hotkey == "Ctrl+Alt+B", "saved shortcut normalised");
    hs.Hotkey = "just nonsense"; hs.Normalize();
    Check(hs.Hotkey == "", "invalid saved shortcut cleared");
}

// ---------------------------------------------------------------------------------------------
Section("R9 start with Windows");
{
    // Uses a throwaway key under HKCU, never the real Run key.
    string key = @"Software\WirelessDeviceMonitor.Tests\" + Guid.NewGuid().ToString("N");
    var auto = new Autostart(key, "TestValue");
    string exe = @"C:\Some Folder\WirelessDeviceMonitor.exe";
    try
    {
        Check(!auto.IsEnabled(exe), "off by default");
        Check(auto.Set(true, exe) && auto.IsEnabled(exe), "turning on writes the entry");
        using (var k = Registry.CurrentUser.OpenSubKey(key))
            Check((string?)k?.GetValue("TestValue") == "\"C:\\Some Folder\\WirelessDeviceMonitor.exe\" --tray", "entry is quoted and starts quietly in the tray");
        Check(!auto.IsEnabled(@"C:\Elsewhere\WirelessDeviceMonitor.exe"), "entry for a different copy does not count");
        Check(auto.Set(false, exe) && !auto.IsEnabled(exe), "turning off removes the entry");
    }
    finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\WirelessDeviceMonitor.Tests", throwOnMissingSubKey: false); }
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

Section("Disconnect on request");
{
    var ds = new Settings { NotifyOnDisconnect = true };
    ds.For("AABBCCDDEEFF").Pinned = true;
    var eng = new AlertEngine();
    DeviceReading Hp(bool on) => new("AABBCCDDEEFF", "Headset", DeviceKind.Headphones, on, 60);
    eng.Evaluate([Hp(true)], ds);
    eng.ExpectDisconnect("AABBCCDDEEFF");
    Check(eng.Evaluate([Hp(false)], ds).Count == 0, "a disconnect the user asked for raises no notification");
    eng.Evaluate([Hp(true)], ds);
    Check(eng.Evaluate([Hp(false)], ds).Any(a => a.Kind == AlertKind.Disconnected), "a later unexpected disconnect still notifies");
    Check(BluetoothConnector.Disconnect("not-an-address", out _) == BluetoothConnector.Outcome.NotAnAudioDevice, "disconnect refuses anything that is not a Bluetooth address");
    Check(BluetoothConnector.Disconnect("000000000000", out _) == BluetoothConnector.Outcome.NotAnAudioDevice, "disconnect does nothing for an address with no audio link");
    Check(!BluetoothConnector.DropLink("not-an-address"), "closing a link refuses anything that is not a Bluetooth address");
    Check(BluetoothConnector.OutputOf("not-an-address") is null && BluetoothConnector.OutputOf("000000000000") is null, "an unknown device has no sound output to switch to");
    Check(BluetoothConnector.Classify(false, 1, true) == BluetoothConnector.OutputKind.BuiltIn, "the PC's own speakers are recognised as built in");
Check(BluetoothConnector.Classify(false, 1, false) == BluetoothConnector.OutputKind.Speakers, "plugged-in speakers are not taken for the PC's own");
Check(BluetoothConnector.Classify(true, 1, true) == BluetoothConnector.OutputKind.Bluetooth, "a Bluetooth output is never taken for the PC's own speakers");
Check(BluetoothConnector.Classify(false, 3, true) == BluetoothConnector.OutputKind.Headphones && BluetoothConnector.Classify(false, 9, false) == BluetoothConnector.OutputKind.Display, "wired headphones and a screen's speakers are told apart");
Check(BluetoothConnector.Classify(false, 4, true, input: true) == BluetoothConnector.OutputKind.BuiltIn && BluetoothConnector.Classify(false, 4, false, input: true) == BluetoothConnector.OutputKind.Speakers, "the PC's own microphone is told apart from a plugged-in one");
Check(new Settings().QuietInFullScreen, "popups are held back in full-screen apps by default");
    Check(System.Text.Json.JsonSerializer.Deserialize<Settings>("{\"QuietInFullScreen\":false}")!.QuietInFullScreen == false, "the full-screen setting is read back from the settings file");
}

Section("Connect popup");
{
    DeviceReading PopDev(string id, bool on, bool sleeper = false) => new(id, id, DeviceKind.Headphones, on, 50) { SleepsWhenIdle = sleeper };
    var ps = new Settings();
    var cw = new ConnectWatcher();
    Check(cw.Evaluate([PopDev("A", true), PopDev("B", false)], ps).Count == 0, "nothing announced for devices already connected at start-up");
    Check(cw.Evaluate([PopDev("A", true), PopDev("B", false)], ps).Count == 0, "no change, no popup");
    var popGot = cw.Evaluate([PopDev("A", true), PopDev("B", true)], ps);
    Check(popGot.Count == 1 && popGot[0].Id == "B", "a device that connects is announced once");
    Check(cw.Evaluate([PopDev("A", true), PopDev("B", true)], ps).Count == 0, "still connected: not announced again");
    cw.Evaluate([PopDev("A", true), PopDev("B", false)], ps);
    Check(cw.Evaluate([PopDev("A", true), PopDev("B", true)], ps).Count == 1, "reconnecting announces again");
    Check(cw.Evaluate([PopDev("A", true), PopDev("B", true), PopDev("C", true)], ps).Count == 1, "a newly paired, connected device is announced");
    cw.Evaluate([PopDev("A", true), PopDev("LOGI1", false, true), PopDev("D", false)], ps);
    Check(cw.Evaluate([PopDev("A", true), PopDev("LOGI1", true, true), PopDev("D", false)], ps).Count == 0, "USB-receiver devices are left out");
    ps.For("D").Hidden = true;
    Check(cw.Evaluate([PopDev("A", true), PopDev("D", true)], ps).Count == 0, "hidden devices are left out");
    ps.ConnectPopup = false;
    cw.Evaluate([PopDev("A", false)], ps);
    Check(cw.Evaluate([PopDev("A", true)], ps).Count == 0, "switched off in settings: nothing announced");
    ps.ConnectPopup = true;
    Check(cw.Evaluate([PopDev("A", true)], ps).Count == 0, "switching it back on does not announce what connected meanwhile");
    Check(new Settings().ConnectPopup, "connect popup on by default");
}

try { Directory.Delete(temp, true); } catch { }
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class Throwing : IDeviceSource { public IReadOnlyList<DeviceReading> Scan() => throw new InvalidOperationException("reader failed"); }

