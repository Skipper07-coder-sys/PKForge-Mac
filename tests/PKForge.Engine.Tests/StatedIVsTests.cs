using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Gen 3/4 tie a wild or gift Pokémon's IVs to its PID, so the IVs a pasted set stated came out
/// random unless it had an egg-only move. A bred egg's IVs are free: when the text states IVs the
/// catch doesn't have, the import hatches it instead, where an egg can be it at all.
/// </summary>
public sealed class StatedIVsTests
{
    private const string PerfectIVs = "IVs: 31 HP / 31 Atk / 31 Def / 31 SpA / 31 SpD / 31 Spe";
    private static readonly int[] Perfect = [31, 31, 31, 31, 31, 31];

    private static int[] IVs(PKM pk) => [pk.IV_HP, pk.IV_ATK, pk.IV_DEF, pk.IV_SPA, pk.IV_SPD, pk.IV_SPE];

    private static PKM Pasted(ISaveEngineSession session, string text)
    {
        var outcome = new LegalizerService().GenerateFromShowdown(session, 0, 0, text);
        Assert.True(outcome.Success, outcome.Message);
        var pk = ((SaveEngineSession)session).GetEntity(0, 0);
        Assert.True(new LegalityAnalysis(pk).Valid, new LegalityAnalysis(pk).Report());
        return pk;
    }

    [Fact]
    public void StatedIVsAreMetByHatchingIt()
    {
        using var session = new SaveEngine().OpenBlankSession(3);
        var pk = Pasted(session, $"Mightyena\nLevel: 20\nAdamant Nature\n{PerfectIVs}\n- Bite");
        Assert.Equal(Perfect, IVs(pk));
        Assert.Equal(Nature.Adamant, pk.Nature);
        Assert.Equal(0, pk.MetLevel); // hatched: Gen 3's only mark of it
        Assert.Equal((byte)Ball.Poke, pk.Ball);
        Assert.True(((SaveEngineSession)session).SaveFile.IsFromTrainer(pk));
    }

    [Fact]
    public void ATeamImportHatchesItToo()
    {
        using var session = new SaveEngine().OpenBlankSession(3);
        var generated = new LegalizerService().GenerateDataFromShowdown(session, $"Swampert\nLevel: 36\nBrave Nature\n{PerfectIVs}\n- Surf");
        Assert.NotNull(generated);
        var pk = EntityFormat.GetFromBytes(generated!.Data)!;
        Assert.True(new LegalityAnalysis(pk).Valid, new LegalityAnalysis(pk).Report());
        Assert.Equal(Perfect, IVs(pk));
        Assert.Equal(Nature.Brave, pk.Nature);
    }

    [Fact]
    public void AnHMUserWithPerfectIVsIsLegal()
    {
        // Before, a Tropius with four HMs came out with random IVs; HaX forced the 31s, illegally.
        using var session = new SaveEngine().OpenBlankSession(3);
        var pk = Pasted(session, $"Tropius\nLevel: 36\n{PerfectIVs}\n- Cut\n- Fly\n- Flash\n- Rock Smash");
        Assert.Equal(Perfect, IVs(pk));
        Assert.Equal([(ushort)Move.Cut, (ushort)Move.Fly, (ushort)Move.Flash, (ushort)Move.RockSmash],
            new[] { pk.Move1, pk.Move2, pk.Move3, pk.Move4 });
    }

    [Fact]
    public void WithoutStatedIVsItStaysACatch()
    {
        using var session = new SaveEngine().OpenBlankSession(3);
        Assert.NotEqual(0, Pasted(session, "Mightyena\nLevel: 20\nAdamant Nature\n- Bite").MetLevel);
    }

    [Fact]
    public void BelowTheHatchLevelItStaysACatch()
    {
        // Gen 3 eggs hatch at Lv 5: a Lv 3 Poochyena can only be a catch, IVs as they come.
        using var session = new SaveEngine().OpenBlankSession(3);
        var pk = Pasted(session, $"Poochyena\nLevel: 3\n{PerfectIVs}\n- Tackle");
        Assert.NotEqual(0, pk.MetLevel);
        Assert.Equal(3, pk.CurrentLevel);
    }

    [Fact]
    public void ASpeciesThatCannotBreedStaysACatch()
    {
        using var session = new SaveEngine().OpenBlankSession(3);
        var pk = Pasted(session, $"Regirock\nLevel: 40\n{PerfectIVs}");
        Assert.NotEqual(0, pk.MetLevel);
        Assert.Equal((ushort)Species.Regirock, pk.Species);
    }
}
