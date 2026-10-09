using System.Text.Json;

namespace PKForge.App.Services;

/// <summary>
/// Saves opened lately, newest first: File ▸ Open Recent on the Mac. Only local paths are kept
/// (the Mac app is not sandboxed, so a path reopens as is); Android's document URIs are skipped.
/// </summary>
public static class RecentSaves
{
    private const string Key = "recent_saves";
    private const int Max = 10;

    /// <summary>Raised after the list changes (the Mac menu bar rebuilds its Open Recent menu).</summary>
    public static event Action? Changed;

    public static IReadOnlyList<string> All
    {
        get
        {
            try { return JsonSerializer.Deserialize<List<string>>(Preferences.Default.Get(Key, "[]")) ?? []; }
            catch (JsonException) { return []; }
        }
    }

    public static void Add(string documentId)
    {
        if (!Path.IsPathRooted(documentId) || documentId.Contains("://", StringComparison.Ordinal)) return;
        var list = All.Where(p => p != documentId).Prepend(documentId).Take(Max).ToList();
        Store(list);
    }

    public static void Remove(string documentId) => Store(All.Where(p => p != documentId).ToList());

    public static void Clear() => Store([]);

    private static void Store(List<string> list)
    {
        Preferences.Default.Set(Key, JsonSerializer.Serialize(list));
        Changed?.Invoke();
    }
}
