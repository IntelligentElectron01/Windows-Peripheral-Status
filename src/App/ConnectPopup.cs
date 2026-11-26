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

/// <summary>
/// A small dark pill that drops down from the top centre of the screen when a device connects, shows its name
/// and battery for a few seconds, then slides back up. It never takes keyboard focus; a click dismisses it.
/// The window exists only while it is on screen.
/// </summary>
public sealed class ConnectPopup : Window
{
    static readonly Queue<DeviceReading> Waiting = new();
    static ConnectPopup? _open;

    /// <summary>Shows the popup for this device, or queues it behind the one already showing.</summary>
    public static void Announce(AppController c, DeviceReading d)
    {
        // Stay out of the way of exclusive full-screen games and presentations. (Windows also reports a looser
        // "busy" state, but that is set by ordinary maximised or borderless windows, so it is not used here.)
        if (SHQueryUserNotificationState(out int state) == 0 && state is 3 or 4) return;
        if (_open is not null)
        {
            if (_open._id != d.Id && Waiting.All(w => w.Id != d.Id)) Waiting.Enqueue(d);
            return;
        }
        _open = new ConnectPopup(c, d);
        _open.Show();
    }

    const double Travel = 96;   // how far above its resting place the pill starts and ends

    readonly AppController _c;
    readonly string _id;
    readonly Border _pill;
    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    readonly TranslateTransform _move = new(0, -Travel);
    readonly ScaleTransform _scale = new(0.82, 0.82);
    readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromSeconds(3.2) };
    string _shown = "";
    bool _leaving;

    /// <summary>The pill at rest, with no motion or timer (used for automated screenshots).</summary>
    internal static ConnectPopup Still(AppController c, DeviceReading d) => new(c, d, still: true);

    ConnectPopup(AppController c, DeviceReading d, bool still = false)
    {
        _c = c;
        _id = d.Id;
        Title = "BT Battery Indicator connected";
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
            // Room below for the little overshoot at the end of the drop.
            Margin = new Thickness(12, 10, 12, 18),
            Child = _row,
            Opacity = 0,
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
        if (still) { _move.Y = 0; _scale.ScaleX = _scale.ScaleY = 1; _pill.Opacity = 1; return; }
        SizeChanged += (_, _) => Centre();
        Loaded += (_, _) => { Centre(); Enter(); };
        MouseLeftButtonUp += (_, _) => Leave();
        _hold.Tick += (_, _) => Leave();
        _c.Updated += Refresh;
        Closed += (_, _) =>
        {
            _hold.Stop();
            _c.Updated -= Refresh;
            _open = null;
            if (Waiting.TryDequeue(out var next)) Announce(_c, next);
        };
    }

    void Centre()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top;
    }

    /// <summary>Battery levels often arrive a few seconds after the connection (earbuds especially), so keep the pill current.</summary>
    void Refresh()
    {
        if (!_leaving && _c.Devices.FirstOrDefault(x => x.Id == _id) is { Connected: true } d) Fill(d);
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
                Text = FlyoutWindow.GlyphFor(d.Kind), FontFamily = (FontFamily)Application.Current.Resources["Icons"],
                FontSize = 23, Foreground = fg,
            };
        icon.VerticalAlignment = VerticalAlignment.Center;

        var text = new StackPanel { Margin = new Thickness(13, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = name, Foreground = fg, FontSize = 13.5, FontWeight = FontWeights.SemiBold,
            MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = "Connected", FontSize = 11.5, Margin = new Thickness(0, 1, 0, 0),
            Foreground = Theme.Brush(Color.FromRgb(0x30, 0xD1, 0x58)),
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
        var drop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 };
        Animate(_move, TranslateTransform.YProperty, 0, 560, drop);
        Animate(_scale, ScaleTransform.ScaleXProperty, 1, 560, drop);
        Animate(_scale, ScaleTransform.ScaleYProperty, 1, 560, drop);
        Animate(_pill, OpacityProperty, 1, 160, null);
        _hold.Start();
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
        Timeline.SetDesiredFrameRate(a, 60);
        if (done is not null) a.Completed += (_, _) => done();
        target.BeginAnimation(property, a);
    }

    static void Animate(UIElement target, DependencyProperty property, double to, int ms, IEasingFunction? ease, int beginMs = 0)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(beginMs) };
        Timeline.SetDesiredFrameRate(a, 60);
        target.BeginAnimation(property, a);
    }

    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
}
