using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace BTBatteryIndicator;

/// <summary>
/// Used only while the user is choosing a shortcut in settings: takes every key press before Windows or any
/// other app can react to it, so pressing e.g. Win+E or Alt+Tab is recorded instead of carried out.
/// It is installed when recording starts and removed when it ends; nothing is stored or watched otherwise.
/// (Ctrl+Alt+Del and Win+L are reserved by Windows and cannot be taken.)
/// </summary>
public sealed class KeyCapture : IDisposable
{
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    readonly HookProc _proc;                 // kept in a field so it is not collected while Windows holds it
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly HashSet<int> _held = new();     // keys whose press was taken, so their release is taken too
    IntPtr _hook;
    bool _draining;
    long _drainUntil;

    /// <summary>A non-modifier key was pressed, with the modifiers held at that moment.</summary>
    public event Action<Key, ModifierKeys>? Pressed;

    public KeyCapture() => _proc = OnKey;

    /// <summary>False if Windows refused; the caller then falls back to ordinary key handling.</summary>
    public bool Start()
    {
        _draining = false;
        if (_hook == IntPtr.Zero)
        {
            _held.Clear();
            _hook = SetWindowsHookEx(13 /* low-level keyboard */, _proc, GetModuleHandle(null), 0);
        }
        return _hook != IntPtr.Zero;
    }

    /// <summary>Stops taking new keys. Keys still held down are followed until released so no stray input leaks out.</summary>
    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        if (_held.Count == 0) { Unhook(); return; }
        _draining = true;
        _drainUntil = Environment.TickCount64 + 3000;
    }

    void Unhook()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _draining = false;
        _held.Clear();
    }

    IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || _hook == IntPtr.Zero) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        int vk = Marshal.ReadInt32(lParam);
        bool down = wParam == 0x100 || wParam == 0x104;

        if (_draining)
        {
            if (Environment.TickCount64 > _drainUntil || _held.Count == 0)
            {
                _ui.BeginInvoke(() => { if (_draining) Unhook(); });
                return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            }
            if (!_held.Contains(vk)) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            if (!down) _held.Remove(vk);
            if (_held.Count == 0) _ui.BeginInvoke(() => { if (_draining) Unhook(); });
            return 1;
        }

        if (!down) return _held.Remove(vk) ? 1 : CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        bool repeat = !_held.Add(vk);
        if (!repeat && !IsModifier(vk))
        {
            var mods = ModifierKeys.None;
            if (Held(0x11, 0xA2, 0xA3)) mods |= ModifierKeys.Control;
            if (Held(0x12, 0xA4, 0xA5)) mods |= ModifierKeys.Alt;
            if (Held(0x10, 0xA0, 0xA1)) mods |= ModifierKeys.Shift;
            if (Held(0x5B, 0x5C)) mods |= ModifierKeys.Windows;
            var key = KeyInterop.KeyFromVirtualKey(vk);
            _ui.BeginInvoke(() => Pressed?.Invoke(key, mods));   // keep the hook itself instant
        }
        return 1;
    }

    bool Held(params int[] keys) => keys.Any(_held.Contains);

    static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    public void Dispose() => Unhook();

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
