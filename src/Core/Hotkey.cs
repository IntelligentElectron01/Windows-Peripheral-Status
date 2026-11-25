namespace BTBatteryIndicator.Core;

/// <summary>
/// A keyboard shortcut such as "Ctrl+Alt+B", stored as text in settings. The key part uses Windows key names
/// ("B", "F9", "D1" for the 1 key). A shortcut must include Ctrl, Alt or Win so it cannot swallow normal typing.
/// </summary>
public sealed record Hotkey(bool Ctrl, bool Alt, bool Shift, bool Win, string Key)
{
    static readonly HashSet<string> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ctrl", "Control", "Alt", "Shift", "Win", "Windows", "LeftCtrl", "RightCtrl", "LeftAlt", "RightAlt",
        "LeftShift", "RightShift", "LWin", "RWin", "System", "None",
    };

    public bool IsValid => Key.Length > 0 && !ModifierNames.Contains(Key) && (Ctrl || Alt || Win);

    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        bool ctrl = false, alt = false, shift = false, win = false;
        string key = "";
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "windows": win = true; break;
                default:
                    if (key.Length > 0) return null;   // two non-modifier keys is not a shortcut
                    key = raw;
                    break;
            }
        }
        var h = new Hotkey(ctrl, alt, shift, win, key);
        return h.IsValid ? h : null;
    }

    /// <summary>The form saved in settings.</summary>
    public override string ToString() => Join(Key);

    /// <summary>The form shown to the user: "D1" reads as "1", "OemComma" as ",", and so on.</summary>
    public string Display() => Join(Key switch
    {
        ['D', >= '0' and <= '9'] => Key[1..],
        ['N', 'u', 'm', 'P', 'a', 'd', >= '0' and <= '9'] => "Num " + Key[6..],
        "OemComma" => ",", "OemPeriod" => ".", "OemMinus" => "-", "OemPlus" => "=", "OemTilde" => "`",
        "Oem1" or "OemSemicolon" => ";", "Oem2" or "OemQuestion" => "/", "Oem5" or "OemPipe" => "\\",
        "Oem4" or "OemOpenBrackets" => "[", "Oem6" or "OemCloseBrackets" => "]", "Oem7" or "OemQuotes" => "'",
        "Return" => "Enter", "Next" => "Page Down", "Prior" => "Page Up", "Capital" => "Caps Lock",
        _ => Key,
    });

    string Join(string key) => string.Join("+", new[] { Ctrl ? "Ctrl" : null, Alt ? "Alt" : null, Shift ? "Shift" : null, Win ? "Win" : null, key }
        .Where(p => p is not null));
}
