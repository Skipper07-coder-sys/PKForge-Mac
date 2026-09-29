using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PKForge.App.Services;

public static class WeakSubscription
{
    private static readonly ConditionalWeakTable<object, List<Delegate>> Keep = new();

    /// <summary>
    /// Subscribes handler to source.PropertyChanged for exactly as long as owner lives: the source
    /// holds only a relay, and the handler (which may capture owner) is kept alive by owner itself.
    /// </summary>
    public static void PropertyChanged(INotifyPropertyChanged source, object owner, PropertyChangedEventHandler handler)
    {
        lock (Keep) Keep.GetOrCreateValue(owner).Add(handler);
        var weak = new WeakReference<PropertyChangedEventHandler>(handler);
        PropertyChangedEventHandler? relay = null;
        relay = (s, e) =>
        {
            if (weak.TryGetTarget(out var h)) h(s, e);
            else source.PropertyChanged -= relay;
        };
        source.PropertyChanged += relay;
    }
}
