using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class ColosseumRejectionTests
{
    /// <summary>A GCI-sized file of noise decrypts to garbage that PKHeX still calls Colosseum;
    /// it must be refused rather than shown as a save with a scrambled trainer name.</summary>
    [Fact]
    public void ColosseumSizedNoiseIsNotASave()
    {
        var noise = new byte[393280];
        new Random(7).NextBytes(noise);
        Assert.False(SaveParser.TryGetSaveFile(noise, out var save));
        Assert.Null(save);
    }
}
