using System.IO.Compression;
using System.Text;
using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class ItemTextTests
{
    private static ItemTextTable Table(string tsv)
    {
        var raw = new MemoryStream();
        using (var gzip = new GZipStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(tsv));
        raw.Position = 0;
        return ItemTextTable.Parse(raw);
    }

    [Fact]
    public void AGameReadsItsOwnLineOrTheNearestEarlierOne()
    {
        var table = Table("#\theader\nG\t7\nG\t12\nT\t12\tsafariball\tGreat Marsh\nT\t7\tsafariball\tSafari Zone\nT\tx\tbad\trow\n");
        Assert.Null(table.For("Safari Ball", 5));             // before any game with text: none
        Assert.Equal("Safari Zone", table.For("Safari Ball", 8));  // Emerald borrows Ruby/Sapphire's line
        Assert.Equal("Great Marsh", table.For("Safari Ball", 30)); // newer games borrow the latest
        Assert.Null(table.For("Poké Ball", 12));
        Assert.True(table.HasOwnText(12));
        Assert.False(table.HasOwnText(8));
        Assert.Equal(1, table.ItemCount);
    }

    [Fact]
    public void TheEmbeddedTableCoversTheGbaToSwitchGames()
    {
        var table = ItemTexts.Default;
        Assert.True(table.ItemCount > 1000);
        Assert.Equal("A special BALL that is used only in the SAFARI ZONE.", table.For("Safari Ball", 8));
        Assert.True(table.HasOwnText(8));   // Emerald
        Assert.True(table.HasOwnText(22));  // Sword/Shield
        Assert.False(table.HasOwnText(30)); // Z-A: newer than the data
    }
}
