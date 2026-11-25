using BTBatteryIndicator.Core;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace BTBatteryIndicator;

/// <summary>
/// Owns the tray icons: one per pinned device showing its level (R2, R3), or a single app icon
/// when nothing is pinned. Every icon's tooltip lists all connected devices (R4).
/// </summary>
public sealed class TrayManager : IDisposable
{
    sealed class Entry
    {
        public required WF.NotifyIcon Icon;
        public IntPtr Handle;
        public string Key = "";
    }

    readonly AppController _c;
    readonly Dictionary<string, Entry> _entries = new(); // "" is the general app icon
    readonly WF.ContextMenuStrip _menu = new();

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayManager(AppController c)
    {
        _c = c;
        _menu.Items.Add("Open", null, (_, _) => OpenRequested?.Invoke());
        _menu.Items.Add("Refresh now", null, async (_, _) => await _c.RefreshAsync());
        if (_c.Demo is { } demo)
        {
            _menu.Items.Add(new WF.ToolStripSeparator());
            _menu.Items.Add("Demo: drain all by 10%", null, async (_, _) => { demo.Drain(10); await _c.RefreshAsync(); });
            _menu.Items.Add("Demo: recharge all", null, async (_, _) => { demo.Recharge(); await _c.RefreshAsync(); });
            _menu.Items.Add("Demo: connect/disconnect keyboard", null,
                async (_, _) => { demo.ToggleConnected("DEMO00000001"); await _c.RefreshAsync(); });
        }
        _menu.Items.Add(new WF.ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _c.Updated += Update;
        Theme.Changed += Update;
        Update();
    }

    public void Update()
    {
        var s = _c.Settings;
        var pinned = s.Devices.Where(kv => kv.Value.Pinned && !kv.Value.Hidden).Select(kv => kv.Key).ToList();
        var wanted = pinned.Count > 0 ? pinned : [""];
        string tip = _c.TooltipText();
        int size = WF.SystemInformation.SmallIconSize.Width;

        foreach (var id in _entries.Keys.Except(wanted).ToList()) Remove(id);

        foreach (var id in wanted)
        {
            if (!_entries.TryGetValue(id, out var e))
            {
                e = new Entry { Icon = new WF.NotifyIcon { ContextMenuStrip = _menu } };
                e.Icon.MouseClick += (_, a) => { if (a.Button == WF.MouseButtons.Left) OpenRequested?.Invoke(); };
                e.Icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
                _entries[id] = e;
            }

            string key;
            Func<SD.Bitmap> draw;
            if (id == "")
            {
                key = $"app|{Theme.Accent}|{size}";
                draw = () => IconArt.AppBitmap(size, ToSd(Theme.Accent));
            }
            else
            {
                var d = _c.Devices.FirstOrDefault(x => x.Id == id);
                bool live = d is { Connected: true, Battery: not null };
                var band = live ? Levels.Band(d!.Battery) : LevelBand.Unknown;
                string text = live ? d!.Battery!.Value.ToString() : "–";
                var bg = Theme.BandColor(band);
                var fg = band == LevelBand.Yellow ? System.Windows.Media.Colors.Black
                       : band == LevelBand.Normal ? Theme.OnAccent
                       : System.Windows.Media.Colors.White;
                key = $"{text}|{bg}|{fg}|{size}";
                draw = () => IconArt.LevelBitmap(size, text, ToSd(bg), ToSd(fg));
            }

            if (e.Key != key)
            {
                using var bmp = draw();
                var handle = IconArt.ToHicon(bmp);
                e.Icon.Icon = SD.Icon.FromHandle(handle);
                if (e.Handle != IntPtr.Zero) IconArt.DestroyIcon(e.Handle);
                e.Handle = handle;
                e.Key = key;
            }
            e.Icon.Text = tip;
            e.Icon.Visible = true;
        }
    }

    public void ShowAlert(Alert a)
    {
        // Prefer the icon of the device the alert is about; otherwise any icon will do.
        var entry = _entries.TryGetValue(a.DeviceId, out var own) ? own : _entries.Values.FirstOrDefault();
        entry?.Icon.ShowBalloonTip(8000, a.Title, a.Message,
            a.Kind == AlertKind.LowBattery ? WF.ToolTipIcon.Warning : WF.ToolTipIcon.Info);
    }

    void Remove(string id)
    {
        if (!_entries.Remove(id, out var e)) return;
        e.Icon.Visible = false;
        e.Icon.Dispose();
        if (e.Handle != IntPtr.Zero) IconArt.DestroyIcon(e.Handle);
    }

    static SD.Color ToSd(System.Windows.Media.Color c) => SD.Color.FromArgb(255, c.R, c.G, c.B);

    public void Dispose()
    {
        _c.Updated -= Update;
        Theme.Changed -= Update;
        foreach (var id in _entries.Keys.ToList()) Remove(id);
        _menu.Dispose();
    }
}
