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
    }
}
