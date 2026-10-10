using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Legalize rebuilt a broken Pokémon from its encounter and kept only ball, level, moves and
/// name: the held item was dropped, the EVs zeroed and the IVs re-rolled. What the player
/// trained is kept wherever it stays legal; EVs over the caps are trimmed in proportion.
/// </summary>
public sealed class LegalizeKeepsTrainingTests
{
    private static readonly int[] Trimmed = [85, 85, 85, 85, 85, 85]; // 6 × 252 scaled to 510

    private static (ISaveEngineSession Session, SaveEngineSession Engine, PKM Broken) Break(int generation, string species, int? badMove)
    {
        var session = new SaveEngine().OpenBlankSession(generation);
        Assert.True(new LegalizerService().GenerateFromShowdown(session, 0, 0, $"{species} @ Leftovers\nLevel: 45").Success);
        session.ApplyEdit(0, 0, new EntityEdit(Level: 47, Move4: badMove, EVs: [252, 252, 252, 252, 252, 252]));
        var engine = (SaveEngineSession)session;
        var broken = engine.GetEntity(0, 0);
        Assert.NotEqual(0, broken.HeldItem);
        Assert.False(new LegalityAnalysis(broken).Valid);
        return (session, engine, broken);
    }

    private static byte[] Stored(PKM pk)
    {
        var data = new byte[pk.SIZE_STORED];
        pk.WriteDecryptedDataStored(data);
        return data;
    }

    private static int[] EVs(PKM pk)
    {
        var evs = new int[6];
        pk.GetEVs(evs);
        return evs;
    }

    [Theory]
    [InlineData(3, "Swampert")]
    [InlineData(8, "Gardevoir")]
    public void EVsOverTheCapAreTrimmedAndEverythingElseIsKept(int generation, string species)
    {
        var (session, engine, broken) = Break(generation, species, badMove: null);
        using (session)
        {
            var outcome = new LegalizerService().LegalizeSlot(session, 0, 0);
            Assert.True(outcome.Success, outcome.Message);
            var placed = engine.GetEntity(0, 0);
            Assert.True(new LegalityAnalysis(placed).Valid, new LegalityAnalysis(placed).Report());
            Assert.Equal(Trimmed, EVs(placed));
            // Not rebuilt: byte for byte the same Pokémon (item, IVs, PID, met data...) but the EVs.
            var expected = broken.Clone();
            expected.SetEVs(Trimmed);
            expected.RefreshChecksum();
            Assert.Equal(Stored(expected), Stored(placed));
        }
    }

    [Fact]
    public void AnUnlearnableMoveIsDroppedAndNothingElseChanges()
    {
        var (session, engine, broken) = Break(3, "Swampert", badMove: (int)Move.Transform);
        using (session)
        {
            var outcome = new LegalizerService().LegalizeSlot(session, 0, 0);
            Assert.True(outcome.Success, outcome.Message);
            var placed = engine.GetEntity(0, 0);
            Assert.True(new LegalityAnalysis(placed).Valid, new LegalityAnalysis(placed).Report());
            var expected = broken.Clone();
            expected.SetMoves([broken.Move1, broken.Move2, broken.Move3]);
            expected.SetEVs(Trimmed);
            expected.RefreshChecksum();
            Assert.Equal(Stored(expected), Stored(placed));
        }
    }

    [Theory]
    [InlineData(3, "Swampert")]
    [InlineData(8, "Gardevoir")]
    public void ARebuiltMonKeepsItsItemIVsAndTrimmedEVs(int generation, string species)
    {
        // An unlearnable move and a met level its encounter never has, so it has to be rebuilt.
        var (session, engine, broken) = Break(generation, species, badMove: (int)Move.Transform);
        broken.MetLevel = (byte)(new LegalityAnalysis(broken).EncounterOriginal.LevelMax + 1);
        broken.RefreshChecksum();
        engine.SaveFile.SetBoxSlotAtIndex(broken, 0, 0, EntityImportSettings.None);
        using (session)
        {
            var outcome = new LegalizerService().LegalizeSlot(session, 0, 0);
            Assert.True(outcome.Success, outcome.Message);
            var placed = engine.GetEntity(0, 0);
            Assert.True(new LegalityAnalysis(placed).Valid, new LegalityAnalysis(placed).Report());
            Assert.NotEqual((ushort)Move.Transform, placed.Move4);
            Assert.Equal(broken.Species, placed.Species);
            Assert.Equal(47, placed.CurrentLevel);
            Assert.Equal(Trimmed, EVs(placed));
            Assert.Equal(broken.HeldItem, placed.HeldItem);
            Assert.Equal(broken.GetIVs(), placed.GetIVs());
        }
    }

    [Fact]
    public void AMonWithNoEXPSinceItWasMetKeepsVitaminEVs()
    {
        // Gen 3/4: a Pokémon that has gained no EXP since it was met can only have vitamin EVs
        // (multiples of 10, at most 100 each), so six 85s are not legal for it but six 80s are.
        using var session = new SaveEngine().OpenBlankSession(3);
        Assert.True(new LegalizerService().GenerateFromShowdown(session, 0, 0, "Poochyena\nLevel: 2").Success);
        var engine = (SaveEngineSession)session;
        var broken = engine.GetEntity(0, 0);
        broken.EXP = Experience.GetEXP(broken.MetLevel, broken.PersonalInfo.EXPGrowth); // no EXP since it was met
        broken.SetEVs([252, 252, 252, 252, 252, 252]);
        broken.RefreshChecksum();
        engine.SaveFile.SetBoxSlotAtIndex(broken, 0, 0, EntityImportSettings.None);
        Assert.False(new LegalityAnalysis(broken).Valid);

        var outcome = new LegalizerService().LegalizeSlot(session, 0, 0);
        Assert.True(outcome.Success, outcome.Message);
        var placed = engine.GetEntity(0, 0);
        Assert.True(new LegalityAnalysis(placed).Valid, new LegalityAnalysis(placed).Report());
        Assert.Equal([80, 80, 80, 80, 80, 80], EVs(placed));
        var expected = broken.Clone();
        expected.SetEVs([80, 80, 80, 80, 80, 80]);
        expected.RefreshChecksum();
        Assert.Equal(Stored(expected), Stored(placed));
    }

    [Fact]
    public void ARebuiltMonKeepsItsMetLevelLevelAndEXP()
    {
        // A wild slot spans levels (Surf, Lv 5-35); a rebuild must not re-roll where in it the
        // Pokémon was met, nor drop its progress towards the next level.
        using var session = new SaveEngine().OpenBlankSession(3);
        Assert.True(new LegalizerService().GenerateFromShowdown(session, 0, 0, "Tentacool\nLevel: 30").Success);
        var engine = (SaveEngineSession)session;
        var broken = engine.GetEntity(0, 0);
        var growth = broken.PersonalInfo.EXPGrowth;
        broken.EXP = (Experience.GetEXP(30, growth) + Experience.GetEXP(31, growth)) / 2; // halfway to Lv 31
        broken.Ball = (byte)Ball.Safari; // only legal in the Safari Zone: it has to be rebuilt
        broken.Move4 = (ushort)Move.Transform;
        broken.HealPP();
        broken.SetEVs([252, 252, 252, 252, 252, 252]);
        broken.RefreshChecksum();
        engine.SaveFile.SetBoxSlotAtIndex(broken, 0, 0, EntityImportSettings.None);
        Assert.False(new LegalityAnalysis(broken).Valid);

        var outcome = new LegalizerService().LegalizeSlot(session, 0, 0);
        Assert.True(outcome.Success, outcome.Message);
        var placed = engine.GetEntity(0, 0);
        Assert.True(new LegalityAnalysis(placed).Valid, new LegalityAnalysis(placed).Report());
        Assert.Equal(broken.MetLevel, placed.MetLevel);
        Assert.Equal(30, placed.CurrentLevel);
        Assert.Equal(broken.EXP, placed.EXP);
        Assert.Equal(Trimmed, EVs(placed));
    }

    [Fact]
    public void AHatchedMonWithAnUnlearnableMoveIsRepaired()
    {
        // Gen 3 marks a hatched mon only by met level 0 (it has no egg date), so it was rebuilt as a
        // catch; and Auto-Legality found nothing for a set with a move the species can never learn.
        using var session = new SaveEngine().OpenBlankSession(3);
        Assert.True(new LegalizerService().GenerateFromShowdown(session, 0, 0, "Blaziken\nLevel: 38\n- Reversal").Success);
        var engine = (SaveEngineSession)session;
        var broken = engine.GetEntity(0, 0);
        Assert.Equal(0, broken.MetLevel); // hatched
        broken.Move4 = (ushort)Move.Transform;
        broken.HealPP();
        broken.SetEVs([252, 252, 252, 252, 252, 252]);
        broken.Ball = (byte)Ball.Master; // Gen 3 eggs hatch in a Poké Ball: only a rebuild fixes this
        broken.RefreshChecksum();
        engine.SaveFile.SetBoxSlotAtIndex(broken, 0, 0, EntityImportSettings.None);
        Assert.False(new LegalityAnalysis(broken).Valid);

        var outcome = new LegalizerService().LegalizeSlot(session, 0, 0);
        Assert.True(outcome.Success, outcome.Message);
        var placed = engine.GetEntity(0, 0);
        Assert.True(new LegalityAnalysis(placed).Valid, new LegalityAnalysis(placed).Report());
        Assert.Equal(broken.Species, placed.Species);
        Assert.Equal(38, placed.CurrentLevel);
        Assert.NotEqual((ushort)Move.Transform, placed.Move4);
        Assert.Equal(0, placed.MetLevel); // still hatched
        Assert.Contains((ushort)Move.Reversal, new[] { placed.Move1, placed.Move2, placed.Move3, placed.Move4 });
        // Auto-Legality hatched it in LeafGreen under another trainer; the rest goes back on.
        Assert.Equal(broken.Version, placed.Version);
        Assert.Equal(broken.MetLocation, placed.MetLocation);
        Assert.Equal(broken.OriginalTrainerName, placed.OriginalTrainerName);
        Assert.Equal(broken.ID32, placed.ID32);
        Assert.Equal(broken.PID, placed.PID);
        Assert.Equal(broken.GetIVs(), placed.GetIVs());
        Assert.Equal(broken.EXP, placed.EXP);
        Assert.Equal(Trimmed, EVs(placed));
    }

    [Fact]
    public void AHatchedGen3MonStaysHatched()
    {
        // Hatched on Route 117, where Marill is also wild: rebuilt as a catch there, it lost its egg
        // origin (Gen 3's only sign of one is met level 0, and PKHeX's WasEgg is false for it).
        using var session = new SaveEngine().OpenBlankSession(3);
        Assert.True(new LegalizerService().GenerateFromShowdown(session, 0, 0, "Marill\nLevel: 20\n- Belly Drum").Success);
        var engine = (SaveEngineSession)session;
        var broken = engine.GetEntity(0, 0);
        Assert.Equal(0, broken.MetLevel); // hatched
        broken.SetMoves([(ushort)Move.WaterGun, (ushort)Move.Rollout, (ushort)Move.TailWhip, (ushort)Move.Transform]);
        broken.HealPP();
        broken.SetEVs([252, 252, 252, 252, 252, 252]);
        broken.Ball = (byte)Ball.Master; // Gen 3 eggs hatch in a Poké Ball: only a rebuild fixes this
        broken.RefreshChecksum();
        engine.SaveFile.SetBoxSlotAtIndex(broken, 0, 0, EntityImportSettings.None);
        Assert.False(new LegalityAnalysis(broken).Valid);

        var outcome = new LegalizerService().LegalizeSlot(session, 0, 0);
        Assert.True(outcome.Success, outcome.Message);
        var placed = engine.GetEntity(0, 0);
        Assert.True(new LegalityAnalysis(placed).Valid, new LegalityAnalysis(placed).Report());
        Assert.Equal(0, placed.MetLevel); // still hatched, not caught on Route 117
        Assert.Equal(20, placed.CurrentLevel);
    }

    [Fact]
    public void TheIVsStayWhenOnlyThePidHasToChange()
    {
        // Gen 5 wild IVs are not tied to the PID: a broken PID is replaced, the IVs are kept.
        using var session = new SaveEngine().OpenBlankSession(5);
        Assert.True(new LegalizerService().GenerateFromShowdown(session, 0, 0, "Watchog @ Oran Berry\nLevel: 45").Success);
        session.ApplyEdit(0, 0, new EntityEdit(IVs: [31, 30, 29, 28, 27, 26], EVs: [252, 252, 252, 252, 252, 252]));
        var engine = (SaveEngineSession)session;
        var broken = engine.GetEntity(0, 0);
        broken.PID ^= 0x8000_0000; // breaks the Gen 5 wild PID rule, which no edit of the EVs fixes
        broken.RefreshChecksum();
        engine.SaveFile.SetBoxSlotAtIndex(broken, 0, 0, EntityImportSettings.None);
        Assert.False(new LegalityAnalysis(broken).Valid);

        var outcome = new LegalizerService().LegalizeSlot(session, 0, 0);
        Assert.True(outcome.Success, outcome.Message);
        var placed = engine.GetEntity(0, 0);
        Assert.True(new LegalityAnalysis(placed).Valid, new LegalityAnalysis(placed).Report());
        Assert.NotEqual(broken.PID, placed.PID);
        Assert.Equal(broken.GetIVs(), placed.GetIVs());
        Assert.Equal(broken.HeldItem, placed.HeldItem);
        Assert.Equal(Trimmed, EVs(placed));
    }
}
