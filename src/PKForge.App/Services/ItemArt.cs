using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>
/// Item sprites from the PokeAPI sprite database, keyed by the item's English name
/// ("Master Ball" → items/master-ball.png). Fetched once, cached forever.
/// Misses expire after a day: a network blip must not blind an item forever.
/// </summary>
public static class ItemArt
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim Gate = new(6);
    private static TimeSpan MissTtl { get; } = TimeSpan.FromHours(20);

    /// <summary>
    /// PokeAPI slugs are plain ascii with dashes. Names arrive with diacritics
    /// ("Poké Doll") that must fold to their base letters (poke-doll) - the old
    /// filter turned é into a dash and every accented item 404'd forever.
    /// </summary>
    public static string Slug(string itemName) => SpritePack.ItemSlug(itemName);

    /// <summary>True when the icon is on disk, or a recent attempt found none upstream.</summary>
    public static bool IsCachedOrKnownMissing(string itemName)
    {
        var cache = Path.Combine(AppPaths.Data, "items", Slug(itemName) + ".png");
        return File.Exists(cache) || IsFreshMiss(cache + ".miss");
    }

    /// <summary>Local path of the item's sprite, or null (unknown item / offline first time).</summary>
    public static async Task<string?> GetAsync(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return null;
        var slug = Slug(itemName);
        var directory = Path.Combine(AppPaths.Data, "items");
        var cache = Path.Combine(directory, slug + ".png");
        var miss = cache + ".miss";
        if (File.Exists(cache)) return cache;
        if (IsFreshMiss(miss)) return null;

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(cache)) return cache;
            Directory.CreateDirectory(directory);
            var bytes = await Http.GetByteArrayAsync(
                $"https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/items/{slug}.png").ConfigureAwait(false);
            await File.WriteAllBytesAsync(cache, bytes).ConfigureAwait(false);
            return cache;
        }
        catch
        {
            try { await File.WriteAllTextAsync(miss, DateTimeOffset.UtcNow.ToString("O")).ConfigureAwait(false); }
            catch { }
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Fetches, in the background, the sprites of <paramref name="itemNames"/> not on disk yet, so
    /// a picker shows them next time. The sprite pack holds the modern items only; older ones (Gen 3
    /// Mail) arrive this way. Machines are skipped: PokeAPI draws TMs by type, never by number.
    /// </summary>
    public static void WarmMissing(IEnumerable<string> itemNames)
    {
        var wanted = itemNames.Where(n => !string.IsNullOrWhiteSpace(n) && !IsMachine(Slug(n)) && !IsCachedOrKnownMissing(n))
            .Distinct().ToList();
        if (wanted.Count > 0)
            _ = Task.Run(() => Task.WhenAll(wanted.Select(GetAsync)));
    }

    private static bool IsMachine(string slug) =>
        slug.Length > 2 && slug[..2] is "tm" or "hm" or "tr" && slug[2..].All(char.IsAsciiDigit);

    private static bool IsFreshMiss(string miss)
    {
        try
        {
            if (!File.Exists(miss)) return false;
            var written = DateTimeOffset.Parse(File.ReadAllText(miss), System.Globalization.CultureInfo.InvariantCulture);
            if (DateTimeOffset.UtcNow - written < MissTtl) return true;
            File.Delete(miss); // expired: let the next look retry
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A tiny pixel capsule tile drawn once and reused whenever a sprite is missing,
    /// so bag rows never render blank. Pure-local: no network, no dependency on names.
    /// </summary>
    public static string PlaceholderPath()
    {
        if (_placeholder is { } known) return known;
        var path = Path.Combine(AppPaths.Data, "items", "_placeholder.png");
        // Older builds wrote a tile no decoder could read: replace it rather than reuse it.
        if (IsPng(path)) return _placeholder = path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 24x24: navy plate, cyan rim, white question dot - reads as "item" at row size.
            var px = new byte[24 * 24 * 4];
            for (var y = 0; y < 24; y++)
                for (var x = 0; x < 24; x++)
                {
                    var edge = x is 0 or 23 || y is 0 or 23;
                    var inner = x is >= 2 and <= 21 && y is >= 2 and <= 21;
                    var r = edge ? (byte)0x2E : inner ? (byte)0x14 : (byte)0x20;
                    var g = edge ? (byte)0x8A : inner ? (byte)0x2E : (byte)0x3C;
                    var b = edge ? (byte)0xC8 : inner ? (byte)0x6E : (byte)0x88;
                    // question-mark dot cluster
                    if (inner && y is >= 8 and <= 15 && ((y < 11 && x is >= 10 and <= 14) || (y is 11 or 14 && x is >= 12 and <= 14) || (y is >= 12 and <= 13 && x == 13) || (y is >= 14 and <= 16 && x is >= 13 and <= 14)))
                    { r = 0xFC; g = 0xFD; b = 0xFE; }
                    var o = (y * 24 + x) * 4;
                    px[o] = r; px[o + 1] = g; px[o + 2] = b; px[o + 3] = 0xFF;
                }
            File.WriteAllBytes(path, EncodePng(px, 24, 24));
            return _placeholder = path;
        }
        catch
        {
            return path; // best effort: a blank row beats a crash
        }
    }

    private static string? _placeholder;

    // "\x89PNG"u8 would not do: \x89 is the character U+0089, two bytes in UTF-8.
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static bool IsPng(string path)
    {
        try
        {
            // The signature, then the first chunk's length and the IHDR type.
            using var file = File.OpenRead(path);
            Span<byte> head = stackalloc byte[16];
            return file.ReadAtLeast(head, 16, throwOnEndOfStream: false) == 16
                && head[..8].SequenceEqual(PngSignature) && head[12..].SequenceEqual("IHDR"u8);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        var stride = 1 + width * 4;
        var raw = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            raw[y * stride] = 0; // filter: none
            Array.Copy(rgba, y * width * 4, raw, y * stride + 1, width * 4);
        }
        using var output = new MemoryStream();
        output.Write(PngSignature);
        Span<byte> ihdr = stackalloc byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        WriteChunk(output, "IHDR"u8, ihdr.ToArray());
        WriteChunk(output, "IDAT"u8, Deflate(raw));
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream to, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        to.Write(length);
        to.Write(type);
        to.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (var b in type) crc = CrcStep(crc, b);
        foreach (var b in data) crc = CrcStep(crc, b);
        Span<byte> tail = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tail, ~crc);
        to.Write(tail);
    }

    private static uint CrcStep(uint crc, byte b)
    {
        crc ^= b;
        for (var k = 0; k < 8; k++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        return crc;
    }

    /// <summary>PNG image data is a zlib stream (header + Adler-32), not bare deflate.</summary>
    private static byte[] Deflate(byte[] raw)
    {
        using var zipped = new MemoryStream();
        using (var deflate = new System.IO.Compression.ZLibStream(zipped, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw);
        return zipped.ToArray();
    }

    /// <summary>
    /// Upgrade sweep: misses recorded by the old broken slugger (é mangled to a dash)
    /// poisoned items forever. One launch, wipe them all so every item retries.
    /// </summary>
    public static void PurgeLegacyMisses()
    {
        try
        {
            var directory = Path.Combine(AppPaths.Data, "items");
            if (!Directory.Exists(directory)) return;
            foreach (var miss in Directory.EnumerateFiles(directory, "*.miss"))
                File.Delete(miss);
        }
        catch
        {
            // Never block startup on a cache sweep.
        }
    }
}
