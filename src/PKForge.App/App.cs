#if ANDROID
using PlatformMusicPlayer = PKForge.App.Platforms.Android.MusicPlayer;
#elif MACCATALYST
using PlatformMusicPlayer = PKForge.App.Platforms.MacCatalyst.MusicPlayer;
#endif

namespace PKForge.App;

public sealed class App : Application
{
    /// <summary>Raised when Android resumes the app (including after the install-permission screen).</summary>
    public static event Action? Resumed;
    public static event Action? Suspended;
    protected override void OnSleep()
    {
        Suspended?.Invoke();
        base.OnSleep();
    }

    /// <summary>The user's optional default background music starts with the app, once.</summary>
    protected override void OnStart()
    {
        base.OnStart();
#if ANDROID || MACCATALYST
        var music = IPlatformApplication.Current?.Services.GetService<Domain.IMusicPlayer>() as PlatformMusicPlayer;
        music?.MaybeAutostart();
#endif
    }

    protected override void OnResume()
    {
        base.OnResume();
        Resumed?.Invoke();
    }

    public App()
    {
        Trace("App ctor");

        // Warm both Skia faces off the ctor: the party view paints before the first
        // save opens, and its cached nickname font must never pin the placeholder.
        _ = Views.PixelFont.WarmAsync();
        _ = Views.PixelFont.WarmFallbackAsync();

        // The DS system font (NDS12/"PixelUI") is the app's voice everywhere. Symbol glyphs
        // that NDS12 lacks (Ⓐ, ♂, ▼, ◓ ...) are pinned to "Rounded" at their few call sites.
        Style FontStyle(Type target, BindableProperty property) =>
            new(target) { Setters = { new Setter { Property = property, Value = "PixelUI" } } };
        Resources.Add(FontStyle(typeof(Label), Label.FontFamilyProperty));
        Resources.Add(FontStyle(typeof(Button), Button.FontFamilyProperty));
        Resources.Add(FontStyle(typeof(Entry), Entry.FontFamilyProperty));
        Resources.Add(FontStyle(typeof(Editor), Editor.FontFamilyProperty));
    }

    internal static void Trace(string message)
    {
#if ANDROID
        Android.Util.Log.Info("PKForgeBoot", message);
#else
        System.Diagnostics.Debug.WriteLine($"PKForgeBoot: {message}");
#endif
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        Trace("CreateWindow enter");
        // Never let a startup failure become a silent blank screen: show the error.
        try
        {
            var services = IPlatformApplication.Current?.Services
                ?? throw new InvalidOperationException("MAUI services are unavailable.");
            Trace("resolving HomePage");
            var page = services.GetRequiredService<Views.HomePage>();
            Trace("HomePage resolved");
            var window = new Window(new NavigationPage(page)
            {
                BarBackgroundColor = Theme.UiTokens.Navy1,
                BarTextColor = Colors.White,
            });
#if MACCATALYST
            // Laid out for a 16:9 handheld: open at that shape, never squeeze it below usable.
            window.Title = "PKForge";
            MacWindowChrome.Apply(window, new(1280, 760), new(960, 580));
            // The lower screen belongs to this window: closing it closes both, so the Dock can reopen cleanly.
            window.Destroying += (_, _) => _ = services.GetService<Domain.ISecondaryDisplayHost>()?.DismissAsync();
#endif
            return window;
        }
        catch (Exception error)
        {
            Trace($"CreateWindow FAILED: {error}");
            return new Window(new ContentPage
            {
                BackgroundColor = Theme.UiTokens.Navy0,
                Content = new ScrollView
                {
                    Content = new Label
                    {
                        Text = $"PKForge failed to start:\n\n{error}",
                        TextColor = Colors.White,
                        Margin = new Thickness(20),
                    },
                },
            });
        }
    }
}
