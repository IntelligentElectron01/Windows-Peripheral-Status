using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WirelessDeviceMonitor.Core;
using Microsoft.Win32;

namespace WirelessDeviceMonitor;

/// <summary>
/// A small pill that sits on the taskbar, just left of the clock area, showing each pinned device
/// with a battery gauge and percentage. Windows 11 has no official way to add things to the taskbar
/// (and draws over any window placed inside it), so this is an always-on-top window that follows the
/// taskbar's position. It only reads where the taskbar is; it never changes the taskbar or any Windows setting.
/// </summary>
public sealed class TaskbarStrip : Window
{
    readonly AppController _c;
    readonly Border _pill = new() { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), Padding = new Thickness(4, 0, 4, 0) };
    readonly StackPanel _items = new() { Orientation = Orientation.Horizontal };
    // Slow safety-net timer (z-order, tray width changes). Movement itself is followed frame by frame, see the glide below.
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly WinEventProc _onExplorerMoved;
    // Shortly after the taskbar is re-ordered or finishes sliding, put the strip back in front of it.
    readonly DispatcherTimer _raise = new() { Interval = TimeSpan.FromMilliseconds(40) };
    IntPtr _hwnd, _hook, _orderHook, _frontHook, _hookedBar;
    int _lastX = int.MinValue, _lastY = int.MinValue;
    string _builtKey = "";
    bool _wanted;
    bool _lightTaskbar;

    /// <summary>Stay visible wherever it is, ignoring the taskbar (used for automated screenshots).</summary>
    public bool Sticky { get; set; }

    /// <summary>Raised with the device ID of the item that was clicked, or null for the strip's edge.</summary>
    public event Action<string?>? Clicked;

    public TaskbarStrip(AppController c)
    {
        _c = c;
        Title = "Wireless Device Monitor taskbar strip";
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

        MouseLeftButtonUp += (_, e) => Clicked?.Invoke(ItemIdAt(e.OriginalSource as DependencyObject));
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // Never take keyboard focus and stay out of Alt+Tab.
            long ex = GetWindowLongPtrW(_hwnd, -20).ToInt64();
            SetWindowLongPtrW(_hwnd, -20, new IntPtr(ex | 0x08000000L /* NOACTIVATE */ | 0x00000080L /* TOOLWINDOW */));
        };
        _onExplorerMoved = (_, evt, hwnd, idObject, _, _, _) =>
        {
            if (evt == 0x0003)   // another window came to the front (Start, Search, a full-screen app, ...)
            {
                if (_hookedBar != IntPtr.Zero && ConnectPopup.FullScreenInFront(_hookedBar)) Park();
                Nudge(10);
                return;
            }
            if (hwnd != _hookedBar || idObject != 0) return;
            if (evt == 0x800B) StartGlide(); else Nudge(3);
        };
        // Windows sometimes lifts the taskbar above the strip (when it is revealed, clicked, or Start opens).
        // The strip is put back in front at once, then again a few times shortly after in case Windows was
        // still rearranging, and finally placed properly once any slide has finished.
        _raise.Tick += (_, _) =>
        {
            if (BarLifted()) BringToFront();
            if (--_raisesLeft > 0 || _gliding) return;
            _raise.Stop(); Place();
        };
        _timer.Tick += (_, _) => Place();
        _c.Updated += Rebuild;
        Theme.Changed += Rebuild;
        Closed += (_, _) =>
        {
            _timer.Stop(); _raise.Stop(); Unhook();
            _c.Updated -= Rebuild; Theme.Changed -= Rebuild;
        };
        Rebuild();
    }

    public void Rebuild()
    {
        var s = _c.Settings;
        var pinned = s.Devices.Where(kv => kv.Value.Pinned && !kv.Value.Hidden)
            .Select(kv => (Id: kv.Key, Name: s.DisplayName(kv.Key, kv.Value.LastKnownName ?? "Bluetooth device"),
                           Dev: _c.Devices.FirstOrDefault(d => d.Id == kv.Key), Kind: kv.Value.LastKnownKind))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _wanted = s.PinnedDisplay == "Taskbar" && pinned.Count > 0;

        _lightTaskbar = Theme.ForceDark is bool forced ? !forced : ReadSystemLight();

        // Most 30-second scans change nothing; skip rebuilding the visuals when that is the case.
        string key = $"{_wanted}|{_lightTaskbar}|{Theme.Accent}|{Theme.Dark}|" + string.Join(";",
            pinned.Select(p => $"{p.Id},{p.Name},{p.Kind},{p.Dev?.Kind},{p.Dev?.Connected},{p.Dev?.Battery},{p.Dev?.Pods},{p.Dev?.Charging},{_c.IsConnecting(p.Id)}"));
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
            _items.Children.Add(MakeItem(pinned[i].Id, pinned[i].Name, pinned[i].Dev, pinned[i].Kind, fg));
        }
        Place();
    }

    FrameworkElement MakeItem(string id, string name, DeviceReading? d, DeviceKind lastKind, Brush fg)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(9, 0, 9, 0), Background = Brushes.Transparent, Tag = id };

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
            row.ToolTip = Tip($"{name}: {pods.Describe()}");
            return row;
        }

        var glyph = new TextBlock
        {
            Text = FlyoutWindow.GlyphFor(d?.Kind ?? (lastKind == DeviceKind.Generic && id.StartsWith("LOGI", StringComparison.Ordinal) ? DeviceKind.Mouse : lastKind)),
            FontFamily = (FontFamily)Application.Current.Resources["Icons"],
            FontSize = 15, Foreground = fg, VerticalAlignment = VerticalAlignment.Center,
        };
        bool connecting = _c.IsConnecting(id);
        Gauge(row, glyph, d is { Connected: true } ? d.Battery : null, fg, placeholder: connecting ? "…" : "–");
        row.ToolTip = Tip(connecting ? $"{name}: connecting…"
            : d is { SleepsWhenIdle: true } || id.StartsWith("LOGI", StringComparison.Ordinal) ? $"{name}: asleep or switched off. Move it to wake it."
            : d is null || !d.Connected ? $"{name}: not connected. Click to try to connect."
            : $"{name}: {Levels.Text(d.Battery)}{(d.Charging ? " (charging)" : "")}");
        return row;
    }

    /// <summary>A hover label centred just above the item it describes (the default lands off to one side, over the tray icons).</summary>
    static ToolTip Tip(string text) => new()
    {
        Content = text,
        Placement = System.Windows.Controls.Primitives.PlacementMode.Custom,
        CustomPopupPlacementCallback = (label, item, _) =>
            [new System.Windows.Controls.Primitives.CustomPopupPlacement(
                new Point((item.Width - label.Width) / 2, -label.Height - 12), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)],
    };

    /// <summary>Adds "icon, battery gauge, percentage" to a row. A null level draws an empty, dimmed gauge.</summary>
    internal static void Gauge(StackPanel row, FrameworkElement icon, int? level, Brush fg, double gapBefore = 0, string placeholder = "–")
    {
        bool live = level is not null;
        var levelBrush = Theme.Brush(Theme.BandColor(Levels.Band(level)));
        icon.Opacity = live ? 0.9 : 0.55;
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
            Text = live ? $"{level}%" : placeholder, Foreground = fg, Opacity = live ? 1 : 0.6,
            Margin = new Thickness(7, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(icon);
        row.Children.Add(battery);
        row.Children.Add(text);
    }

    /// <summary>Which pinned device's item a click landed on.</summary>
    static string? ItemIdAt(DependencyObject? o)
    {
        while (o is not null)
        {
            if (o is StackPanel { Tag: string id }) return id;
            o = o is Visual ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o);
        }
        return null;
    }

    // ---- staying on the taskbar ----------------------------------------------------------------------

    /// <summary>Puts the strip where it belongs for the taskbar's current position.</summary>
    void Place(bool raise = true) => MoveTo(null, raise);

    /// <param name="barTopOverride">Use this taskbar top instead of the measured one (the glide passes its smoothed estimate).</param>
    /// <param name="raise">Also bring the strip to the front.</param>
    void MoveTo(double? barTopOverride, bool raise)
    {
        if (Sticky) return;
        if (!_wanted) { SetAvailable(true); Unhook(); HideStrip(); return; }

        IntPtr bar = FindWindowW("Shell_TrayWnd", null);
        if (bar == IntPtr.Zero || !GetWindowRect(bar, out RECT tb)) { SetAvailable(false); Unhook(); HideStrip(); return; }
        int barW = tb.Right - tb.Left, barH = tb.Bottom - tb.Top;
        if (barH > barW) { SetAvailable(false); Unhook(); HideStrip(); return; } // vertical taskbar: fall back to tray icons
        SetAvailable(true);
        Hook(bar);

        // A taskbar set to auto-hide takes the strip with it when it slides off screen. But a full-screen app can
        // also simply cover the taskbar while it is still up (and a fixed taskbar never moves at all), and the
        // strip, being always on top, would be left floating over that app. So it steps aside while a
        // full-screen app, game or presentation is in front.
        if (ConnectPopup.FullScreenInFront(bar)
            || (!IsAutoHide() && SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4)) { Park(); return; }

        // The strip is never hidden while wanted (hiding and re-showing a window blinks); it rides along with the
        // taskbar, off the edge of the screen and back.
        if (!IsVisible) Show();
        if (_hwnd == IntPtr.Zero) return;
        // Make the taskbar the strip's owner: Windows then keeps the strip in front of it, including while
        // Start or Search is open, when the taskbar is lifted above every ordinary always-on-top window.
        if (!_gliding && GetWindow(_hwnd, 4 /* owner */) != bar) SetWindowLongPtrW(_hwnd, -8 /* owner */, bar);

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
        IntPtr tray = FindWindowExW(bar, IntPtr.Zero, "TrayNotifyWnd", null);
        int rightEdge = tray != IntPtr.Zero && GetWindowRect(tray, out RECT tr) && tr.Left > tb.Left ? tr.Left : tb.Right - (int)(280 * scale);
        int x = Math.Max(tb.Left, rightEdge - w - (int)(8 * scale));
        int y = (int)Math.Round(barTopOverride ?? tb.Top) + (barH - h) / 2;
        if (_gliding) return;   // the glide thread owns the position while the taskbar is moving
        if (!raise && x == _lastX && y == _lastY) return;
        _lastX = x; _lastY = y;
        // While gliding, only move: re-ordering windows involves Explorer, which is busy animating the taskbar and
        // would hold this call up for a noticeable fraction of the slide.
        if (raise) SetWindowPos(_hwnd, new IntPtr(-1) /* always on top */, x, y, 0, 0, 0x0001 /* NOSIZE */ | 0x0010 /* NOACTIVATE */);
        else SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 /* NOZORDER */ | 0x0010 | 0x0200 /* NOOWNERZORDER */ | 0x0400 /* NOSENDCHANGING */);
    }

    int _raisesLeft;

    /// <summary>
    /// While Start or Search is open Windows lifts the taskbar above every ordinary always-on-top window. Only
    /// then is the strip pushed to the front straight away; at other times re-ordering waits until the taskbar
    /// has stopped moving, because Windows makes such requests wait for the slide, which would freeze the strip.
    /// </summary>
    void Nudge(int checks)
    {
        if (BarLifted()) BringToFront();
        _raisesLeft = Math.Max(_raisesLeft, checks);
        _raise.Start();
    }

    bool BarLifted()
    {
        // Only while a shell surface (Start, Search, the taskbar itself) is in front. Once the user has clicked
        // away the taskbar is about to slide out, and a re-order request then would freeze the strip mid-slide.
        var cls = new System.Text.StringBuilder(64);
        GetClassNameW(GetForegroundWindow(), cls, 64);
        string front = cls.ToString();
        if (front != "Windows.UI.Core.CoreWindow" && front != "Shell_TrayWnd" && !front.StartsWith("Xaml", StringComparison.Ordinal)) return false;
        try { return _hookedBar != IntPtr.Zero && GetWindowBand(_hookedBar, out uint band) && band > 1; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>Front of the always-on-top windows, without moving. Does not wait on other programs.</summary>
    void BringToFront()
    {
        if (!_wanted || Sticky || _hwnd == IntPtr.Zero || !IsVisible || _lastX == -32000) return;
        SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0,
            0x0001 /* NOSIZE */ | 0x0002 /* NOMOVE */ | 0x0010 /* NOACTIVATE */ | 0x0200 /* NOOWNERZORDER */ | 0x0400 /* NOSENDCHANGING */);
    }

    /// <summary>Moves the strip off screen without hiding it.</summary>
    void Park()
    {
        // Not mid-slide: the slide would move the strip straight back. The next placement parks it instead.
        if (_hwnd == IntPtr.Zero || !IsVisible || _gliding || _lastX == -32000) return;
        _lastX = -32000; _lastY = -32000;
        SetWindowPos(_hwnd, IntPtr.Zero, -32000, -32000, 0, 0, 0x0001 | 0x0004 /* NOZORDER */ | 0x0010);
    }

    void HideStrip() { if (IsVisible) { Hide(); _lastX = int.MinValue; } }

    // ---- gliding with the taskbar while it slides ---------------------------------------------------
    //
    // Windows moves an auto-hiding taskbar in small jumps about 30 times a second. Copying those jumps a moment
    // late makes the strip visibly trail and judder. Instead, while the taskbar is moving, the strip is moved
    // once per screen refresh to where the taskbar is heading: its last measured position plus its measured
    // speed. The result is a smooth slide that stays locked to the taskbar at whatever speed Windows animates it.
    //
    // This runs on its own short-lived thread, timed by the screen's refresh, because the app's normal drawing
    // loop goes to sleep while the strip is off screen (which is exactly when a reveal starts).

    volatile bool _gliding;

    void StartGlide()
    {
        if (!_wanted || Sticky || _gliding || _hwnd == IntPtr.Zero || !IsVisible) return;
        IntPtr bar = _hookedBar, hwnd = _hwnd;
        if (bar == IntPtr.Zero || !GetWindowRect(bar, out RECT tb) || _lastX is int.MinValue or -32000) return;
        var screen = System.Windows.Forms.Screen.FromHandle(bar).Bounds;
        int barH = tb.Bottom - tb.Top, x = _lastX;
        int offset = (barH - (int)Math.Ceiling(ActualHeight * VisualTreeHelper.GetDpi(this).DpiScaleX)) / 2;
        int shownTop = screen.Bottom - barH, hiddenTop = screen.Bottom - 2;
        int startTop = _lastY - offset;   // where the taskbar was when the strip was last placed
        _gliding = true;

        var thread = new Thread(() =>
        {
            try { Glide(hwnd, bar, x, offset, shownTop, hiddenTop, startTop); }
            finally { _gliding = false; Dispatcher.BeginInvoke(() => Nudge(3)); }
        }) { IsBackground = true, Name = "strip-glide", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    void Glide(IntPtr hwnd, IntPtr bar, int x, int offset, int shownTop, int hiddenTop, int startTop)
    {
        var clock = Stopwatch.StartNew();
        int seenTop = startTop, prevTop = startTop, lastY = int.MinValue;
        double seenAt = -31, speed = 0;          // ms; pixels per ms, positive = moving down (hiding)

        while (true)
        {
            if (!GetWindowRect(bar, out RECT tb)) return;
            double now = clock.Elapsed.TotalMilliseconds;
            if (tb.Top != seenTop)
            {
                double measured = (tb.Top - seenTop) / Math.Max(1, now - seenAt);
                // Blend with the earlier estimate so one late or early jump does not jerk the strip.
                speed = speed == 0 || Math.Sign(measured) != Math.Sign(speed) ? measured : speed * 0.5 + measured * 0.5;
                prevTop = seenTop; seenTop = tb.Top; seenAt = now;
            }

            double sinceJump = now - seenAt;
            bool settled = sinceJump > 110;
            double top = seenTop;
            if (!settled)
            {
                // Fill in the motion between the taskbar's jumps: move at its measured speed from the last jump,
                // never more than one jump ahead of what was measured, and never past where the taskbar can
                // actually be (fully shown, or tucked away at the screen edge).
                double limit = Math.Abs(seenTop - prevTop);
                top = Math.Clamp(seenTop + Math.Clamp(speed * sinceJump * 0.8, -limit, limit), shownTop, hiddenTop);
            }
            int y = (int)Math.Round(top) + offset;
            if (y != lastY)
            {
                // Move only: re-ordering windows involves Explorer, which is busy animating and would stall this call.
                SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 /* NOZORDER */ | 0x0010 | 0x0200 /* NOOWNERZORDER */ | 0x0400 /* NOSENDCHANGING */);
                lastY = y; _lastY = y;
            }
            if (settled && sinceJump > 400) return;   // quiet for a moment: done
            if (DwmFlush() != 0) Thread.Sleep(8);      // wait for the next screen refresh
        }
    }

    void StopGlide() { }
    /// <summary>Asks Windows to say when Explorer moves a window, which is how a slide is noticed the moment it starts.</summary>
    void Hook(IntPtr bar)
    {
        if (bar == _hookedBar && _hook != IntPtr.Zero) return;
        Unhook();
        // Any Explorer thread may be the one that moves the taskbar, so listen to the whole Explorer process
        // and ignore everything that is not the taskbar window itself.
        GetWindowThreadProcessId(bar, out uint pid);
        _hook = SetWinEventHook(0x800B, 0x800B /* object location change */, IntPtr.Zero, _onExplorerMoved, pid, 0, 0);
        _orderHook = SetWinEventHook(0x8004, 0x8004 /* object re-ordered */, IntPtr.Zero, _onExplorerMoved, pid, 0, 0);
        _frontHook = SetWinEventHook(0x0003, 0x0003 /* a window came to the front */, IntPtr.Zero, _onExplorerMoved, 0, 0, 0);
        _hookedBar = bar;
    }

    void Unhook()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        if (_orderHook != IntPtr.Zero) UnhookWinEvent(_orderHook);
        _hook = IntPtr.Zero;
        if (_frontHook != IntPtr.Zero) UnhookWinEvent(_frontHook);
        _orderHook = IntPtr.Zero;
        _frontHook = IntPtr.Zero;
        _hookedBar = IntPtr.Zero;
        StopGlide();
    }

    void SetAvailable(bool available)
    {
        if (_c.StripAvailable == available) return;
        _c.StripAvailable = available;
    }

    /// <summary>The taskbar's rectangle in screen pixels if it is currently showing on screen, otherwise null.</summary>
    public static System.Drawing.Rectangle? VisibleTaskbar()
    {
        IntPtr bar = FindWindowW("Shell_TrayWnd", null);
        if (bar == IntPtr.Zero || !GetWindowRect(bar, out RECT tb)) return null;
        var screen = System.Windows.Forms.Screen.FromHandle(bar).Bounds;
        var rect = System.Drawing.Rectangle.FromLTRB(tb.Left, tb.Top, tb.Right, tb.Bottom);
        var on = System.Drawing.Rectangle.Intersect(rect, screen);
        return on.Width * (long)on.Height >= rect.Width * (long)rect.Height * 0.8 ? rect : null;
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

    static bool IsAutoHide()
    {
        var d = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        return (SHAppBarMessage(4 /* get state */, ref d).ToUInt64() & 1) != 0;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public uint cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }

    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] static extern bool GetWindowBand(IntPtr hwnd, out uint band);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
}
