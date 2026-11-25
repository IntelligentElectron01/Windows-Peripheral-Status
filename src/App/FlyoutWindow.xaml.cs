using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BTBatteryIndicator.Core;
using WF = System.Windows.Forms;

namespace BTBatteryIndicator;

public sealed class DeviceRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Glyph { get; init; }
    /// <summary>Drawn icon used instead of the font glyph (earbuds).</summary>
    public Geometry? Icon { get; init; }
    public required string LevelText { get; init; }
    public double Level { get; init; }
    public Visibility BarVisibility { get; init; }
    public required Brush BarBrush { get; init; }
    public required Brush GlyphBrush { get; init; }
    public required Brush PinBrush { get; init; }
    public required string PinGlyph { get; init; }
    public required string PinTip { get; init; }
    public required string RowTip { get; init; }
    public double Fade { get; init; } = 1;
}

public sealed record HiddenRow(string Id, string Name);

public partial class FlyoutWindow : Window
{
    readonly AppController _c;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };
    DateTime _hiddenAt = DateTime.MinValue;
    bool _loading;

    /// <summary>Keeps the flyout open when it loses focus (used for automated screenshots).</summary>
    public bool Sticky { get; set; }

    /// <summary>Raised when the flyout hides, so the app can hand unused memory back to Windows.</summary>
    public event Action? Closed_;
    public event Action? ExitRequested;
    public event Action<string>? HistoryRequested;
    public event Action<string>? RenameRequested;

    public FlyoutWindow(AppController c)
    {
        InitializeComponent();
        _c = c;
        // While hidden there is nothing to draw, so skip the work; ShowFlyout rebuilds on open.
        _c.Updated += () => { if (IsVisible) Rebuild(); };
        Theme.Changed += () => { if (IsVisible) Rebuild(); };
        _clock.Tick += (_, _) => UpdateSyncText();
        Deactivated += (_, _) => { if (!Sticky) HideFlyout(); };
        SizeChanged += (_, _) => Place();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) HideFlyout(); };
        Rebuild();
    }

    /// <summary>Tray click: open, or close if it was open a moment ago (the click itself deactivates it first).</summary>
    public void Toggle()
    {
        if (IsVisible) { HideFlyout(); return; }
        if ((DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 300) return;
        ShowFlyout();
    }

    public void ShowFlyout()
    {
        Rebuild();
        Show();
        UpdateLayout();
        Place();
        Activate();
        _clock.Start();
        _ = _c.RefreshAsync();
    }

    void HideFlyout()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.UtcNow;
        _clock.Stop();
        Hide();
        DeviceList.ItemsSource = null; // let the row visuals be freed while hidden
        Closed_?.Invoke();
    }

    /// <summary>Sits the flyout in the corner of the screen next to the taskbar, wherever the taskbar is.</summary>
    void Place()
    {
        if (!IsVisible) return;
        var screen = WF.Screen.FromPoint(WF.Cursor.Position);
        var wa = screen.WorkingArea;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // With an auto-hide taskbar Windows reports the whole screen as free, so also step clear of the
        // taskbar itself while it is showing.
        int freeTop = wa.Top, freeBottom = wa.Bottom;
        if (TaskbarStrip.VisibleTaskbar() is { } bar && bar.Width > bar.Height && screen.Bounds.IntersectsWith(bar))
        {
            if (bar.Top > screen.Bounds.Top + screen.Bounds.Height / 2) freeBottom = Math.Min(freeBottom, bar.Top);
            else freeTop = Math.Max(freeTop, bar.Bottom);
        }
        double left = wa.Right / s - ActualWidth;
        double top = freeBottom / s - ActualHeight;
        if (wa.Left > screen.Bounds.Left) left = wa.Left / s;      // taskbar on the left
        if (freeTop > screen.Bounds.Top) top = freeTop / s;        // taskbar on the top
        Left = left;
        Top = top;
    }

    public void Rebuild()
    {
        _loading = true;
        var s = _c.Settings;
        var rows = _c.Visible.Select(MakeRow).ToList();
        DeviceList.ItemsSource = rows;

        if (rows.Count == 0)
        {
            EmptyText.Text = _c.Error ?? (_c.LastSync is null
                ? "Looking for Bluetooth devices…"
                : "No paired Bluetooth devices found. Check that Bluetooth is turned on and a device is paired in Windows Settings.");
            EmptyText.Visibility = Visibility.Visible;
        }
        else if (_c.Error is not null)
        {
            EmptyText.Text = _c.Error;
            EmptyText.Visibility = Visibility.Visible;
        }
        else EmptyText.Visibility = Visibility.Collapsed;

        var hidden = s.Devices.Where(kv => kv.Value.Hidden)
            .Select(kv => new HiddenRow(kv.Key, s.DisplayName(kv.Key, kv.Value.LastKnownName ?? "Bluetooth device")))
            .OrderBy(h => h.Name).ToList();
        HiddenList.ItemsSource = hidden;
        HiddenPanel.Visibility = hidden.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        StartToggle.IsChecked = _c.StartWithWindows;
        DisconnectToggle.IsChecked = s.NotifyOnDisconnect;
        LowToggle.IsChecked = s.LowBatteryNotify;
        ThresholdSlider.Value = s.LowBatteryThreshold;
        ThresholdSlider.IsEnabled = s.LowBatteryNotify;
        ThresholdSlider.Opacity = s.LowBatteryNotify ? 1 : 0.4;
        ThresholdText.Text = $"Notify at {s.LowBatteryThreshold}%";
        AccentBlue.IsChecked = s.Accent != "System";
        AccentSystem.IsChecked = s.Accent == "System";
        LogitechToggle.IsChecked = s.LogitechEnabled;
        PinTaskbar.IsChecked = s.PinnedDisplay != "Tray";
        PinTray.IsChecked = s.PinnedDisplay == "Tray";
        UpdateSyncText();
        _loading = false;
    }

    DeviceRow MakeRow(DeviceReading d)
    {
        bool pinned = _c.Settings.For(d.Id).Pinned;
        bool live = d.Connected && d.Battery is not null;
        var band = live ? Levels.Band(d.Battery) : LevelBand.Unknown;
        var levelBrush = Theme.Brush(Theme.BandColor(band));
        string name = _c.NameOf(d);
        return new DeviceRow
        {
            Id = d.Id,
            Name = name,
            Glyph = d is { Connected: true, Pods: not null } ? "" : GlyphFor(d.Kind),
            Icon = d is { Connected: true, Pods: { } shown } ? PodIcons.For(shown.View) : null,
            LevelText = !d.Connected ? "Not connected" : d.Pods is { } pods ? pods.Short()
                      : d.Charging && d.Battery is not null ? $"⚡ {d.Battery}%" : Levels.Text(d.Battery),
            Level = live ? d.Battery!.Value : 0,
            BarVisibility = live ? Visibility.Visible : Visibility.Collapsed,
            BarBrush = levelBrush,
            GlyphBrush = pinned && live ? levelBrush : Theme.Res("Fg2"),
            PinBrush = pinned ? Theme.Res("Accent") : Theme.Res("Fg2"),
            PinGlyph = pinned ? "" : "",
            PinTip = pinned ? "Unpin" : _c.Settings.PinnedDisplay == "Tray" ? "Pin to the tray as its own icon" : "Pin to the taskbar",
            RowTip = d is { Connected: true, Pods: { } p } ? $"{name}: {p.Describe()}\nClick for battery history"
                   : $"{name}{(d is { Connected: true, Charging: true } ? " (charging)" : "")} — click for battery history",
            Fade = d.Connected ? 1 : 0.55,
        };
    }

    public static string GlyphFor(DeviceKind k) => k switch
    {
        DeviceKind.Mouse => "",
        DeviceKind.Keyboard => "",
        DeviceKind.Headphones => "",
        DeviceKind.Speaker => "",
        DeviceKind.Gamepad => "",
        DeviceKind.Phone => "",
        _ => "",
    };

    void UpdateSyncText() => SyncText.Text = "Last synced: " + Ago(_c.LastSync);

    public static string Ago(DateTime? utc)
    {
        if (utc is not DateTime t) return "not yet";
        double sec = (DateTime.UtcNow - t).TotalSeconds;
        if (sec < 60) return "a few seconds ago";
        if (sec < 120) return "a minute ago";
        if (sec < 3600) return $"{(int)(sec / 60)} minutes ago";
        return t.ToLocalTime().ToString("t");
    }

    // ---- handlers ----------------------------------------------------------------------------

    async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        await _c.RefreshAsync();
        RefreshButton.IsEnabled = true;
    }

    void Settings_Click(object sender, RoutedEventArgs e) =>
        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    public void ShowSettings(bool show) => SettingsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        string id = (string)((FrameworkElement)sender).Tag;
        _c.Change(s => s.For(id).Pinned = !s.For(id).Pinned);
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        string id = (string)button.Tag;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(Item("Battery history", () => HistoryRequested?.Invoke(id)));
        menu.Items.Add(Item("Rename", () => RenameRequested?.Invoke(id)));
        menu.Items.Add(Item("Hide", () => _c.Change(s => s.For(id).Hidden = true)));
        menu.IsOpen = true;
    }

    static MenuItem Item(string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        return mi;
    }

    void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject o && FindParent<Button>(o) is not null) return;
        HistoryRequested?.Invoke((string)((FrameworkElement)sender).Tag);
    }

    static T? FindParent<T>(DependencyObject? o) where T : DependencyObject
    {
        while (o is not null and not T) o = o is Visual ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o);
        return o as T;
    }

    void Unhide_Click(object sender, RoutedEventArgs e)
    {
        string id = (string)((FrameworkElement)sender).Tag;
        _c.Change(s => s.For(id).Hidden = false);
    }

    void StartToggle_Click(object sender, RoutedEventArgs e) => _c.StartWithWindows = StartToggle.IsChecked == true;

    void DisconnectToggle_Click(object sender, RoutedEventArgs e) =>
        _c.Change(s => s.NotifyOnDisconnect = DisconnectToggle.IsChecked == true);

    void LowToggle_Click(object sender, RoutedEventArgs e) =>
        _c.Change(s => s.LowBatteryNotify = LowToggle.IsChecked == true);

    void Threshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsInitialized) return;
        int v = (int)Math.Round(e.NewValue);
        if (v != _c.Settings.LowBatteryThreshold) _c.Change(s => s.LowBatteryThreshold = v);
    }

    void Accent_Click(object sender, RoutedEventArgs e)
    {
        string mode = AccentSystem.IsChecked == true ? "System" : "Blue";
        _c.Change(s => s.Accent = mode);
        Theme.Apply(mode);
    }

    async void LogitechToggle_Click(object sender, RoutedEventArgs e)
    {
        _c.Change(s => s.LogitechEnabled = LogitechToggle.IsChecked == true);
        await _c.RefreshAsync();
    }

    void PinMode_Click(object sender, RoutedEventArgs e)
    {
        string mode = PinTray.IsChecked == true ? "Tray" : "Taskbar";
        _c.Change(s => s.PinnedDisplay = mode);
    }

    void Exit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
}
