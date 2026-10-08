using PKForge.Domain;

namespace PKForge.App;

/// <summary>
/// The lower screen as a companion window. The dual-screen UI is driven from the main window;
/// this window mirrors it. Closing it by hand keeps it closed until the next launch.
/// <para>
/// Two layouts, picked in Window ▸ Second Screen (⌘2) or Settings ▸ Screens and remembered:
/// Dual opens the AYN Thor-style companion window; Single (the default) reports no second
/// display, so every page uses its phone layout and the details live in the slot menu's Summary.
/// A laptop screen has no room for a second window beside the main one, so Single comes first.
/// </para>
/// </summary>
public sealed class MacSecondaryDisplayHost(IServiceProvider services) : ISecondaryDisplayHost
{
    private const double Width = 640, Height = 480;
    private const string DualKey = "mac_dual_screen";
    private Window? _window;
    private ContentPage? _page;
    private bool _closedByUser;
    private Window? _dismissed;

    /// <summary>The remembered layout: true = Dual (second window), false = Single.</summary>
    public static bool DualPreferred => Preferences.Default.Get(DualKey, false);

    public bool IsAvailable => DualPreferred && !_closedByUser;

    /// <summary>Switches layout now and remembers it for the next launch.</summary>
    public void SetDual(bool dual)
    {
        Preferences.Default.Set(DualKey, dual);
        _closedByUser = false;
        if (dual) _ = ReopenAsync();
        else _ = DismissAsync();
    }

    /// <summary>The ⌘2 menu toggle: a showing second screen goes away, a missing one comes back.</summary>
    public void Toggle() => SetDual(!IsAvailable);

    public ValueTask ShowAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable || Application.Current is not { } app) return ValueTask.CompletedTask;
        if (_window is not null)
        {
            if (_page is Views.PokeparkJournalPage journalPage)
                _ = journalPage.RefreshAsync(cancellationToken);
            return ValueTask.CompletedTask;
        }

        _page = services.GetRequiredService<Views.SecondScreenBoxPage>();
        var window = new Window(_page) { Title = "PKForge — Second Screen" };
        MacWindowChrome.Apply(window, new(Width, Height), new(Width / 2, Height / 2), Place);
        window.Destroying += (_, _) =>
        {
            // A late Destroying from an old window must not touch a newer one.
            if (!ReferenceEquals(_window, window)) return;
            if (!ReferenceEquals(_dismissed, window)) _closedByUser = true;
            _dismissed = null;
            Cleanup();
        };
        _window = window;
        try { app.OpenWindow(window); }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"Second screen failed to open: {e}");
            Cleanup();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ShowPokeparkJournalAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_page is Views.SecondScreenBoxPage boxPage)
            return boxPage.RefreshPokeparkJournalAsync(cancellationToken);
        if (_page is Views.PokeparkJournalPage journalPage)
            return journalPage.RefreshAsync(cancellationToken);
        return ValueTask.CompletedTask;
    }

    public ValueTask RefreshPokeparkJournalAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _page is Views.PokeparkJournalPage journalPage
            ? journalPage.RefreshAsync(cancellationToken)
            : ValueTask.CompletedTask;
    }

    public ValueTask DismissAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_window is { } window && Application.Current is { } app)
        {
            _dismissed = window;
            app.CloseWindow(window);
        }
        Cleanup();
        return ValueTask.CompletedTask;
    }

    private async Task ReopenAsync()
    {
        try { await ShowAsync(); }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine($"Second screen reopen failed: {e}"); }
    }

    /// <summary>
    /// Beside the main window when the screen has room (right, else below), so neither hides the
    /// other; otherwise the bottom-right corner, like a companion screen on a laptop display.
    /// </summary>
    private static CoreGraphics.CGPoint Place(CoreGraphics.CGRect screen, CoreGraphics.CGSize size)
    {
        const double Gap = 8, Margin = 16;
        var main = MainFrame();
        if (main is { } m)
        {
            if (m.Right + Gap + size.Width <= screen.Right)
                return new(m.Right + Gap, Math.Min(m.Top, Math.Max(screen.Top, screen.Bottom - size.Height)));
            if (m.Bottom + Gap + size.Height <= screen.Bottom)
                return new(Math.Min(m.Left, Math.Max(screen.Left, screen.Right - size.Width)), m.Bottom + Gap);
        }
        return new(Math.Max(0, screen.Width - size.Width - Margin), Math.Max(0, screen.Height - size.Height - Margin));
    }

    private static CoreGraphics.CGRect? MainFrame()
    {
        var main = Application.Current?.Windows.FirstOrDefault(w => w.Page is not (Views.SecondScreenBoxPage or Views.PokeparkJournalPage));
        return (main?.Handler?.PlatformView as UIKit.UIWindow)?.WindowScene?.EffectiveGeometry.SystemFrame;
    }

    private void Cleanup()
    {
        if (_page is Views.SecondScreenBoxPage boxPage)
            boxPage.Cleanup();
        else if (_page is Views.PokeparkJournalPage journalPage)
            journalPage.Cleanup();
        _page = null;
        _window = null;
    }
}
