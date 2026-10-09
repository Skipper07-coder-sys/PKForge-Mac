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
            .Where(id => id > 0 && session.GetItemDescription(id) is null)
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

    /// <summary>The summary's held-item line finds the effect of an item Gen 3 spells its own way.</summary>
    [Fact]
    public void TheSummaryDescribesARenamedGen3Item()
    {
        var save = BlankSaveFile.Get(GameVersion.E, "TEST", LanguageID.English);
        var mon = (PK3)save.BlankPKM;
        mon.Species = (ushort)Species.Absol;
        mon.CurrentLevel = 30;
        mon.Language = (int)LanguageID.English;
        mon.HeldItem = 206; // Gen 3 BlackGlasses
        mon.RefreshChecksum();
        save.SetBoxSlotAtIndex(mon, 0, 0);

        using var session = new SaveEngineSession(save, null);
        var summary = new MonSummaryService().Build(session, 0, 0)!;
        Assert.Equal("BlackGlasses", summary.HeldItemName);
        Assert.Equal("A hold item that raises the power of DARK-type moves.", summary.HeldItemEffect); // Emerald's own words
    }

    /// <summary>Each game numbers its machines: the disc's colour and the effect line follow this game's move.</summary>
    [Theory]
    [InlineData(GameVersion.E, "TM01", "TM Fighting", "Focus Punch")]
    [InlineData(GameVersion.E, "TM26", "TM Ground", "Earthquake")]
    [InlineData(GameVersion.E, "HM05", "HM Normal", "Flash")]
    [InlineData(GameVersion.E, "HM08", "HM Water", "Dive")]
    [InlineData(GameVersion.RD, "TM01", "TM Normal", "Mega Punch")]
    [InlineData(GameVersion.C, "TM01", "TM Fighting", "Dynamic Punch")]
    [InlineData(GameVersion.C, "HM06", "HM Water", "Whirlpool")]
    [InlineData(GameVersion.Pt, "HM05", "HM Flying", "Defog")]
    [InlineData(GameVersion.HG, "HM05", "HM Water", "Whirlpool")]
    [InlineData(GameVersion.B, "TM01", "TM Dark", "Hone Claws")]
    [InlineData(GameVersion.SN, "TM01", "TM Normal", "Work Up")]
    [InlineData(GameVersion.SW, "TM00", "TM Normal", "Mega Punch")]
    [InlineData(GameVersion.BD, "TM01", "TM Fighting", "Focus Punch")]
    [InlineData(GameVersion.SL, "TM001", "TM Normal", "Take Down")]
    // The last machine of each table: the numbering holds to the end, not only at TM01.
    [InlineData(GameVersion.RD, "TM50", "TM Normal", "Substitute")]
    [InlineData(GameVersion.C, "TM50", "TM Ghost", "Nightmare")]
    [InlineData(GameVersion.E, "TM50", "TM Fire", "Overheat")]
    [InlineData(GameVersion.Pt, "TM92", "TM Psychic", "Trick Room")]
    [InlineData(GameVersion.B2, "TM95", "TM Dark", "Snarl")]
    [InlineData(GameVersion.US, "TM100", "TM Normal", "Confide")]
    [InlineData(GameVersion.SW, "TM99", "TM Dragon", "Breaking Swipe")]
    [InlineData(GameVersion.SL, "TM229", "TM Fighting", "Upper Hand")]
    public void MachinesShowThisGamesMove(GameVersion version, string machine, string art, string move)
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(version, "TEST", LanguageID.English), null);
        var id = session.GetItemNames().ToList().IndexOf(machine);
        Assert.True(id > 0, $"{machine} is not in {version}'s item table");
        Assert.Equal(art, session.GetItemArtNames()[id]);
        Assert.Equal($"Teaches {move} to a compatible Pokémon.", session.GetItemDescription(id));
    }

    /// <summary>Every machine of every game with a table lands on a disc PokeAPI has (checked 2026-10-09).</summary>
    [Theory]
    [InlineData(GameVersion.RD)]
    [InlineData(GameVersion.C)]
    [InlineData(GameVersion.E)]
    [InlineData(GameVersion.FR)]
    [InlineData(GameVersion.Pt)]
    [InlineData(GameVersion.HG)]
    [InlineData(GameVersion.B2)]
    [InlineData(GameVersion.US)]
    [InlineData(GameVersion.SW)]
    [InlineData(GameVersion.BD)]
    [InlineData(GameVersion.SL)]
    public void EveryMachineHasADisc(GameVersion version)
    {
        string[] hmTypes = ["Normal", "Fighting", "Flying", "Water"];
        var save = BlankSaveFile.Get(version, "TEST", LanguageID.English);
        // The item table lists every modern item; only the machines this game's bag can hold count.
        var inGame = save.Inventory.Pouches.SelectMany(p => save.Inventory.Info.GetItems(p.Type).ToArray()).Select(i => (int)i).ToHashSet();
        using var session = new SaveEngineSession(save, null);
        var names = session.GetItemNames();
        var art = session.GetItemArtNames();
        var machines = Enumerable.Range(0, names.Count)
            .Where(id => inGame.Contains(id) && System.Text.RegularExpressions.Regex.IsMatch(names[id], "^(TM|HM)[0-9]+$")).ToList();
        Assert.True(machines.Count >= 50, $"only {machines.Count} machines");
        var wrong = machines.Where(id => art[id].Split(' ') is not [var kind, var type]
                || !(kind == "TM" ? PKForge.Domain.TypeFacts.Names.Contains(type) : kind == "HM" && hmTypes.Contains(type)))
            .Select(id => $"{names[id]}→{art[id]}").ToList();
        Assert.True(wrong.Count == 0, string.Join(", ", wrong));
        Assert.All(machines, id => Assert.StartsWith("Teaches ", session.GetItemDescription(id)));
    }

    /// <summary>A TR says its move too; PokeAPI has no TR art, so its name is left alone.</summary>
    [Fact]
    public void RecordsSayTheirMove()
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(GameVersion.SW, "TEST", LanguageID.English), null);
        var id = session.GetItemNames().ToList().IndexOf("TR00");
        Assert.Equal("TR00", session.GetItemArtNames()[id]);
        Assert.Equal("Teaches Swords Dance to a compatible Pokémon.", session.GetItemDescription(id));
    }

    /// <summary>
    /// A game without a machine table in PKHeX keeps the shared names; its TM text is its own bag's
    /// (X's TM01 is Hone Claws), and a game newer than the bag-text data keeps the shared text, since
    /// borrowing an older game's TM01 would describe another move.
    /// </summary>
    [Fact]
    public void GamesWithoutAMachineTableReadTheirOwnBagText()
    {
        using var x = new SaveEngineSession(BlankSaveFile.Get(GameVersion.X, "TEST", LanguageID.English), null);
        var id = x.GetItemNames().ToList().IndexOf("TM01");
        Assert.Equal("TM01", x.GetItemArtNames()[id]);
        Assert.Equal("The user sharpens its claws to boost its Attack stat and accuracy.", x.GetItemDescription(id));

        using var za = new SaveEngineSession(BlankSaveFile.Get(GameVersion.ZA, "TEST", LanguageID.English), null);
        var zaId = za.GetItemNames().ToList().IndexOf("TM001");
        if (zaId < 0) zaId = za.GetItemNames().ToList().IndexOf("TM01");
        Assert.True(zaId >= 0);
        Assert.Equal(PKForge.Domain.DexFacts.Item(za.GetItemArtNames()[zaId]), za.GetItemDescription(zaId));
    }

    /// <summary>Items read the open game's own bag text: places and rules change between games.</summary>
    [Theory]
    [InlineData(GameVersion.E, "Safari Ball", "A special BALL that is used only in the SAFARI ZONE.")]
    [InlineData(GameVersion.Pt, "Safari Ball", "A special Poké Ball that is used only in the Great Marsh. It is decorated in a camouflage pattern.")]
    [InlineData(GameVersion.X, "Soul Dew", "A wondrous orb to be held by either Latios or Latias. It raises both the Sp. Atk and Sp. Def stats.")]
    [InlineData(GameVersion.SN, "Soul Dew", "A wondrous orb to be held by either Latios or Latias. It raises the power of Psychic- and Dragon-type moves.")]
    public void ItemsReadTheirGamesOwnText(GameVersion version, string item, string expected)
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(version, "TEST", LanguageID.English), null);
        var id = session.GetItemNames().ToList().IndexOf(item);
        Assert.True(id > 0, item);
        Assert.Equal(expected, session.GetItemDescription(id));
    }

    /// <summary>Every item id of every game names its art and its text without throwing (Z-A's table holds nulls).</summary>
    [Theory]
    [InlineData(GameVersion.C)]
    [InlineData(GameVersion.E)]
    [InlineData(GameVersion.Pt)]
    [InlineData(GameVersion.B2)]
    [InlineData(GameVersion.X)]
    [InlineData(GameVersion.UM)]
    [InlineData(GameVersion.GP)]
    [InlineData(GameVersion.SW)]
    [InlineData(GameVersion.BD)]
    [InlineData(GameVersion.PLA)]
    [InlineData(GameVersion.SL)]
    [InlineData(GameVersion.ZA)]
    public void EveryItemIdNamesItsArtAndText(GameVersion version)
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(version, "TEST", LanguageID.English), null);
        var art = session.GetItemArtNames();
        Assert.Equal(session.GetItemNames().Count, art.Count);
        Assert.All(art, Assert.NotNull);
        for (var id = 0; id < art.Count; id++)
            _ = session.GetItemDescription(id);
    }

    /// <summary>Gen 1-2 bags print no item text: those games keep the general line.</summary>
    [Fact]
    public void GameBoyGamesKeepTheGeneralText()
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(GameVersion.C, "TEST", LanguageID.English), null);
        var id = session.GetItemNames().ToList().IndexOf("Leftovers");
        Assert.True(id > 0);
        Assert.Equal(PKForge.Domain.DexFacts.Item("Leftovers"), session.GetItemDescription(id));
    }

    /// <summary>Every game the engine opens maps to a place in the bag-text order (0 only for Gen 1-2 and odd entries).</summary>
    [Theory]
    [InlineData(GameVersion.E, 8)]
    [InlineData(GameVersion.FR, 11)]
    [InlineData(GameVersion.HG, 14)]
    [InlineData(GameVersion.W2, 16)]
    [InlineData(GameVersion.UM, 20)]
    [InlineData(GameVersion.SH, 22)]
    [InlineData(GameVersion.BD, 25)]
    [InlineData(GameVersion.VL, 27)]
    [InlineData(GameVersion.C, 0)]
    public void GamesMapToTheBagTextOrder(GameVersion version, int expected) =>
        Assert.Equal(expected, SaveEngineSession.ItemTextGame(version));

    /// <summary>Bank search mixes generations: a banked Gen 3 item is counted and named by its national id.</summary>
    [Fact]
    public void BankSearchCountsAGen3ItemUnderItsNationalId()
    {
        var save = BlankSaveFile.Get(GameVersion.E, "TEST", LanguageID.English);
        var mon = (PK3)save.BlankPKM;
        mon.Species = (ushort)Species.Castform;
        mon.CurrentLevel = 30;
        mon.Language = (int)LanguageID.English;
        mon.HeldItem = 209; // Gen 3 Mystic Water
        mon.RefreshChecksum();

        var info = new SaveEngine().TryDescribeEntity(mon.Data.ToArray(), "test")!;
        Assert.Equal(209, info.HeldItem); // the index keeps the game's own id
        var national = EntityBytes.NationalItem(info, info.HeldItem!.Value);
        Assert.Equal("Mystic Water", GameInfo.GetStrings("en").itemlist[national]);
        Assert.Equal(national, EntityBytes.NationalItem(info with { Format = null }, 209)); // pre-format entries too

        var modern = info with { Format = "PK8", Generation = 8 };
        Assert.Equal(209, EntityBytes.NationalItem(modern, 209)); // Micle Berry stays Micle Berry
    }
}
