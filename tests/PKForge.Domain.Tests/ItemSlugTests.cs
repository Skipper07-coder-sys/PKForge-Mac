using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>Item sprites are PokeAPI files: each name must slug to the file that exists there (checked 2026-10-09).</summary>
public sealed class ItemSlugTests
{
    [Theory]
    [InlineData("King’s Rock", "kings-rock")]
    [InlineData("Oak’s Letter", "oaks-letter")]
    [InlineData("Professor’s Mask", "professors-mask")]
    [InlineData("Upgrade", "up-grade")]
    [InlineData("Leek", "stick")]
    [InlineData("Paralyze Heal", "paralyze-heal")]
    [InlineData("Never-Melt Ice", "never-melt-ice")]
    public void NamesSlugToPokeApiFiles(string name, string slug) => Assert.Equal(slug, SpritePack.ItemSlug(name));
}
