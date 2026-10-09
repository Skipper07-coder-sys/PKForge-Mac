using System.Diagnostics;

namespace PKForge.App.Services;

/// <summary>
/// Timing marks for the test harness (tools/pkf). Marks compile away unless the app is built
/// with PKF_AUTOMATION, so the shipping app pays nothing for them.
/// </summary>
public static class Perf
{
    public readonly record struct Entry(string Name, double AtMs, double? TookMs);

    private static readonly Lock Gate = new();
    private static readonly List<Entry> Entries = [];
    private static readonly DateTime Started = SafeStartTime();

    /// <summary>Milliseconds since the process started (runtime start-up included).</summary>
    public static double Now => (DateTime.Now - Started).TotalMilliseconds;

    [Conditional("PKF_AUTOMATION")]
    public static void Mark(string name)
    {
        lock (Gate) Entries.Add(new(name, Now, null));
    }

    [Conditional("PKF_AUTOMATION")]
    public static void Took(string name, long startTimestamp)
    {
        var took = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        lock (Gate) Entries.Add(new(name, Now, took));
    }

    public static IReadOnlyList<Entry> Snapshot()
    {
        lock (Gate) return [.. Entries];
    }

    public static void Clear()
    {
        lock (Gate) Entries.Clear();
    }

    private static DateTime SafeStartTime()
    {
        try { return Process.GetCurrentProcess().StartTime; }
        catch (Exception) { return DateTime.Now; }
    }
}
