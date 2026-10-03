using Kerblox.Core;

namespace Kerblox.Core.Tests;

public class VoxelGridTests
{
    private static readonly BlockState Stone = BlockState.Parse("minecraft:stone");

    [Fact]
    public void OutOfBoundsReadsAreAir()
    {
        var g = new VoxelGrid(2, 2, 2);
        g.Fill(0, 0, 0, 1, 1, 1, Stone);
        Assert.True(g.Get(-1, 0, 0).IsAir);
        Assert.True(g.Get(0, 2, 0).IsAir);
        Assert.Equal(Stone, g.Get(1, 1, 1));
    }

    [Fact]
    public void SetRaisesChangedAndBumpsRevisionOnlyWhenDifferent()
    {
        var g = new VoxelGrid(3, 3, 3);
        var changes = new List<GridChange>();
        g.Changed += changes.Add;

        Assert.True(g.Set(1, 2, 0, Stone));
        Assert.False(g.Set(1, 2, 0, Stone));

        var c = Assert.Single(changes);
        Assert.Equal((1, 2, 0), (c.X, c.Y, c.Z));
        Assert.True(c.Old.IsAir);
        Assert.Equal(Stone, c.New);
        Assert.Equal(g.Revision, c.Revision);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 257, 1)]
    public void RejectsBadDimensions(int x, int y, int z) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoxelGrid(x, y, z));

    [Fact]
    public void CloneIsIndependent()
    {
        var g = DefaultGrids.Capsule();
        var c = g.Clone();
        Assert.True(g.ContentEquals(c));
        c.Set(0, 0, 0, BlockState.Air);
        Assert.False(g.ContentEquals(c));
    }

    [Fact]
    public void NewGridPaletteIsJustAir()
    {
        var g = new VoxelGrid(2, 2, 2);
        Assert.Equal(new[] { BlockState.Air }, g.Palette);
        Assert.Equal(0, g.CountNonAir());
    }

    [Fact]
    public void PaletteCompactsAfterRemovals()
    {
        var glass = BlockState.Parse("minecraft:glass");
        var log = BlockState.Parse("minecraft:oak_log[axis=y]");
        var g = new VoxelGrid(4, 1, 1);
        g.Set(0, 0, 0, glass);
        g.Set(1, 0, 0, Stone);
        g.Set(2, 0, 0, log);
        g.Set(3, 0, 0, Stone);
        Assert.Equal(4, g.Palette.Count);

        g.Set(0, 0, 0, BlockState.Air); // glass now unused
        g.Set(1, 0, 0, log);
        Assert.Equal(4, g.Palette.Count);
        Assert.Equal(3, g.CountNonAir());

        var before = g.Clone();
        ulong revision = g.Revision;
        g.Compact();

        // Air first, then states in order of first appearance: log at x=1, stone at x=3.
        Assert.Equal(new[] { BlockState.Air, log, Stone }, g.Palette);
        Assert.True(g.ContentEquals(before));
        Assert.Equal(revision, g.Revision);
        Assert.Equal(3, g.CountNonAir());

        // The compacted grid keeps working: reusing and adding states after compaction.
        Assert.True(g.Set(0, 0, 0, glass));
        Assert.Equal(glass, g.Get(0, 0, 0));
        Assert.Equal(4, g.CountNonAir());
    }

    [Fact]
    public void FillAndCountTrackPaletteUse()
    {
        var g = new VoxelGrid(3, 3, 3);
        g.Fill(0, 0, 0, 2, 2, 2, Stone);
        g.Fill(0, 0, 0, 2, 0, 2, BlockState.Air);
        Assert.Equal(18, g.CountNonAir());
        g.Compact();
        Assert.Equal(new[] { BlockState.Air, Stone }, g.Palette);
    }

    [Fact]
    public void ContentEqualsIgnoresPaletteOrder()
    {
        var glass = BlockState.Parse("minecraft:glass");
        var a = new VoxelGrid(2, 1, 1);
        a.Set(0, 0, 0, Stone);
        a.Set(1, 0, 0, glass);
        var b = new VoxelGrid(2, 1, 1);
        b.Set(1, 0, 0, glass);
        b.Set(0, 0, 0, Stone);
        Assert.NotEqual(a.Palette, b.Palette);
        Assert.True(a.ContentEquals(b));
        b.Set(1, 0, 0, Stone);
        Assert.False(a.ContentEquals(b));
    }
}
