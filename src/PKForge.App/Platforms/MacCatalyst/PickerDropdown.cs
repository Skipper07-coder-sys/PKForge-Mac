using System.Runtime.CompilerServices;
using CoreGraphics;
using UIKit;

namespace PKForge.App;

/// <summary>
/// Where the last click or tap landed, so a picker can open as a dropdown under the field that
/// was clicked instead of in the middle of the window. Recorded by <see cref="PKForgeApplication"/>.
/// </summary>
public static class PointerAnchor
{
    // A picker opened from the keyboard or a controller has no click behind it: only a click
    // this recent counts (building a long list can take a moment before the picker shows).
    private const long RecentMs = 3000;

    private static WeakReference<UIWindow>? _window;
    private static CGPoint _point;
    private static long _at;

    public static void Record(UIEvent touches)
    {
        foreach (var touch in touches.AllTouches?.ToArray<UITouch>() ?? [])
        {
            if (touch.Phase is not (UITouchPhase.Began or UITouchPhase.Ended) || touch.Window is not { } window) continue;
            _window = new WeakReference<UIWindow>(window);
            _point = touch.LocationInView(window);
            _at = Environment.TickCount64;
        }
    }

    /// <summary>Drops the last click: a picker opened by a menu choice belongs to that menu, not to a field.</summary>
    public static void Forget() => _at = long.MinValue / 2;

    /// <summary>The last click in <paramref name="view"/>'s coordinates, when it was just now and in its window.</summary>
    public static Point? Recent(View view)
    {
        if (Environment.TickCount64 - _at > RecentMs || _window is null || !_window.TryGetTarget(out var window)) return null;
        if (view.Handler?.PlatformView is not UIView platform || platform.Window != window) return null;
        var point = platform.ConvertPointFromView(_point, window);
        return new Point(point.X, point.Y);
    }
}

/// <summary>
/// Text fields that keep the typing but hand the list keys (↑ ↓, Return, Esc) to the pad, so a
/// picker's search box filters as you type while the arrows move through what it found.
/// </summary>
public static class PadKeysWhileTyping
{
    private static readonly ConditionalWeakTable<UIView, object> Marked = new();

    public static void Mark(Entry entry) => entry.HandlerChanged += (_, _) =>
    {
        if (entry.Handler?.PlatformView is UIView field) Marked.AddOrUpdate(field, entry);
    };

    public static bool IsMarked(UIView view) => Marked.TryGetValue(view, out _);

    public static bool IsListKey(UIKeyboardHidUsage key) => key is UIKeyboardHidUsage.KeyboardUpArrow
        or UIKeyboardHidUsage.KeyboardDownArrow or UIKeyboardHidUsage.KeyboardReturnOrEnter
        or UIKeyboardHidUsage.KeypadEnter or UIKeyboardHidUsage.KeyboardEscape;
}
