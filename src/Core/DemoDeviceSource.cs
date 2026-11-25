namespace BTBatteryIndicator.Core;

/// <summary>Made-up devices for --demo mode and tests, so the UI can be tried without real hardware.</summary>
public sealed class DemoDeviceSource : IDeviceSource
{
    readonly List<DeviceReading> _devices =
    [
        new("DEMO00000001", "Demo Keyboard", DeviceKind.Keyboard, true, 76),
        new("DEMO00000002", "Demo Mouse", DeviceKind.Mouse, true, 55),
        new("DEMO00000003", "Demo Headphones", DeviceKind.Headphones, true, 24),
        new("DEMO00000004", "Demo Speaker", DeviceKind.Speaker, true, null),
        new("DEMO00000005", "Demo Earbuds", DeviceKind.Headphones, true, 70) { Pods = new PodLevels(0, 70, 80, 40, CaseCharging: true) },
    ];

    public IReadOnlyList<DeviceReading> Scan() { lock (_devices) return _devices.ToList(); }

    /// <summary>Lowers every reported level, to try the colour changes and low-battery alert.</summary>
    public void Drain(int points)
    {
        lock (_devices)
            for (int i = 0; i < _devices.Count; i++)
                if (_devices[i].Battery is int b)
                    _devices[i] = _devices[i] with { Battery = Math.Max(0, b - points) };
    }

    public void Recharge()
    {
        lock (_devices)
            for (int i = 0; i < _devices.Count; i++)
                if (_devices[i].Battery is not null)
                    _devices[i] = _devices[i] with { Battery = 100 };
    }

    public void ToggleConnected(string id)
    {
        lock (_devices)
            for (int i = 0; i < _devices.Count; i++)
                if (_devices[i].Id == id)
                    _devices[i] = _devices[i] with { Connected = !_devices[i].Connected };
    }

    /// <summary>Seven days of plausible history so the chart has something to show.</summary>
    public void SeedHistory(HistoryStore h, DateTime utcNow)
    {
        foreach (var d in Scan())
        {
            if (d.Battery is not int now || h.Get(d.Id).Count > 0) continue;
            double perHour = d.Kind == DeviceKind.Headphones ? 2.2 : 0.35;
            double level = now + perHour * 80;
            for (int hoursAgo = 160; hoursAgo >= 0; hoursAgo -= 2)
            {
                level -= perHour * 2;
                if (hoursAgo == 80) level = Math.Max(level, 100); // one recharge mid-week
                if (level < 5) level = 100;
                int v = hoursAgo == 0 ? now : (int)Math.Clamp(Math.Round(hoursAgo < 80 ? now + perHour * hoursAgo : level), 1, 100);
                h.Record(d.Id, v, utcNow.AddHours(-hoursAgo));
            }
        }
    }
}
