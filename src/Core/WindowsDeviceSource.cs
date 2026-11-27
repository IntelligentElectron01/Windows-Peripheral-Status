using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WirelessDeviceMonitor.Core;

/// <summary>
/// Reads paired Bluetooth devices from the Windows device tree (Configuration Manager API).
/// Read-only: it only queries properties Windows already stores, and never opens a connection
/// to a device, so it cannot pair, unpair, wake, or alter anything.
/// </summary>
public sealed partial class WindowsDeviceSource : IDeviceSource
{
    static readonly string[] Enumerators = ["BTHENUM", "BTHLE", "BTHLEDEVICE", "HID"];

    static readonly DevPropKey KeyBattery = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);    // byte, 0-100
    static readonly DevPropKey KeyConnected = new("83DA6326-97A6-4088-9453-A1923F573B29", 15); // boolean
    static readonly DevPropKey KeyFriendlyName = new("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
    static readonly DevPropKey KeyName = new("B725F130-47EF-101A-A5F1-02608C9EEBAC", 10);
    static readonly DevPropKey KeyClass = new("A45C254E-DF1C-4EFD-8020-67D146A850E0", 9);
    static readonly DevPropKey KeyClassOfDevice = new("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A", 10); // uint32

    public sealed record Node(string InstanceId, string? Address, bool Present, string? Name, string? Class,
        int? Battery, bool? Connected, uint? ClassOfDevice);

    /// <summary>Things about a node that almost never change (name, type), cached between scans.</summary>
    sealed record Fixed(string? Address, string? Name, string? Class, uint? ClassOfDevice);

    /// <summary>Every Nth scan re-reads everything, so a device renamed in Windows is picked up.</summary>
    const int FullScanEvery = 15;

    readonly Dictionary<string, Fixed> _fixed = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Node> _hidNodes = new();
    int _scans;

    public IReadOnlyList<DeviceReading> Scan()
    {
        lock (_fixed)
            return Combine(ReadNodes(_fixed, _hidNodes, full: _scans++ % FullScanEvery == 0));
    }

    /// <summary>Raw device-tree nodes that belong to Bluetooth devices, read from scratch. Used by --dump and tests.</summary>
    public static List<Node> ReadNodes() => ReadNodes(new(StringComparer.OrdinalIgnoreCase), new(), full: true);

    /// <summary>
    /// A quick scan reads only what changes (battery level, connected flag) and reuses cached names and types.
    /// HID nodes are only used to tell mice from keyboards, so they are listed on full scans only.
    /// </summary>
    static List<Node> ReadNodes(Dictionary<string, Fixed> cache, List<Node> hidNodes, bool full)
    {
        if (full) { cache.Clear(); hidNodes.Clear(); }
        var nodes = new List<Node>();
        foreach (var enumerator in Enumerators)
        {
            bool hid = enumerator == "HID";
            if (hid && !full) continue;
            foreach (var id in DeviceIds(enumerator))
            {
                cache.TryGetValue(id, out var info);
                if (info is null)
                {
                    var addr = AddressFromInstanceId(id);
                    if (addr is null) { cache[id] = NotBluetooth; continue; }
                }
                else if (info.Address is null) continue;

                bool present = CM_Locate_DevNodeW(out uint inst, id, 0) == 0;
                if (!present && CM_Locate_DevNodeW(out inst, id, 1 /* phantom */) != 0) continue;
                if (info is null)
                    cache[id] = info = new Fixed(AddressFromInstanceId(id),
                        GetString(inst, KeyFriendlyName) ?? GetString(inst, KeyName),
                        GetString(inst, KeyClass), GetUInt(inst, KeyClassOfDevice));

                var node = new Node(id, info.Address, present, info.Name, info.Class,
                    hid ? null : GetByte(inst, KeyBattery),
                    IsRoot(id) ? GetBool(inst, KeyConnected) : null,
                    info.ClassOfDevice);
                (hid ? hidNodes : nodes).Add(node);
            }
        }
        nodes.AddRange(hidNodes);
        return nodes;
    }

    static readonly Fixed NotBluetooth = new(null, null, null, null);

    /// <summary>Groups nodes by Bluetooth address into one reading per physical device.</summary>
    public static IReadOnlyList<DeviceReading> Combine(IEnumerable<Node> nodes)
    {
        // The root node (BTHENUM\DEV_x or BTHLE\DEV_x) is what proves the device is paired.
        var groups = nodes.Where(n => n.Address is not null).GroupBy(n => n.Address!)
            .Select(g => (Id: g.Key, Nodes: g.ToList()))
            .Where(g => g.Nodes.Any(n => IsRoot(n.InstanceId)))
            .Select(g => (g.Id, g.Nodes, Name: RootName(g.Nodes),
                          Classic: g.Nodes.Any(n => IsClassicRoot(n.InstanceId)),
                          Le: g.Nodes.Any(n => IsLeRoot(n.InstanceId))))
            .ToList();

        // Some devices (phones, some speakers) pair twice: once over classic Bluetooth and once over
        // Low Energy with a different address. Same name + one of each = one physical device.
        foreach (var same in groups.GroupBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList())
        {
            var classicOnly = same.Where(g => g.Classic && !g.Le).ToList();
            var leOnly = same.Where(g => g.Le && !g.Classic).ToList();
            if (classicOnly.Count != 1 || leOnly.Count != 1) continue;
            groups.Remove(classicOnly[0]);
            groups.Remove(leOnly[0]);
            groups.Add((classicOnly[0].Id, classicOnly[0].Nodes.Concat(leOnly[0].Nodes).ToList(), classicOnly[0].Name, true, true));
        }

        var result = new List<DeviceReading>();
        foreach (var g in groups)
        {
            bool connected = g.Nodes.Any(n => IsRoot(n.InstanceId) && n.Present && n.Connected == true);
            var batteryNode = g.Nodes.Where(n => n.Battery is >= 0 and <= 100)
                               .OrderByDescending(n => n.Connected == true) // the live link's value beats a stale one
                               .ThenByDescending(n => IsLeRoot(n.InstanceId)) // Low Energy reports exact %, hands-free is coarse
                               .ThenByDescending(n => n.Present).FirstOrDefault();
            result.Add(new DeviceReading(g.Id, g.Name, GuessKind(g.Nodes, g.Name), connected, batteryNode?.Battery)
            {
                AppleModel = g.Nodes.Select(n => AppleModelFromInstanceId(n.InstanceId)).FirstOrDefault(m => m is not null),
            });
        }
        return result.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    [GeneratedRegex(@"VID&(0001004C|000205AC)_PID&([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex AppleRx();

    /// <summary>Apple's vendor number appears in the instance ID of its devices, followed by the product number.</summary>
    public static int? AppleModelFromInstanceId(string id)
    {
        var m = AppleRx().Match(id);
        return m.Success ? Convert.ToInt32(m.Groups[2].Value, 16) : null;
    }

    static string RootName(List<Node> nodes) =>
        nodes.Where(n => IsRoot(n.InstanceId)).OrderByDescending(n => n.Present)
             .Select(n => n.Name).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "Bluetooth device";

    static bool IsClassicRoot(string id) => id.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase);
    static bool IsLeRoot(string id) => id.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase);
    static bool IsRoot(string id) => IsClassicRoot(id) || IsLeRoot(id);

    [GeneratedRegex(@"\{[0-9A-Fa-f\-]{36}\}")] private static partial Regex GuidRx();
    [GeneratedRegex(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{12}(?![0-9A-Fa-f])")] private static partial Regex AddrRx();

    /// <summary>Finds the 12-hex-digit Bluetooth address in an instance ID, ignoring GUIDs.</summary>
    public static string? AddressFromInstanceId(string id)
    {
        bool bt = id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)
                  || id.StartsWith(@"HID\{0000", StringComparison.OrdinalIgnoreCase);
        if (!bt) return null;
        var m = AddrRx().Matches(GuidRx().Replace(id, ""));
        return m.Count == 0 ? null : m[^1].Value.ToUpperInvariant();
    }

    public static DeviceKind GuessKind(IReadOnlyList<Node> nodes, string name)
    {
        // Classic devices carry a Class-of-Device number: bits 8-12 major class, bits 2-7 minor class.
        var cod = nodes.Select(n => n.ClassOfDevice).FirstOrDefault(c => c is > 0);
        if (cod is uint c)
        {
            uint major = (c >> 8) & 0x1F, minor = (c >> 2) & 0x3F;
            switch (major)
            {
                case 2: return DeviceKind.Phone;
                case 4: return minor is 5 or 10 ? DeviceKind.Speaker : DeviceKind.Headphones;
                case 5:
                    if ((minor & 0x30) == 0x10) return DeviceKind.Keyboard;
                    if ((minor & 0x30) == 0x20) return DeviceKind.Mouse;
                    if ((minor & 0x30) == 0x30) return DeviceKind.Keyboard;
                    if ((minor & 0x0F) is 1 or 2) return DeviceKind.Gamepad;
                    break;
            }
        }
        var classes = nodes.Where(n => n.Class is not null).Select(n => n.Class!.ToUpperInvariant()).ToHashSet();
        string n2 = name.ToUpperInvariant();
        if (n2.Contains("MOUSE") || n2.Contains("TRACKPAD") || n2.Contains("TRACKBALL")) return DeviceKind.Mouse;
        if (n2.Contains("KEYBOARD") || n2.Contains("KEYS")) return DeviceKind.Keyboard;
        if (n2.Contains("CONTROLLER") || n2.Contains("GAMEPAD")) return DeviceKind.Gamepad;
        if (n2.Contains("BUDS") || n2.Contains("PODS") || n2.Contains("HEADPHONE") || n2.Contains("HEADSET")) return DeviceKind.Headphones;
        if (n2.Contains("SPEAKER")) return DeviceKind.Speaker;
        bool mouse = classes.Contains("MOUSE"), kb = classes.Contains("KEYBOARD");
        if (mouse && !kb) return DeviceKind.Mouse;
        if (kb && !mouse) return DeviceKind.Keyboard;
        if (mouse) return DeviceKind.Mouse; // mice often expose a small keyboard collection too
        if (classes.Contains("MEDIA") || classes.Contains("AUDIOENDPOINT")) return DeviceKind.Headphones;
        if (classes.Contains("XNACOMPOSITE") || classes.Contains("XBOXCOMPOSITE")) return DeviceKind.Gamepad;
        return DeviceKind.Generic;
    }

    // ---- native plumbing -------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct DevPropKey(string fmtid, uint pid)
    {
        public Guid Fmtid = new(fmtid);
        public uint Pid = pid;
    }

    const uint FilterEnumerator = 0x1;
    const uint TypeByte = 0x03, TypeUInt32 = 0x07, TypeBoolean = 0x11, TypeString = 0x12;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_List_SizeW(out uint len, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint len, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_PropertyW(uint devInst, in DevPropKey key, out uint type,
        [Out] byte[]? buffer, ref uint size, uint flags);

    static IEnumerable<string> DeviceIds(string enumerator)
    {
        if (CM_Get_Device_ID_List_SizeW(out uint len, enumerator, FilterEnumerator) != 0 || len <= 1)
            return [];
        var buf = new char[len];
        if (CM_Get_Device_ID_ListW(enumerator, buf, len, FilterEnumerator) != 0) return [];
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    [ThreadStatic] static byte[]? _scratch;

    /// <summary>Reads a property in a single call using a reusable buffer (most values are a few bytes).</summary>
    static byte[]? GetRaw(uint inst, DevPropKey key, out uint type)
    {
        var buf = _scratch ??= new byte[512];
        uint size = (uint)buf.Length;
        int r = CM_Get_DevNode_PropertyW(inst, key, out type, buf, ref size, 0);
        if (r == 26 /* CR_BUFFER_SMALL */ && size is > 0 and <= 4096)
        {
            buf = new byte[size];
            r = CM_Get_DevNode_PropertyW(inst, key, out type, buf, ref size, 0);
        }
        return r == 0 && size > 0 ? buf.AsSpan(0, (int)size).ToArray() : null;
    }

    static string? GetString(uint inst, DevPropKey key)
    {
        var b = GetRaw(inst, key, out uint t);
        return b is null || t != TypeString ? null : Encoding.Unicode.GetString(b).TrimEnd('\0');
    }

    static int? GetByte(uint inst, DevPropKey key)
    {
        var b = GetRaw(inst, key, out uint t);
        return b is { Length: >= 1 } && t == TypeByte ? b[0] : null;
    }

    static bool? GetBool(uint inst, DevPropKey key)
    {
        var b = GetRaw(inst, key, out uint t);
        return b is { Length: >= 1 } && t == TypeBoolean ? b[0] != 0 : null;
    }

    static uint? GetUInt(uint inst, DevPropKey key)
    {
        var b = GetRaw(inst, key, out uint t);
        return b is { Length: >= 4 } && t == TypeUInt32 ? BitConverter.ToUInt32(b, 0) : null;
    }
}
