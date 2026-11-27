namespace WirelessDeviceMonitor.Core;

/// <summary>Decisions about the taskbar strip that do not depend on Windows itself, so they can be checked automatically.</summary>
public static class StripRules
{
    /// <summary>
    /// The strip belongs to the taskbar and goes wherever it goes, on screen or slid away. It steps aside in one
    /// case only: a full-screen app is in front and the taskbar is still on screen underneath it, where the
    /// strip (always on top) would be left floating over the app.
    /// </summary>
    /// <param name="barOnScreen">The taskbar is at its shown position, not slid off the edge.</param>
    /// <param name="barAboveApp">The taskbar is stacked above the full-screen app, so the user can see it.</param>
    public static bool ShouldStepAside(bool barOnScreen, bool barAboveApp) => barOnScreen && !barAboveApp;
}
