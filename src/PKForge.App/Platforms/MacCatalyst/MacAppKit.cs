using System.Runtime.InteropServices;

namespace PKForge.App;

/// <summary>The few AppKit calls Mac Catalyst does not expose (NSApplication, NSWindow), by message send.</summary>
internal static class MacAppKit
{
    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, nuint argument);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nuint SendNUInt(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern bool SendBool(IntPtr receiver, IntPtr selector);

    public static IntPtr Class(string name) => objc_getClass(name);

    public static IntPtr Call(IntPtr target, string selector) =>
        target == IntPtr.Zero ? IntPtr.Zero : Send(target, sel_registerName(selector));

    public static IntPtr Call(IntPtr target, string selector, IntPtr argument) =>
        target == IntPtr.Zero ? IntPtr.Zero : Send(target, sel_registerName(selector), argument);

    public static nuint CallNUInt(IntPtr target, string selector) =>
        target == IntPtr.Zero ? 0 : SendNUInt(target, sel_registerName(selector));

    public static bool CallBool(IntPtr target, string selector) =>
        target != IntPtr.Zero && SendBool(target, sel_registerName(selector));

    public static IntPtr Application => Call(Class("NSApplication"), "sharedApplication");

    /// <summary>The app's AppKit windows (the main window, the second screen, panels).</summary>
    public static IEnumerable<IntPtr> Windows()
    {
        var array = Call(Application, "windows");
        var count = Send(array, sel_registerName("count"));
        for (nuint i = 0; i < (nuint)count; i++)
            yield return Send(array, sel_registerName("objectAtIndex:"), i);
    }
}
