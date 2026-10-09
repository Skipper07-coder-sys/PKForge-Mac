namespace PKForge.App.Services;

/// <summary>
/// Whether anyone can see the app. Android pauses an app the player leaves (App.OnSleep); a Mac keeps a
/// window "active" while it sits behind other windows, on another desktop or under a full-screen app, so
/// animations kept running for nobody. The Mac feeds this from the window's occlusion state
/// (MacWindowVisibility); everywhere else it stays true. Loops pause on it without closing anything.
/// </summary>
public static class AppVisibility
{
    public static bool Visible { get; private set; } = true;

    /// <summary>Raised on the main thread with the new value.</summary>
    public static event Action<bool>? Changed;

    public static void Set(bool visible)
    {
        if (Visible == visible) return;
        Visible = visible;
        Perf.Mark(visible ? "visibility: shown" : "visibility: unseen");
        Changed?.Invoke(visible);
    }
}
