using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace WirelessDeviceMonitor.Core;

/// <summary>The message format Logitech wireless devices use ("HID++ 2.0"), as pure functions so it can be tested.</summary>
public static class Hidpp
{
    public const byte LongReport = 0x11;
    public const int LongLength = 20;
    /// <summary>Tag put on our requests so replies meant for other software (e.g. Logitech G HUB) are not mistaken for ours.</summary>
    public const byte SoftwareId = 0x0A;

    public const ushort FeatureDeviceName = 0x0005;
    public const ushort FeatureBatteryStatus = 0x1000;
    public const ushort FeatureBatteryVoltage = 0x1001;
    public const ushort FeatureUnifiedBattery = 0x1004;

    public enum Reply { NotOurs, Ok, Error }

    public static byte[] BuildRequest(byte device, byte featureIndex, byte function, params byte[] args)
    {
        var r = new byte[LongLength];
        r[0] = LongReport;
        r[1] = device;
        r[2] = featureIndex;
        r[3] = (byte)((function << 4) | SoftwareId);
        Array.Copy(args, 0, r, 4, Math.Min(args.Length, LongLength - 4));
        return r;
    }

    /// <summary>Decides whether an incoming report answers the given request.</summary>
    public static Reply Match(ReadOnlySpan<byte> request, ReadOnlySpan<byte> report)
    {
        if (report.Length < 7 || report[0] != LongReport || report[1] != request[1]) return Reply.NotOurs;
        if (report[2] == request[2] && report[3] == request[3]) return Reply.Ok;
        if (report[2] == 0xFF && report[3] == request[2] && report[4] == request[3]) return Reply.Error;
        return Reply.NotOurs;
    }

    // Lithium cell voltage (millivolts) to remaining charge. Gaming mice report voltage, not a percentage.
    static readonly (int Mv, int Pct)[] Curve =
    [
        (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40),
        (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
    ];

    public static int PercentFromMillivolts(int mv)
    {
        if (mv >= Curve[0].Mv) return 100;
        for (int i = 1; i < Curve.Length; i++)
        {
            if (mv < Curve[i].Mv) continue;
            var (hiMv, hiPct) = Curve[i - 1];
            var (loMv, loPct) = Curve[i];
            return (int)Math.Round(loPct + (double)(mv - loMv) / (hiMv - loMv) * (hiPct - loPct));
        }
        return 0;
    }

    /// <summary>Feature 0x1001: two bytes of millivolts, then a flags byte whose top bit means "on charge".</summary>
    public static (int Percent, bool Charging)? DecodeVoltage(ReadOnlySpan<byte> p)
    {
        if (p.Length < 3) return null;
        int mv = (p[0] << 8) | p[1];
        if (mv is < 2500 or > 5000) return null;
        return (PercentFromMillivolts(mv), (p[2] & 0x80) != 0);
    }

    /// <summary>Feature 0x1004: percentage, coarse level flags, charging state.</summary>
    public static (int Percent, bool Charging)? DecodeUnified(ReadOnlySpan<byte> p)
    {
        if (p.Length < 3) return null;
        int pct = p[0];
        if (pct is 0 or > 100)
            pct = (p[1] & 8) != 0 ? 90 : (p[1] & 4) != 0 ? 50 : (p[1] & 2) != 0 ? 20 : (p[1] & 1) != 0 ? 5 : -1;
        if (pct < 0) return null;
        return (pct, p[2] is 1 or 2 or 3);
    }

    /// <summary>Feature 0x1000: percentage, next step, status (1-4 are the charging states).</summary>
    public static (int Percent, bool Charging)? DecodeStatus(ReadOnlySpan<byte> p)
    {
        if (p.Length < 3 || p[0] > 100) return null;
        return (p[0], p[2] is >= 1 and <= 4);
    }

    public static DeviceKind KindFromType(int type) => type switch
    {
        0 => DeviceKind.Keyboard,
        3 or 4 or 5 => DeviceKind.Mouse,
        8 => DeviceKind.Headphones,
        _ => DeviceKind.Mouse,
    };
}

/// <summary>
/// Reads battery levels of Logitech wireless devices that connect through a USB receiver (Lightspeed, Unifying,
/// Bolt). Windows has no battery value for these, so this asks the device directly. Unlike the Bluetooth
/// reader this one does send messages: small "what is your battery level / name" questions. It never changes
/// a setting on the device.
/// </summary>
public sealed partial class LogitechSource(Func<bool>? enabled = null) : IDeviceSource
{
    /// <summary>Battery level moves slowly; asking every two minutes keeps radio and CPU use negligible.</summary>
    public static readonly TimeSpan PollEvery = TimeSpan.FromMinutes(2);
    const int TimeoutMs = 350;
    /// <summary>While something is not answering (asleep, or not found yet), look again on the next scan instead.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(25);
    /// <summary>An idle mouse goes to sleep and stops answering. Keep showing its last level this long before calling it disconnected.</summary>
    public static readonly TimeSpan AsleepGrace = TimeSpan.FromMinutes(45);

    sealed class Known
    {
        public required string Id, Name;
        public DeviceKind Kind;
        public ushort BatteryFeature;
        public byte BatteryIndex;
        public int? LastLevel;
        public DateTime LastReply;
    }

    readonly Dictionary<string, Known> _known = new(StringComparer.OrdinalIgnoreCase);
    IReadOnlyList<DeviceReading> _cache = [];
    DateTime _next = DateTime.MinValue;

    /// <summary>What happened on the last poll, for the diagnostic command.</summary>
    public List<string> Trace { get; } = [];

    public IReadOnlyList<DeviceReading> Scan()
    {
        if (enabled is not null && !enabled()) { _next = DateTime.MinValue; return _cache = []; }
        var now = DateTime.UtcNow;
        if (now < _next) return _cache;
        lock (_known)
        {
            _cache = Poll(now, out bool allAnswered);
            _next = now + (allAnswered ? PollEvery : RetryEvery);
            return _cache;
        }
    }

    List<DeviceReading> Poll(DateTime now, out bool allAnswered)
    {
        allAnswered = true;
        Trace.Clear();
        var result = new List<DeviceReading>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, pid) in LongCollections())
        {
            using var ch = Channel.Open(path);
            if (ch is null) { Trace.Add($"{pid:X4}: could not open"); continue; }
            foreach (byte index in CandidateIndexes(pid))
            {
                string key = $"{path}|{index}";
                seen.Add(key);
                _known.TryGetValue(key, out var k);
                if (k is null)
                {
                    k = Probe(ch, index, $"LOGI{pid:X4}{index:X2}");
                    if (k is null) { if (index != 0xFF) allAnswered = false; continue; }
                    _known[key] = k;
                }
                var level = ReadBattery(ch, index, k);
                Trace.Add($"{pid:X4} #{index}: {k.Name} feature {k.BatteryFeature:X4} -> {(level is null ? "no reply" : $"{level.Value.Percent}%{(level.Value.Charging ? " charging" : "")}")}");
                if (level is not null) { k.LastLevel = level.Value.Percent; k.LastReply = now; }
                else allAnswered = false;
                bool asleep = level is null && k.LastLevel is not null && now - k.LastReply < AsleepGrace;
                result.Add(new DeviceReading(k.Id, k.Name, k.Kind, level is not null || asleep, level?.Percent ?? k.LastLevel)
                    { Charging = level?.Charging ?? false, SleepsWhenIdle = true });
            }
        }
        // Receiver unplugged: forget its devices so they disappear from the list.
        foreach (var gone in _known.Keys.Where(k => !seen.Contains(k)).ToList()) _known.Remove(gone);
        return result;
    }

    // Lightspeed receivers carry one device (number 1). Unifying and Bolt receivers carry up to six.
    // 0xFF addresses a device plugged in directly by cable.
    static IEnumerable<byte> CandidateIndexes(int pid) => pid switch
    {
        0xC52B or 0xC532 or 0xC548 => [1, 2, 3, 4, 5, 6],
        0xC539 or 0xC53A or 0xC53D or 0xC53F or 0xC541 or 0xC545 or 0xC547 or 0xC54D => [1],
        _ => [0xFF, 1],
    };

    static Known? Probe(Channel ch, byte index, string id)
    {
        // Ask the device where its battery feature lives. No reply means nothing is on this slot (or it is asleep).
        ushort feature = 0; byte featureIndex = 0;
        foreach (ushort f in new[] { Hidpp.FeatureUnifiedBattery, Hidpp.FeatureBatteryVoltage, Hidpp.FeatureBatteryStatus })
        {
            var r = ch.Request(index, 0x00, 0, (byte)(f >> 8), (byte)f);
            if (r.Reply == Hidpp.Reply.NotOurs) return null;           // timed out
            if (r.Reply == Hidpp.Reply.Ok && r.Data[0] != 0) { feature = f; featureIndex = r.Data[0]; break; }
        }
        if (feature == 0) return null;

        string name = "Logitech device";
        var kind = DeviceKind.Mouse;
        var nf = ch.Request(index, 0x00, 0, 0x00, 0x05);
        if (nf.Reply == Hidpp.Reply.Ok && nf.Data[0] != 0)
        {
            byte ni = nf.Data[0];
            var count = ch.Request(index, ni, 0);
            if (count.Reply == Hidpp.Reply.Ok && count.Data[0] is > 0 and <= 64)
            {
                var sb = new StringBuilder();
                while (sb.Length < count.Data[0])
                {
                    var part = ch.Request(index, ni, 1, (byte)sb.Length);
                    if (part.Reply != Hidpp.Reply.Ok) break;
                    int take = Math.Min(16, count.Data[0] - sb.Length);
                    sb.Append(Encoding.ASCII.GetString(part.Data, 0, take));
                }
                var text = sb.ToString().Trim('\0', ' ');
                foreach (var suffix in new[] { " Wireless Gaming Mouse", " Wireless Mouse", " Wireless Gaming Keyboard", " Wireless Keyboard" })
                    if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) text = text[..^suffix.Length];
                if (text.Length > 0 && text.All(c => c is >= ' ' and <= '~')) name = text;
            }
            var type = ch.Request(index, ni, 2);
            if (type.Reply == Hidpp.Reply.Ok) kind = Hidpp.KindFromType(type.Data[0]);
        }
        return new Known { Id = id, Name = name, Kind = kind, BatteryFeature = feature, BatteryIndex = featureIndex };
    }

    static (int Percent, bool Charging)? ReadBattery(Channel ch, byte index, Known k)
    {
        byte function = k.BatteryFeature == Hidpp.FeatureUnifiedBattery ? (byte)1 : (byte)0;
        var r = ch.Request(index, k.BatteryIndex, function);
        if (r.Reply != Hidpp.Reply.Ok) return null;
        return k.BatteryFeature switch
        {
            Hidpp.FeatureUnifiedBattery => Hidpp.DecodeUnified(r.Data),
            Hidpp.FeatureBatteryVoltage => Hidpp.DecodeVoltage(r.Data),
            _ => Hidpp.DecodeStatus(r.Data),
        };
    }

    // ---- talking to the receiver ------------------------------------------------------------------

    sealed class Channel : IDisposable
    {
        readonly FileStream _stream;
        Task<int>? _pending;
        readonly byte[] _buffer = new byte[64];

        Channel(FileStream s) => _stream = s;

        public static Channel? Open(string path)
        {
            var handle = CreateFileW(path, 0xC0000000 /* read + write */, 3 /* share */, IntPtr.Zero, 3 /* open existing */,
                0x40000000 /* overlapped */, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            try { return new Channel(new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true)); }
            catch { handle.Dispose(); return null; }
        }

        public (Hidpp.Reply Reply, byte[] Data) Request(byte device, byte featureIndex, byte function, params byte[] args)
        {
            var request = Hidpp.BuildRequest(device, featureIndex, function, args);
            try
            {
                _stream.Write(request, 0, request.Length);
                _stream.Flush();
                var deadline = Environment.TickCount64 + TimeoutMs;
                while (true)
                {
                    _pending ??= _stream.ReadAsync(_buffer, 0, _buffer.Length);
                    int wait = (int)(deadline - Environment.TickCount64);
                    if (wait <= 0 || !_pending.Wait(wait)) return (Hidpp.Reply.NotOurs, []);
                    int n = _pending.Result;
                    _pending = null;
                    var reply = Hidpp.Match(request, _buffer.AsSpan(0, n));
                    if (reply == Hidpp.Reply.NotOurs) continue;   // someone else's message; keep waiting
                    return (reply, _buffer.AsSpan(4, Math.Max(0, n - 4)).ToArray());
                }
            }
            catch (Exception e) when (e is IOException or AggregateException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _pending = null;
                return (Hidpp.Reply.NotOurs, []);
            }
        }

        public void Dispose()
        {
            var pending = _pending;
            _stream.Dispose();  // cancels a read that is still waiting
            pending?.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    [GeneratedRegex(@"vid_046d&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex LogitechRx();

    /// <summary>Finds Logitech HID collections that speak the long HID++ report (usage page 0xFF00, usage 2).</summary>
    static List<(string Path, int Pid)> LongCollections()
    {
        var found = new List<(string, int)>();
        var hid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
        if (CM_Get_Device_Interface_List_SizeW(out uint len, hid, null, 0) != 0 || len <= 1) return found;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(hid, null, buf, len, 0) != 0) return found;
        foreach (var path in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = LogitechRx().Match(path);
            if (!m.Success) continue;
            // Opened with no access rights just to read its description; this never fails for keyboards/mice.
            using var h = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid || !HidD_GetPreparsedData(h, out IntPtr data)) continue;
            try
            {
                if (HidP_GetCaps(data, out HidCaps caps) == 0x00110000 /* success */
                    && caps.UsagePage == 0xFF00 && caps.Usage == 2 && caps.OutputReportByteLength == Hidpp.LongLength)
                    found.Add((path, Convert.ToInt32(m.Groups[1].Value, 16)));
            }
            finally { HidD_FreePreparsedData(data); }
        }
        return found;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    struct HidCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint len, in Guid cls, string? deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(in Guid cls, string? deviceId, [Out] char[] buffer, uint len, uint flags);
}

/// <summary>Combines several readers; a failure in one never hides the others' devices.</summary>
public sealed class CompositeSource(params IDeviceSource[] sources) : IDeviceSource
{
    public IReadOnlyList<DeviceReading> Scan()
    {
        var all = new List<DeviceReading>();
        Exception? first = null;
        int failed = 0;
        foreach (var s in sources)
        {
            try { all.AddRange(s.Scan()); }
            catch (Exception e) { first ??= e; failed++; }
        }
        if (failed == sources.Length && first is not null) throw first;
        return all;
    }
}
