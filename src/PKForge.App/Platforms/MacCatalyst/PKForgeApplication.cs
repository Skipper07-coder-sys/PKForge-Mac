using Foundation;
using UIKit;

namespace PKForge.App;

/// <summary>
/// UIKit hands a key-up to the view that got the key-down; when that view is gone (a menu opened
/// over it) the up never reaches the app delegate and the pad button stays held, so arrows run
/// away. Every press event passes through here first, so the release is always seen.
/// </summary>
[Register("PKForgeApplication")]
public sealed class PKForgeApplication : UIApplication
{
    public override void SendEvent(UIEvent uiEvent)
    {
        if (uiEvent is UIPressesEvent { AllPresses: { } listKeys } && ListKeysWhileTyping(listKeys)) return;
        if (uiEvent is UIPressesEvent { AllPresses: { } presses })
            foreach (var press in presses)
            {
                if (press.Phase is UIPressPhase.Ended or UIPressPhase.Cancelled)
                    AppDelegate.RouteKey(press, down: false);
#if IOS
                // iOS hands a hardware key only to the first responder, and with no text field
                // focused there is none, so keys never reached the app delegate as they do on the
                // Mac. Route them here unless a field is typing or a native sheet owns the keyboard
                // (a key that also arrives through the delegate is ignored as a repeat).
                else if (press.Phase == UIPressPhase.Began && !MacPadInput.NativeSheetShowing() && !TextInputFocused())
                    AppDelegate.RouteKey(press, down: true);
#endif
            }
        base.SendEvent(uiEvent);
        if (uiEvent.Type == UIEventType.Touches) PointerAnchor.Record(uiEvent);
#if IOS
        if (uiEvent.Type == UIEventType.Touches) SettleCanvasTouches(uiEvent);
#endif
    }

    /// <summary>
    /// In a picker's search box the arrows, Return and Esc drive the list (the pad) while every
    /// other key types. Those keys are routed here and kept from the text field.
    /// </summary>
    private static bool ListKeysWhileTyping(NSSet<UIPress> presses)
    {
        var all = presses.ToArray<UIPress>();
        if (all.Length == 0 || all.Any(press => press.Key is not { } key || !PadKeysWhileTyping.IsListKey(key.KeyCode))) return false;
        if (FocusedTextInput() is not { } field || !PadKeysWhileTyping.IsMarked(field)) return false;
        foreach (var press in all)
            if (press.Phase is UIPressPhase.Began or UIPressPhase.Ended or UIPressPhase.Cancelled)
                AppDelegate.RouteKey(press, down: press.Phase == UIPressPhase.Began);
        return true;
    }

    private static bool TextInputFocused() => FocusedTextInput() is not null;

    private static UIView? FocusedTextInput()
    {
        foreach (var scene in SharedApplication.ConnectedScenes.ToArray<UIScene>())
            if (scene is UIWindowScene windows)
                foreach (var window in windows.Windows)
                    if (FirstResponder(window) is (UITextField or UITextView) and var field) return field;
        return null;

        static UIView? FirstResponder(UIView view)
        {
            if (view.IsFirstResponder) return view;
            foreach (var child in view.Subviews)
                if (FirstResponder(child) is { } found) return found;
            return null;
        }
    }

#if IOS
    /// <summary>
    /// SkiaSharp's canvas touch recognizer reports touches but never leaves the "possible" state.
    /// iOS then holds back every other tap in the window until it gives up on it (about 30 s), so
    /// the app looks frozen after touching a canvas. Once a touch sequence ends, fail any still waiting.
    /// </summary>
    private static void SettleCanvasTouches(UIEvent uiEvent)
    {
        var touches = uiEvent.AllTouches?.ToArray<UITouch>() ?? [];
        if (touches.Length == 0 || touches.Any(touch => touch.Phase is not (UITouchPhase.Ended or UITouchPhase.Cancelled))) return;
        foreach (var touch in touches)
            foreach (var recognizer in touch.GestureRecognizers ?? [])
                if (recognizer.State == UIGestureRecognizerState.Possible && recognizer.GetType().Name == "SKTouchHandler")
                    recognizer.State = UIGestureRecognizerState.Failed;
    }
#endif
}
