using Foundation;
using GameController;
using PKForge.App.Services;
using UIKit;

namespace PKForge.App;

/// <summary>
/// Feeds the keyboard and game controllers into <see cref="GamepadRouter"/>, with the same
/// hold-to-repeat cadence the Android build uses for d-pads and shoulders.
/// Keys (DS-emulator style): arrows = d-pad, Return/Space/X = A, Esc/Delete/Z = B, S = X,
/// A = Y, Q = L, W = R, Tab = Start, right Shift = Select.
/// </summary>
public static class MacPadInput
{
    private const int HoldDelayMs = 320, RepeatMs = 80;
    private const int ShoulderHoldDelayMs = 380, ShoulderRepeatMs = 140;
    private const float StickThreshold = 0.5f;

    private static PadButton? _held;
    private static readonly Dictionary<PadButton, int> Down = [];
    private static int _holdToken;
    private static readonly Dictionary<GCController, PadButton?> StickDirection = [];

    private static GamepadRouter? Router => IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

    public static PadButton? Resolve(UIKeyboardHidUsage key) => key switch
    {
        UIKeyboardHidUsage.KeyboardUpArrow => PadButton.Up,
        UIKeyboardHidUsage.KeyboardDownArrow => PadButton.Down,
        UIKeyboardHidUsage.KeyboardLeftArrow => PadButton.Left,
        UIKeyboardHidUsage.KeyboardRightArrow => PadButton.Right,
        UIKeyboardHidUsage.KeyboardReturnOrEnter or UIKeyboardHidUsage.KeypadEnter
            or UIKeyboardHidUsage.KeyboardSpacebar or UIKeyboardHidUsage.KeyboardX => PadButton.A,
        UIKeyboardHidUsage.KeyboardEscape or UIKeyboardHidUsage.KeyboardDeleteOrBackspace
            or UIKeyboardHidUsage.KeyboardZ => PadButton.B,
        UIKeyboardHidUsage.KeyboardS => PadButton.X,
        UIKeyboardHidUsage.KeyboardA => PadButton.Y,
        UIKeyboardHidUsage.KeyboardQ => PadButton.L,
        UIKeyboardHidUsage.KeyboardW => PadButton.R,
        UIKeyboardHidUsage.KeyboardTab => PadButton.Start,
        UIKeyboardHidUsage.KeyboardRightShift => PadButton.Select,
        _ => null,
    };

    /// <summary>A native sheet (alert, open panel) is up: it owns input, not the page beneath it.</summary>
    public static bool NativeSheetShowing()
    {
        foreach (var w in Application.Current?.Windows ?? [])
        {
            if ((w.Handler?.PlatformView as UIWindow)?.RootViewController?.PresentedViewController is { IsBeingDismissed: false })
                return true;
        }
        return false;
    }

    /// <summary>A press from any source. Returns whether the app consumed it.</summary>
    public static bool Press(PadButton button)
    {
        if (NativeSheetShowing()) return false;
        // Two sources can hold one button (Return and Space, key and stick): count them.
        if (Down.TryGetValue(button, out var count) && count > 0) { Down[button] = count + 1; return true; }
        Down[button] = 1;
        var router = Router;
        if (router is null) return false;
        // Asked before dispatching: the press itself may open another screen.
        var repeats = IsDirectional(button) || (button is PadButton.L or PadButton.R && router.TopPagesWithShoulders);
        if (!router.Dispatch(button))
            return IsDirectional(button); // never let arrows fall through to native focus movement
        if (repeats) StartRepeat(button);
        return true;
    }

    public static void Release(PadButton button)
    {
        if (!Down.TryGetValue(button, out var count) || count == 0) return;
        if (count > 1) { Down[button] = count - 1; return; }
        Down.Remove(button);
        if (_held == button) { _held = null; _holdToken++; }
        Router?.DispatchRelease(button);
    }

    /// <summary>Lets go of every held button: focus loss and unplugged pads never send their releases.</summary>
    public static void ReleaseAll()
    {
        _held = null;
        _holdToken++;
        foreach (var button in Down.Keys.ToArray())
            Router?.DispatchRelease(button);
        Down.Clear();
        foreach (var controller in StickDirection.Keys.ToArray())
            StickDirection[controller] = null;
    }

    private static void StartRepeat(PadButton button)
    {
        _held = button;
        var token = ++_holdToken;
        var directional = IsDirectional(button);
        void Step()
        {
            if (token != _holdToken || _held != button) return;
            if (NativeSheetShowing()) { _held = null; return; }
            var router = Router;
            if (router is null || (!directional && !router.TopPagesWithShoulders) || !router.Dispatch(button))
            {
                _held = null;
                return;
            }
            Schedule(directional ? RepeatMs : ShoulderRepeatMs, Step);
        }
        Schedule(directional ? HoldDelayMs : ShoulderHoldDelayMs, Step);
    }

    private static void Schedule(int milliseconds, Action action) =>
        Application.Current?.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(milliseconds), action);

    private static bool IsDirectional(PadButton button) =>
        button is PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right;

    // ---------------- Game controllers ----------------

    public static void StartControllers()
    {
        NSNotificationCenter.DefaultCenter.AddObserver(GCController.DidConnectNotification,
            n => { if (n.Object is GCController c) Attach(c); });
        NSNotificationCenter.DefaultCenter.AddObserver(GCController.DidDisconnectNotification,
            n => { if (n.Object is GCController c) { StickDirection.Remove(c); ReleaseAll(); } });
        foreach (var controller in GCController.Controllers)
            Attach(controller);
        GCController.StartWirelessControllerDiscovery(() => { });
    }

    private static void Attach(GCController controller)
    {
        if (controller.ExtendedGamepad is not { } pad) return;
        controller.HandlerQueue = CoreFoundation.DispatchQueue.MainQueue;
        void Bind(GCControllerButtonInput? input, PadButton button) =>
            input?.PressedChangedHandler = (_, _, pressed) =>
            {
                if (pressed) Press(button);
                else Release(button);
            };

        Bind(pad.ButtonA, PadButton.A);
        Bind(pad.ButtonB, PadButton.B);
        Bind(pad.ButtonX, PadButton.X);
        Bind(pad.ButtonY, PadButton.Y);
        Bind(pad.LeftShoulder, PadButton.L);
        Bind(pad.RightShoulder, PadButton.R);
        Bind(pad.ButtonMenu, PadButton.Start);
        Bind(pad.ButtonOptions, PadButton.Select);
        Bind(pad.DPad.Up, PadButton.Up);
        Bind(pad.DPad.Down, PadButton.Down);
        Bind(pad.DPad.Left, PadButton.Left);
        Bind(pad.DPad.Right, PadButton.Right);

        // The left stick acts as a d-pad: one step on deflection, then the hold repeat.
        StickDirection[controller] = null;
        pad.LeftThumbstick.ValueChangedHandler = (_, x, y) =>
        {
            PadButton? direction = Math.Abs(x) < StickThreshold && Math.Abs(y) < StickThreshold ? null
                : Math.Abs(y) >= Math.Abs(x) ? (y > 0 ? PadButton.Up : PadButton.Down)
                : (x < 0 ? PadButton.Left : PadButton.Right);
            var previous = StickDirection.GetValueOrDefault(controller);
            if (direction == previous) return;
            StickDirection[controller] = direction;
            if (previous is { } released) Release(released);
            if (direction is { } pressed) Press(pressed);
        };
    }
}
