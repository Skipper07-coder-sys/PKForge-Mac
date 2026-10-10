using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Legalize rebuilt an evolved Pokémon from its encounter (a gift Mudkip, a wild Ralts) and
/// handed back the encounter's species, so a Swampert came back a Mudkip and LegalizeSlot
/// refused it. An evolved mon must be repaired as itself, at its own level, under its own name.
/// </summary>
public sealed class LegalizeEvolvedTests
{
    [Theory]
    [InlineData(3, "Swampert")]
    [InlineData(3, "Gardevoir")]
    [InlineData(8, "Gardevoir")]
    public void AnEvolvedMonIsLegalizedAsItself(int generation, string species)
    {
        using var session = new SaveEngine().OpenBlankSession(generation);
        var legalizer = new LegalizerService();
        Assert.True(legalizer.GenerateFromShowdown(session, 0, 0, $"{species}\nLevel: 45").Success);
        // Lv 47 + 1512 EVs (cap 510): illegal only because of the EVs.
        session.ApplyEdit(0, 0, new EntityEdit(Level: 47, EVs: [252, 252, 252, 252, 252, 252]));
        var engine = (SaveEngineSession)session;
        var broken = engine.GetEntity(0, 0);
        Assert.False(new LegalityAnalysis(broken).Valid);

        var repaired = LegalizerService.LegalizeKeepingOrigin(engine.SaveFile, broken);
        Assert.Equal(broken.Species, repaired.Species);
        Assert.Equal(47, repaired.CurrentLevel);
        Assert.Equal(broken.Nickname, repaired.Nickname); // "SWAMPERT", not the encounter's "MUDKIP"

        var outcome = legalizer.LegalizeSlot(session, 0, 0);
        Assert.True(outcome.Success, outcome.Message);
        var placed = engine.GetEntity(0, 0);
        Assert.Equal(broken.Species, placed.Species);
        Assert.Equal(47, placed.CurrentLevel);
        Assert.Equal(broken.Nickname, placed.Nickname);
        Assert.True(new LegalityAnalysis(placed).Valid, new LegalityAnalysis(placed).Report());
    }
}
