using System.Runtime.CompilerServices;
using Foundation;
using Microsoft.Maui.Handlers;
using UIKit;

namespace PKForge.App;

/// <summary>
/// MAUI on Apple platforms measures some views differently from Android, and the app's layouts
/// were built against Android's behavior. These restore it on the Mac.
/// </summary>
public static class MacLayoutFixes
{
    private static readonly ConditionalWeakTable<VisualElement, object> Hooked = new();

    public static void Register()
    {
        // A ScrollView is sized from its content once; content that fills in later (the home
        // shelf after a scan, bank lists) stayed clipped to the old, often zero, size.
        ScrollViewHandler.Mapper.AppendToMapping(nameof(IScrollView.Content), (handler, view) =>
        {
            if (view is not ScrollView { Content: VisualElement content } scroll || Hooked.TryGetValue(content, out _)) return;
            Hooked.Add(content, scroll);
            var owner = new WeakReference<ScrollView>(scroll);
            content.MeasureInvalidated += (_, _) =>
            {
                if (owner.TryGetTarget(out var target) && target.Content == content)
                    ((IView)target).InvalidateMeasure();
            };
        });

        // A CollectionView asks for no height of its own, so in a window that sizes to its content
        // (every picker) the list collapsed to nothing. Ask for the content height, as Android does;
        // the window's own cap still applies and the list scrolls beyond it.
        ViewHandler.ViewMapper.AppendToMapping("PKForgeCollectionHeight", (handler, view) =>
        {
            if (view is not CollectionView list || Hooked.TryGetValue(list, out _)) return;
            if (handler.PlatformView is not UIView platform) return;
            Hooked.Add(list, platform);
            MainThread.BeginInvokeOnMainThread(() => TrackContentHeight(list, platform));
        });

        // UIKit's focus ring for a focused text field never showed (the app draws its own focus
        // look), but placing it passed NaN to CoreGraphics on every focus change: dozens of
        // console errors per click into a field. Text fields go without it.
        EntryHandler.Mapper.AppendToMapping("PKForgeNoFocusRing", (handler, _) => handler.PlatformView.FocusEffect = null);
        EditorHandler.Mapper.AppendToMapping("PKForgeNoFocusRing", (handler, _) => handler.PlatformView.FocusEffect = null);
    }

    private static void TrackContentHeight(CollectionView list, UIView platform, int attempt = 0)
    {
        if (Find<UICollectionView>(platform) is not { } native)
        {
            // The native list may not be built yet; try again shortly.
            if (attempt < 10)
                list.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () => TrackContentHeight(list, platform, attempt + 1));
            return;
        }
        var owner = new WeakReference<CollectionView>(list);
        var nativeRef = new WeakReference<UICollectionView>(native);
        var observer = native.AddObserver("contentSize", NSKeyValueObservingOptions.Initial | NSKeyValueObservingOptions.New, _ =>
        {
            if (!owner.TryGetTarget(out var target) || !nativeRef.TryGetTarget(out var view)) return;
            var height = Math.Ceiling((double)view.ContentSize.Height);
            if (Math.Abs(target.MinimumHeightRequest - height) >= 1)
                MainThread.BeginInvokeOnMainThread(() => target.MinimumHeightRequest = height);
        });
        // Keep the observation alive exactly as long as the list.
        Hooked.AddOrUpdate(list, observer);
        // Stop observing once the list loses its handler.
        void OnHandlerChanged(object? sender, EventArgs e)
        {
            if (list.Handler is not null) return;
            list.HandlerChanged -= OnHandlerChanged;
            observer.Dispose();
        }
        list.HandlerChanged += OnHandlerChanged;
    }

    private static T? Find<T>(UIView view) where T : UIView
    {
        if (view is T match) return match;
        foreach (var child in view.Subviews)
            if (Find<T>(child) is { } found) return found;
        return null;
    }
}
