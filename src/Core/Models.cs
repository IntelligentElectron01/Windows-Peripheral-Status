namespace WirelessDeviceMonitor.Core;

public enum DeviceKind { Generic, Mouse, Keyboard, Headphones, Speaker, Gamepad, Phone }

/// <summary>One paired Bluetooth device as read from Windows. Id is the Bluetooth address.</summary>
public sealed record DeviceReading(string Id, string Name, DeviceKind Kind, bool Connected, int? Battery)
{
    /// <summary>Apple/Beats product number when the device is one, used to match its battery broadcast.</summary>
    public int? AppleModel { get; init; }
    /// <summary>Per-bud and case levels, when they came from an earbud broadcast rather than from Windows.</summary>
    public PodLevels? Pods { get; init; }
    /// <summary>True when the device says it is on charge (only some device types report this).</summary>
    public bool Charging { get; init; }
    /// <summary>True for devices that go quiet to save power when idle (receiver mice and keyboards). Silence from these is not a disconnect.</summary>
    public bool SleepsWhenIdle { get; init; }
}

public interface IDeviceSource
{
    /// <summary>Read-only snapshot of paired devices. Must never change device state.</summary>
    IReadOnlyList<DeviceReading> Scan();
}

public enum LevelBand { Normal, Yellow, Red, Unknown }

public static class Levels
{
    /// <summary>R3: yellow below 50%, red below 20%.</summary>
    public static LevelBand Band(int? level) => level switch
    {
        null => LevelBand.Unknown,
        < 20 => LevelBand.Red,
        < 50 => LevelBand.Yellow,
        _ => LevelBand.Normal,
    };

    public static string Text(int? level) => level is int v ? $"{v}%" : "No battery info";
}

public static class Tooltip
{
    /// <summary>R4: one line per connected, visible device. Windows caps tray tooltips at 127 chars.</summary>
    public static string Build(IEnumerable<(string Name, int? Battery)> connected, int max = 127)
    {
        var lines = connected.Select(d => $"{d.Name}: {Levels.Text(d.Battery)}").ToList();
        if (lines.Count == 0) return "No Bluetooth devices connected";
        var text = string.Join("\n", lines);
        if (text.Length <= max) return text;
        // Shorten names evenly before dropping whole devices.
        int perLine = Math.Max(12, (max - lines.Count) / lines.Count);
        lines = connected.Select(d =>
        {
            var suffix = ": " + (d.Battery is int v ? $"{v}%" : "n/a");
            int room = Math.Max(3, perLine - suffix.Length);
            var name = d.Name.Length > room ? d.Name[..(room - 1)] + "…" : d.Name;
            return name + suffix;
        }).ToList();
        text = string.Join("\n", lines);
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
