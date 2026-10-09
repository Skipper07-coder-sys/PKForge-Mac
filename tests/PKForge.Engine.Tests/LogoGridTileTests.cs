using PKForge.Chrome;
using SkiaSharp;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>The Mac and iPhone tile the menu grid from one cell instead of drawing a window-sized bitmap: it must be the same picture.</summary>
public sealed class LogoGridTileTests
{
    [Theory]
    [InlineData(250, 170)]   // not a multiple of the cell
    [InlineData(256, 192)]   // a multiple
    [InlineData(31, 33)]     // smaller than two cells
    public void TheTiledCellIsTheFullDrawingPixelForPixel(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var full = SKSurface.Create(info);
        PksmPaint.LogoGrid(full.Canvas, new SKRect(0, 0, width, height));

        using var tile = PksmPaint.LogoGridTile();
        using var tiled = SKSurface.Create(info);
        using var shader = tile.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
        using var paint = new SKPaint { Shader = shader };
        tiled.Canvas.DrawRect(new SKRect(0, 0, width, height), paint);

        using var a = full.Snapshot();
        using var b = tiled.Snapshot();
        using var pa = a.PeekPixels();
        using var pb = b.PeekPixels();
        Assert.Equal(pa.GetPixelSpan().ToArray(), pb.GetPixelSpan().ToArray());
    }
}
