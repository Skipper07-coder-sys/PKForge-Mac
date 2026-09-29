namespace PKForge.App;

/// <summary>
/// Where the app keeps its own data and caches. Android uses MAUI's app-private folders. The
/// unsandboxed Mac build would otherwise get the bare ~/Library and ~/Library/Caches, so it uses
/// named folders inside them instead.
/// </summary>
public static class AppPaths
{
#if MACCATALYST
    private static readonly string Library =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library");

    // Created on first use, not in the static initializer: a failure there would be a permanent TypeInitializationException.
    private static string? _data, _cache;

    public static string Data => _data ??= Ensure(Path.Combine(Library, "Application Support", "PKForge"));

    public static string Cache => _cache ??= Ensure(Path.Combine(Library, "Caches", "PKForge"));

    private static string Ensure(string path)
    {
        try { Directory.CreateDirectory(path); }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine($"Could not create {path}: {e.Message}"); }
        return path;
    }
#else
    public static string Data => FileSystem.AppDataDirectory;

    public static string Cache => FileSystem.CacheDirectory;
#endif
}
