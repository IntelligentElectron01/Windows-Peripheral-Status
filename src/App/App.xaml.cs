using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BTBatteryIndicator.Core;
using Microsoft.Win32;
using SD = System.Drawing;

namespace BTBatteryIndicator;

public partial class App : Application
{
    AppController? _c;
    TrayManager? _tray;
    FlyoutWindow? _flyout;
    TaskbarStrip? _strip;
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
        int shotArg = args.IndexOf("--shot");
        bool shot = shotArg >= 0 && e.Args.Length > shotArg + 1;
        bool demo = shot || args.Contains("--demo");
        bool quiet = args.Contains(Autostart.QuietArg);

        string instance = demo ? "BTBatteryIndicator.Demo" : "BTBatteryIndicator";
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
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BTBatteryIndicator");
        if (demo) dataDir = Path.Combine(dataDir, shot ? "shot" : "demo");
        if (shot && Directory.Exists(dataDir)) Directory.Delete(dataDir, true);

        IDeviceSource source = demo ? new DemoDeviceSource() : new WindowsDeviceSource();
        _c = new AppController(source, dataDir, demo ? null : new BlePodsListener());
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
        ScheduleTrim();

        _strip = new TaskbarStrip(_c);
        _strip.Clicked += () => _flyout.Toggle();
        _tray.OpenRequested += () => _flyout.Toggle();
        _tray.ExitRequested += Quit;
        _flyout.ExitRequested += Quit;
        _flyout.HistoryRequested += ShowHistory;
        _flyout.RenameRequested += id => new RenameWindow(_c, id).Show();
        _c.AlertRaised += a => _tray.ShowAlert(a);

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
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None }, null,
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
