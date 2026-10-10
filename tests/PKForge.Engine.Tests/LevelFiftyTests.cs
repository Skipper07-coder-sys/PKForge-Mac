using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The AutoMod regenerates every set at Lv 100 when it asks for exactly Lv 50 (a VGC convenience,
/// on by default). In a save editor "Level 50" means 50: pastes, the create form and Legalize alike.
/// </summary>
public sealed class LevelFiftyTests
{
    [Theory]
    [InlineData(3, "Gardevoir")]
    [InlineData(8, "Garchomp")]
    public void AShowdownSetAtLevelFiftyStaysAtFifty(int generation, string species)
    {
        using var session = new SaveEngine().OpenBlankSession(generation);
        var outcome = new LegalizerService().GenerateFromShowdown(session, 0, 0, $"{species}\nLevel: 50");
        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(50, session.ReadEntity(0, 0).Level);
    }

    [Fact]
    public void TheCreateFormAtLevelFiftyStaysAtFifty()
    {
        using var session = new SaveEngine().OpenBlankSession(3);
        var request = new GenerationRequest((int)Species.Gardevoir, 50, false, null, null, null, null);
        var outcome = new LegalizerService().Generate(session, 0, 0, request);
        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(50, session.ReadEntity(0, 0).Level);
    }

    [Fact]
    public void LegalizingALevelFiftyMonKeepsItAtFifty()
    {
        using var session = new SaveEngine().OpenBlankSession(3);
        var legalizer = new LegalizerService();
        Assert.True(legalizer.GenerateFromShowdown(session, 0, 0, "Ralts\nLevel: 45").Success);
        // Lv 50 + 1512 EVs (cap 510): illegal, so Legalize has to rebuild it from the mon itself.
        session.ApplyEdit(0, 0, new EntityEdit(Level: 50, EVs: [252, 252, 252, 252, 252, 252]));
        var engine = (SaveEngineSession)session;
        Assert.False(new LegalityAnalysis(engine.GetEntity(0, 0)).Valid);

        var outcome = legalizer.LegalizeSlot(session, 0, 0);
        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(50, session.ReadEntity(0, 0).Level);
        Assert.True(new LegalityAnalysis(engine.GetEntity(0, 0)).Valid);
    }
}
