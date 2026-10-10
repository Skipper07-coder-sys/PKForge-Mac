using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// HaX mode builds a Showdown set exactly as written when no legal version matches it:
/// illegal moves, a species below its evolution level, forced IVs. With HaX off nothing changes.
/// </summary>
public sealed class HaxShowdownTests
{
    // Dragon Dance has no legal source for Altaria in Emerald.
    private const string DragonDanceAltaria = """
        Altaria @ Dragon Fang
        Ability: Natural Cure
        Level: 36
        EVs: 252 Atk / 4 SpA / 252 Spe
        Hasty Nature
        - Dragon Dance
        - Dragon Claw
        - Earthquake
        - Flamethrower
        """;

    private static readonly GameStrings Strings = GameInfo.GetStrings("en");

    private static PKM Built(GeneratedEntity? generated)
    {
        Assert.NotNull(generated);
        return EntityFormat.GetFromBytes(generated!.Data)!;
    }

    private static string[] MoveNames(PKM pk) =>
        [.. new[] { pk.Move1, pk.Move2, pk.Move3, pk.Move4 }.Select(m => Strings.movelist[m])];

    [Fact]
    public void WithoutHaxAnImpossibleSetIsStillRefused()
    {
        using var session = new SaveEngine().OpenBlankSession(3);
        Assert.Null(new LegalizerService().GenerateDataFromShowdown(session, DragonDanceAltaria));
    }

    [Fact]
    public void HaxBuildsAnImpossibleSetExactlyAsWritten()
    {
        using var session = new SaveEngine().OpenBlankSession(3);
        var pk = Built(new LegalizerService().GenerateDataFromShowdown(session, DragonDanceAltaria, allowUnsupportedSpecies: true));

        Assert.Equal((ushort)Species.Altaria, pk.Species);
        Assert.Equal(36, pk.CurrentLevel);
        Assert.Equal(["Dragon Dance", "Dragon Claw", "Earthquake", "Flamethrower"], MoveNames(pk));
        Assert.Equal("Dragon Fang", Strings.itemlist[pk.SpriteItem]);
        Assert.Equal(Nature.Hasty, pk.Nature);
        Assert.Equal((int)Ability.NaturalCure, pk.Ability);
        Assert.Equal(252, pk.EV_ATK);
        Assert.Equal(252, pk.EV_SPE);
        Assert.False(new LegalityAnalysis(pk).Valid);
    }

    [Fact]
    public void HaxBuildsASpeciesBelowItsEvolutionLevel()
    {
        using var session = new SaveEngine().OpenBlankSession(3);
        var pk = Built(new LegalizerService().GenerateDataFromShowdown(session, "Flygon\nLevel: 36\n- Earthquake", allowUnsupportedSpecies: true));
        Assert.Equal((ushort)Species.Flygon, pk.Species);
        Assert.Equal(36, pk.CurrentLevel);
    }

    [Fact]
    public void HaxForcesIVsTheTextStates()
    {
        // Wild-only moveset: its legal version gets PID-tied IVs, so the stated 31s must be forced.
        const string tropius = """
            Tropius
            Level: 36
            IVs: 31 HP / 31 Atk / 31 Def / 31 SpA / 31 SpD / 31 Spe
            - Cut
            - Fly
            - Flash
            - Rock Smash
            """;
        using var session = new SaveEngine().OpenBlankSession(3);
        var pk = Built(new LegalizerService().GenerateDataFromShowdown(session, tropius, allowUnsupportedSpecies: true));
        Assert.Equal([31, 31, 31, 31, 31, 31], new[] { pk.IV_HP, pk.IV_ATK, pk.IV_DEF, pk.IV_SPA, pk.IV_SPD, pk.IV_SPE });
        Assert.Equal(["Cut", "Fly", "Flash", "Rock Smash"], MoveNames(pk));
    }

    [Fact]
    public void HaxLeavesALegalSetLegalWhenTheTextStatesNoIVs()
    {
        using var session = new SaveEngine().OpenBlankSession(3);
        var pk = Built(new LegalizerService().GenerateDataFromShowdown(session, "Gardevoir\nLevel: 36\n- Psychic", allowUnsupportedSpecies: true));
        Assert.True(new LegalityAnalysis(pk).Valid);
    }

    [Fact]
    public void TheTeamPreviewMarksAHaxBuildAsNotLegalButPlaceable()
    {
        using var session = new SaveEngine().OpenBlankSession(3);
        var legalizer = new LegalizerService();
        var preview = Assert.Single(ShowdownTeamService.Preview(session, legalizer, DragonDanceAltaria, allowUnsupportedSpecies: true));
        Assert.True(preview.Generated);
        Assert.False(preview.Legal);
        Assert.Contains("HaX", preview.Verdict, StringComparison.Ordinal);

        var outcome = ShowdownTeamService.Place(session, legalizer, [preview], ShowdownTeamService.EmptySlots(session, 0, onlyThisBox: true), allowUnsupportedSpecies: true);
        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal((int)Species.Altaria, session.ReadEntity(0, 0).Species);
    }

    [Fact]
    public void HaxPastesIntoOneSlotToo()
    {
        using var session = new SaveEngine().OpenBlankSession(3);
        var outcome = new LegalizerService().GenerateFromShowdown(session, 0, 0, DragonDanceAltaria, allowUnsupportedSpecies: true);
        Assert.True(outcome.Success, outcome.Message);
        Assert.Contains("HaX", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(36, session.ReadEntity(0, 0).Level);
    }
}
