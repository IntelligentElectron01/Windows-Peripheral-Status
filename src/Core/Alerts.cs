namespace WirelessDeviceMonitor.Core;

public enum AlertKind { LowBattery, Disconnected, Info }

public sealed record Alert(AlertKind Kind, string DeviceId, string Title, string Message);

/// <summary>
/// R5: one low-battery alert per downward crossing of the threshold.
/// R6: one alert when a pinned device goes from connected to disconnected.
/// </summary>
public sealed class AlertEngine
{
    /// <summary>Level must climb this far above the threshold before a new alert can fire (stops flicker at the edge).</summary>
    public const int Hysteresis = 2;

    readonly HashSet<string> _lowNotified = new();
    readonly Dictionary<string, bool> _wasConnected = new();
    readonly HashSet<string> _sleepers = new(); // devices that go quiet when idle; their silence is not a disconnect

    /// <summary>The user is disconnecting this device on purpose, so its next drop is not worth a notification.</summary>
    public void ExpectDisconnect(string id) => _wasConnected[id] = false;

    public IReadOnlyList<Alert> Evaluate(IReadOnlyList<DeviceReading> devices, Settings s)
    {
        var alerts = new List<Alert>();
        foreach (var d in devices)
        {
            var prefs = s.Devices.TryGetValue(d.Id, out var p) ? p : null;
            bool hidden = prefs?.Hidden == true;
            string name = s.DisplayName(d.Id, d.Name);

            if (d.Connected && d.Battery is int level)
            {
                if (level <= s.LowBatteryThreshold)
                {
                    if (_lowNotified.Add(d.Id) && s.LowBatteryNotify && !hidden)
                        alerts.Add(new Alert(AlertKind.LowBattery, d.Id, $"{name} battery low", $"{level}% remaining."));
                }
                else if (level > s.LowBatteryThreshold + Hysteresis)
                {
                    _lowNotified.Remove(d.Id);
                }
            }

            if (d.SleepsWhenIdle) _sleepers.Add(d.Id);
            if (_wasConnected.TryGetValue(d.Id, out bool was) && was && !d.Connected && !d.SleepsWhenIdle
                && s.NotifyOnDisconnect && prefs?.Pinned == true && !hidden)
                alerts.Add(new Alert(AlertKind.Disconnected, d.Id, $"{name} disconnected", "The pinned device is no longer connected."));
            _wasConnected[d.Id] = d.Connected;
        }

        // A device that vanished entirely (e.g. Bluetooth switched off) also counts as disconnected.
        var present = devices.Select(d => d.Id).ToHashSet();
        foreach (var id in _wasConnected.Keys.Where(k => !present.Contains(k)).ToList())
        {
            if (_wasConnected[id] && !_sleepers.Contains(id) && s.NotifyOnDisconnect && s.Devices.TryGetValue(id, out var p) && p.Pinned && !p.Hidden)
                alerts.Add(new Alert(AlertKind.Disconnected, id,
                    $"{s.DisplayName(id, p.LastKnownName ?? "Device")} disconnected", "The pinned device is no longer connected."));
            _wasConnected[id] = false;
        }
        return alerts;
    }
}
