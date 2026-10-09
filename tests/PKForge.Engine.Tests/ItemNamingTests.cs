using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Held items are stored in each generation's own id space. The editor must name them from
/// the open save's table: Gen 1-3 number items differently from the modern list.
/// </summary>
public sealed class ItemNamingTests
{
    [Theory]
    [InlineData(GameVersion.C, 156, "Sacred Ash")]
    [InlineData(GameVersion.E, 156, "Hondew Berry")]
    public void TheSavesOwnTableNamesItsItems(GameVersion version, int storedId, string expected)
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(version, "TEST", LanguageID.English), null);
        var names = session.GetItemNames();
        Assert.Equal(expected, names[storedId]);
        Assert.NotEqual(expected, GameInfo.GetStrings("en").itemlist[storedId]); // the modern list would name it wrong
    }

    /// <summary>Sprites and effects are filed under modern names: renamed Gen 3 items find them by those.</summary>
    [Theory]
    [InlineData(18, "Parlyz Heal", "Paralyze Heal")]
    [InlineData(206, "BlackGlasses", "Black Glasses")]
    [InlineData(212, "NeverMeltIce", "Never-Melt Ice")]
    [InlineData(209, "Mystic Water", "Mystic Water")]
    public void RenamedGen3ItemsFindTheirModernArt(int storedId, string gameName, string modernName)
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(GameVersion.E, "TEST", LanguageID.English), null);
        Assert.Equal(gameName, session.GetItemNames()[storedId]);
        Assert.Equal(modernName, session.GetItemArtNames()[storedId]);
        Assert.NotNull(PKForge.Domain.DexFacts.Item(session.GetItemArtNames()[storedId]));
    }

    /// <summary>Every Gen 3 item a Pokémon can hold has an effect line under its modern name.</summary>
    [Fact]
    public void EveryHoldableGen3ItemHasADescription()
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(GameVersion.E, "TEST", LanguageID.English), null);
        var art = session.GetItemArtNames();
        var holdable = new MonInfoService().GetHeldItems(session);
        Assert.True(holdable.Count > 100, $"only {holdable.Count} holdable items");
        var missing = holdable
            .Where(id => id > 0 && PKForge.Domain.DexFacts.Item(art[id]) is null)
            .Select(id => $"{id}:{session.GetItemNames()[id]}→{art[id]}")
            .ToList();
        Assert.True(missing.Count == 0, string.Join(", ", missing));
    }

    /// <summary>The Bank's editor opens a banked mon in a blank save of its own game: that session names its item too.</summary>
    [Fact]
    public void ABankedGen3MonNamesItsItemFromItsOwnGame()
    {
        var save = BlankSaveFile.Get(GameVersion.E, "TEST", LanguageID.English);
        var mon = (PK3)save.BlankPKM;
        mon.Species = (ushort)Species.Castform;
        mon.CurrentLevel = 30;
        mon.Language = (int)LanguageID.English;
        mon.HeldItem = 209; // Gen 3 Mystic Water
        mon.RefreshChecksum();

        using var session = new SaveEngine().OpenEntitySession(mon.Data.ToArray(), "test")!;
        var held = session.ReadEntity(0, 0).HeldItem;
        Assert.Equal(209, held);
        Assert.Equal("Mystic Water", session.GetItemNames()[held]);
        Assert.Equal("Micle Berry", GameInfo.GetStrings("en").itemlist[held]); // what the editor used to show
    }
}
