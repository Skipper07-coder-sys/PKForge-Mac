using Foundation;
using PKForge.App.Services;

namespace PKForge.App;

/// <summary>
/// Feeds <see cref="AppVisibility"/> from AppKit's window occlusion: visible while any of the app's
/// windows shows at least one pixel on some screen. Hidden (⌘H) and minimized windows already pause the
/// app through the scene lifecycle (App.OnSleep); this covers what a Mac adds: covered windows, other
/// desktops, a full-screen app in front.
/// </summary>
internal static class MacWindowVisibility
{
    private const nuint OcclusionStateVisible = 1 << 1;

    public static void Start()
    {
        NSNotificationCenter.DefaultCenter.AddObserver((NSString)"NSWindowDidChangeOcclusionStateNotification", _ => Update());
        Update();
    }

#if PKF_AUTOMATION
    /// <summary>Test builds: a simulated covered/uncovered window that real occlusion updates don't override.</summary>
    internal static bool? Simulated;
#endif

    private static void Update()
    {
#if PKF_AUTOMATION
        if (Simulated is { } simulated) { AppVisibility.Set(simulated); return; }
#endif
        var windows = MacAppKit.Windows().ToArray();
        // No AppKit window yet (launching): count as visible.
        var visible = windows.Length == 0 || windows.Any(window =>
            MacAppKit.CallBool(window, "isVisible") && (MacAppKit.CallNUInt(window, "occlusionState") & OcclusionStateVisible) != 0);
        AppVisibility.Set(visible);
    }
}
