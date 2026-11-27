using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WirelessDeviceMonitor.Core;

/// <summary>
/// Listens for Bluetooth Low Energy broadcasts using Windows' built-in advertisement watcher.
/// It is receive-only: nothing is transmitted to any device and no connection is made.
///
/// Windows offers this only as a "Windows Runtime" component. Rather than pull in Microsoft's 25 MB
/// helper library for it, this class calls the handful of functions it needs directly.
/// </summary>
public sealed class BlePodsListener : IPodsListener
{
    static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    static readonly Guid IID_IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    static readonly Guid IID_Watcher = new("A6AC336F-F3D3-4297-8D6C-C81EA6623F40");

    /// <summary>Identifier of the "advertisement received" callback type, derived the way Windows derives it.</summary>
    public static readonly Guid IID_ReceivedHandler = ParameterizedIid(
        "pinterface({9de1c534-6ae1-11e0-84e1-18a905bcc53f};" +
        "rc(Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher;{a6ac336f-f3d3-4297-8d6c-c81ea6623f40});" +
        "rc(Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementReceivedEventArgs;{27987ddf-e596-41be-8d43-9e6731d4a913}))");

    static readonly object Gate = new();
    static List<(PodLevels, int)>? s_heard;   // filled by the callback while a burst is running
    static TaskCompletionSource? s_first;     // completed when a nearby earbud broadcast arrives
    static IntPtr s_handler;                  // the callback object handed to Windows (lives for the process)

    IntPtr _watcher;
    bool _failed;

    /// <summary>Last problem, if listening could not start (e.g. Bluetooth is off). For diagnostics only.</summary>
    public string? LastError { get; private set; }

    public async Task<IReadOnlyList<(PodLevels Levels, int Rssi)>> ListenAsync(TimeSpan duration)
    {
        if (_failed) return [];
        var heard = new List<(PodLevels, int)>();
        try
        {
            if (!EnsureWatcher()) return [];
            lock (Gate) s_heard = heard;
            int hr = StartStop(17);
            if (hr < 0) { LastError = $"Start failed 0x{hr:X8}"; return []; }
            // Scanning is the expensive part, so stop shortly after the first nearby earbud broadcast instead of
            // running the full time. The short tail lets a second pair nearby be heard so the stronger one can win.
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Gate) s_first = first;
            if (await Task.WhenAny(first.Task, Task.Delay(duration)).ConfigureAwait(false) == first.Task)
                await Task.Delay(300).ConfigureAwait(false);
        }
        catch (Exception e) { LastError = e.Message; _failed = true; }
        finally
        {
            lock (Gate) { s_heard = null; s_first = null; }
            if (_watcher != IntPtr.Zero) StartStop(18);
        }
        lock (heard) return heard.ToList();
    }

    /// <summary>Slot 17 is Start, slot 18 is Stop.</summary>
    unsafe int StartStop(int slot) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_watcher, slot))(_watcher);

    unsafe bool EnsureWatcher()
    {
        if (_watcher != IntPtr.Zero) return true;
        RoInitialize(1); // harmless if this thread is already set up
        const string cls = "Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher";
        int hr = WindowsCreateString(cls, (uint)cls.Length, out IntPtr name);
        if (hr < 0) return Fail("string", hr);
        hr = RoActivateInstance(name, out IntPtr instance);
        WindowsDeleteString(name);
        if (hr < 0 || instance == IntPtr.Zero) return Fail("activate", hr);

        Guid iid = IID_Watcher;
        hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(instance, 0))(instance, &iid, UnsafePtr(out IntPtr watcher));
        Release(instance);
        if (hr < 0 || watcher == IntPtr.Zero) return Fail("interface", hr);

        if (s_handler == IntPtr.Zero) s_handler = CreateHandler();
        long token;
        hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(watcher, 19))(watcher, s_handler, &token); // add_Received
        if (hr < 0) { Release(watcher); return Fail("subscribe", hr); }
        _watcher = watcher;
        return true;

        static unsafe IntPtr* UnsafePtr(out IntPtr p) { p = IntPtr.Zero; return (IntPtr*)Unsafe.AsPointer(ref p); }
    }

    bool Fail(string step, int hr)
    {
        LastError = $"{step} failed 0x{hr:X8}";
        _failed = true;
        return false;
    }

    // ---- the callback object Windows calls for each broadcast ---------------------------------------

    static unsafe IntPtr CreateHandler()
    {
        var vtbl = (void**)NativeMemory.Alloc((nuint)(4 * sizeof(void*)));
        vtbl[0] = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QueryInterface;
        vtbl[1] = (delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefRelease;
        vtbl[2] = (delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefRelease;
        vtbl[3] = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)&Invoke;
        var obj = (void***)NativeMemory.Alloc((nuint)sizeof(void*));
        *obj = vtbl;
        return (IntPtr)obj;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static unsafe int QueryInterface(IntPtr self, Guid* iid, IntPtr* result)
    {
        if (*iid == IID_IUnknown || *iid == IID_IAgileObject || *iid == IID_ReceivedHandler)
        {
            *result = self;
            return 0;
        }
        *result = IntPtr.Zero;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint AddRefRelease(IntPtr self) => 1; // never freed: one small object for the life of the process

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int Invoke(IntPtr self, IntPtr sender, IntPtr args)
    {
        try { OnReceived(args); } catch { /* never let an exception cross back into Windows */ }
        return 0;
    }

    /// <summary>Diagnostic counters: callbacks received, manufacturer entries seen, Apple entries seen.</summary>
    public static int Callbacks, Entries, AppleEntries;
    public static string Diagnostic = "";

    static unsafe void OnReceived(IntPtr args)
    {
        Interlocked.Increment(ref Callbacks);
        if (args == IntPtr.Zero) return;
        short rssi;
        if (((delegate* unmanaged[Stdcall]<IntPtr, short*, int>)Slot(args, 6))(args, &rssi) < 0) return;
        IntPtr adv;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(args, 10))(args, &adv) < 0 || adv == IntPtr.Zero) return;
        try
        {
            IntPtr list;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(adv, 11))(adv, &list) < 0 || list == IntPtr.Zero) return;
            try
            {
                uint count;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(list, 7))(list, &count) < 0) return;
                for (uint i = 0; i < count && i < 8; i++)
                {
                    IntPtr item;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(list, 6))(list, i, &item) < 0 || item == IntPtr.Zero) continue;
                    try { ReadManufacturerData(item, rssi); }
                    finally { Release(item); }
                }
            }
            finally { Release(list); }
        }
        finally { Release(adv); }
    }

    static unsafe void ReadManufacturerData(IntPtr item, short rssi)
    {
        ushort company;
        Interlocked.Increment(ref Entries);
        if (((delegate* unmanaged[Stdcall]<IntPtr, ushort*, int>)Slot(item, 6))(item, &company) < 0 || company != ApplePods.AppleCompanyId) return;
        Interlocked.Increment(ref AppleEntries);
        IntPtr buffer;
        int h1 = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(item, 8))(item, &buffer);
        if (h1 < 0 || buffer == IntPtr.Zero) return;
        try
        {
            uint length;
            int h2 = ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(buffer, 7))(buffer, &length);
            if (h2 < 0 || length is < 2 or > 64) return;

            // Copy the payload out through Windows' DataReader (the buffer here does not offer direct byte access).
            IntPtr statics = ReaderStatics();
            if (statics == IntPtr.Zero) return;
            IntPtr reader;
            int h3 = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(statics, 6))(statics, buffer, &reader); // FromBuffer
            if (h3 < 0 || reader == IntPtr.Zero) { Diagnostic = $"FromBuffer 0x{h3:X8}"; return; }
            try
            {
                uint available;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(reader, 6))(reader, &available) < 0 || available != length)
                { Diagnostic = $"reader length {available} vs {length}"; return; }
                byte* bytes = stackalloc byte[(int)length];
                int h4 = ((delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, int>)Slot(reader, 14))(reader, length, bytes); // ReadBytes
                if (h4 < 0) { Diagnostic = $"ReadBytes 0x{h4:X8}"; return; }
                var levels = ApplePods.Parse(new ReadOnlySpan<byte>(bytes, (int)length));
                if (levels is null) return;
                lock (Gate)
                {
                    var heard = s_heard;
                    if (heard is not null) lock (heard) heard.Add((levels, rssi));
                    if (rssi >= PodsTracker.MinRssi) s_first?.TrySetResult();
                }
            }
            finally { Release(reader); }
        }
        finally { Release(buffer); }
    }

    static IntPtr s_readerStatics;

    static unsafe IntPtr ReaderStatics()
    {
        if (s_readerStatics != IntPtr.Zero) return s_readerStatics;
        const string cls = "Windows.Storage.Streams.DataReader";
        if (WindowsCreateString(cls, (uint)cls.Length, out IntPtr name) < 0) return IntPtr.Zero;
        Guid iid = new("11FCBFC8-F93A-471B-B121-F379E349313C"); // IDataReaderStatics
        IntPtr factory;
        int hr = RoGetActivationFactory(name, &iid, &factory);
        WindowsDeleteString(name);
        if (hr < 0) { Diagnostic = $"reader factory 0x{hr:X8}"; return IntPtr.Zero; }
        return s_readerStatics = factory;
    }

    // ---- small helpers ------------------------------------------------------------------------------

    static unsafe void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    static unsafe void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, 2))(obj);
    }

    /// <summary>Windows Runtime rule for naming generic interfaces: a version-5 UUID of the type signature.</summary>
    public static Guid ParameterizedIid(string signature)
    {
        byte[] ns = new Guid("11f47ad5-7b73-42c0-abae-878b1e16adee").ToByteArray(bigEndian: true);
        byte[] hash = SHA1.HashData([.. ns, .. Encoding.UTF8.GetBytes(signature)]);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    [DllImport("combase.dll")] static extern int RoInitialize(int type);
    [DllImport("combase.dll")] static extern int RoActivateInstance(IntPtr classId, out IntPtr instance);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string source, uint length, out IntPtr hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] static extern unsafe int RoGetActivationFactory(IntPtr classId, Guid* iid, IntPtr* factory);
}
