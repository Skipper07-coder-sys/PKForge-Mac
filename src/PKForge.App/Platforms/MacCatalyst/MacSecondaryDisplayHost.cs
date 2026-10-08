using PKForge.App.Services;
using PKForge.Domain;

namespace PKForge.App;

/// <summary>
/// The lower screen as a companion window. The dual-screen UI is driven from the main window;
/// this window mirrors it. Closing it by hand keeps it closed until the next launch.
/// <para>
/// Whether it opens follows <see cref="SecondScreenMode"/>, the same setting as Settings ▸ Misc ▸
/// Second screen, also on Window ▸ Second Screen (⌘2). OFF reports no second display, so every
/// page uses its one-screen layout and a Pokémon's details live in its menu's Summary. A Mac
/// starts with it OFF (<see cref="ApplyMacDefault"/>): a laptop screen has no room for a second
/// window beside the main one.
/// </para>
/// </summary>
public sealed class MacSecondaryDisplayHost(IServiceProvider services) : ISecondaryDisplayHost
{
    private const double Width = 640, Height = 480;
    private Window? _window;
    private ContentPage? _page;
    private bool _closedByUser;
    private Window? _dismissed;

    public bool IsAvailable => SecondScreenMode.Allowed && !_closedByUser;

    /// <summary>
    /// Runs once per Mac, before the first page asks: the second screen starts OFF here, unlike
    /// on a dual-screen handheld. Carries over the choice made with the earlier Mac-only setting.
    /// </summary>
    public static void ApplyMacDefault()
    {
        const string Applied = "mac_second_screen_default_applied", Legacy = "mac_dual_screen";
        if (Preferences.Default.Get(Applied, false)) return;
        SecondScreenMode.SetUserOff(!Preferences.Default.Get(Legacy, false));
        Preferences.Default.Remove(Legacy);
        Preferences.Default.Set(Applied, true);
    }

    /// <summary>The ⌘2 menu toggle: a showing second screen goes away, a missing one comes back.</summary>
    public void Toggle()
    {
        var on = !IsAvailable;
        SecondScreenMode.SetUserOff(!on);
        _closedByUser = false;
        AppLog.Info("second", on ? "Player turned the second screen on (⌘2)" : "Player turned the second screen off (⌘2)");
        if (on) _ = ReopenAsync();
        else _ = DismissAsync();
    }

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
            Cleanup();
            SecondScreenMode.DisableForSession("the second screen window failed to open", e);
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
        // Turned off in Settings: a window closed by hand earlier no longer blocks turning it on again.
        if (SecondScreenMode.UserOff) _closedByUser = false;
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
