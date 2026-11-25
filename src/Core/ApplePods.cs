namespace BTBatteryIndicator.Core;

/// <summary>Battery levels broadcast by Apple/Beats earbuds. Null means "not reported right now".</summary>
public sealed record PodLevels(int Model, int? Left, int? Right, int? Case,
    bool LeftCharging = false, bool RightCharging = false, bool CaseCharging = false)
{
    /// <summary>The single number used for the tray, alerts and history: the lower of the two buds.</summary>
    public int? Lowest => (Left, Right) switch
    {
        (int l, int r) => Math.Min(l, r),
        (int l, null) => l,
        (null, int r) => r,
        _ => null,
    };

    public string Describe()
    {
        var parts = new List<string>();
        if (Left is int l) parts.Add($"Left {l}%{(LeftCharging ? " (charging)" : "")}");
        if (Right is int r) parts.Add($"Right {r}%{(RightCharging ? " (charging)" : "")}");
        if (Case is int c) parts.Add($"Case {c}%{(CaseCharging ? " (charging)" : "")}");
        return string.Join(" · ", parts);
    }

    public string Short()
    {
        var parts = new List<string>();
        if (Left is int l) parts.Add($"L {l}%");
        if (Right is int r) parts.Add($"R {r}%");
        return parts.Count > 0 ? string.Join(" · ", parts) : Case is int c ? $"Case {c}%" : "No battery info";
    }
}

/// <summary>
/// Decodes the "proximity pairing" broadcast that Apple and Beats earbuds send. Apple does not publish this
/// format; the layout below is the commonly observed one and levels come in steps of 10.
/// </summary>
public static class ApplePods
{
    public const ushort AppleCompanyId = 0x004C;

    /// <param name="data">Manufacturer data after the two company-ID bytes.</param>
    public static PodLevels? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || data[0] != 0x07 || data[1] != 0x19) return null;
        int model = data[3] | (data[4] << 8);
        bool flipped = (data[5] & 0x20) == 0;      // which bud is currently the "primary" one
        int hi = data[6] >> 4, lo = data[6] & 0x0F;
        int charge = data[7] >> 4;
        return new PodLevels(model,
            Level(flipped ? hi : lo),
            Level(flipped ? lo : hi),
            Level(data[7] & 0x0F),
            (charge & (flipped ? 0b0010 : 0b0001)) != 0,
            (charge & (flipped ? 0b0001 : 0b0010)) != 0,
            (charge & 0b0100) != 0);
    }

    static int? Level(int nibble) => nibble is >= 0 and <= 10 ? nibble * 10 : null;
}

public interface IPodsListener
{
    /// <summary>Listens passively for earbud broadcasts for a short time. Sends nothing; never connects to a device.</summary>
    Task<IReadOnlyList<(PodLevels Levels, int Rssi)>> ListenAsync(TimeSpan duration);
}

/// <summary>Chooses which broadcast belongs to the user's earbuds and remembers the last known levels.</summary>
public sealed class PodsTracker
{
    /// <summary>Weaker signals than this are probably someone else's earbuds across the room.</summary>
    public const int MinRssi = -72;
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(20);

    readonly Dictionary<int, (PodLevels Levels, DateTime At)> _current = new();

    /// <summary>Feeds in one listening burst. Returns true if anything changed.</summary>
    public bool Update(IEnumerable<(PodLevels Levels, int Rssi)> heard, DateTime utcNow)
    {
        bool changed = false;
        foreach (var g in heard.Where(h => h.Rssi >= MinRssi).GroupBy(h => h.Levels.Model))
        {
            // Same model heard several times: the strongest signal is the closest pair.
            var best = g.OrderByDescending(h => h.Rssi).First().Levels;
            if (_current.TryGetValue(g.Key, out var old) && utcNow - old.At < MaxAge)
                best = best with   // a bud in the closed case reports nothing; keep its last known value
                {
                    Left = best.Left ?? old.Levels.Left,
                    Right = best.Right ?? old.Levels.Right,
                    Case = best.Case ?? old.Levels.Case,
                };
            if (!_current.TryGetValue(g.Key, out var prev) || prev.Levels != best) changed = true;
            _current[g.Key] = (best, utcNow);
        }
        return changed;
    }

    public PodLevels? Get(int model, DateTime utcNow) =>
        _current.TryGetValue(model, out var c) && utcNow - c.At < MaxAge ? c.Levels : null;

    /// <summary>Most recently heard earbuds of any model (used when the product number can't be matched).</summary>
    public PodLevels? GetAny(DateTime utcNow) => _current.Values
        .Where(c => utcNow - c.At < MaxAge).OrderByDescending(c => c.At).Select(c => c.Levels).FirstOrDefault();

    /// <summary>True if a connected Apple audio device has no level from Windows, i.e. listening is worthwhile.</summary>
    public static bool Wanted(IEnumerable<DeviceReading> devices) => devices.Any(NeedsPods);

    static bool NeedsPods(DeviceReading d) =>
        d is { Connected: true, Battery: null, AppleModel: not null, Kind: DeviceKind.Headphones or DeviceKind.Generic };

    /// <summary>Fills in levels for connected Apple earbuds that Windows has no level for.</summary>
    public IReadOnlyList<DeviceReading> Enrich(IReadOnlyList<DeviceReading> devices, DateTime utcNow)
    {
        var need = devices.Where(NeedsPods).ToList();
        if (need.Count == 0) return devices;
        return devices.Select(d =>
        {
            if (!NeedsPods(d)) return d;
            // Exact product match first; if there is only one candidate device, accept whatever earbuds were heard.
            var pods = Get(d.AppleModel!.Value, utcNow) ?? (need.Count == 1 ? GetAny(utcNow) : null);
            return pods?.Lowest is int level ? d with { Battery = level, Pods = pods } : d;
        }).ToList();
    }
}
