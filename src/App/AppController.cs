using System.IO;
using System.Windows.Threading;
using BTBatteryIndicator.Core;

namespace BTBatteryIndicator;

/// <summary>Holds the app's state: scans devices on a timer, records history, raises alerts, saves settings.</summary>
public sealed class AppController
{
    public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);

    readonly IDeviceSource _source;
    readonly SettingsStore _settingsStore;
    readonly AlertEngine _alerts = new();
    readonly Autostart _autostart = new();
    readonly DispatcherTimer _timer;
    readonly string _logPath;
    bool _scanning;
    readonly IPodsListener? _pods;
    readonly PodsTracker _podsTracker = new();
    IReadOnlyList<DeviceReading> _raw = [];
    DateTime _lastListen = DateTime.MinValue;
    bool _listening;

    public Settings Settings { get; }
    public HistoryStore History { get; }
    public DemoDeviceSource? Demo => _source as DemoDeviceSource;
    public bool IsDemo => Demo is not null;
    public IReadOnlyList<DeviceReading> Devices { get; private set; } = [];
    public DateTime? LastSync { get; private set; }
    public string? Error { get; private set; }
    public string ExePath { get; } = Environment.ProcessPath ?? "";

    /// <summary>False when the taskbar strip cannot be shown (no taskbar found, or a vertical one); pins then fall back to tray icons.</summary>
    public bool StripAvailable
    {
        get => _stripAvailable;
        set { if (_stripAvailable == value) return; _stripAvailable = value; Updated?.Invoke(); }
    }
    bool _stripAvailable = true;

    public event Action? Updated;
    public event Action<Alert>? AlertRaised;

    public AppController(IDeviceSource source, string dataDir, IPodsListener? pods = null)
    {
        _source = source;
        _pods = pods;
        Directory.CreateDirectory(dataDir);
        _logPath = Path.Combine(dataDir, "log.txt");
        _settingsStore = new SettingsStore(dataDir);
        Settings = _settingsStore.Load();
        History = new HistoryStore(dataDir);
        History.Load();
        Demo?.SeedHistory(History, DateTime.UtcNow);
        _timer = new DispatcherTimer { Interval = ScanInterval };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            _raw = await Task.Run(_source.Scan);
            Apply(_raw);
            Error = null;
            _ = ListenForPodsAsync();
        }
        catch (Exception e)
        {
            Error = "Couldn't read Bluetooth devices. Will retry.";
            Log(e);
        }
        finally { _scanning = false; }
        Updated?.Invoke();
    }

    /// <summary>
    /// Apple earbuds don't give Windows a battery level; they broadcast it instead. While such a device is
    /// connected, listen for a few seconds (receive-only): every scan until a level is known, then every 2 minutes.
    /// </summary>
    async Task ListenForPodsAsync()
    {
        if (_pods is null || _listening || !PodsTracker.Wanted(_raw)) return;
        var now = DateTime.UtcNow;
        bool known = _podsTracker.Enrich(_raw, now).Any(d => d.Pods is not null);
        if (known && now - _lastListen < TimeSpan.FromMinutes(2)) return;
        _listening = true;
        try
        {
            var heard = await _pods.ListenAsync(TimeSpan.FromSeconds(5));
            _lastListen = DateTime.UtcNow;
            if (_podsTracker.Update(heard, _lastListen))
            {
                Apply(_raw);
                Updated?.Invoke();
            }
        }
        catch (Exception e) { Log(e); }
        finally { _listening = false; }
    }

    void Apply(IReadOnlyList<DeviceReading> list)
    {
        var now = DateTime.UtcNow;
        list = WithPinnedPlaceholders(_podsTracker.Enrich(list, now));
        Devices = list;
        LastSync = now;
        bool settingsChanged = false;
        foreach (var d in list)
        {
            var p = Settings.For(d.Id);
            if (p.LastKnownName != d.Name) { p.LastKnownName = d.Name; settingsChanged = true; }
            if (p.LastKnownKind != d.Kind) { p.LastKnownKind = d.Kind; settingsChanged = true; }
            if (d.Connected && d.Battery is int level) History.Record(d.Id, level, now);
        }
        History.Prune(now);
        History.SaveIfDirty();
        if (settingsChanged) Save();
        foreach (var a in _alerts.Evaluate(list, Settings)) AlertRaised?.Invoke(a);
        foreach (var d in _connects.Evaluate(list, Settings)) DeviceConnected?.Invoke(d);
    }

    readonly ConnectWatcher _connects = new();

    /// <summary>A device has just connected (not raised for devices already connected at start-up).</summary>
    public event Action<DeviceReading>? DeviceConnected;

    /// <summary>A pinned device that is not currently found (asleep, switched off, Bluetooth off) stays listed as not connected.</summary>
    IReadOnlyList<DeviceReading> WithPinnedPlaceholders(IReadOnlyList<DeviceReading> list)
    {
        var missing = Settings.Devices.Where(kv => kv.Value.Pinned && list.All(d => d.Id != kv.Key))
            .Select(kv => new DeviceReading(kv.Key, kv.Value.LastKnownName ?? "Device",
                // Receiver devices saved before their type was recorded are almost always mice.
                kv.Value.LastKnownKind == DeviceKind.Generic && kv.Key.StartsWith("LOGI", StringComparison.Ordinal) ? DeviceKind.Mouse : kv.Value.LastKnownKind,
                false, null) { SleepsWhenIdle = kv.Key.StartsWith("LOGI", StringComparison.Ordinal) }).ToList();
        return missing.Count == 0 ? list : [.. list, .. missing];
    }

    // ---- connecting a pinned device on request ------------------------------------------------------

    readonly HashSet<string> _connecting = new();

    public bool IsConnecting(string id) => _connecting.Contains(id);

    /// <summary>True for a known device that is currently not connected.</summary>
    public bool NeedsConnect(string id) => !_connecting.Contains(id) && Devices.FirstOrDefault(d => d.Id == id) is { Connected: false };

    /// <summary>
    /// Asks Windows to connect a paired audio device, then watches for up to 12 seconds to see whether it worked.
    /// Only happens when the user clicks a not-connected device; the app never connects anything by itself.
    /// </summary>
    /// <returns>False if this device is not one Windows can connect on request (the caller then just opens the flyout).</returns>
    public async Task<bool> TryConnectAsync(string id)
    {
        // Receiver devices and anything without a Windows audio link reconnect by themselves; nothing to ask for.
        if (id.StartsWith("LOGI", StringComparison.Ordinal)) return false;
        if ((await Task.Run(() => BluetoothConnector.AudioLinks(id))).Count == 0) return false;
        if (!_connecting.Add(id)) return true;
        string name = Settings.DisplayName(id, Settings.For(id).LastKnownName ?? "Device");
        Updated?.Invoke();
        try
        {
            string detail = "";
            var outcome = await Task.Run(() => BluetoothConnector.Connect(id, out detail));
            bool connected = false;
            if (outcome == BluetoothConnector.Outcome.Requested)
            {
                foreach (int waitMs in new[] { 2000, 3000, 3000, 4000 })
                {
                    await Task.Delay(waitMs);
                    await RefreshAsync();
                    if (Devices.FirstOrDefault(d => d.Id == id) is { Connected: true }) { connected = true; break; }
                }
            }
            if (!connected)
                AlertRaised?.Invoke(new Alert(AlertKind.Info, id, $"Couldn't connect to {name}", outcome switch
                {
                    BluetoothConnector.Outcome.NotAnAudioDevice => "This kind of device reconnects by itself when it is switched on or woken.",
                    BluetoothConnector.Outcome.Requested => "Make sure it is switched on, in range, and not connected to another device.",
                    _ => "Windows did not accept the request. Try connecting from Windows Settings.",
                }));
        }
        catch (Exception e) { Log(e); }
        finally
        {
            _connecting.Remove(id);
            Updated?.Invoke();
        }
        return true;
    }

    /// <summary>True if Windows can connect or disconnect this device on request (Bluetooth audio devices only).</summary>
    /// <summary>A device has just been made the sound output from the taskbar strip.</summary>
    public event Action<DeviceReading>? OutputSwitched;

    /// <summary>
    /// When more than one Bluetooth audio device is connected, makes this one the sound output (only on the user's
    /// click). Returns false when there was nothing to switch: one audio device, not an audio device, or already playing.
    /// </summary>
    public async Task<bool> TrySwitchOutputAsync(string id)
    {
        if (Devices.FirstOrDefault(d => d.Id == id) is not { Connected: true } device) return false;
        var others = Devices.Where(d => d.Connected && d.Id != id).Select(d => d.Id).ToList();
        if (others.Count == 0) return false;
        try
        {
            bool? switched = await Task.Run<bool?>(() =>
            {
                if (BluetoothConnector.OutputOf(id) is not { } mine || mine == BluetoothConnector.CurrentOutput()) return null;
                if (!others.Any(o => BluetoothConnector.OutputOf(o) is not null)) return null;
                return BluetoothConnector.SetOutput(mine);
            });
            if (switched is null) return false;
            if (switched == true) OutputSwitched?.Invoke(device);
            else AlertRaised?.Invoke(new Alert(AlertKind.Info, id, $"Couldn't switch sound to {NameOf(device)}", "Pick it from the sound menu on the taskbar instead."));
            return true;
        }
        catch (Exception e) { Log(e); return false; }
    }

    public Task<bool> CanSwitchAsync(string id) =>
        id.StartsWith("LOGI", StringComparison.Ordinal) ? Task.FromResult(false) : Task.Run(() => BluetoothConnector.AudioLinks(id).Count > 0);

    /// <summary>
    /// Asks Windows to drop the connection to a Bluetooth audio device, then checks that it went. The device stays
    /// paired. Only happens when the user picks Disconnect in the device menu.
    /// </summary>
    public async Task TryDisconnectAsync(string id)
    {
        if (!_connecting.Add(id)) return;
        string name = Settings.DisplayName(id, Settings.For(id).LastKnownName ?? "Device");
        _alerts.ExpectDisconnect(id);   // the user asked for it, so no "disconnected" notification
        Updated?.Invoke();
        try
        {
            string detail = "";
            var outcome = await Task.Run(() => BluetoothConnector.Disconnect(id, out detail));
            bool gone = false;
            if (outcome == BluetoothConnector.Outcome.Requested)
            {
                // Usually gone within a second. If the radio link could not be closed directly, Windows takes about
                // 5 to 7 seconds by itself (measured on real earbuds), so keep checking for 16.
                for (int i = 0; i < 26; i++)
                {
                    await Task.Delay(i < 12 ? 350 : 1000);
                    await RefreshAsync();
                    if (Devices.FirstOrDefault(d => d.Id == id) is not { Connected: true }) { gone = true; break; }
                }
            }
            if (!gone)
                AlertRaised?.Invoke(new Alert(AlertKind.Info, id, $"Couldn't disconnect {name}", outcome == BluetoothConnector.Outcome.NotAnAudioDevice
                    ? "Windows can only disconnect audio devices on request."
                    : "Windows did not drop the connection. Try disconnecting from Windows Settings."));
        }
        catch (Exception e) { Log(e); }
        finally
        {
            _connecting.Remove(id);
            Updated?.Invoke();
        }
    }

    /// <summary>Devices the user has not hidden, connected ones first.</summary>
    public IEnumerable<DeviceReading> Visible => Devices
        .Where(d => !(Settings.Devices.TryGetValue(d.Id, out var p) && p.Hidden))
        .OrderByDescending(d => d.Connected)
        .ThenBy(d => Settings.DisplayName(d.Id, d.Name), StringComparer.CurrentCultureIgnoreCase);

    public string NameOf(DeviceReading d) => Settings.DisplayName(d.Id, d.Name);

    public string TooltipText() =>
        Tooltip.Build(Visible.Where(d => d.Connected).Select(d => (NameOf(d), d.Battery)));

    public void Change(Action<Settings> edit)
    {
        edit(Settings);
        Settings.Normalize();
        Save();
        Updated?.Invoke();
    }

    public bool StartWithWindows
    {
        get => _autostart.IsEnabled(ExePath);
        set { if (ExePath.Length > 0) _autostart.Set(value, ExePath); Updated?.Invoke(); }
    }

    void Save() => _settingsStore.Save(Settings);

    public void Shutdown()
    {
        _timer.Stop();
        History.SaveIfDirty();
        Save();
    }

    public void Log(Exception e)
    {
        try
        {
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 200_000) File.Delete(_logPath);
            File.AppendAllText(_logPath, $"{DateTime.Now:s} {e}\n\n");
        }
        catch { /* logging must never crash the app */ }
    }
}
