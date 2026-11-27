using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BTBatteryIndicator.Core;

namespace BTBatteryIndicator;

/// <summary>What the pill is announcing.</summary>
public enum PopupKind { Connected, LowBattery, Output, Input }

/// <summary>
/// A small dark pill that drops down from the top centre of the screen when a device connects, runs low, or
/// becomes the sound output or the microphone. It shows the name and battery for a few seconds, then slides back up. It never
/// takes keyboard focus; a click dismisses it. The window exists only while it is on screen.
/// </summary>
public sealed class ConnectPopup : Window
{
    static readonly Queue<(DeviceReading Device, PopupKind Kind, string? Glyph)> Waiting = new();
    static ConnectPopup? _open;
    static (string Id, DateTime At) _lastConnected;

    /// <summary>Icons for sound outputs that are not Bluetooth devices.</summary>
    public const string LaptopGlyph = "", ScreenGlyph = "", MicGlyph = "";

    /// <summary>Developer aid: receives one line of frame timing after each drop-down.</summary>
    internal static Action<string>? FrameReport;

    /// <summary>Raised when the last popup has gone, so the app can hand the memory it used back to Windows.</summary>
    public static event Action? AllClosed;

    /// <summary>
    /// Shows the popup for this device, or queues it behind the one already showing.
    /// Returns false when it was held back because a full-screen app is in front.
    /// </summary>
    /// <param name="glyph">An icon to use instead of the one for the device's kind (the laptop, for its own speakers).</param>
    public static bool Announce(AppController c, DeviceReading d, PopupKind kind = PopupKind.Connected, string? glyph = null)
    {
        if (HeldBack(c.Settings)) return false;
        // A device that has just connected usually becomes the sound output in the same moment. Its "Connected"
        // pill already says what happened, so a second pill for the same device straight after is left out.
        if (kind is PopupKind.Output or PopupKind.Input && ((_lastConnected.Id == d.Id && (DateTime.UtcNow - _lastConnected.At).TotalSeconds < 8)
                                         || Waiting.Any(w => w.Device.Id == d.Id && w.Kind == PopupKind.Connected))) return true;
        if (kind == PopupKind.Connected) _lastConnected = (d.Id, DateTime.UtcNow);
        if (_open is not null)
        {
            if ((_open._id != d.Id || _open._kind != kind) && !Waiting.Any(w => w.Device.Id == d.Id && w.Kind == kind))
            {
                // Only the newest sound output (or microphone) is worth showing; older ones still waiting are dropped.
                if (kind is PopupKind.Output or PopupKind.Input && Waiting.Any(w => w.Kind == kind))
                {
                    var keep = Waiting.Where(w => w.Kind != kind).ToList();
                    Waiting.Clear();
                    foreach (var w in keep) Waiting.Enqueue(w);
                }
                Waiting.Enqueue((d, kind, glyph));
            }
            return true;
        }
        _open = new ConnectPopup(c, d, kind, glyph);
        _open.Show();
        return true;
    }

    /// <summary>
    /// Exclusive full-screen games and presentations are never drawn over. With the setting on, the same goes for
    /// any app whose window fills the whole screen (borderless games, full-screen video).
    /// </summary>
    static bool HeldBack(Settings s)
    {
        if (SHQueryUserNotificationState(out int state) == 0 && state is 3 or 4) return true;
        return s.QuietInFullScreen && FullScreenInFront();
    }

    /// <summary>
    /// True while the window in front fills its whole screen (a game, full-screen video, a presentation).
    /// </summary>
    /// <param name="sameScreenAs">Only count it when it is on the same screen as this window.</param>
    internal static bool FullScreenInFront(IntPtr sameScreenAs = default)
    {
        IntPtr front = GetForegroundWindow();
        if (front == IntPtr.Zero) return false;
        var cls = new System.Text.StringBuilder(64);
        GetClassNameW(front, cls, 64);
        string name = cls.ToString();
        // The desktop, the taskbar, Start and Search are not apps covering the screen.
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow" || name.StartsWith("Xaml", StringComparison.Ordinal)) return false;
        IntPtr screen = MonitorFromWindow(front, 2);
        if (sameScreenAs != IntPtr.Zero && MonitorFromWindow(sameScreenAs, 2) != screen) return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetWindowRect(front, out var w) || !GetMonitorInfoW(screen, ref info)) return false;
        // Exactly the screen's size. An ordinary maximised window is a few pixels larger on every side (its hidden
        // border), which is how it is told apart, including on a PC whose taskbar auto-hides.
        var m = info.Monitor;
        return w.Left == m.Left && w.Top == m.Top && w.Right == m.Right && w.Bottom == m.Bottom;
    }

    const double Headroom = 140; // how much of the window sits above the top of the screen
    const double Travel = 96;   // how far above its resting place the pill starts and ends

    readonly AppController _c;
    readonly string _id;
    readonly PopupKind _kind;
    readonly Border _pill;
    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    readonly TranslateTransform _move = new(0, -Travel);
    readonly ScaleTransform _scale = new(0.82, 0.82);
    readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromSeconds(3.2) };
    string _shown = "";
    bool _entering, _leaving;

    /// <summary>The pill at rest, with no motion or timer (used for automated screenshots).</summary>
    internal static ConnectPopup Still(AppController c, DeviceReading d, PopupKind kind = PopupKind.Connected, string? glyph = null) => new(c, d, kind, glyph, still: true);

    readonly string? _glyph;

    ConnectPopup(AppController c, DeviceReading d, PopupKind kind, string? glyph, bool still = false)
    {
        _c = c;
        _id = d.Id;
        _kind = kind;
        _glyph = glyph;
        if (kind == PopupKind.LowBattery) _hold.Interval = TimeSpan.FromSeconds(4.5);
        Title = "BT Battery Indicator popup";
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

        _pill = new Border
        {
            Background = Theme.Brush(Color.FromRgb(0x0B, 0x0B, 0x0C)),
            BorderBrush = Theme.Brush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(27),
            Padding = new Thickness(18, 0, 20, 0),
            Height = 54,
            MinWidth = 220,
            // Room above for the whole climb, so the pill is cut off by the edge of the screen and never by the
            // edge of its own window (which costs frames), and room below for the little overshoot of the drop.
            Margin = new Thickness(12, 10 + Headroom, 12, 18),
            Child = _row,
            RenderTransformOrigin = new Point(0.5, 0),
            RenderTransform = new TransformGroup { Children = { _scale, _move } },
        };
        Content = _pill;
        Fill(d);

        SourceInitialized += (_, _) =>
        {
            // Never take keyboard focus and stay out of Alt+Tab.
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtrW(hwnd, -20).ToInt64();
            SetWindowLongPtrW(hwnd, -20, new IntPtr(ex | 0x08000000L /* NOACTIVATE */ | 0x00000080L /* TOOLWINDOW */));
        };
        if (still) { _pill.Margin = new Thickness(12, 10, 12, 18); _move.Y = 0; _scale.ScaleX = _scale.ScaleY = 1; return; }
        SizeChanged += (_, _) => Centre();
        Loaded += (_, _) =>
        {
            Centre();
            // The pill is drawn once into a picture, and the motion then only moves and scales that picture.
            // Re-drawing text and shapes on every frame is what makes a slide look uneven.
            _pill.CacheMode = new BitmapCache { RenderAtScale = VisualTreeHelper.GetDpi(this).DpiScaleX, SnapsToDevicePixels = true };
            Dispatcher.BeginInvoke(Enter, DispatcherPriority.ContextIdle);
        };
        MouseLeftButtonUp += (_, _) => Leave();
        _hold.Tick += (_, _) => Leave();
        _c.Updated += Refresh;
        Closed += (_, _) =>
        {
            _hold.Stop();
            _c.Updated -= Refresh;
            _open = null;
            if (Waiting.TryDequeue(out var next)) Announce(_c, next.Device, next.Kind, next.Glyph);
            if (_open is null) AllClosed?.Invoke();
        };
    }

    void Centre()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top - Headroom;
    }

    /// <summary>Battery levels often arrive a few seconds after the connection (earbuds especially), so keep the pill current.</summary>
    void Refresh()
    {
        // Not while the pill is moving: rebuilding its contents mid-slide would cost a frame.
        if (!_leaving && !_entering && _c.Devices.FirstOrDefault(x => x.Id == _id) is { Connected: true } d) Fill(d);
    }

    void Fill(DeviceReading d)
    {
        string name = _c.NameOf(d);
        string key = $"{name}|{d.Kind}|{d.Battery}|{d.Pods}|{d.Charging}";
        if (key == _shown) return;
        _shown = key;

        var fg = Brushes.White;
        var pods = d.Pods is { } p && p.View != PodView.None ? d.Pods : null;
        FrameworkElement icon = pods is not null
            ? PodIcons.Element(PodIcons.For(pods.View == PodView.Separate ? PodView.Pair : pods.View), fg, 25)
            : new TextBlock
            {
                Text = _glyph ?? FlyoutWindow.GlyphFor(d.Kind), FontFamily = (FontFamily)Application.Current.Resources["Icons"],
                FontSize = 23, Foreground = fg,
            };
        icon.VerticalAlignment = VerticalAlignment.Center;

        var (status, statusColour) = _kind switch
        {
            PopupKind.LowBattery => ("Battery low", Color.FromRgb(0xFF, 0x45, 0x3A)),
            PopupKind.Output => ("Output set", Color.FromRgb(0x64, 0xD2, 0xFF)),
            PopupKind.Input => ("Input set", Color.FromRgb(0x64, 0xD2, 0xFF)),
            _ => ("Connected", Color.FromRgb(0x30, 0xD1, 0x58)),
        };
        var text = new StackPanel { Margin = new Thickness(13, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = name, Foreground = fg, FontSize = 13.5, FontWeight = FontWeights.SemiBold,
            MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = status, FontSize = 11.5, Margin = new Thickness(0, 1, 0, 0),
            Foreground = Theme.Brush(statusColour),
        });

        var levels = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(26, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (pods is not null)
        {
            switch (pods.View)
            {
                case PodView.Pair: TaskbarStrip.Gauge(levels, new Border(), pods.Primary, fg); break;
                case PodView.Left: TaskbarStrip.Gauge(levels, new Border(), pods.Left, fg); break;
                case PodView.Right: TaskbarStrip.Gauge(levels, new Border(), pods.Right, fg); break;
                case PodView.Separate:
                    TaskbarStrip.Gauge(levels, PodIcons.Element(PodIcons.Left, fg), pods.Left, fg);
                    TaskbarStrip.Gauge(levels, PodIcons.Element(PodIcons.Right, fg), pods.Right, fg, gapBefore: 12);
                    break;
            }
            if (pods.ShowCase) TaskbarStrip.Gauge(levels, PodIcons.Element(PodIcons.Case, fg, 13), pods.Case, fg, gapBefore: 12);
        }
        else if (d.Battery is int level)
            TaskbarStrip.Gauge(levels, new Border(), level, fg);

        _row.Children.Clear();
        _row.Children.Add(icon);
        _row.Children.Add(text);
        if (levels.Children.Count > 0) _row.Children.Add(levels);
    }

    // ---- motion --------------------------------------------------------------------------------------

    void Enter()
    {
        _entering = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var frames = new List<double>();
        EventHandler tick = (_, _) => frames.Add(clock.Elapsed.TotalMilliseconds);
        if (FrameReport is not null) CompositionTarget.Rendering += tick;

        Drop(_scale, ScaleTransform.ScaleXProperty, 0.82, 0.82, 1, null);
        Drop(_scale, ScaleTransform.ScaleYProperty, 0.82, 0.82, 1, null);
        Drop(_move, TranslateTransform.YProperty, -Travel - 30, -Travel, 0, () =>
        {
            _entering = false;
            Refresh();
            if (FrameReport is null) return;
            CompositionTarget.Rendering -= tick;
            var gaps = frames.Zip(frames.Skip(1), (a, b) => (At: a, Gap: b - a)).Where(g => g.Gap > 0.5 && g.At >= LeadIn).ToList();
            string slow = string.Join(" ", gaps.Where(g => g.Gap > 11).Select(g => $"{g.Gap:0}@{g.At - LeadIn:0}"));
            FrameReport($"drop-down: {gaps.Count} frames in {clock.Elapsed.TotalMilliseconds - LeadIn:0} ms, target {Rate} per second, " +
                        $"slow frames (ms@when): {(slow.Length > 0 ? slow : "none")}");
        });
        _hold.Start();
    }

    const int LeadIn = 180;   // milliseconds of unseen movement before the visible drop

    /// <summary>
    /// The drop as one continuous movement with an unseen lead-in. The first frames of any new movement come
    /// slowly, while the drawing clock gets up to speed, and the start of the drop is its fastest part, where a
    /// slow frame shows most. So the pill first creeps for a moment above the top edge of its window, out of
    /// sight, and the visible drop begins with the clock already at full speed.
    /// </summary>
    static void Drop(Animatable target, DependencyProperty property, double leadFrom, double from, double to, Action? done)
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(LeadIn + 560) };
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(leadFrom, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(LeadIn))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(LeadIn + 560)),
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 }));
        Timeline.SetDesiredFrameRate(a, Rate);
        if (done is not null) a.Completed += (_, _) => done();
        target.BeginAnimation(property, a);
    }

    void Leave()
    {
        if (_leaving) return;
        _leaving = true;
        _hold.Stop();
        var lift = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.4 };
        Animate(_scale, ScaleTransform.ScaleXProperty, 0.86, 400, lift);
        Animate(_scale, ScaleTransform.ScaleYProperty, 0.86, 400, lift);
        Animate(_pill, OpacityProperty, 0, 180, null, beginMs: 240);
        Animate(_move, TranslateTransform.YProperty, -Travel, 420, lift, done: Close);
    }

    static void Animate(Animatable target, DependencyProperty property, double to, int ms, IEasingFunction? ease, int beginMs = 0, Action? done = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(beginMs) };
        Timeline.SetDesiredFrameRate(a, Rate);
        if (done is not null) a.Completed += (_, _) => done();
        target.BeginAnimation(property, a);
    }

    static void Animate(UIElement target, DependencyProperty property, double to, int ms, IEasingFunction? ease, int beginMs = 0)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(beginMs) };
        Timeline.SetDesiredFrameRate(a, Rate);
        target.BeginAnimation(property, a);
    }

    /// <summary>Frames per second to draw the motion at: the screen's own refresh rate, so every screen refresh gets a new frame.</summary>
    static int Rate => _rate ??= ScreenRate();
    static int? _rate;

    static int ScreenRate()
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        try { int hz = GetDeviceCaps(dc, 116 /* vertical refresh */); return hz is >= 50 and <= 360 ? hz : 60; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
}
