using CoreGraphics;
using UIKit;

namespace PKForge.App;

/// <summary>
/// Console-style window chrome: no toolbar strip or duplicate title (the app draws its own DS title
/// bar), plus an initial size and a floor so the handheld layout is never crushed. Placement is left
/// to macOS, which cascades the second screen beside the main window.
/// </summary>
public static class MacWindowChrome
{
    /// <summary>Scenes already sized, so a new window never resizes another window's scene
    /// (its handler briefly reports the key window's scene while its own attaches).</summary>
    private static readonly HashSet<UIWindowScene> Claimed = [];

    /// <summary>
    /// Where native sheets (pickers, alerts) belong: the top-most controller of the main window,
    /// never the second screen, even while the second screen is the key window.
    /// </summary>
    public static UIViewController? MainPresenter()
    {
        // The second screen is never the presenter; fall back to the first window.
        var windows = Application.Current?.Windows;
        var mainWindow = windows?.FirstOrDefault(w => w.Page is not (Views.SecondScreenBoxPage or Views.PokeparkJournalPage))
            ?? windows?.FirstOrDefault();
        var main = mainWindow?.Handler?.PlatformView as UIWindow;
        var controller = main?.RootViewController ?? Platform.GetCurrentUIViewController();
        while (controller?.PresentedViewController is { IsBeingDismissed: false } presented)
            controller = presented;
        main?.MakeKeyWindow();
        return controller;
    }

    public static void Apply(Window window, CGSize size, CGSize minimum, Func<CGRect, CGSize, CGPoint>? place = null)
    {

        UIWindowScene? Scene() => (window.Handler?.PlatformView as UIWindow)?.WindowScene;

        void HideToolbar(UIWindowScene scene)
        {
            if (scene.Titlebar is not { } titlebar) return;
            titlebar.TitleVisibility = UITitlebarTitleVisibility.Hidden;
            titlebar.Toolbar = null;
        }

        async void Configure()
        {
            try
            {
                // The scene attaches a beat after Created; wait for this window's own scene.
                UIWindowScene? scene = null;
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    if ((scene = Scene()) is not null && !Claimed.Contains(scene)) break;
                    scene = null;
                    await Task.Delay(50);
                }
                if (scene is null) return;
                Claimed.Add(scene);
                window.Destroying += (_, _) => Claimed.Remove(scene);
                HideToolbar(scene);
                if (scene.SizeRestrictions is { } limits)
                    limits.MinimumSize = minimum;
                var origin = place?.Invoke(scene.Screen.Bounds, size) ?? scene.EffectiveGeometry.SystemFrame.Location;
                scene.RequestGeometryUpdate(new UIWindowSceneGeometryPreferencesMac(new CGRect(origin, size)), _ => { });
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine($"Window chrome setup failed: {e}");
            }
        }

        window.Created += (_, _) => MainThread.BeginInvokeOnMainThread(Configure);
        // Navigation can re-create the toolbar; keep the console look.
        window.Activated += (_, _) => { if (Scene() is { } scene) HideToolbar(scene); };
    }
}
