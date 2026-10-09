using Foundation;
using UIKit;

namespace PKForge.App;

/// <summary>
/// Multi-window support: the second screen opens as its own scene. A scene that arrives with no
/// activation request while another is already connected is one macOS restored from an earlier
/// session; it is discarded so a relaunch never shows duplicate home windows. With no window
/// left (closed with ⌘W), the Dock's reopen gets a fresh main window.
/// Save files opened from Finder (Open With, a double-click, a drop on the Dock icon) arrive here
/// as URLs, with the launch or later, and open in the main window.
/// </summary>
[Register("SceneDelegate")]
public sealed class SceneDelegate : MauiUISceneDelegate
{
    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        OpenFiles(connectionOptions.UrlContexts);
        var requested = connectionOptions.UserActivities.Count > 0;
        var othersConnected = UIApplication.SharedApplication.ConnectedScenes.ToArray<UIScene>().Any(s => s != scene);
        if (othersConnected && !requested)
        {
            UIApplication.SharedApplication.RequestSceneSessionDestruction(session, null, null);
            return;
        }
        base.WillConnect(scene, session, connectionOptions);
    }

    /// <summary>
    /// A save from Finder: with the launch (<see cref="WillConnect"/>) or while running (MAUI's
    /// SceneOpenUrl lifecycle event, wired in MauiProgram). One save opens at a time: with several
    /// dropped together, the first is opened. True when a file was taken.
    /// </summary>
    internal static bool OpenFiles(NSSet<UIOpenUrlContext>? contexts)
    {
        if (contexts?.ToArray<UIOpenUrlContext>().Select(c => c.Url).FirstOrDefault(u => u.IsFileUrl) is not { Path: { } path } url)
            return false;
        // Opened in place (Info.plist LSSupportsOpeningDocumentsInPlace): the emulator's own file, never a copy.
        url.StartAccessingSecurityScopedResource();
        Views.HomePage.RequestOpen(path);
        return true;
    }
}
