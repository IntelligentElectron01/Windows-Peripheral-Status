using System.Text.Json;

namespace BTBatteryIndicator.Core;

public sealed class DevicePrefs
{
    public string? CustomName { get; set; }
    public bool Hidden { get; set; }
    public bool Pinned { get; set; }
    /// <summary>Last name Windows reported, so hidden/disconnected devices can still be listed.</summary>
    public string? LastKnownName { get; set; }
    /// <summary>Last device type seen, so a pinned device keeps its icon while it is away.</summary>
    public DeviceKind LastKnownKind { get; set; }
}

public sealed class Settings
{
    public const int MinThreshold = 10, MaxThreshold = 50;

    public bool LowBatteryNotify { get; set; } = true;
    public int LowBatteryThreshold { get; set; } = 20;
    public bool NotifyOnDisconnect { get; set; }
    /// <summary>Show a small popup at the top of the screen when a Bluetooth device connects.</summary>
    public bool ConnectPopup { get; set; } = true;
    /// <summary>"Blue" (default) or "System" (Windows accent colour).</summary>
    public string Accent { get; set; } = "Blue";
    /// <summary>Where pinned devices show: "Taskbar" (a strip on the taskbar, default) or "Tray" (one tray icon each).</summary>
    public string PinnedDisplay { get; set; } = "Taskbar";
    /// <summary>Ask Logitech wireless devices on a USB receiver for their battery level (the one reader that sends messages).</summary>
    public bool LogitechEnabled { get; set; } = true;
    /// <summary>System-wide keyboard shortcut that opens the flyout, e.g. "Ctrl+Alt+B". Empty means none.</summary>
    public string Hotkey { get; set; } = "";
    public Dictionary<string, DevicePrefs> Devices { get; set; } = new();

    public DevicePrefs For(string id)
    {
        if (!Devices.TryGetValue(id, out var p)) Devices[id] = p = new DevicePrefs();
        return p;
    }

    public string DisplayName(string id, string windowsName)
    {
        var custom = Devices.TryGetValue(id, out var p) ? p.CustomName : null;
        return string.IsNullOrWhiteSpace(custom) ? windowsName : custom!;
    }

    public void Normalize()
    {
        LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, MinThreshold, MaxThreshold);
        if (Accent != "System") Accent = "Blue";
        if (PinnedDisplay != "Tray") PinnedDisplay = "Taskbar";
        Hotkey = Core.Hotkey.Parse(Hotkey)?.ToString() ?? "";
        Devices ??= new();
        foreach (var k in Devices.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
            Devices.Remove(k);
    }
}

/// <summary>Atomic JSON file storage that recovers from a missing or corrupt file (R12).</summary>
public static class JsonFile
{
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
            {
                var v = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Opt);
                if (v is not null) return v;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { /* keeping a copy is best-effort */ }
        }
        return new T();
    }

    public static bool Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opt));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public sealed class SettingsStore(string dir)
{
    public string PathOnDisk { get; } = Path.Combine(dir, "settings.json");

    public Settings Load()
    {
        var s = JsonFile.Load<Settings>(PathOnDisk);
        s.Normalize();
        return s;
    }

    public bool Save(Settings s) => JsonFile.Save(PathOnDisk, s);
}
