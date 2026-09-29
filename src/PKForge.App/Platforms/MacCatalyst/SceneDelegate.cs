using Foundation;
using UIKit;

namespace PKForge.App;

/// <summary>
/// Multi-window support: the second screen opens as its own scene. A scene that arrives with no
/// activation request while another is already connected is one macOS restored from an earlier
/// session; it is discarded so a relaunch never shows duplicate home windows. With no window
/// left (closed with ⌘W), the Dock's reopen gets a fresh main window.
/// </summary>
[Register("SceneDelegate")]
public sealed class SceneDelegate : MauiUISceneDelegate
{
    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        var requested = connectionOptions.UserActivities.Count > 0;
        var othersConnected = UIApplication.SharedApplication.ConnectedScenes.ToArray<UIScene>().Any(s => s != scene);
        if (othersConnected && !requested)
        {
            UIApplication.SharedApplication.RequestSceneSessionDestruction(session, null, null);
            return;
        }
        base.WillConnect(scene, session, connectionOptions);
    }
}
