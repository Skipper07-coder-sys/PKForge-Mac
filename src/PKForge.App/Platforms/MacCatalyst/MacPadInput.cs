using Foundation;
using GameController;
using PKForge.App.Services;
using UIKit;

namespace PKForge.App;

/// <summary>
/// Feeds the keyboard and game controllers into <see cref="GamepadRouter"/>, with the same
/// hold-to-repeat cadence the Android build uses for d-pads and shoulders.
/// Keys: arrows = d-pad, Return/Space/A = A, Esc/Delete/B = B, X = X, Y = Y, L/PageUp = L,
/// R/PageDown = R, Tab/+ = Start, − = Select (see <see cref="Resolve"/>).
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

    /// <summary>
    /// The pad button a key presses. The rule is "press what the hint shows": hints name Return
    /// for A, Esc for B, and the button's own letter or symbol for the rest (X, Y, L, R, +, −).
    /// Letters and symbols go by the character the key types, never its position, so every
    /// layout works (on QWERTZ the key printed Y is Y, not Z).
    /// </summary>
    public static PadButton? Resolve(UIKeyboardHidUsage usage, string? characters)
    {
        switch (usage)
        {
            case UIKeyboardHidUsage.KeyboardUpArrow: return PadButton.Up;
            case UIKeyboardHidUsage.KeyboardDownArrow: return PadButton.Down;
            case UIKeyboardHidUsage.KeyboardLeftArrow: return PadButton.Left;
            case UIKeyboardHidUsage.KeyboardRightArrow: return PadButton.Right;
            case UIKeyboardHidUsage.KeyboardReturnOrEnter or UIKeyboardHidUsage.KeypadEnter or UIKeyboardHidUsage.KeyboardSpacebar:
                return PadButton.A;
            case UIKeyboardHidUsage.KeyboardEscape or UIKeyboardHidUsage.KeyboardDeleteOrBackspace:
                return PadButton.B;
            case UIKeyboardHidUsage.KeyboardTab: return PadButton.Start;
            case UIKeyboardHidUsage.KeyboardPageUp: return PadButton.L;
            case UIKeyboardHidUsage.KeyboardPageDown: return PadButton.R;
        }
        return (characters ?? "").ToLowerInvariant() switch
        {
            "a" => PadButton.A,
            "b" => PadButton.B,
            "x" => PadButton.X,
            "y" => PadButton.Y,
            "l" or "[" => PadButton.L,
            "r" or "]" => PadButton.R,
            "+" or "=" => PadButton.Start,
            "-" => PadButton.Select,
            _ => null,
        };
    }

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
            n => { if (n.Object is GCController c) Attach(c); UpdateGlyphs(); });
        NSNotificationCenter.DefaultCenter.AddObserver(GCController.DidDisconnectNotification,
            n => { if (n.Object is GCController c) { StickDirection.Remove(c); ReleaseAll(); } UpdateGlyphs(n.Object as GCController); });
#if IOS
        // An iPhone or iPad shows keyboard keys only while a hardware keyboard is attached.
        NSNotificationCenter.DefaultCenter.AddObserver(GCKeyboard.DidConnectNotification, _ => UpdateGlyphs());
        NSNotificationCenter.DefaultCenter.AddObserver(GCKeyboard.DidDisconnectNotification, _ => UpdateGlyphs());
#endif
        foreach (var controller in GCController.Controllers)
            Attach(controller);
        UpdateGlyphs();
        GCController.StartWirelessControllerDiscovery(() => { });
    }

    /// <summary>Hints name keyboard keys while no controller is connected (<see cref="InputGlyphs"/>).</summary>
    private static void UpdateGlyphs(GCController? leaving = null)
    {
        // A disconnecting controller may still be listed while its notification runs.
        var controllers = GCController.Controllers.Count(c => c != leaving && c.ExtendedGamepad is not null);
#if IOS
        var keyboard = GCKeyboard.CoalescedKeyboard is not null;
#else
        const bool keyboard = true;
#endif
        InputGlyphs.Update(keyboard && controllers == 0);
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
