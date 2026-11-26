using System.Runtime.InteropServices;

namespace BTBatteryIndicator.Core;

/// <summary>
/// Asks Windows to connect or disconnect an already-paired Bluetooth audio device (headphones, earbuds, speakers).
/// A disconnected device stays paired and can be connected again at any time.
/// This is the same request the "Connect" button in Windows' own sound and Bluetooth panels sends: it goes to
/// Windows' Bluetooth audio driver, which then tries to reach the device. It never pairs or unpairs anything.
/// Only audio devices can be connected this way; mice, keyboards and controllers reconnect by themselves
/// when they are switched on.
/// </summary>
public static class BluetoothConnector
{
    public enum Outcome
    {
        /// <summary>The request was handed to Windows. Whether the device answers depends on it being on and in range.</summary>
        Requested,
        /// <summary>Windows has no audio connection for this device, so there is nothing to ask.</summary>
        NotAnAudioDevice,
        Failed,
    }

    /// <summary>The Windows audio connections that belong to a Bluetooth address (for diagnostics and tests).</summary>
    public static List<string> AudioLinks(string address)
    {
        var found = new List<string>();
        ForEachLink(address, (id, _) => found.Add(id));
        return found;
    }

    /// <summary>For diagnostics: per audio connection, whether the driver says it supports each request. Sends no request.</summary>
    public static List<string> Describe(string address)
    {
        var lines = new List<string>();
        ForEachLink(address, (id, enumerator) =>
        {
            string kind = id.Replace(address, "…", StringComparison.OrdinalIgnoreCase);
            kind = System.Text.RegularExpressions.Regex.Replace(kind, "[0-9a-f]&[0-9a-f]{6,8}&", "…&", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (enumerator.GetDevice(id, out var filter) < 0 || filter is null) { lines.Add(kind + " | cannot open"); return; }
            var iid = typeof(IKsControl).GUID;
            if (filter.Activate(ref iid, 23, IntPtr.Zero, out object obj) < 0 || obj is not IKsControl ks) { lines.Add(kind + " | no control interface"); return; }
            string text = kind;
            IntPtr buffer = Marshal.AllocHGlobal(4);
            try
            {
                foreach (uint request in new uint[] { 0, 1 })
                {
                    Marshal.WriteInt32(buffer, 0);
                    var ask = new KsProperty { Set = BluetoothAudioSet, Id = request, Flags = 0x200 /* is it supported? */ };
                    int hr = ks.KsProperty(ref ask, Marshal.SizeOf<KsProperty>(), buffer, 4, out _);
                    text += $" | request {request}: 0x{hr:X8} access {Marshal.ReadInt32(buffer)}";
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            lines.Add(text);
        });
        return lines;
    }

    public static Outcome Connect(string address, out string detail) => Send(address, 0 /* reconnect once */, out detail);

    /// <summary>Drops the audio connection, like "Disconnect" in Windows' Bluetooth settings. The device stays paired.</summary>
    public static Outcome Disconnect(string address, out string detail) => Send(address, 1 /* disconnect once */, out detail);

    static Outcome Send(string address, uint requestId, out string detail)
    {
        int sent = 0, failed = 0, lastError = 0, links = 0;
        try
        {
            ForEachLink(address, (id, enumerator) =>
            {
                links++;
                if (enumerator.GetDevice(id, out var filter) < 0 || filter is null) { failed++; return; }
                var iid = typeof(IKsControl).GUID;
                if (filter.Activate(ref iid, 23 /* any server */, IntPtr.Zero, out object obj) < 0 || obj is not IKsControl ks) { failed++; return; }
                var request = new KsProperty { Set = BluetoothAudioSet, Id = requestId, Flags = 1 };
                int hr = ks.KsProperty(ref request, Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _);
                if (hr >= 0) sent++; else { failed++; lastError = hr; }
            });
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            detail = e.Message;
            return Outcome.Failed;
        }
        detail = $"audio links {links}, requests accepted {sent}, refused {failed}{(lastError != 0 ? $" (0x{lastError:X8})" : "")}";
        return links == 0 ? Outcome.NotAnAudioDevice : sent > 0 ? Outcome.Requested : Outcome.Failed;
    }

    /// <summary>
    /// Walks Windows' list of sound inputs and outputs (including disconnected ones), follows each to the driver
    /// component it is wired to, and calls back for those whose identifier contains the Bluetooth address.
    /// </summary>
    static void ForEachLink(string address, Action<string, IMMDeviceEnumerator> onLink)
    {
        if (address.Length != 12 || !address.All(Uri.IsHexDigit)) return;
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        if (enumerator.EnumAudioEndpoints(2 /* inputs and outputs */, 0xF /* every state */, out var endpoints) < 0 || endpoints is null) return;
        endpoints.GetCount(out uint count);
        var topologyId = typeof(IDeviceTopology).GUID;
        var all = new List<(string Id, Guid Container)>();
        for (uint i = 0; i < count; i++)
        {
            if (endpoints.Item(i, out var endpoint) < 0 || endpoint is null) continue;
            if (endpoint.Activate(ref topologyId, 23, IntPtr.Zero, out object obj) < 0 || obj is not IDeviceTopology topology) continue;
            if (topology.GetConnectorCount(out uint connectors) < 0 || connectors == 0) continue;
            if (topology.GetConnector(0, out var connector) < 0 || connector is null) continue;
            if (connector.GetDeviceIdConnectedTo(out string? id) < 0 || string.IsNullOrEmpty(id)) continue;
            all.Add((id, ContainerOf(endpoint)));
        }

        // The music connection carries the address in its identifier. The calls (hands-free) connection does not,
        // so it is matched by Windows' "same physical device" number, and must still be a Bluetooth connection.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var containers = all.Where(e => e.Id.Contains(address, StringComparison.OrdinalIgnoreCase) && e.Container != Guid.Empty)
                            .Select(e => e.Container).ToHashSet();
        foreach (var (id, container) in all)
        {
            bool mine = id.Contains(address, StringComparison.OrdinalIgnoreCase)
                        || (containers.Contains(container) && id.Contains("bth", StringComparison.OrdinalIgnoreCase));
            if (mine && seen.Add(id)) onLink(id, enumerator);
        }
    }

    static Guid ContainerOf(IMMDevice endpoint)
    {
        if (endpoint.OpenPropertyStore(0 /* read */, out var store) < 0 || store is null) return Guid.Empty;
        var key = new PropertyKey { Set = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), Id = 2 }; // container id
        if (store.GetValue(ref key, out var value) < 0) return Guid.Empty;
        try { return value.Type == 72 /* GUID */ && value.Pointer != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(value.Pointer) : Guid.Empty; }
        finally { PropVariantClear(ref value); }
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid Set; public uint Id; }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant { public ushort Type; ushort _r1, _r2, _r3; public IntPtr Pointer; IntPtr _more; }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    static readonly Guid BluetoothAudioSet = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");

    [StructLayout(LayoutKind.Sequential)]
    struct KsProperty { public Guid Set; public uint Id; public uint Flags; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice? device);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore? store);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("2A07407E-6497-4A18-9787-32F79BD0D98F")]
    interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector? connector);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E")]
    interface IConnector
    {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected(out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector? other);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string? id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string? id);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("28F54685-06FD-11D2-B27A-00A0C9223196")]
    interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KsProperty property, int propertyLength, IntPtr data, int dataLength, out int bytesReturned);
    }
}
