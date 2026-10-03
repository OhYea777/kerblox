using Kerblox.Core;

namespace Kerblox.Core.Tests;

public class VoxelGridTests
{
    private static readonly BlockState Stone = new(BlockRegistry.Ids.Stone);

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

    [Fact]
    public void BlockStatePacksTypeAndData()
    {
        var s = new BlockState(typeId: 0x1234, data: 0xABCD);
        Assert.Equal(0x1234, s.TypeId);
        Assert.Equal(0xABCD, s.Data);
        Assert.Equal(0xABCD1234u, s.Raw);
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
}
