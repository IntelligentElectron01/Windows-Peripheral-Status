using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WirelessDeviceMonitor.Core;

namespace WirelessDeviceMonitor;

/// <summary>R7: seven-day battery chart with a time-left estimate for one device.</summary>
public sealed class HistoryWindow : Window
{
    readonly AppController _c;
    readonly string _id;
    readonly TextBlock _headline = new() { FontSize = 20, FontWeight = FontWeights.SemiBold };
    readonly TextBlock _estimate = new() { FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    readonly ChartControl _chart = new() { Margin = new Thickness(0, 16, 0, 0) };

    public string DeviceId => _id;

    public HistoryWindow(AppController c, string id)
    {
        _c = c;
        _id = id;
        Width = 620; Height = 400; MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        _estimate.SetResourceReference(TextBlock.ForegroundProperty, "Fg2");
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Fg2");

        var caption = new TextBlock { Text = "Last 7 days", FontSize = 12, Margin = new Thickness(0, 14, 0, 0) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Fg2");

        var top = new StackPanel();
        top.Children.Add(_headline);
        top.Children.Add(_estimate);
        top.Children.Add(caption);
        top.Children.Add(_empty);
        var dock = new DockPanel { Margin = new Thickness(20, 16, 20, 16) };
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_chart);
        Content = dock;

        _c.Updated += Refresh;
        Theme.Changed += Refresh;
        Closed += (_, _) => { _c.Updated -= Refresh; Theme.Changed -= Refresh; };
        SourceInitialized += (_, _) => TitleBar.Apply(this);
        Refresh();
    }

    void Refresh()
    {
        var d = _c.Devices.FirstOrDefault(x => x.Id == _id);
        string name = _c.Settings.DisplayName(_id, d?.Name ?? _c.Settings.For(_id).LastKnownName ?? "Bluetooth device");
        Title = $"{name} — battery history";
        var samples = _c.History.Get(_id);
        bool live = d is { Connected: true, Battery: not null };

        _headline.Text = live ? $"{name}: {d!.Battery}%"
            : d is { Connected: true } ? $"{name}: no battery info"
            : $"{name}: not connected";
        _estimate.Text = live
            ? Estimator.Describe(Estimator.TimeLeft(samples, DateTime.UtcNow)) + EstimateNote(samples)
            : samples.Count > 0 ? $"Last reading: {samples[^1].L}% at {samples[^1].T.ToLocalTime():g}" : "";
        _empty.Text = "No history yet. Levels are recorded while the device is connected and this app is running.";
        _empty.Visibility = samples.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _chart.Samples = samples.ToList();
        _chart.Threshold = _c.Settings.LowBatteryNotify ? _c.Settings.LowBatteryThreshold : null;
        _chart.InvalidateVisual();
        TitleBar.Apply(this);
    }

    static string EstimateNote(IReadOnlyList<Sample> samples) =>
        Estimator.TimeLeft(samples, DateTime.UtcNow) is null ? "" : " (based on how fast it has drained since the last charge)";
}

/// <summary>Makes a window's title bar dark when the app is in dark mode. Purely cosmetic and local.</summary>
public static class TitleBar
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = Theme.Dark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, 20 /* immersive dark mode */, ref dark, sizeof(int)); }
        catch { /* older Windows 10 builds: title bar simply stays light */ }
    }
}

public sealed class ChartControl : FrameworkElement
{
    public List<Sample> Samples { get; set; } = [];
    public int? Threshold { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        const double L = 40, R = 8, T = 8, B = 24;
        double pw = w - L - R, ph = h - T - B;
        if (pw < 40 || ph < 40) return;

        var now = DateTime.UtcNow;
        var start = now - HistoryStore.Keep;
        double X(DateTime t) => L + Math.Clamp((t - start).TotalSeconds / HistoryStore.Keep.TotalSeconds, 0, 1) * pw;
        double Y(double level) => T + (1 - level / 100.0) * ph;

        var fg2 = Theme.Res("Fg2");
        var gridPen = new Pen(Theme.Res("Stroke"), 1);
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface("Segoe UI");
        FormattedText Text(string s) =>
            new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 11, fg2, ppd);

        foreach (int level in new[] { 0, 25, 50, 75, 100 })
        {
            double y = Math.Round(Y(level)) + 0.5;
            dc.DrawLine(gridPen, new Point(L, y), new Point(L + pw, y));
            var ft = Text(level + "%");
            dc.DrawText(ft, new Point(L - ft.Width - 6, y - ft.Height / 2));
        }

        // A tick and weekday label at each local midnight.
        var localStart = start.ToLocalTime();
        for (var day = localStart.Date.AddDays(1); day <= now.ToLocalTime(); day = day.AddDays(1))
        {
            double x = Math.Round(X(day.ToUniversalTime())) + 0.5;
            dc.DrawLine(gridPen, new Point(x, T), new Point(x, T + ph));
            var ft = Text(day.ToString("ddd"));
            if (x + 4 + ft.Width < L + pw) dc.DrawText(ft, new Point(x + 4, T + ph + 5));
        }

        if (Threshold is int th)
        {
            var pen = new Pen(Theme.Brush(Theme.Red), 1) { DashStyle = new DashStyle([4, 4], 0) };
            double y = Math.Round(Y(th)) + 0.5;
            dc.DrawLine(pen, new Point(L, y), new Point(L + pw, y));
        }

        var pts = Samples.Where(s => s.T >= start && s.T <= now.AddMinutes(1)).OrderBy(s => s.T).ToList();
        if (pts.Count == 0) return;

        var accent = Theme.Accent;
        var linePen = new Pen(Theme.Brush(accent), 2) { LineJoin = PenLineJoin.Round };
        var fill = Theme.Brush(Color.FromArgb(40, accent.R, accent.G, accent.B));

        // Split into runs wherever there is a gap (device off or app not running), so gaps are not drawn as data.
        var run = new List<Sample>();
        void Flush()
        {
            if (run.Count == 0) return;
            if (run.Count == 1)
            {
                dc.DrawEllipse(linePen.Brush, null, new Point(X(run[0].T), Y(run[0].L)), 2.5, 2.5);
            }
            else
            {
                var line = new StreamGeometry();
                using (var g = line.Open())
                {
                    g.BeginFigure(new Point(X(run[0].T), Y(run[0].L)), false, false);
                    foreach (var s in run.Skip(1)) g.LineTo(new Point(X(s.T), Y(s.L)), true, true);
                }
                var area = new StreamGeometry();
                using (var g = area.Open())
                {
                    g.BeginFigure(new Point(X(run[0].T), Y(0)), true, true);
                    foreach (var s in run) g.LineTo(new Point(X(s.T), Y(s.L)), false, false);
                    g.LineTo(new Point(X(run[^1].T), Y(0)), false, false);
                }
                dc.DrawGeometry(fill, null, area);
                dc.DrawGeometry(null, linePen, line);
            }
            run = [];
        }
        foreach (var s in pts)
        {
            if (run.Count > 0 && s.T - run[^1].T > TimeSpan.FromHours(3)) Flush();
            run.Add(s);
        }
        Flush();
    }
}

/// <summary>R8: small dialog to give a device your own name.</summary>
public sealed class RenameWindow : Window
{
    public RenameWindow(AppController c, string id)
    {
        var prefs = c.Settings.For(id);
        string original = c.Devices.FirstOrDefault(d => d.Id == id)?.Name ?? prefs.LastKnownName ?? "Bluetooth device";
        Title = "Rename device";
        Width = 380; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true; ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 13; UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");

        var label = new TextBlock { Text = $"Name shown for “{original}”", TextWrapping = TextWrapping.Wrap };
        var box = new TextBox { Text = c.Settings.DisplayName(id, original), MaxLength = 40, Margin = new Thickness(0, 8, 0, 0),
                                Style = (Style)Application.Current.Resources["Field"] };
        var hint = new TextBlock { Text = "Only changes the name inside this app. Leave empty to use the original name.",
                                   TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Fg2");

        var std = (Style)Application.Current.Resources["StdButton"];
        var save = new Button { Content = "Save", Style = std, IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", Style = std, IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        var reset = new Button { Content = "Use original", Style = std, HorizontalAlignment = HorizontalAlignment.Left };
        void Commit(string? name)
        {
            c.Change(s => s.For(id).CustomName = string.IsNullOrWhiteSpace(name) || name.Trim() == original ? null : name.Trim());
            Close();
        }
        save.Click += (_, _) => Commit(box.Text);
        reset.Click += (_, _) => Commit(null);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(save);
        right.Children.Add(cancel);
        var buttons = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(reset);
        buttons.Children.Add(right);

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        panel.Children.Add(label);
        panel.Children.Add(box);
        panel.Children.Add(hint);
        panel.Children.Add(buttons);
        Content = panel;

        SourceInitialized += (_, _) => TitleBar.Apply(this);
        Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }
}
