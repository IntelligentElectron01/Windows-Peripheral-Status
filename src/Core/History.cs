namespace WirelessDeviceMonitor.Core;

public sealed class Sample
{
    public DateTime T { get; set; }   // UTC
    public int L { get; set; }        // level 0-100
}

public sealed class HistoryData
{
    public Dictionary<string, List<Sample>> Devices { get; set; } = new();
}

/// <summary>R7: keeps the last 7 days of battery samples per device.</summary>
public sealed class HistoryStore(string dir)
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(30);

    readonly string _path = Path.Combine(dir, "history.json");
    HistoryData _data = new();
    bool _dirty;

    public void Load()
    {
        _data = JsonFile.Load<HistoryData>(_path);
        _data.Devices ??= new();
        foreach (var k in _data.Devices.Keys.ToList())
            _data.Devices[k] = (_data.Devices[k] ?? new()).Where(s => s is not null && s.L is >= 0 and <= 100)
                .OrderBy(s => s.T).ToList();
    }

    /// <summary>Stores a sample when the level changed or 30 minutes passed. Returns true if stored.</summary>
    public bool Record(string id, int level, DateTime utcNow)
    {
        if (!_data.Devices.TryGetValue(id, out var list)) _data.Devices[id] = list = new();
        var last = list.Count > 0 ? list[^1] : null;
        if (last is not null && last.L == level && utcNow - last.T < Heartbeat) return false;
        if (last is not null && utcNow < last.T) return false; // clock went backwards; ignore
        list.Add(new Sample { T = utcNow, L = level });
        _dirty = true;
        return true;
    }

    public void Prune(DateTime utcNow)
    {
        var cutoff = utcNow - Keep;
        foreach (var (id, list) in _data.Devices.ToList())
        {
            int removed = list.RemoveAll(s => s.T < cutoff);
            if (removed > 0) _dirty = true;
            if (list.Count == 0) { _data.Devices.Remove(id); _dirty = true; }
        }
    }

    public IReadOnlyList<Sample> Get(string id) =>
        _data.Devices.TryGetValue(id, out var l) ? l : Array.Empty<Sample>();

    public void SaveIfDirty()
    {
        if (_dirty && JsonFile.Save(_path, _data)) _dirty = false;
    }
}

public static class Estimator
{
    /// <summary>
    /// Time left based on the current discharge run: the samples since the level last rose by 3+ points
    /// (a charge). Needs at least one hour of data and a real drop; otherwise returns null.
    /// </summary>
    public static TimeSpan? TimeLeft(IReadOnlyList<Sample> samples, DateTime utcNow)
    {
        if (samples.Count < 2) return null;
        int start = 0;
        for (int i = 1; i < samples.Count; i++)
            if (samples[i].L - samples[i - 1].L >= 3) start = i;
        var first = samples[start];
        var last = samples[^1];
        double hours = (last.T - first.T).TotalHours;
        int drop = first.L - last.L;
        if (hours < 1 || drop < 1 || last.L <= 0) return null;
        double perHour = drop / hours;
        double left = last.L / perHour - (utcNow - last.T).TotalHours;
        if (double.IsNaN(left) || left <= 0) return null;
        return TimeSpan.FromHours(Math.Min(left, 24 * 365));
    }

    public static string Describe(TimeSpan? left)
    {
        if (left is not TimeSpan t) return "Not enough data for an estimate yet";
        if (t.TotalDays >= 2) return $"About {(int)Math.Round(t.TotalDays)} days left";
        if (t.TotalHours >= 2) return $"About {(int)Math.Round(t.TotalHours)} hours left";
        return t.TotalMinutes >= 60 ? "About 1 hour left" : $"About {Math.Max(1, (int)t.TotalMinutes)} minutes left";
    }
}
