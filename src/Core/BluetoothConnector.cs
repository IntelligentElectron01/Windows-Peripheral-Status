using System.Runtime.InteropServices;

namespace WirelessDeviceMonitor.Core;

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
    public static Outcome Disconnect(string address, out string detail)
    {
        var outcome = Send(address, 1 /* disconnect once */, out detail);
        if (outcome != Outcome.Requested) return outcome;
        // Windows closes the audio connections at once but keeps the radio link open for about five more seconds.
        // Give the audio a moment to close cleanly, then close the link too so the device shows as gone straight away.
        Thread.Sleep(300);
        detail += DropLink(address) ? ", link closed" : $", link not closed (error {Marshal.GetLastWin32Error()})";
        return outcome;
    }

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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Mine(address, Scan(enumerator, 2 /* inputs and outputs */, 0xF /* every state */)))
            if (seen.Add(e.Link)) onLink(e.Link, enumerator);
    }

    /// <summary>Every sound input/output of the wanted kind, with the driver component it is wired to and its physical-device number.</summary>
    static List<(IMMDevice Endpoint, string Link, Guid Container)> Scan(IMMDeviceEnumerator enumerator, int flow, int states)
    {
        var all = new List<(IMMDevice, string, Guid)>();
        if (enumerator.EnumAudioEndpoints(flow, states, out var endpoints) < 0 || endpoints is null) return all;
        endpoints.GetCount(out uint count);
        var topologyId = typeof(IDeviceTopology).GUID;
        for (uint i = 0; i < count; i++)
        {
            if (endpoints.Item(i, out var endpoint) < 0 || endpoint is null) continue;
            if (endpoint.Activate(ref topologyId, 23, IntPtr.Zero, out object obj) < 0 || obj is not IDeviceTopology topology) continue;
            if (topology.GetConnectorCount(out uint connectors) < 0 || connectors == 0) continue;
            if (topology.GetConnector(0, out var connector) < 0 || connector is null) continue;
            if (connector.GetDeviceIdConnectedTo(out string? id) < 0 || string.IsNullOrEmpty(id)) continue;
            all.Add((endpoint, id, ContainerOf(endpoint)));
        }
        return all;
    }

    /// <summary>
    /// The music connection carries the address in its identifier. The calls (hands-free) connection does not,
    /// so it is matched by Windows' "same physical device" number, and must still be a Bluetooth connection.
    /// </summary>
    static List<(IMMDevice Endpoint, string Link, Guid Container)> Mine(string address, List<(IMMDevice Endpoint, string Link, Guid Container)> all)
    {
        var containers = all.Where(e => e.Link.Contains(address, StringComparison.OrdinalIgnoreCase) && e.Container != Guid.Empty)
                            .Select(e => e.Container).ToHashSet();
        return all.Where(e => e.Link.Contains(address, StringComparison.OrdinalIgnoreCase)
                              || (containers.Contains(e.Container) && e.Link.Contains("bth", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // ---- sound output ------------------------------------------------------------------------------------

    /// <summary>The identifier of this device's working sound output (the music one when there are two), or null.</summary>
    public static string? OutputOf(string address) => EndpointOf(address, 0);

    /// <summary>The identifier of this device's working microphone, or null.</summary>
    public static string? InputOf(string address) => EndpointOf(address, 1);

    static string? EndpointOf(string address, int flow)
    {
        if (address.Length != 12 || !address.All(Uri.IsHexDigit)) return null;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            var known = Mine(address, Scan(enumerator, 2, 0xF)).Select(e => e.Link).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var outputs = Scan(enumerator, flow, 1 /* working */).Where(e => known.Contains(e.Link))
                .OrderByDescending(e => e.Link.Contains(address, StringComparison.OrdinalIgnoreCase));
            foreach (var e in outputs)
                if (e.Endpoint.GetId(out string? id) >= 0 && !string.IsNullOrEmpty(id)) return id;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>The identifier of the output Windows currently plays sound through, or null.</summary>
    public static string? CurrentOutput() => Current(0);

    /// <summary>The identifiers of every output that is working right now (for diagnostics and tests).</summary>
    public static List<string> WorkingOutputs()
    {
        var ids = new List<string>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            foreach (var e in Scan(enumerator, 0, 1 /* working */))
                if (e.Endpoint.GetId(out string? id) >= 0 && !string.IsNullOrEmpty(id)) ids.Add(id);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException) { }
        return ids;
    }

    /// <summary>The identifier of the microphone Windows currently listens through, or null.</summary>
    public static string? CurrentInput() => Current(1);

    static string? Current(int flow)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            return enumerator.GetDefaultAudioEndpoint(flow, 1 /* music and video */, out var device) >= 0 && device is not null
                   && device.GetId(out string? id) >= 0 ? id : null;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Makes an output the one Windows plays through, like picking it in the taskbar's sound menu. Windows has no
    /// published call for this; this is the one its own sound panel uses.
    /// </summary>
    public static bool SetOutput(string endpointId)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            bool ok = true;
            foreach (int role in new[] { 0 /* system sounds */, 1 /* music and video */, 2 /* calls */ })
                ok &= policy.SetDefaultEndpoint(endpointId, role) >= 0;
            return ok;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    /// <summary>What sort of thing Windows is playing sound through.</summary>
    public enum OutputKind { Speakers, BuiltIn, Headphones, Display, Bluetooth }

    /// <summary>The output Windows is playing through: its identifier, the name Windows shows for it, and its sort.</summary>
    public sealed record OutputInfo(string Id, string Name, OutputKind Kind);

    /// <summary>Describes the output Windows currently plays sound through. Reads only; null when there is none.</summary>
    public static OutputInfo? DescribeOutput() => DescribeDefault(false);

    /// <summary>The same for the microphone Windows currently listens through.</summary>
    public static OutputInfo? DescribeInput() => DescribeDefault(true);

    static OutputInfo? DescribeDefault(bool input)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (enumerator.GetDefaultAudioEndpoint(input ? 1 : 0, 1 /* music and video */, out var device) < 0 || device is null) return null;
            if (device.GetId(out string? id) < 0 || string.IsNullOrEmpty(id)) return null;
            string name = "Sound output";
            uint form = 10 /* unknown */;
            if (device.OpenPropertyStore(0 /* read */, out var store) >= 0 && store is not null)
            {
                var deviceSet = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
                name = Text(store, deviceSet, 14 /* full name */) ?? Text(store, deviceSet, 2 /* short name */) ?? name;
                form = Number(store, new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"), 0 /* form factor */) ?? form;
            }
            bool bluetooth = (LinkOf(device) ?? "").Contains("bth", StringComparison.OrdinalIgnoreCase);
            // Sound hardware that is part of the PC itself carries the PC's own "physical device" number.
            bool partOfPc = ContainerOf(device) == new Guid("00000000-0000-0000-FFFF-FFFFFFFFFFFF");
            return new OutputInfo(id, name, Classify(bluetooth, form, partOfPc, input));
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException) { return null; }
    }

    /// <param name="form">Windows' "form factor" number for the output (1 speakers, 3 headphones, 5 headset, 9 a screen's speakers).</param>
    /// <param name="input">A microphone rather than an output (4 is then a plain microphone).</param>
    public static OutputKind Classify(bool bluetooth, uint form, bool partOfPc, bool input = false) =>
        bluetooth ? OutputKind.Bluetooth
        : input ? (form == 5 ? OutputKind.Headphones : partOfPc && form == 4 ? OutputKind.BuiltIn : OutputKind.Speakers)
        : form is 3 or 5 ? OutputKind.Headphones
        : form == 9 ? OutputKind.Display
        : partOfPc && form == 1 ? OutputKind.BuiltIn
        : OutputKind.Speakers;

    static IMMDeviceEnumerator? _watching;
    static OutputListener? _listener;

    /// <summary>
    /// Asks Windows to say whenever the sound output or the microphone changes, however it was changed. Costs
    /// nothing while nothing changes. The callback (true for the microphone) arrives on a Windows thread and
    /// must return quickly.
    /// </summary>
    public static bool WatchOutput(Action<bool> changed)
    {
        try
        {
            if (_listener is not null) return true;
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            var listener = new OutputListener(changed);
            if (enumerator.RegisterEndpointNotificationCallback(listener) < 0) return false;
            _watching = enumerator; _listener = listener;   // kept so they are not cleaned up while Windows holds them
            return true;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    sealed class OutputListener(Action<bool> changed) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string id, int state) => 0;
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) => 0;
        public int OnDefaultDeviceChanged(int flow, int role, string? id)
        {
            if (flow is 0 or 1 /* output or microphone */) { try { changed(flow == 1); } catch { /* never throw into Windows */ } }
            return 0;
        }
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    static string? LinkOf(IMMDevice endpoint)
    {
        var topologyId = typeof(IDeviceTopology).GUID;
        if (endpoint.Activate(ref topologyId, 23, IntPtr.Zero, out object obj) < 0 || obj is not IDeviceTopology topology) return null;
        if (topology.GetConnectorCount(out uint connectors) < 0 || connectors == 0) return null;
        if (topology.GetConnector(0, out var connector) < 0 || connector is null) return null;
        return connector.GetDeviceIdConnectedTo(out string? id) < 0 ? null : id;
    }

    static string? Text(IPropertyStore store, Guid set, uint id)
    {
        var key = new PropertyKey { Set = set, Id = id };
        if (store.GetValue(ref key, out var value) < 0) return null;
        try { return value.Type == 31 /* text */ && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null; }
        finally { PropVariantClear(ref value); }
    }

    static uint? Number(IPropertyStore store, Guid set, uint id)
    {
        var key = new PropertyKey { Set = set, Id = id };
        if (store.GetValue(ref key, out var value) < 0) return null;
        try { return value.Type == 19 /* whole number */ ? (uint)(value.Pointer.ToInt64() & 0xFFFFFFFF) : null; }
        finally { PropVariantClear(ref value); }
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigClient { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    interface IPolicyConfig
    {
        // Ten calls this app never uses come first; they are listed only to keep the one below in its place.
        [PreserveSig] int Unused1(); [PreserveSig] int Unused2(); [PreserveSig] int Unused3(); [PreserveSig] int Unused4(); [PreserveSig] int Unused5();
        [PreserveSig] int Unused6(); [PreserveSig] int Unused7(); [PreserveSig] int Unused8(); [PreserveSig] int Unused9(); [PreserveSig] int Unused10();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string endpointId, int role);
    }

    // ---- dropping the radio link ---------------------------------------------------------------------------

    /// <summary>
    /// Tells the PC's Bluetooth radio to close its link to a device now, instead of waiting the several seconds
    /// Windows takes by itself after the audio connections close. The device stays paired.
    /// </summary>
    public static bool DropLink(string address)
    {
        if (address.Length != 12 || !address.All(Uri.IsHexDigit)) return false;
        ulong target = Convert.ToUInt64(address, 16);
        var find = new RadioFind { Size = (uint)Marshal.SizeOf<RadioFind>() };
        IntPtr search = BluetoothFindFirstRadio(ref find, out IntPtr radio);
        if (search == IntPtr.Zero) return false;
        try { return DeviceIoControl(radio, 0x41000C /* disconnect device */, ref target, 8, IntPtr.Zero, 0, out _, IntPtr.Zero); }
        finally { CloseHandle(radio); BluetoothFindRadioClose(search); }
    }

    [StructLayout(LayoutKind.Sequential)] struct RadioFind { public uint Size; }
    [DllImport("bluetoothapis.dll", SetLastError = true)] static extern IntPtr BluetoothFindFirstRadio(ref RadioFind find, out IntPtr radio);
    [DllImport("bluetoothapis.dll")] static extern bool BluetoothFindRadioClose(IntPtr search);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(IntPtr device, uint code, ref ulong input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

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
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
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
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);
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
