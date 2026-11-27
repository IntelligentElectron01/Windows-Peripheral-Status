<#
  Records how closely the battery strip follows the taskbar while the taskbar slides in or out.
    .\tools\capture-transition.ps1 [-Seconds 20]
  Run it, then make the taskbar hide and show a few times. It writes dist\capture\positions.txt with the time,
  taskbar top, strip top, and the gap between them at every change (the gap at rest is the target), and prints
  a summary. It only reads window positions; everything stays on this PC.
#>
param([int]$Seconds = 20, [string]$Out = (Join-Path (Split-Path $PSScriptRoot) 'dist\capture'))

New-Item -ItemType Directory -Force $Out | Out-Null
Add-Type @'
using System; using System.Collections.Generic; using System.Diagnostics; using System.Runtime.InteropServices; using System.Text;
public static class StripCapture {
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string c, string n);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out R r);
  delegate bool E(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(E e, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);

  public static string Run(int seconds, string outDir) {
    SetProcessDPIAware();
    // Looked up by title: the strip rides in the taskbar's own window layer, which plain window listing skips.
    IntPtr bar = FindWindowW("Shell_TrayWnd", null), strip = FindWindowW(null, "Wireless Device Monitor taskbar strip");
    if (strip == IntPtr.Zero) return "Strip window not found. Pin a device first and make sure the app is running.";

    var log = new List<string>(); var sw = Stopwatch.StartNew();
    int lastBar = int.MinValue, lastStrip = int.MinValue, rest = int.MinValue, worst = 0, moving = 0; long lastChange = -1000; R b, s;
    while (sw.ElapsedMilliseconds < seconds * 1000) {
      GetWindowRect(bar, out b); GetWindowRect(strip, out s);
      long t = sw.ElapsedMilliseconds;
      if (b.T != lastBar || s.T != lastStrip) {
        int gap = s.T - b.T;
        if (rest == int.MinValue) rest = gap;
        else if (t - lastChange < 80) { moving++; worst = Math.Max(worst, Math.Abs(gap - rest)); }
        log.Add(t + " ms | taskbar top " + b.T + " | strip top " + s.T + " | gap " + gap);
        lastBar = b.T; lastStrip = s.T; lastChange = t;
      }
      System.Threading.Thread.Sleep(1);
    }
    System.IO.File.WriteAllLines(System.IO.Path.Combine(outDir, "positions.txt"), log);
    return moving == 0 ? "The taskbar did not move during the recording."
      : "Samples while moving: " + moving + ". Furthest the strip strayed from its resting gap: " + worst + " px.";
  }
}
'@
[StripCapture]::Run($Seconds, $Out)
