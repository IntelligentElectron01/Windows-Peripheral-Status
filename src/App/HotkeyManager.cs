using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using WirelessDeviceMonitor.Core;

namespace WirelessDeviceMonitor;

/// <summary>
/// Registers one system-wide keyboard shortcut with Windows and reports when it is pressed.
/// Windows delivers the key press itself; the app does not watch the keyboard (except while a shortcut is being recorded, see KeyCapture).
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    const int Id = 0xB7B1;
    readonly HwndSource _window;
    bool _registered;

    public event Action? Pressed;

    public HotkeyManager()
    {
        // An invisible window that exists only to receive the "shortcut pressed" message.
        _window = new HwndSource(new HwndSourceParameters("Wireless Device Monitor shortcut") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _window.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == 0x0312 /* shortcut pressed */ && wParam.ToInt32() == Id) { handled = true; Pressed?.Invoke(); }
            return IntPtr.Zero;
        });
    }

    /// <summary>Sets (or with empty text, clears) the shortcut. False if Windows refused, e.g. another app already uses it.</summary>
    public bool Set(string? text)
    {
        Clear();
        var hotkey = Hotkey.Parse(text);
        if (hotkey is null) return string.IsNullOrWhiteSpace(text);
        if (!Enum.TryParse<Key>(hotkey.Key, ignoreCase: true, out var key)) return false;
        uint mods = (hotkey.Alt ? 1u : 0) | (hotkey.Ctrl ? 2u : 0) | (hotkey.Shift ? 4u : 0) | (hotkey.Win ? 8u : 0) | 0x4000 /* no auto-repeat */;
        _registered = RegisterHotKey(_window.Handle, Id, mods, (uint)KeyInterop.VirtualKeyFromKey(key));
        return _registered;
    }

    public void Clear()
    {
        if (_registered) UnregisterHotKey(_window.Handle, Id);
        _registered = false;
    }

    public void Dispose()
    {
        Clear();
        _window.Dispose();
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
