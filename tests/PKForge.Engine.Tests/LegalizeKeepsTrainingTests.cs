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

    [Theory]
    [InlineData(3, "Swampert")]
    [InlineData(8, "Gardevoir")]
    public void ARebuiltMonKeepsItsItemIVsAndTrimmedEVs(int generation, string species)
    {
        // An unlearnable move as well, so trimming the EVs is not enough and it is rebuilt.
        var (session, engine, broken) = Break(generation, species, badMove: (int)Move.Transform);
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
