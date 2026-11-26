namespace BTBatteryIndicator.Core;

/// <summary>
/// Notices the moment a device goes from not connected to connected, for the popup at the top of the screen.
/// The first scan after start-up only records what is already connected. Hidden devices never count, and
/// neither do devices on a USB receiver: they drop off and come back every time they sleep and wake.
/// </summary>
public sealed class ConnectWatcher
{
    Dictionary<string, bool>? _last;

    public IReadOnlyList<DeviceReading> Evaluate(IReadOnlyList<DeviceReading> devices, Settings settings)
    {
        var now = devices.ToDictionary(d => d.Id, d => d.Connected);
        var previous = _last;
        _last = now;
        if (previous is null || !settings.ConnectPopup) return [];

        return devices.Where(d => d.Connected
                && !(previous.TryGetValue(d.Id, out bool was) && was)
                && !d.SleepsWhenIdle && !d.Id.StartsWith("LOGI", StringComparison.Ordinal)
                && !(settings.Devices.TryGetValue(d.Id, out var p) && p.Hidden))
            .ToList();
    }
}
