using Kerblox.Core;

namespace Kerblox.Core.Tests;

public class GridCodecTests
{
    [Fact]
    public void RoundTripsDefaultGrid()
    {
        var g = DefaultGrids.Capsule();
        var back = GridCodec.FromText(GridCodec.ToText(g));
        Assert.True(g.ContentEquals(back));
    }

    [Fact]
    public void RoundTripsBlockStateData()
    {
        var g = new VoxelGrid(5, 3, 7);
        g.Set(4, 2, 6, new BlockState(3, 0xFFFF));
        g.Set(0, 0, 0, new BlockState(0xFFFF, 1));
        Assert.True(g.ContentEquals(GridCodec.FromBytes(GridCodec.ToBytes(g))));
    }

    [Fact]
    public void RoundTripsMaxSizeEmptyGrid()
    {
        var g = new VoxelGrid(256, 256, 256);
        var bytes = GridCodec.ToBytes(g);
        Assert.True(bytes.Length < 32, "a single run should encode tiny");
        Assert.True(g.ContentEquals(GridCodec.FromBytes(bytes)));
    }

    [Fact]
    public void TextIsConfigNodeSafe()
    {
        // Random-ish content to exercise all base64 characters.
        var g = new VoxelGrid(16, 16, 16);
        var rng = new Random(1234);
        for (int i = 0; i < 2000; i++)
            g.Set(rng.Next(16), rng.Next(16), rng.Next(16), new BlockState((uint)rng.Next()));

        string text = GridCodec.ToText(g);
        Assert.DoesNotContain("//", text);
        Assert.DoesNotContain("=", text);
        Assert.DoesNotContain("{", text);
        Assert.DoesNotContain("}", text);
        Assert.DoesNotContain("\n", text);
        Assert.True(g.ContentEquals(GridCodec.FromText(text)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAA")]
    [InlineData("S0JHUgE")] // "KBGR" + version 1, then truncated
    public void RejectsGarbage(string text) =>
        Assert.Throws<FormatException>(() => GridCodec.FromText(text));

    [Fact]
    public void RejectsTrailingBytes()
    {
        var bytes = GridCodec.ToBytes(new VoxelGrid(1, 1, 1)).Append((byte)0).ToArray();
        Assert.Throws<FormatException>(() => GridCodec.FromBytes(bytes));
    }

    [Fact]
    public void RejectsOverlongRun()
    {
        var bytes = GridCodec.ToBytes(new VoxelGrid(1, 1, 1));
        bytes[11] = 5; // run length varint: claims 5 cells in a 1-cell grid
        Assert.Throws<FormatException>(() => GridCodec.FromBytes(bytes));
    }
}
