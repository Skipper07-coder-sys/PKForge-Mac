using System.IO.Compression;
using System.Text;

namespace PKForge.Domain;

/// <summary>
/// Each game's own bag text for its items (English), built by tools/DexFacts/itemtext.py from
/// PokeAPI's data (github.com/PokeAPI/pokeapi, BSD-3-Clause, © 2014 Paul Hallett and PokeAPI
/// contributors). Games are numbered by PokeAPI's chronological version-group order (Ruby/Sapphire
/// 7, Emerald 8, FireRed/LeafGreen 11 … Sword/Shield 22), and a game without its own line reads
/// the nearest earlier game's. Items are keyed like <see cref="DexFacts"/>: by normalised name.
/// </summary>
public sealed class ItemTextTable
{
    // Per item, its lines oldest game first.
    private readonly Dictionary<string, List<(int Game, string Text)>> _texts;
    private readonly HashSet<int> _gamesWithText;

    private ItemTextTable(Dictionary<string, List<(int Game, string Text)>> texts, HashSet<int> gamesWithText) =>
        (_texts, _gamesWithText) = (texts, gamesWithText);

    public int ItemCount => _texts.Count;

    /// <summary>
    /// True when the game has bag text of its own; a game without (newer than the data) reads older
    /// games' lines, which suits most items but not machines, whose numbers teach other moves there.
    /// </summary>
    public bool HasOwnText(int game) => _gamesWithText.Contains(game);

    /// <summary>
    /// What the bag of game <paramref name="game"/> (a version-group order) says about the item, or
    /// the nearest earlier game's line; null when no game up to it has one.
    /// </summary>
    public string? For(string? name, int game)
    {
        if (string.IsNullOrWhiteSpace(name) || !_texts.TryGetValue(DexFacts.NormalizeName(name), out var lines))
            return null;
        string? text = null;
        foreach (var line in lines)
        {
            if (line.Game > game) break;
            text = line.Text;
        }
        return text;
    }

    /// <summary>Reads the gzip'd TSV the build script writes (malformed rows are skipped).</summary>
    public static ItemTextTable Parse(Stream gzip)
    {
        var texts = new Dictionary<string, List<(int Game, string Text)>>(StringComparer.Ordinal);
        var games = new HashSet<int>();
        using var inflate = new GZipStream(gzip, CompressionMode.Decompress);
        using var reader = new StreamReader(inflate, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var f = line.Split('\t');
            if (f is ["G", var own] && int.TryParse(own, out var ownGame))
            {
                games.Add(ownGame);
                continue;
            }
            if (f.Length < 4 || f[0] != "T" || !int.TryParse(f[1], out var game) || f[2].Length == 0 || f[3].Length == 0)
                continue;
            if (!texts.TryGetValue(f[2], out var lines)) texts[f[2]] = lines = [];
            lines.Add((game, f[3]));
        }
        foreach (var lines in texts.Values)
            lines.Sort((a, b) => a.Game.CompareTo(b.Game));
        return new ItemTextTable(texts, games);
    }
}

/// <summary>The embedded <see cref="ItemTextTable"/>, loaded once on first use.</summary>
public static class ItemTexts
{
    private static readonly Lazy<ItemTextTable> Table = new(() =>
    {
        using var stream = typeof(ItemTexts).Assembly.GetManifestResourceStream("PKForge.Domain.itemtext.tsv.gz")
            ?? throw new InvalidOperationException("itemtext.tsv.gz is not embedded");
        return ItemTextTable.Parse(stream);
    });

    public static ItemTextTable Default => Table.Value;
}
