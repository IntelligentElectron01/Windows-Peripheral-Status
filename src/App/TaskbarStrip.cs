using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BTBatteryIndicator.Core;
using Microsoft.Win32;

namespace BTBatteryIndicator;

/// <summary>
/// A small pill that sits on the taskbar, just left of the clock area, showing each pinned device
/// with a battery gauge and percentage. Windows 11 has no official way to add things to the taskbar,
/// so this is an ordinary always-on-top window that follows the taskbar's position. It only reads
/// where the taskbar is; it never changes the taskbar or any Windows setting.
/// </summary>
public sealed class TaskbarStrip : Window
{
    readonly AppController _c;
    readonly Border _pill = new() { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), Padding = new Thickness(4, 0, 4, 0) };
    readonly StackPanel _items = new() { Orientation = Orientation.Horizontal };
    // Slow safety-net timer; real-time following of the taskbar is event-driven (see Hook) so the app can sleep.
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly WinEventProc _onTaskbarMoved;
    IntPtr _hwnd, _hook, _hookedBar;
    int _lastX = int.MinValue, _lastY = int.MinValue;
    string _builtKey = "";
    bool _wanted;
    bool _lightTaskbar;

    /// <summary>Stay visible wherever it is, ignoring the taskbar (used for automated screenshots).</summary>
    public bool Sticky { get; set; }

    public event Action? Clicked;

    public TaskbarStrip(AppController c)
    {
        _c = c;
        Title = "BT Battery Indicator taskbar strip";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000; Top = -10000;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        Cursor = Cursors.Hand;
        _pill.Child = _items;
        _pill.Height = 32;
        Content = _pill;

        MouseLeftButtonUp += (_, _) => Clicked?.Invoke();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // Never take keyboard focus and stay out of Alt+Tab.
            long ex = GetWindowLongPtrW(_hwnd, -20).ToInt64();
            SetWindowLongPtrW(_hwnd, -20, new IntPtr(ex | 0x08000000L /* NOACTIVATE */ | 0x00000080L /* TOOLWINDOW */));
        };
        _onTaskbarMoved = (_, _, hwnd, idObject, _, _, _) => { if (hwnd == _hookedBar && idObject == 0) Place(topmost: false); };
        _timer.Tick += (_, _) => Place();
        _c.Updated += Rebuild;
        Theme.Changed += Rebuild;
        Closed += (_, _) => { _timer.Stop(); Unhook(); _c.Updated -= Rebuild; Theme.Changed -= Rebuild; };
        Rebuild();
    }

    public void Rebuild()
    {
        var s = _c.Settings;
        var pinned = s.Devices.Where(kv => kv.Value.Pinned && !kv.Value.Hidden)
            .Select(kv => (Id: kv.Key, Name: s.DisplayName(kv.Key, kv.Value.LastKnownName ?? "Bluetooth device"),
                           Dev: _c.Devices.FirstOrDefault(d => d.Id == kv.Key)))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _wanted = s.PinnedDisplay == "Taskbar" && pinned.Count > 0;

        _lightTaskbar = Theme.ForceDark is bool forced ? !forced : ReadSystemLight();

        // Most 30-second scans change nothing; skip rebuilding the visuals when that is the case.
        string key = $"{_wanted}|{_lightTaskbar}|{Theme.Accent}|{Theme.Dark}|" + string.Join(";",
            pinned.Select(p => $"{p.Id},{p.Name},{p.Dev?.Kind},{p.Dev?.Connected},{p.Dev?.Battery},{p.Dev?.Pods}"));
        if (key == _builtKey) { Place(); return; }
        _builtKey = key;
        _timer.IsEnabled = _wanted;
        if (!_wanted) { _items.Children.Clear(); Place(); return; }

        var fg = Theme.Brush(_lightTaskbar ? Color.FromRgb(0x1B, 0x1B, 0x1B) : Colors.White);
        var accent = Theme.Accent;
        _pill.Background = Theme.Brush(_lightTaskbar ? Color.FromArgb(0xE6, 0xF6, 0xF6, 0xF6) : Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1C));
        _pill.BorderBrush = Theme.Brush(Color.FromArgb(0xB0, accent.R, accent.G, accent.B));

        _items.Children.Clear();
        for (int i = 0; i < pinned.Count; i++)
        {
            if (i > 0)
                _items.Children.Add(new Border { Width = 1, Height = 18, Background = fg, Opacity = 0.22, VerticalAlignment = VerticalAlignment.Center });
            _items.Children.Add(MakeItem(pinned[i].Name, pinned[i].Dev, fg));
        }
        Place();
    }

    FrameworkElement MakeItem(string name, DeviceReading? d, Brush fg)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(9, 0, 9, 0), Background = Brushes.Transparent };

        if (d is { Connected: true, Pods: { } pods } && pods.View != PodView.None)
        {
            // Earbuds, shown the way Apple devices show them: one icon for the pair when the buds are level,
            // one bud when only one is being worn, left and right separately when they differ.
            switch (pods.View)
            {
                case PodView.Pair: Gauge(row, PodIcons.Element(PodIcons.Pair, fg), pods.Primary, fg); break;
                case PodView.Left: Gauge(row, PodIcons.Element(PodIcons.Left, fg), pods.Left, fg); break;
                case PodView.Right: Gauge(row, PodIcons.Element(PodIcons.Right, fg), pods.Right, fg); break;
                case PodView.Separate:
                    Gauge(row, PodIcons.Element(PodIcons.Left, fg), pods.Left, fg);
                    Gauge(row, PodIcons.Element(PodIcons.Right, fg), pods.Right, fg, gapBefore: 12);
                    break;
            }
            if (pods.ShowCase) Gauge(row, PodIcons.Element(PodIcons.Case, fg, 13), pods.Case, fg, gapBefore: 12);
            row.ToolTip = $"{name}: {pods.Describe()}";
            return row;
        }

        var glyph = new TextBlock
        {
            Text = FlyoutWindow.GlyphFor(d?.Kind ?? DeviceKind.Generic),
            FontFamily = (FontFamily)Application.Current.Resources["Icons"],
            FontSize = 15, Foreground = fg, VerticalAlignment = VerticalAlignment.Center,
        };
        Gauge(row, glyph, d is { Connected: true } ? d.Battery : null, fg);
        row.ToolTip = d is null || !d.Connected ? $"{name}: not connected" : $"{name}: {Levels.Text(d.Battery)}";
        return row;
    }

    /// <summary>Adds "icon, battery gauge, percentage" to a row. A null level draws an empty, dimmed gauge.</summary>
    static void Gauge(StackPanel row, FrameworkElement icon, int? level, Brush fg, double gapBefore = 0)
    {
        bool live = level is not null;
        var levelBrush = Theme.Brush(Theme.BandColor(Levels.Band(level)));
        icon.Opacity = 0.9;
        icon.Margin = new Thickness(gapBefore, 0, 0, 0);

        // Battery gauge: outline, proportional fill, and the little terminal on the right.
        const double innerW = 19, innerH = 8;
        var fill = new Border
        {
            Width = live ? Math.Max(2, innerW * level!.Value / 100.0) : 0, Height = innerH, CornerRadius = new CornerRadius(1.5),
            Background = levelBrush, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        };
        var body = new Border
        {
            Width = innerW + 6, Height = innerH + 6, CornerRadius = new CornerRadius(3.5), BorderThickness = new Thickness(1.5),
            BorderBrush = fg, Padding = new Thickness(1.5), Child = fill, Opacity = live ? 1 : 0.5,
        };
        var nub = new Border { Width = 2, Height = 5, CornerRadius = new CornerRadius(0, 1, 1, 0), Background = fg, VerticalAlignment = VerticalAlignment.Center, Opacity = live ? 1 : 0.5 };
        var battery = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        battery.Children.Add(body);
        battery.Children.Add(nub);

        var text = new TextBlock
        {
            Text = live ? $"{level}%" : "–", Foreground = fg, Opacity = live ? 1 : 0.6,
            Margin = new Thickness(7, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(icon);
        row.Children.Add(battery);
        row.Children.Add(text);
    }

    /// <summary>Keeps the strip glued to the taskbar; hides it when the taskbar is hidden or an app is full-screen.</summary>
    void Place(bool topmost = true)
    {
        if (Sticky) return;
        if (!_wanted) { SetAvailable(true); Unhook(); HideStrip(); return; }

        IntPtr bar = FindWindowW("Shell_TrayWnd", null);
        if (bar == IntPtr.Zero || !GetWindowRect(bar, out RECT tb)) { SetAvailable(false); Unhook(); HideStrip(); return; }
        Hook(bar);
        int barW = tb.Right - tb.Left, barH = tb.Bottom - tb.Top;
        if (barH > barW) { SetAvailable(false); HideStrip(); return; } // vertical taskbar: fall back to tray icons
        SetAvailable(true);

        // Full-screen game, presentation, or exclusive app: stay out of the way.
        if (SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4) { HideStrip(); return; }

        // Auto-hide taskbar: only show while most of the taskbar is actually on screen.
        var screen = System.Windows.Forms.Screen.FromHandle(bar).Bounds;
        int visible = Math.Min(tb.Bottom, screen.Bottom) - Math.Max(tb.Top, screen.Top);
        if (visible < barH * 0.8) { HideStrip(); return; }

        if (!IsVisible) Show();
        if (_hwnd == IntPtr.Zero) return;

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
        IntPtr tray = FindWindowExW(bar, IntPtr.Zero, "TrayNotifyWnd", null);
        int rightEdge = tray != IntPtr.Zero && GetWindowRect(tray, out RECT tr) && tr.Left > tb.Left ? tr.Left : tb.Right - (int)(280 * scale);
        int x = Math.Max(tb.Left, rightEdge - w - (int)(8 * scale));
        int y = tb.Top + (barH - h) / 2;
        if (!topmost && x == _lastX && y == _lastY) return;
        _lastX = x; _lastY = y;
        SetWindowPos(_hwnd, new IntPtr(-1) /* topmost */, x, y, 0, 0, 0x0001 /* NOSIZE */ | 0x0010 /* NOACTIVATE */);
    }

    void HideStrip() { if (IsVisible) { Hide(); _lastX = int.MinValue; } }

    /// <summary>Asks Windows to tell us when the taskbar moves (auto-hide sliding in and out), instead of polling.</summary>
    void Hook(IntPtr bar)
    {
        if (bar == _hookedBar && _hook != IntPtr.Zero) return;
        Unhook();
        // Limited to the taskbar's own thread, so unrelated Explorer windows don't wake this app.
        uint thread = GetWindowThreadProcessId(bar, out uint pid);
        _hook = SetWinEventHook(0x800B, 0x800B /* object location change */, IntPtr.Zero, _onTaskbarMoved, pid, thread, 0);
        _hookedBar = bar;
    }

    void Unhook()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _hookedBar = IntPtr.Zero;
    }

    void SetAvailable(bool available)
    {
        if (_c.StripAvailable == available) return;
        _c.StripAvailable = available;
    }

    static bool ReadSystemLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
