using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WirelessDeviceMonitor.Core;
using Microsoft.Win32;
using SD = System.Drawing;

namespace WirelessDeviceMonitor;

public partial class App : Application
{
    AppController? _c;
    TrayManager? _tray;
    FlyoutWindow? _flyout;
    TaskbarStrip? _strip;
    HotkeyManager? _hotkey;
    Mutex? _mutex;
    EventWaitHandle? _showSignal;
    readonly Dictionary<string, HistoryWindow> _historyWindows = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The UI is a few rows of text; drawing it on the CPU avoids holding graphics-card memory for a tray app.
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToList();
        // Developer helpers (no effect on normal use).
        int iconArg = args.IndexOf("--make-icon");
        if (iconArg >= 0 && e.Args.Length > iconArg + 1)
        {
            IconArt.WriteIco(e.Args[iconArg + 1], SD.Color.FromArgb(0x16, 0x67, 0xC4));
            Shutdown();
            return;
        }
        int probeArg = args.IndexOf("--gc-probe");
        if (probeArg >= 0 && e.Args.Length > probeArg + 1)
        {
            // How much short-lived memory may pile up before .NET clears it out (measured, not assumed).
            GC.Collect();
            int before = GC.CollectionCount(0);
            long start = GC.GetTotalAllocatedBytes(true);
            while (GC.CollectionCount(0) == before) GC.KeepAlive(new byte[1024]);
            File.WriteAllText(e.Args[probeArg + 1], FormattableString.Invariant($"{(GC.GetTotalAllocatedBytes(true) - start) / 1048576.0:0.0} MB allocated before the first clear-out"));
            Shutdown();
            return;
        }
        int shotArg = args.IndexOf("--shot");
        bool shot = shotArg >= 0 && e.Args.Length > shotArg + 1;
        bool demo = shot || args.Contains("--demo");
        bool quiet = args.Contains(Autostart.QuietArg);

        string instance = demo ? "WirelessDeviceMonitor.Demo" : "WirelessDeviceMonitor";
        if (!shot)
        {
            _mutex = new Mutex(true, @"Local\" + instance, out bool first);
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + instance + ".Show");
            if (!first)
            {
                _showSignal.Set(); // ask the running copy to open its flyout, then leave
                Shutdown();
                return;
            }
        }

        DispatcherUnhandledException += (_, ex) => { _c?.Log(ex.Exception); ex.Handled = true; };

        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WirelessDeviceMonitor");
        // The app used to be called BT Battery Indicator: carry its settings and history over, once.
        string oldDataDir = Path.Combine(Path.GetDirectoryName(dataDir)!, "BTBatteryIndicator");
        if (!Directory.Exists(dataDir) && Directory.Exists(oldDataDir))
        {
            try { Directory.Move(oldDataDir, dataDir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (demo) dataDir = Path.Combine(dataDir, shot ? "shot" : "demo");
        if (shot && Directory.Exists(dataDir)) Directory.Delete(dataDir, true);

        Settings? live = null; // the Logitech reader checks the user's on/off switch each time it runs
        IDeviceSource source = demo ? new DemoDeviceSource()
            : new CompositeSource(new WindowsDeviceSource(), new LogitechSource(() => live?.LogitechEnabled ?? true));
        _c = new AppController(source, dataDir, demo ? null : new BlePodsListener());
        live = _c.Settings;
        Theme.Apply(_c.Settings.Accent);

        if (shot)
        {
            try { await TakeScreenshots(e.Args[shotArg + 1]); }
            catch (Exception ex) { _c.Log(ex); }
            finally { Environment.Exit(0); } // developer helper: always end, even if a render failed
            return;
        }

        _tray = new TrayManager(_c);
        _flyout = new FlyoutWindow(_c);
        _trim.Tick += (_, _) => { _trim.Stop(); TrimMemory(); };
        _flyout.Closed_ += ScheduleTrim;
        ConnectPopup.AllClosed += ScheduleTrim;
        // The clean-up runs once after the app's windows close, when their memory will not be wanted again for
        // a while. It is deliberately not repeated on a timer: while idle the app does not grow (see --memlog),
        // and pushing out memory that the next device scan needs only makes Windows fetch it straight back.
        if (args.Contains("--strip-trace"))
        {
            // Developer helper: timings of how the strip follows the taskbar, one line per event.
            string traceFile = Path.Combine(dataDir, "strip-trace.txt");
            File.WriteAllText(traceFile, "");
            var traceClock = System.Diagnostics.Stopwatch.StartNew();
            // Lines are only queued here and written by a background thread, so tracing does not itself slow
            // down the thread it is timing.
            var pending = new System.Collections.Concurrent.ConcurrentQueue<string>();
            TaskbarStrip.Trace = line => pending.Enqueue($"{traceClock.ElapsedMilliseconds,7} | {line}\n");
            new Thread(() =>
            {
                var text = new System.Text.StringBuilder();
                while (true)
                {
                    Thread.Sleep(500);
                    text.Clear();
                    while (pending.TryDequeue(out string? line)) text.Append(line);
                    if (text.Length > 0) File.AppendAllText(traceFile, text.ToString());
                }
            }) { IsBackground = true, Name = "strip-trace" }.Start();
        }
        if (args.Contains("--memlog"))
        {
            // Developer helper: one line of memory figures every 30 seconds.
            string log = Path.Combine(dataDir, "memlog.txt");
            File.WriteAllText(log, "min | RAM in use | committed | managed heap | heap committed | allocated since start | collections 0/1/2\n");
            var began = DateTime.UtcNow;
            var sample = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            sample.Tick += (_, _) =>
            {
                using var me = System.Diagnostics.Process.GetCurrentProcess();
                var gc = GC.GetGCMemoryInfo();
                File.AppendAllText(log, FormattableString.Invariant(
                    $"{(DateTime.UtcNow - began).TotalMinutes:0.0} | {me.WorkingSet64 / 1048576.0:0.0} | {me.PrivateMemorySize64 / 1048576.0:0.0} | {GC.GetTotalMemory(false) / 1048576.0:0.00} | {gc.TotalCommittedBytes / 1048576.0:0.0} | {GC.GetTotalAllocatedBytes() / 1048576.0:0.0} | {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}\n"));
            };
            sample.Start();
        }
        ScheduleTrim();

        CreateStrip();
        _tray.OpenRequested += () => _flyout.Toggle();
        _tray.ExitRequested += Quit;
        _flyout.ExitRequested += Quit;
        _flyout.HistoryRequested += ShowHistory;
        _flyout.RenameRequested += id => new RenameWindow(_c, id).Show();
        _c.AlertRaised += a =>
        {
            // Low battery drops down as a pill. If a full-screen app is in front it goes to Windows' own
            // notifications instead, so the warning is kept for later rather than lost.
            if (a.Kind == AlertKind.LowBattery && _c.Devices.FirstOrDefault(d => d.Id == a.DeviceId) is { } low
                && ConnectPopup.Announce(_c, low, PopupKind.LowBattery)) return;
            _tray.ShowAlert(a);
        };
        _c.DeviceConnected += d => ConnectPopup.Announce(_c, d);
        // A pill for every change of sound output or microphone, however it was changed (the strip, Windows' own
        // sound menu, a device connecting or leaving). Windows reports the change; nothing is polled.
        if (!demo)
        {
            _outputPause.Tick += async (_, _) => { _outputPause.Stop(); await AnnounceSoundAsync(false); };
            _inputPause.Tick += async (_, _) => { _inputPause.Stop(); await AnnounceSoundAsync(true); };
            Task.Run(() =>
            {
                _lastOutput = BluetoothConnector.CurrentOutput();
                _lastInput = BluetoothConnector.CurrentInput();
                // Windows reports one change several times over; wait for it to settle, then look once.
                bool watching = BluetoothConnector.WatchOutput(input => Dispatcher.BeginInvoke(() =>
                {
                    var pause = input ? _inputPause : _outputPause;
                    pause.Stop(); pause.Start();
                }));
                if (!watching) Dispatcher.BeginInvoke(() => _c.OutputSwitched += d => ConnectPopup.Announce(_c, d, PopupKind.Output));
            });
        }
        if (demo && args.Contains("--popup-test"))
        {
            // Developer helper: disconnect and reconnect two made-up devices so the popup can be watched, then show
            // the other two kinds. Frame timing of each drop-down is written next to the demo settings.
            ConnectPopup.FrameReport = line => File.AppendAllText(Path.Combine(dataDir, "frames.txt"), line + Environment.NewLine);
            Dispatcher.BeginInvoke(async () =>
            {
                string[] ids = ["DEMO00000005", "DEMO00000003"];
                await Task.Delay(2500);
                foreach (var id in ids) _c.Demo!.ToggleConnected(id);
                await _c.RefreshAsync();
                await Task.Delay(1500);
                foreach (var id in ids) _c.Demo!.ToggleConnected(id);
                await _c.RefreshAsync();
                ConnectPopup.Announce(_c, _c.Devices.First(d => d.Id == "DEMO00000003"), PopupKind.LowBattery);
                ConnectPopup.Announce(_c, _c.Devices.First(d => d.Id == "DEMO00000005"), PopupKind.Output);
            });
        }

        // Optional keyboard shortcut that opens the flyout from anywhere (chosen in the gear panel).
        _hotkey = new HotkeyManager();
        _hotkey.Pressed += () => _flyout.Toggle();
        _flyout.ApplyHotkey = text => _hotkey.Set(text);
        if (!_hotkey.Set(_c.Settings.Hotkey)) _c.Log(new InvalidOperationException("Saved shortcut could not be registered: " + _c.Settings.Hotkey));

        SystemEvents.UserPreferenceChanged += (_, _) =>
            Dispatcher.BeginInvoke(() => Theme.Apply(_c.Settings.Accent));
        SystemEvents.PowerModeChanged += (_, p) =>
        {
            if (p.Mode != PowerModes.Resume) return;
            // Bluetooth needs a few seconds to reconnect after waking.
            Dispatcher.BeginInvoke(async () => { await Task.Delay(5000); await _c.RefreshAsync(); });
        };

        var signal = _showSignal!;
        new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(() => _flyout?.ShowFlyout());
        }) { IsBackground = true, Name = "show-signal" }.Start();

        _c.Start();
        if (!quiet) _flyout.ShowFlyout();
    }

    bool _quitting;

    /// <summary>Builds the taskbar strip. It lives inside the taskbar window, so if Explorer restarts it is destroyed with it and rebuilt here.</summary>
    void CreateStrip()
    {
        _strip = new TaskbarStrip(_c!);
        // Clicking a pinned device that is not connected tries to connect it. Clicking a connected audio device
        // that is not the sound output yet moves the sound to it. Anything else opens the flyout, including
        // devices that cannot be connected on request (mice, keyboards, controllers).
        _strip.Clicked += async id =>
        {
            bool handled = id is not null && (_c!.NeedsConnect(id) ? await _c.TryConnectAsync(id) : await _c.TrySwitchOutputAsync(id));
            if (!handled) _flyout?.Toggle();
        };
        _strip.Closed += async (_, _) =>
        {
            if (_quitting) return;
            await Task.Delay(3000);
            if (!_quitting) CreateStrip();
        };
    }

    readonly DispatcherTimer _outputPause = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _inputPause = new() { Interval = TimeSpan.FromMilliseconds(400) };
    string? _lastOutput, _lastInput;

    async Task AnnounceSoundAsync(bool input)
    {
        try
        {
            var now = await Task.Run(() => input ? BluetoothConnector.DescribeInput() : BluetoothConnector.DescribeOutput());
            if (now is null || now.Id == (input ? _lastInput : _lastOutput)) return;
            if (input) _lastInput = now.Id; else _lastOutput = now.Id;
            if (!(input ? _c!.Settings.InputPopup : _c!.Settings.OutputPopup)) return;
            var kind = input ? PopupKind.Input : PopupKind.Output;
            if (await _c!.DeviceForOutputAsync(now.Id, input) is { } device) { ConnectPopup.Announce(_c, device, kind); return; }
            ConnectPopup.Announce(_c, OutputAsDevice(now, HasBattery(), input), kind, OutputGlyph(now.Kind, HasBattery(), input));
        }
        catch (Exception e) { _c?.Log(e); }
    }

    static bool HasBattery() =>
        (System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0;

    /// <summary>A sound output or microphone that is not one of the paired devices, in the shape the pill shows.</summary>
    internal static DeviceReading OutputAsDevice(BluetoothConnector.OutputInfo o, bool laptop, bool input = false) => new(
        (input ? "IN:" : "OUT:") + o.Id,
        o.Kind == BluetoothConnector.OutputKind.BuiltIn ? (laptop ? "Laptop " : "PC ") + (input ? "microphone" : "speakers") : o.Name,
        o.Kind is BluetoothConnector.OutputKind.Headphones or BluetoothConnector.OutputKind.Bluetooth ? DeviceKind.Headphones : DeviceKind.Speaker,
        true, null);

    internal static string? OutputGlyph(BluetoothConnector.OutputKind kind, bool laptop, bool input = false) => kind switch
    {
        BluetoothConnector.OutputKind.BuiltIn when laptop => ConnectPopup.LaptopGlyph,
        BluetoothConnector.OutputKind.Display => ConnectPopup.ScreenGlyph,
        BluetoothConnector.OutputKind.Headphones or BluetoothConnector.OutputKind.Bluetooth => null,
        _ when input => ConnectPopup.MicGlyph,
        _ => null,
    };

    void ShowHistory(string id)
    {
        if (_historyWindows.TryGetValue(id, out var existing)) { existing.Activate(); return; }
        var w = new HistoryWindow(_c!, id);
        _historyWindows[id] = w;
        w.Closed += (_, _) => { _historyWindows.Remove(id); ScheduleTrim(); };
        w.Show();
        w.Activate();
    }

    // ---- memory: after the UI has been used, hand unused RAM back to Windows -------------------

    readonly DispatcherTimer _trim = new() { Interval = TimeSpan.FromSeconds(12) };

    void ScheduleTrim() { _trim.Stop(); _trim.Start(); }

    static void TrimMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        // Tells Windows it may page out everything this app is not actively using. Pages come back on demand.
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    void Quit()
    {
        _quitting = true;
        _hotkey?.Dispose();
        _strip?.Close();
        _tray?.Dispose();
        _c?.Shutdown();
        Shutdown();
    }

    // ---- automated screenshots of the demo UI, used to check the look without real devices ----

    async Task TakeScreenshots(string dir)
    {
        Directory.CreateDirectory(dir);
        var c = _c!;
        c.Demo!.Drain(10);
        c.Change(s => { s.For("DEMO00000001").Pinned = true; s.For("DEMO00000003").Pinned = true; });
        await c.RefreshAsync();

        foreach (bool dark in new[] { false, true })
        {
            string tag = dark ? "dark" : "light";
            Theme.ForceDark = dark;
            Theme.Apply(c.Settings.Accent);

            var fly = new FlyoutWindow(c) { Sticky = true, Left = -4000, Top = 0 };
            fly.Show();
            await Idle();
            Save(fly, Path.Combine(dir, $"flyout-{tag}.png"));
            fly.ShowSettings(true);
            c.Change(s => s.For("DEMO00000004").Hidden = true);
            await Idle();
            Save(fly, Path.Combine(dir, $"flyout-settings-{tag}.png"));
            fly.ShowNotifications();
            await Idle();
            await Task.Delay(200);   // this page is shorter: let the window finish shrinking before the picture is taken
            await Idle();
            Save(fly, Path.Combine(dir, $"flyout-notifications-{tag}.png"));
            c.Change(s => s.For("DEMO00000004").Hidden = false);
            fly.Close();

            var hist = new HistoryWindow(c, "DEMO00000003") { Left = -4000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            hist.Show();
            await Idle();
            Save(hist, Path.Combine(dir, $"history-{tag}.png"));
            hist.Close();

            var ren = new RenameWindow(c, "DEMO00000002") { Left = -4000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            ren.Show();
            await Idle();
            Save(ren, Path.Combine(dir, $"rename-{tag}.png"));
            ren.Close();

            c.Change(s => { s.For("DEMO00000002").Pinned = true; s.For("DEMO00000005").Pinned = true; });
            var bar = new TaskbarStrip(c) { Sticky = true };
            bar.Show();
            await Idle();
            Save(bar, Path.Combine(dir, $"strip-{tag}.png"));
            // The other earbud layouts: one bud worn, uneven levels, one bud charging in its case.
            for (int i = 1; i <= 3; i++)
            {
                c.Demo!.CyclePods();
                await c.RefreshAsync();
                await Idle();
                Save(bar, Path.Combine(dir, $"strip-pods{i}-{tag}.png"));
            }
            c.Demo!.CyclePods();
            await c.RefreshAsync();
            bar.Close();
            c.Change(s => { s.For("DEMO00000002").Pinned = false; s.For("DEMO00000005").Pinned = false; });

            if (dark)
            {
                foreach (var (id, file, kind) in new[]
                {
                    ("DEMO00000005", "popup-earbuds.png", PopupKind.Connected), ("DEMO00000001", "popup-keyboard.png", PopupKind.Connected),
                    ("DEMO00000003", "popup-low.png", PopupKind.LowBattery), ("DEMO00000005", "popup-output.png", PopupKind.Output),
                })
                {
                    var pop = ConnectPopup.Still(c, c.Devices.First(d => d.Id == id), kind);
                    pop.Show();
                    await Idle();
                    Save(pop, Path.Combine(dir, file));
                    pop.Close();
                }
                var own = new BluetoothConnector.OutputInfo("demo", "Speakers", BluetoothConnector.OutputKind.BuiltIn);
                var laptop = ConnectPopup.Still(c, OutputAsDevice(own, true), PopupKind.Output, OutputGlyph(own.Kind, true));
                laptop.Show();
                await Idle();
                Save(laptop, Path.Combine(dir, "popup-laptop.png"));
                laptop.Close();
                var mic = ConnectPopup.Still(c, OutputAsDevice(own, true, true), PopupKind.Input, OutputGlyph(own.Kind, true, true));
                mic.Show();
                await Idle();
                Save(mic, Path.Combine(dir, "popup-input.png"));
                mic.Close();
            }

            // Tray icons at 2x so they can be inspected.
            SD.Color Sd(Color m) => SD.Color.FromArgb(255, m.R, m.G, m.B);
            using var strip = new SD.Bitmap(32 * 6 + 8 * 7, 48);
            using (var g = SD.Graphics.FromImage(strip))
            {
                g.Clear(dark ? SD.Color.FromArgb(32, 32, 32) : SD.Color.FromArgb(243, 243, 243));
                (string text, LevelBand band)[] icons = [("100", LevelBand.Normal), ("66", LevelBand.Normal), ("45", LevelBand.Yellow), ("14", LevelBand.Red), ("–", LevelBand.Unknown)];
                int x = 8;
                foreach (var (text, band) in icons)
                {
                    var fg = band == LevelBand.Yellow ? Colors.Black : band == LevelBand.Normal ? Theme.OnAccent : Colors.White;
                    using var bmp = IconArt.LevelBitmap(32, text, Sd(Theme.BandColor(band)), Sd(fg));
                    g.DrawImage(bmp, x, 8);
                    x += 40;
                }
                using var app = IconArt.AppBitmap(32, Sd(Theme.Accent));
                g.DrawImage(app, x, 8);
            }
            strip.Save(Path.Combine(dir, $"tray-{tag}.png"), SD.Imaging.ImageFormat.Png);
        }
    }

    Task Idle() => Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task;

    static void Save(Window w, string path)
    {
        var root = (FrameworkElement)w.Content;
        root.UpdateLayout();
        const double scale = 1.5;
        // Paint the window background behind the content so transparent areas are not black.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = w.Background is SolidColorBrush { Color.A: > 0 } b ? (Brush)b : Theme.Brush(Theme.Dark ? Color.FromRgb(20, 20, 20) : Color.FromRgb(200, 210, 220));
            var size = new Size(root.ActualWidth + root.Margin.Left + root.Margin.Right, root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
            dc.DrawRectangle(bg, null, new Rect(size));
            // Exactly the content's own rectangle, top-left aligned (the default centres on whatever was drawn,
            // which shifts the picture when a shadow or a page change makes that uneven).
            var exact = new VisualBrush(root)
            {
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect((Point)VisualTreeHelper.GetOffset(root), new Size(root.ActualWidth, root.ActualHeight)),
            };
            dc.DrawRectangle(exact, null,
                new Rect(root.Margin.Left, root.Margin.Top, root.ActualWidth, root.ActualHeight));
        }
        var full = new Size(root.ActualWidth + root.Margin.Left + root.Margin.Right, root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
        var rtb = new RenderTargetBitmap((int)(full.Width * scale), (int)(full.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
