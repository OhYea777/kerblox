using Kerblox.Core;

namespace Kerblox.Core.Tests;

public class BlockPickerTests
{
    private const float Bs = 0.625f;

    // 4x2x2 grid at 0.625 m: origin (-1.25, -0.625, -0.625), max corner (1.25, 0.625, 0.625).
    private static readonly BlockLayout Layout = new BlockLayout(4, 2, 2, Bs);

    [Fact]
    public void SizeConstructorMatchesGridConstructor()
    {
        var fromGrid = new BlockLayout(new VoxelGrid(4, 2, 2), Bs);
        Assert.Equal(fromGrid.Origin, Layout.Origin);
        Assert.Equal(fromGrid.BlockSize, Layout.BlockSize);
    }

    [Theory]
    // +X face of cell (3,1,0): hit there, place one further along +X (outside the grid).
    [InlineData(1.25f, 0.3f, -0.3f, 1, 0, 0, 3, 1, 0)]
    // -X face of cell (0,0,1): place at x = -1, which grows the grid.
    [InlineData(-1.25f, -0.3f, 0.3f, -1, 0, 0, 0, 0, 1)]
    // Top of cell (2,1,1).
    [InlineData(0.1f, 0.625f, 0.1f, 0, 1, 0, 2, 1, 1)]
    // Bottom of cell (1,0,0).
    [InlineData(-0.5f, -0.625f, -0.5f, 0, -1, 0, 1, 0, 0)]
    // -Z face of cell (2,0,0).
    [InlineData(0.2f, -0.2f, -0.625f, 0, 0, -1, 2, 0, 0)]
    public void HitOnOuterFacePicksThatCellAndItsNeighbour(
        float px, float py, float pz, int nx, int ny, int nz, int hx, int hy, int hz)
    {
        BlockPick p = BlockPicker.FromHit(Layout, new Float3(px, py, pz), new Float3(nx, ny, nz));
        Assert.Equal(new Int3(hx, hy, hz), p.Hit);
        Assert.Equal(new Int3(nx, ny, nz), p.Face);
        Assert.Equal(new Int3(hx + nx, hy + ny, hz + nz), p.Place);
    }

    [Fact]
    public void HitOnInternalFaceOfAHollowPicksTheWallCell()
    {
        // Looking into a cavity at the +X face of cell (1,0,0), at x = 0 in part space:
        // the hit block is (1,0,0) and the new block goes into the cavity at (2,0,0).
        BlockPick p = BlockPicker.FromHit(Layout, new Float3(0f, -0.3f, -0.3f), new Float3(1, 0, 0));
        Assert.Equal(new Int3(1, 0, 0), p.Hit);
        Assert.Equal(new Int3(2, 0, 0), p.Place);
    }

    [Fact]
    public void HitOnCellEdgeResolvesToTheStruckFace()
    {
        // Exactly on the edge between cells (3,0,0) and (3,1,0) on the +X face: either
        // cell is a valid answer for y, but x must be the face's cell, not the air beyond.
        BlockPick p = BlockPicker.FromHit(Layout, new Float3(1.25f, 0f, -0.3f), new Float3(1, 0, 0));
        Assert.Equal(3, p.Hit.X);
        Assert.Equal(4, p.Place.X);
        Assert.InRange(p.Hit.Y, 0, 1);
    }

    [Theory]
    [InlineData(1e-4f)]
    [InlineData(-1e-4f)]
    public void FloatNoiseAroundTheSurfaceDoesNotChangeTheCell(float noise)
    {
        BlockPick p = BlockPicker.FromHit(Layout, new Float3(0.1f, 0.625f + noise, 0.1f), new Float3(0, 1, 0));
        Assert.Equal(new Int3(2, 1, 1), p.Hit);
        Assert.Equal(new Int3(2, 2, 1), p.Place);
    }

    [Fact]
    public void SlightlyTiltedNormalSnapsToItsDominantAxis()
    {
        BlockPick p = BlockPicker.FromHit(Layout, new Float3(0.1f, 0.625f, 0.1f), new Float3(0.01f, 0.9999f, -0.01f));
        Assert.Equal(new Int3(0, 1, 0), p.Face);
    }

    [Theory]
    [InlineData(0.7f, 0.7f, 0f, 0, 1, 0)]
    [InlineData(0.7f, 0f, 0.7f, 1, 0, 0)]
    [InlineData(0f, 0f, -2f, 0, 0, -1)]
    [InlineData(-3f, 1f, 1f, -1, 0, 0)]
    public void DominantAxisBreaksTiesTowardYThenX(float x, float y, float z, int ex, int ey, int ez)
    {
        Assert.Equal(new Int3(ex, ey, ez), BlockPicker.DominantAxis(new Float3(x, y, z)));
    }

    [Fact]
    public void ZeroOrNaNNormalIsRejected()
    {
        Assert.Throws<ArgumentException>(() => BlockPicker.DominantAxis(Float3.Zero));
        Assert.Throws<ArgumentException>(() => BlockPicker.DominantAxis(new Float3(float.NaN, 0, 0)));
    }

    [Fact]
    public void PickedPlaceCellFeedsGridEditor()
    {
        // End to end: place on the -X face of a single block, the grid grows on -X.
        var g = new VoxelGrid(1, 1, 1);
        var stone = BlockState.Parse("minecraft:stone");
        g.Set(0, 0, 0, stone);
        var layout = new BlockLayout(g, Bs);
        BlockPick p = BlockPicker.FromHit(layout, new Float3(-Bs / 2, 0.1f, 0.1f), new Float3(-1, 0, 0));

        GridEdit e = GridEditor.SetBlock(g, p.Place.X, p.Place.Y, p.Place.Z, stone);
        Assert.Equal(GridEditStatus.Resized, e.Status);
        Assert.Equal((2, 1, 1), (e.Grid.SizeX, e.Grid.SizeY, e.Grid.SizeZ));
        Assert.Equal(stone, e.Grid.Get(0, 0, 0));
        Assert.Equal(stone, e.Grid.Get(1, 0, 0));
    }
}
