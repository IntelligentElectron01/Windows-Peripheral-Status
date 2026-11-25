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
        list = _podsTracker.Enrich(list, now);
        Devices = list;
        LastSync = now;
        bool settingsChanged = false;
        foreach (var d in list)
        {
            var p = Settings.For(d.Id);
            if (p.LastKnownName != d.Name) { p.LastKnownName = d.Name; settingsChanged = true; }
            if (d.Connected && d.Battery is int level) History.Record(d.Id, level, now);
        }
        History.Prune(now);
        History.SaveIfDirty();
        if (settingsChanged) Save();
        foreach (var a in _alerts.Evaluate(list, Settings)) AlertRaised?.Invoke(a);
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
