using System.Globalization;
using System.Text;
using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The Mac and iPhone builds run without ICU, where Normalize leaves "é" whole; the fold's own
/// table must say exactly what ICU's decomposition says. These tests run with ICU, so they check
/// the table against it.
/// </summary>
public sealed class DiacriticsTests
{
    [Fact]
    public void TheTableMatchesIcuForEveryLatinLetter()
    {
        if ("é".Normalize(NormalizationForm.FormD).Length == 1) return; // no ICU here: nothing to compare against
        for (var c = 'À'; c <= 'ſ'; c++)
        {
            var d = c.ToString().Normalize(NormalizationForm.FormD);
            var splits = d.Length > 1 && d[0] <= 0x7F && char.IsLetter(d[0])
                && d.Skip(1).All(m => CharUnicodeInfo.GetUnicodeCategory(m) == UnicodeCategory.NonSpacingMark);
            Assert.Equal(splits ? d[0] : c, Diacritics.BaseLetter(c));
        }
    }

    [Theory]
    [InlineData("Poké Ball", "poke-ball")]
    [InlineData("Poké Doll", "poke-doll")]
    [InlineData("Pokéblock Kit", "pokeblock-kit")]
    public void AccentedItemsSlugToTheirSprite(string name, string slug) => Assert.Equal(slug, SpritePack.ItemSlug(name));

    [Fact]
    public void AccentedNamesKeyLikeTheirPlainSpelling()
    {
        Assert.Equal("pokeball", DexFacts.NormalizeName("Poké Ball"));
        Assert.Equal("flabebe", DexFacts.NormalizeName("Flabébé"));
        Assert.NotNull(DexFacts.Item("Poké Ball"));
    }
}
