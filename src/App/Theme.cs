using System.Windows;
using System.Windows.Media;
using BTBatteryIndicator.Core;
using Microsoft.Win32;

namespace BTBatteryIndicator;

/// <summary>Owns every colour in the app and keeps them in step with the Windows light/dark setting (R10).</summary>
public static class Theme
{
    public static bool Dark { get; private set; }
    public static Color Accent { get; private set; }
    public static Color OnAccent { get; private set; }
    public static Color Yellow { get; private set; }
    public static Color Red { get; private set; }
    public static Color Grey { get; private set; }
    public static bool? ForceDark { get; set; } // used only for automated screenshots

    public static event Action? Changed;

    public static void Apply(string accentMode)
    {
        Dark = ForceDark ?? ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme") == 0;

        Accent = accentMode == "System" ? SystemAccent() : (Dark ? C("#4AA3F0") : C("#1667C4"));
        OnAccent = Luminance(Accent) > 0.5 ? Colors.Black : Colors.White;
        Yellow = Dark ? C("#F2C037") : C("#D69A00");
        Red = Dark ? C("#FF6B6B") : C("#D13438");
        Grey = Dark ? C("#8A8A8A") : C("#7A7A7A");

        Set("Bg", Dark ? "#2C2C2C" : "#F9F9F9");
        Set("Fg", Dark ? "#FFFFFF" : "#1B1B1B");
        Set("Fg2", Dark ? "#C8C8C8" : "#5C5C5C");
        Set("Stroke", Dark ? "#404040" : "#E3E3E3");
        Set("Track", Dark ? "#5C5C5C" : "#CFCFCF");
        Set("Hover", Dark ? "#18FFFFFF" : "#12000000");
        Set("Control", Dark ? "#3A3A3A" : "#FFFFFF");
        Set("ControlStroke", Dark ? "#505050" : "#D4D4D4");
        Set("Accent", Accent);
        Set("OnAccent", OnAccent);
        Changed?.Invoke();
    }

    public static Color BandColor(LevelBand band) => band switch
    {
        LevelBand.Red => Red,
        LevelBand.Yellow => Yellow,
        LevelBand.Normal => Accent,
        _ => Grey,
    };

    public static Brush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    static Color SystemAccent()
    {
        Color c = SystemParameters.WindowGlassColor;
        if (ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor") is int abgr)
            c = Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
        c.A = 255;
        // Windows itself uses a lighter shade of the accent on dark surfaces.
        return Dark ? Blend(c, Colors.White, 0.35) : c;
    }

    static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static void Set(string key, string hex) => Set(key, C(hex));
    static void Set(string key, Color c) => Application.Current.Resources[key] = Brush(c);

    static int? ReadDword(string key, string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            return k?.GetValue(name) as int?;
        }
        catch { return null; }
    }
}
