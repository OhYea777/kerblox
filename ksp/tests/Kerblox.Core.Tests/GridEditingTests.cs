using Kerblox.Core;

namespace Kerblox.Core.Tests;

public class GridEditingTests
{
    private static readonly BlockState Stone = BlockState.Parse("minecraft:stone");
    private static readonly BlockState Dirt = BlockState.Parse("minecraft:dirt");
    private static readonly BlockState Log = BlockState.Parse("minecraft:oak_log[axis=y]");
    private const float Bs = 0.625f;

    private static VoxelGrid Sample()
    {
        var g = new VoxelGrid(3, 4, 2);
        g.Set(0, 0, 0, Stone);
        g.Set(2, 3, 1, Dirt);
        g.Set(1, 2, 0, Log);
        g.Set(1, 1, 1, Stone);
        return g;
    }

    [Fact]
    public void ResizedMovesCellsByOffsetAndKeepsCounts()
    {
        var g = Sample();
        var r = g.Resized(5, 6, 4, 1, 2, 1);

        Assert.Equal((5, 6, 4), (r.SizeX, r.SizeY, r.SizeZ));
        for (int y = 0; y < g.SizeY; y++)
            for (int z = 0; z < g.SizeZ; z++)
                for (int x = 0; x < g.SizeX; x++)
                    Assert.Equal(g.Get(x, y, z), r.Get(x + 1, y + 2, z + 1));
        Assert.Equal(4, r.CountNonAir());
        Assert.Equal(2, r.CountOf(Stone));
        Assert.Equal(1, r.CountOf(Dirt));
        Assert.Equal(1, r.CountOf(Log));
        Assert.Equal(r.Volume - 4, r.CountOf(BlockState.Air));
        Assert.Equal(g.Revision + 1, r.Revision);
    }

    [Fact]
    public void ResizedCropDropsCellsAndTheirCounts()
    {
        var g = Sample();
        // Keep x 1..2, y 1..3, z 0..1: drops the stone at (0,0,0).
        var r = g.Resized(2, 3, 2, -1, -1, 0);

        Assert.Equal(1, r.CountOf(Stone));
        Assert.Equal(1, r.CountOf(Dirt));
        Assert.Equal(1, r.CountOf(Log));
        Assert.Equal(3, r.CountNonAir());
        Assert.Equal(Dirt, r.Get(1, 2, 1));
        Assert.Equal(Log, r.Get(0, 1, 0));

        // Dropped states disappear from the compacted palette and the codec.
        r.Set(0, 0, 1, BlockState.Air); // the remaining stone
        Assert.Equal(0, r.CountOf(Stone));
        r.Compact();
        Assert.DoesNotContain(Stone, r.Palette);
        Assert.True(r.ContentEquals(GridCodec.FromBytes(GridCodec.ToBytes(r))));
    }

    [Fact]
    public void ResizedLeavesSourceUntouched()
    {
        var g = Sample();
        var before = g.Clone();
        var r = g.Resized(4, 4, 2, 1, 0, 0);
        r.Set(0, 0, 0, Dirt);
        Assert.True(g.ContentEquals(before));
        Assert.Equal(2, g.CountOf(Stone));
    }

    [Fact]
    public void SetInsideEditsInPlace()
    {
        var g = Sample();
        var e = GridEditor.SetBlock(g, 0, 1, 0, Dirt);
        Assert.Equal(GridEditStatus.Edited, e.Status);
        Assert.Same(g, e.Grid);
        Assert.Equal(Float3.Zero, e.Displacement(Bs));
        Assert.Equal(GridEditStatus.NoChange, GridEditor.SetBlock(g, 0, 1, 0, Dirt).Status);
    }

    [Fact]
    public void RemovingOutsideIsNoChange()
    {
        var g = Sample();
        var e = GridEditor.RemoveBlock(g, -1, 0, 0);
        Assert.Equal(GridEditStatus.NoChange, e.Status);
        Assert.Same(g, e.Grid);
    }

    [Fact]
    public void RemoveInsideDoesNotShrink()
    {
        var g = Sample();
        var e = GridEditor.RemoveBlock(g, 2, 3, 1);
        Assert.Equal(GridEditStatus.Edited, e.Status);
        Assert.Equal((3, 4, 2), (e.Grid.SizeX, e.Grid.SizeY, e.Grid.SizeZ));
        Assert.Equal(0, g.CountOf(Dirt));
    }

    [Fact]
    public void GrowOnHighSideHasNoShiftButMovesCentre()
    {
        var g = Sample();
        var e = GridEditor.SetBlock(g, 1, 4, 0, Dirt); // one above the top layer

        Assert.Equal(GridEditStatus.Resized, e.Status);
        Assert.NotSame(g, e.Grid);
        Assert.Equal((3, 5, 2), (e.Grid.SizeX, e.Grid.SizeY, e.Grid.SizeZ));
        Assert.Equal((0, 0, 0), (e.ShiftX, e.ShiftY, e.ShiftZ));
        Assert.Equal(Dirt, e.Grid.Get(1, 4, 0));
        Assert.Equal(Stone, e.Grid.Get(0, 0, 0));
        Assert.Equal(2, e.Grid.CountOf(Dirt));
        // Box grew up by one, so its centre rose half a block and old blocks sit half a block lower.
        Assert.Equal(new Float3(0, -Bs / 2, 0), e.Displacement(Bs));
    }

    [Fact]
    public void GrowOnLowSideShiftsOldCells()
    {
        var g = Sample();
        var e = GridEditor.SetBlock(g, -2, 0, -1, Log);

        Assert.Equal((5, 4, 3), (e.Grid.SizeX, e.Grid.SizeY, e.Grid.SizeZ));
        Assert.Equal((2, 0, 1), (e.ShiftX, e.ShiftY, e.ShiftZ));
        Assert.Equal(Log, e.Grid.Get(0, 0, 0));
        Assert.Equal(Stone, e.Grid.Get(2, 0, 1));
        Assert.Equal(Dirt, e.Grid.Get(4, 3, 2));
        Assert.Equal(2, e.Grid.CountOf(Log));
        Assert.Equal(5, e.Grid.CountNonAir());
        Assert.Equal(new Float3(Bs, 0, Bs / 2), e.Displacement(Bs));
    }

    [Fact]
    public void DisplacementKeepsOldBlocksFixedOnceCompensated()
    {
        // Every old block's part-space centre, plus the displacement, equals its new centre.
        var g = Sample();
        var oldLayout = new BlockLayout(g, Bs);
        var e = GridEditor.SetBlock(g, 5, -3, 2, Stone);
        var newLayout = new BlockLayout(e.Grid, Bs);
        Float3 d = e.Displacement(Bs);

        foreach (var (x, y, z) in new[] { (0, 0, 0), (2, 3, 1), (1, 2, 0) })
        {
            Float3 before = oldLayout.CellCenter(x, y, z) + d;
            Float3 after = newLayout.CellCenter(x + e.ShiftX, y + e.ShiftY, z + e.ShiftZ);
            Assert.Equal(before.X, after.X, 5);
            Assert.Equal(before.Y, after.Y, 5);
            Assert.Equal(before.Z, after.Z, 5);
            Assert.Equal(g.Get(x, y, z), e.Grid.Get(x + e.ShiftX, y + e.ShiftY, z + e.ShiftZ));
        }
    }

    [Theory]
    [InlineData(256, 0, 0)]
    [InlineData(0, -254, 0)]
    [InlineData(0, 0, int.MaxValue)]
    [InlineData(int.MinValue, 0, 0)]
    public void GrowingPastMaxDimensionIsRejected(int x, int y, int z)
    {
        var g = Sample();
        var e = GridEditor.SetBlock(g, x, y, z, Stone);
        Assert.Equal(GridEditStatus.TooLarge, e.Status);
        Assert.Same(g, e.Grid);
        Assert.False(e.Changed);
    }

    [Fact]
    public void GrowingToExactlyMaxDimensionIsAllowed()
    {
        var g = new VoxelGrid(1, 1, 1);
        var e = GridEditor.SetBlock(g, VoxelGrid.MaxDimension - 1, 0, 0, Stone);
        Assert.Equal(GridEditStatus.Resized, e.Status);
        Assert.Equal(VoxelGrid.MaxDimension, e.Grid.SizeX);
    }

    [Fact]
    public void TrimShrinksToContentAndReportsShift()
    {
        var g = new VoxelGrid(6, 6, 6);
        g.Set(2, 1, 3, Stone);
        g.Set(4, 1, 3, Dirt);

        var e = GridEditor.Trim(g);
        Assert.Equal(GridEditStatus.Resized, e.Status);
        Assert.Equal((3, 1, 1), (e.Grid.SizeX, e.Grid.SizeY, e.Grid.SizeZ));
        Assert.Equal((-2, -1, -3), (e.ShiftX, e.ShiftY, e.ShiftZ));
        Assert.Equal(Stone, e.Grid.Get(0, 0, 0));
        Assert.Equal(Dirt, e.Grid.Get(2, 0, 0));
        Assert.Equal(e.Grid.Volume - 2, e.Grid.CountOf(BlockState.Air));

        Assert.Equal(GridEditStatus.NoChange, GridEditor.Trim(e.Grid).Status);
    }

    [Fact]
    public void TrimAllAirGridToSingleCell()
    {
        var e = GridEditor.Trim(new VoxelGrid(3, 3, 3));
        Assert.Equal((1, 1, 1), (e.Grid.SizeX, e.Grid.SizeY, e.Grid.SizeZ));
        Assert.Equal(GridEditStatus.NoChange, GridEditor.Trim(e.Grid).Status);
    }

    [Fact]
    public void OccupiedBounds()
    {
        Assert.False(new VoxelGrid(2, 2, 2).TryGetOccupiedBounds(out _));
        Assert.True(Sample().TryGetOccupiedBounds(out GridBox b));
        Assert.Equal((0, 0, 0, 3, 4, 2), (b.X, b.Y, b.Z, b.SizeX, b.SizeY, b.SizeZ));
    }
}
