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
        if (uiEvent is UIPressesEvent { AllPresses: { } presses })
            foreach (var press in presses)
                if (press.Phase is UIPressPhase.Ended or UIPressPhase.Cancelled)
                    AppDelegate.RouteKey(press, down: false);
        base.SendEvent(uiEvent);
#if IOS
        if (uiEvent.Type == UIEventType.Touches) SettleCanvasTouches(uiEvent);
#endif
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
