using PKForge.Domain;

namespace PKForge.App;

/// <summary>
/// The lower screen as a companion window. The dual-screen UI is driven from the main window;
/// this window mirrors it. Closing it by hand keeps it closed until the next launch.
/// </summary>
public sealed class MacSecondaryDisplayHost(IServiceProvider services) : ISecondaryDisplayHost
{
    private const double Width = 640, Height = 480;
    private Window? _window;
    private ContentPage? _page;
    private bool _closedByUser;
    private Window? _dismissed;

    public bool IsAvailable => !_closedByUser;

    public ValueTask ShowAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_closedByUser || Application.Current is not { } app) return ValueTask.CompletedTask;
        if (_window is not null)
        {
            if (_page is Views.PokeparkJournalPage journalPage)
                _ = journalPage.RefreshAsync(cancellationToken);
            return ValueTask.CompletedTask;
        }

        _page = services.GetRequiredService<Views.SecondScreenBoxPage>();
        var window = new Window(_page) { Title = "PKForge — Second Screen" };
        // Bottom-right corner, like a companion screen; a laptop display is too narrow to sit both side by side.
        MacWindowChrome.Apply(window, new(Width, Height), new(Width / 2, Height / 2),
            (screen, size) => new CoreGraphics.CGPoint(
                Math.Max(0, screen.Width - size.Width - 16),
                Math.Max(0, screen.Height - size.Height - 16)));
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

    /// <summary>Brings a hand-closed second screen back (Window menu / ⌘2).</summary>
    public void Reopen()
    {
        _closedByUser = false;
        _ = ReopenAsync();
    }

    private async Task ReopenAsync()
    {
        try { await ShowAsync(); }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine($"Second screen reopen failed: {e}"); }
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
