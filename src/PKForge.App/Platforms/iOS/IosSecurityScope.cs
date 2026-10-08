using Foundation;
using PKForge.App.Services;

namespace PKForge.App;

/// <summary>
/// Keeps saves picked outside PKForge's own folder reachable. iOS lets the app into a picked file
/// or folder only while its security-scoped URL is open, and forgets that on quit; a bookmark of
/// each pick reopens it on the next launch. Access then stays open while the app runs, so the
/// shared Mac code keeps working with plain paths.
/// </summary>
public static class IosSecurityScope
{
    private const string Key = "ios_security_bookmarks";
    private static readonly Dictionary<string, NSUrl> Open = new(StringComparer.Ordinal);

    /// <summary>Opens a picked URL and remembers it. Items inside PKForge's container need neither.</summary>
    public static void Grant(NSUrl url)
    {
        if (url.Path is not { } path || Open.ContainsKey(path)) return;
        if (!url.StartAccessingSecurityScopedResource()) return;
        Open[path] = url;
        var bookmark = url.CreateBookmarkData(0, null!, null, out var error);
        if (bookmark is null)
        {
            AppLog.Warn("files", $"No bookmark for {Path.GetFileName(path)}; it must be picked again after a restart: {error?.LocalizedDescription}");
            return;
        }
        var saved = Load();
        saved[path] = bookmark.GetBase64EncodedString(0);
        Save(saved);
    }

    /// <summary>Reopens every remembered pick. Called once at launch, before anything reads a save.</summary>
    public static void RestoreAll()
    {
        var saved = Load();
        var changed = false;
        foreach (var (path, encoded) in saved.ToList())
        {
            var url = NSUrl.FromBookmarkData(new NSData(encoded, NSDataBase64DecodingOptions.None), 0, null, out var stale, out var error);
            if (url is null || !url.StartAccessingSecurityScopedResource())
            {
                // Gone (deleted, share unmounted): keep it, a later launch may reach it again.
                AppLog.Warn("files", $"Could not reopen {Path.GetFileName(path)}: {error?.LocalizedDescription ?? "access refused"}");
                continue;
            }
            Open[path] = url;
            if (url.Path != path)
                AppLog.Warn("files", $"{Path.GetFileName(path)} moved to {url.Path}; link it again from its new place");
            if (stale && url.CreateBookmarkData(0, null!, null, out _) is { } fresh)
            {
                saved[path] = fresh.GetBase64EncodedString(0);
                changed = true;
            }
        }
        if (changed) Save(saved);
    }

    // One "path\u0001bookmark" entry per line, like the music library.
    private static Dictionary<string, string> Load()
    {
        var saved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Preferences.Default.Get(Key, "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('\u0001');
            if (parts.Length == 2) saved[parts[0]] = parts[1];
        }
        return saved;
    }

    private static void Save(Dictionary<string, string> saved) =>
        Preferences.Default.Set(Key, string.Join('\n', saved.Select(entry => $"{entry.Key}\u0001{entry.Value}")));
}
