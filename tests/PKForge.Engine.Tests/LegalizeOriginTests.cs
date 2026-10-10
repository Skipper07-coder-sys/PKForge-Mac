using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>Legalize keeps where a caught Pokémon came from instead of making it hatched.</summary>
public sealed class LegalizeOriginTests
{
    private const ushort OldChateau = 70; // Gen 4 met location
    private const ushort LavaridgeTown = 3; // Gen 3 met location: nothing is wild there
    private const ushort RileysEgg = 2010;  // Gen 4 egg location: the Riolu egg Riley gives on Iron Island

    [Fact]
    public void AShinyCatchStaysCaughtAtItsLocationInItsBall()
    {
        var save = BlankSaveFile.Get(GameVersion.Pt, "PKForge", LanguageID.English);
        var gastly = new PK4
        {
            Species = (ushort)Species.Gastly,
            CurrentLevel = 16,
            MetLevel = 16,
            MetLocation = OldChateau,
            Version = GameVersion.Pt,
            Ball = (byte)PKHeX.Core.Ball.Master,
            Language = (int)LanguageID.English,
            OriginalTrainerName = save.OT,
            ID32 = save.ID32,
            Move1 = (ushort)Move.Lick,
        };
        gastly.SetPIDGender(0); // male
        gastly.SetShiny();      // a shiny PID that breaks the wild PID/IV link
        gastly.Heal();
        gastly.RefreshChecksum();
        Assert.False(new LegalityAnalysis(gastly).Valid);

        var repaired = LegalizerService.LegalizeKeepingOrigin(save, gastly);

        Assert.True(new LegalityAnalysis(repaired).Valid, new LegalityAnalysis(repaired).Report());
        Assert.False(repaired.WasEgg);
        Assert.Equal(OldChateau, repaired.MetLocation);
        Assert.Equal((byte)PKHeX.Core.Ball.Master, repaired.Ball);
        Assert.True(repaired.IsShiny);
        Assert.Equal(0, repaired.Gender);
    }

    [Fact]
    public void ACatchIsRebuiltInItsOwnGame()
    {
        // Met in Lavaridge Town, where nothing is wild, so no encounter there fits and Auto-Legality
        // makes it. That searched the newest game first (Colosseum/XD, then LeafGreen): the Poochyena
        // came back hatched in LeafGreen, though Emerald has the same catch on Route 101.
        using var session = new SaveEngine().OpenBlankSession(3);
        Assert.True(new LegalizerService().GenerateFromShowdown(session, 0, 0, "Poochyena\nLevel: 6").Success);
        var engine = (SaveEngineSession)session;
        var broken = engine.GetEntity(0, 0);
        Assert.Equal(GameVersion.E, broken.Version);
        Assert.NotEqual(0, broken.MetLevel); // caught
        broken.MetLocation = LavaridgeTown;
        broken.MetLevel = 3;
        broken.RefreshChecksum();
        engine.SaveFile.SetBoxSlotAtIndex(broken, 0, 0, EntityImportSettings.None);
        Assert.False(new LegalityAnalysis(broken).Valid);

        var outcome = new LegalizerService().LegalizeSlot(session, 0, 0);

        Assert.True(outcome.Success, outcome.Message);
        var placed = engine.GetEntity(0, 0);
        Assert.True(new LegalityAnalysis(placed).Valid, new LegalityAnalysis(placed).Report());
        Assert.Equal(GameVersion.E, placed.Version);
        Assert.NotEqual(0, placed.MetLevel); // still caught, not hatched
        Assert.Equal(broken.OriginalTrainerName, placed.OriginalTrainerName);
        Assert.Equal(broken.ID32, placed.ID32);
        Assert.Equal(6, placed.CurrentLevel);
        Assert.Contains("met location changed", outcome.Message);
    }

    [Fact]
    public void AGiftEggStaysThatGiftEgg()
    {
        // Riley's Riolu egg is a gift, not a bred egg. Only a bred egg counted as a hatched Pokémon's
        // own kind of origin, so the gift never led the search and it came back from the Day-Care.
        using var session = new SaveEngine().OpenBlankSession(4);
        Assert.True(new LegalizerService().GenerateFromShowdown(session, 0, 0, "Riolu\nLevel: 5").Success);
        var engine = (SaveEngineSession)session;
        var broken = engine.GetEntity(0, 0);
        Assert.Equal(RileysEgg, broken.EggLocation);
        broken.Ball = (byte)PKHeX.Core.Ball.Master; // the egg comes in a Poké Ball: only a rebuild fixes this
        broken.RefreshChecksum();
        engine.SaveFile.SetBoxSlotAtIndex(broken, 0, 0, EntityImportSettings.None);
        Assert.False(new LegalityAnalysis(broken).Valid);

        var outcome = new LegalizerService().LegalizeSlot(session, 0, 0);

        Assert.True(outcome.Success, outcome.Message);
        var placed = engine.GetEntity(0, 0);
        Assert.True(new LegalityAnalysis(placed).Valid, new LegalityAnalysis(placed).Report());
        Assert.Equal(RileysEgg, placed.EggLocation);
        Assert.Equal(broken.MetLocation, placed.MetLocation);
        Assert.Equal(broken.Version, placed.Version);
        Assert.Equal(broken.OriginalTrainerName, placed.OriginalTrainerName);
    }
}
