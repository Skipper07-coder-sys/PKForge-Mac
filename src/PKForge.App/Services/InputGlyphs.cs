using System.Runtime.CompilerServices;

namespace PKForge.App.Services;

/// <summary>
/// Which keys the hints show. On a Mac (or an iPhone/iPad with a hardware keyboard) with no
/// controller connected, hints name the keyboard keys that MacPadInput maps to each button
/// (Return for A, Esc for B…); with a controller, or on Android, they show the controller's buttons.
/// Switches live when a controller connects or disconnects.
/// </summary>
public static class InputGlyphs
{
    private static readonly ConditionalWeakTable<object, Action> Listeners = new();

    /// <summary>True while hints should name keyboard keys.</summary>
    public static bool Keyboard { get; private set; }

    /// <summary>
    /// The keyboard key for a hint glyph (see MacPadInput.Resolve and Help ▸ Keyboard &amp; Controller).
    /// Plain words: the pixel font has no ⏎/⇧ symbols.
    /// </summary>
    public static string KeyFor(string glyph) => glyph switch
    {
        // Confirm and back get the Mac's own keys; every other button is the key with its name.
        "A" => "Return",
        "B" => "Esc",
        "LR" => "L R",
        "−" => "-",   // the pixel font has a plain hyphen, not U+2212
        _ => glyph,
    };

    /// <summary>What a hint shows for this glyph right now.</summary>
    public static string Label(string glyph) => Keyboard ? KeyFor(glyph) : glyph;

    // Sentences that name a button use these, so "Press A" reads "Press Return" when the hints say Return.

    /// <summary>The confirm button as a sentence names it: "A", or "Return" on a keyboard.</summary>
    public static string Confirm => Label("A");

    /// <summary>The back button as a sentence names it: "B", or "Esc" on a keyboard.</summary>
    public static string Back => Label("B");

    /// <summary>"key" while hints name keyboard keys, else "button".</summary>
    public static string ButtonWord => Keyboard ? "key" : "button";

    /// <summary>"tap" on a touch screen, "click" on a Mac.</summary>
    public static string Tap(bool capital = false) => OperatingSystem.IsMacCatalyst()
        ? capital ? "Click" : "click"
        : capital ? "Tap" : "tap";

    /// <summary>Re-renders <paramref name="owner"/> through <paramref name="refresh"/> on every switch, for as long as it lives.</summary>
    public static void Track(object owner, Action refresh) => Listeners.AddOrUpdate(owner, refresh);

    public static void Update(bool keyboard)
    {
        if (Keyboard == keyboard) return;
        Keyboard = keyboard;
        PKForge.Chrome.PksmPaint.KeyLabel = keyboard ? KeyFor : static glyph => glyph;
        foreach (var (_, refresh) in Listeners)
            refresh();
    }
}
