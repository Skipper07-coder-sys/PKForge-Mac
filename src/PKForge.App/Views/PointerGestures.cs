namespace PKForge.App.Views;

/// <summary>
/// Mouse and trackpad habits for drawn views on the Mac (and an iPad/iPhone with a pointer):
/// right-click for a menu, scroll or swipe to page. Android has neither and ignores these.
/// </summary>
internal static class PointerGestures
{
    /// <summary>A right-click or two-finger click, at a point in the view's own coordinates.</summary>
    public static void OnSecondaryClick(View view, Action<Point> clicked)
    {
#if MACCATALYST || IOS
        void Hook()
        {
            if (view.Handler?.PlatformView is not UIKit.UIView platform) return;
            UIKit.UITapGestureRecognizer? tap = null;
            tap = new UIKit.UITapGestureRecognizer(() =>
            {
                var at = tap!.LocationInView(platform);
                clicked(new Point(at.X, at.Y));
            })
            {
                ButtonMaskRequired = UIKit.UIEventButtonMask.Secondary,
#if IOS
                // iOS ignores the button mask for a finger: right-click on a trackpad or mouse only.
                AllowedTouchTypes = [new Foundation.NSNumber((long)UIKit.UITouchType.IndirectPointer)],
#endif
            };
            platform.AddGestureRecognizer(tap);
        }
        view.HandlerChanged += (_, _) => Hook();
        Hook();
#endif
    }

    /// <summary>
    /// The scroll wheel or a two-finger swipe, as page steps: +1 = next (swipe left, wheel down),
    /// −1 = previous. Scrolls only, no touches, so clicks and drags on the view are untouched.
    /// One step per <paramref name="distance"/> points, at most one per 200 ms, so a flung
    /// trackpad swipe moves a page or two instead of racing through all of them.
    /// </summary>
    public static void OnScrollSteps(View view, Action<int> step, double distance = 40)
    {
#if MACCATALYST || IOS
        void Hook()
        {
            if (view.Handler?.PlatformView is not UIKit.UIView platform) return;
            double travelled = 0;
            long lastStep = 0;
            platform.AddGestureRecognizer(new UIKit.UIPanGestureRecognizer(pan =>
            {
                if (pan.State == UIKit.UIGestureRecognizerState.Began) travelled = 0;
                if (pan.State is not (UIKit.UIGestureRecognizerState.Began or UIKit.UIGestureRecognizerState.Changed)) return;
                var moved = pan.TranslationInView(platform);
                pan.SetTranslation(CoreGraphics.CGPoint.Empty, platform);
                travelled += Math.Abs(moved.X) > Math.Abs(moved.Y) ? moved.X : moved.Y;
                if (Math.Abs(travelled) < distance || Environment.TickCount64 - lastStep < 200) return;
                step(travelled < 0 ? 1 : -1);
                travelled = 0;
                lastStep = Environment.TickCount64;
            })
            {
                AllowedScrollTypesMask = UIKit.UIScrollTypeMask.All,
                AllowedTouchTypes = [],
            });
        }
        view.HandlerChanged += (_, _) => Hook();
        Hook();
#endif
    }
}
