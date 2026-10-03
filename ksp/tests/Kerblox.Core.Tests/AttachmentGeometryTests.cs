using Kerblox.Core;

namespace Kerblox.Core.Tests;

public class AttachmentGeometryTests
{
    private static readonly BlockState Stone = BlockState.Parse("minecraft:stone");
    private static readonly BlockRegistry Registry = BlockRegistry.CreateDefault();
    private const float Bs = 0.625f;
    private const float Eps = 1e-5f;

    private static void Near(Float3 expected, Float3 actual)
    {
        Assert.Equal(expected.X, actual.X, Eps);
        Assert.Equal(expected.Y, actual.Y, Eps);
        Assert.Equal(expected.Z, actual.Z, Eps);
    }

    private static VoxelGrid Solid(int sx, int sy, int sz)
    {
        var g = new VoxelGrid(sx, sy, sz);
        g.Fill(0, 0, 0, sx - 1, sy - 1, sz - 1, Stone);
        return g;
    }

    private static StackNodes Nodes(VoxelGrid g) =>
        StackNodes.From(MassProperties.Compute(g, Registry, new BlockLayout(g, Bs)));

    /// <summary>
    /// Simulates the plugin: where the stack children and the part itself end up, in
    /// the old part frame, after an edit. Returns world-ish positions of the old blocks'
    /// reference point and of each node's attached part.
    /// </summary>
    private static (Float3 self, Float3 topChild, Float3 bottomChild) Apply(
        VoxelGrid before, GridEdit edit, bool anchoredAtBottom)
    {
        StackNodes oldN = Nodes(before), newN = Nodes(edit.Grid);
        Float3 self = AttachmentGeometry.SelfTranslation(edit.Displacement(Bs), anchoredAtBottom, oldN.Bottom, newN.Bottom);
        // Children ride along with the part, plus their node's move.
        Float3 top = oldN.Top + self + (newN.Top - oldN.Top);
        Float3 bottom = oldN.Bottom + self + (newN.Bottom - oldN.Bottom);
        return (self, top, bottom);
    }

    [Fact]
    public void StackNodesSitOnOccupiedBoundsOnTheAxis()
    {
        var g = new VoxelGrid(4, 8, 4);
        g.Fill(1, 2, 0, 2, 5, 3, Stone);   // occupies y 2..5 of 8
        StackNodes n = Nodes(g);
        Assert.True(n.Valid);
        Near(new Float3(0, 2 * Bs, 0), n.Top);
        Near(new Float3(0, -2 * Bs, 0), n.Bottom);
        Assert.False(Nodes(new VoxelGrid(2, 2, 2)).Valid);
    }

    [Fact]
    public void RootGrowingUpKeepsBlocksFixedAndCarriesTopChild()
    {
        var g = Solid(4, 8, 4);
        var before = g.Clone();
        GridEdit edit = GridEditor.SetBlock(g, 1, 8, 1, Stone);

        var (self, top, bottom) = Apply(before, edit, anchoredAtBottom: false);

        // Old blocks displaced by D in part space; the part moves by -D: net zero.
        Near(Float3.Zero, edit.Displacement(Bs) + self);
        Near(new Float3(0, 4 * Bs + Bs, 0), top);    // one block higher
        Near(new Float3(0, -4 * Bs, 0), bottom);      // unchanged
    }

    [Fact]
    public void RootGrowingDownCarriesBottomChildDown()
    {
        var g = Solid(4, 8, 4);
        var before = g.Clone();
        GridEdit edit = GridEditor.SetBlock(g, 1, -1, 1, Stone);

        var (self, top, bottom) = Apply(before, edit, anchoredAtBottom: false);

        Near(Float3.Zero, edit.Displacement(Bs) + self);
        Near(new Float3(0, 4 * Bs, 0), top);
        Near(new Float3(0, -4 * Bs - Bs, 0), bottom);
    }

    [Fact]
    public void PartHangingFromBottomNodeStaysOnItsParentWhenGrowingDown()
    {
        var g = Solid(4, 8, 4);
        var before = g.Clone();
        GridEdit edit = GridEditor.SetBlock(g, 1, -1, 1, Stone);

        var (self, top, bottom) = Apply(before, edit, anchoredAtBottom: true);

        Near(new Float3(0, -4 * Bs, 0), bottom);       // still touching the parent
        Near(new Float3(0, 4 * Bs + Bs, 0), top);      // pushed up by the new layer
        Near(new Float3(0, Bs, 0), edit.Displacement(Bs) + self);  // old blocks pushed up a block
    }

    [Fact]
    public void PartHangingFromBottomNodeGrowingUpDoesNotMove()
    {
        var g = Solid(4, 8, 4);
        var before = g.Clone();
        GridEdit edit = GridEditor.SetBlock(g, 1, 8, 1, Stone);

        var (self, top, _) = Apply(before, edit, anchoredAtBottom: true);

        Near(Float3.Zero, edit.Displacement(Bs) + self);
        Near(new Float3(0, 5 * Bs, 0), top);
    }

    [Fact]
    public void SideGrowthOnRootKeepsBlocksFixed()
    {
        var g = Solid(4, 8, 4);
        GridEdit edit = GridEditor.SetBlock(g, -1, 3, 1, Stone);
        Float3 self = AttachmentGeometry.SelfTranslation(edit.Displacement(Bs), false, Float3.Zero, Float3.Zero);
        Near(Float3.Zero, edit.Displacement(Bs) + self);
        Assert.NotEqual(0f, self.X);
    }

    [Fact]
    public void FindsSideFaceContact()
    {
        var g = Solid(4, 8, 4);
        var layout = new BlockLayout(g, Bs);
        // Middle of the +X face of cell (3,5,1).
        var p = new Float3(2 * Bs, layout.CellCenter(3, 5, 1).Y, layout.CellCenter(3, 5, 1).Z);

        SurfaceContact c = AttachmentGeometry.FindContact(g, Registry, layout, p, new Float3(1, 0, 0));

        Assert.NotNull(c);
        Assert.Equal(0, c!.Axis);
        Assert.Equal(1, c.Sign);
        Assert.Equal((3, 3), (c.Lo(0), c.Hi(0)));
        Assert.Equal((5, 5), (c.Lo(1), c.Hi(1)));
        Assert.Equal((1, 1), (c.Lo(2), c.Hi(2)));
    }

    [Fact]
    public void PointOnBlockBoundaryCoversBothNeighbours()
    {
        var g = Solid(4, 8, 4);
        var layout = new BlockLayout(g, Bs);
        // Centre of the -Z face: x = 0 and y = 0 are both block boundaries.
        var p = new Float3(0, 0, -2 * Bs);

        SurfaceContact c = AttachmentGeometry.FindContact(g, Registry, layout, p, new Float3(0, 0, -1));

        Assert.NotNull(c);
        Assert.Equal((2, -1), (c!.Axis, c.Sign));
        Assert.Equal((1, 2), (c.Lo(0), c.Hi(0)));
        Assert.Equal((3, 4), (c.Lo(1), c.Hi(1)));
        Assert.Equal((0, 0), (c.Lo(2), c.Hi(2)));
    }

    [Fact]
    public void HintPicksFaceOnAnEdge()
    {
        var g = Solid(2, 2, 2);
        var layout = new BlockLayout(g, Bs);
        var edge = new Float3(Bs, Bs, 0.1f);   // +X/+Y edge

        Assert.Equal(0, AttachmentGeometry.FindContact(g, Registry, layout, edge, new Float3(1, 0.2f, 0))!.Axis);
        Assert.Equal(1, AttachmentGeometry.FindContact(g, Registry, layout, edge, new Float3(0.2f, 1, 0))!.Axis);
    }

    [Fact]
    public void PointOffTheSurfaceHasNoContact()
    {
        var g = Solid(4, 8, 4);
        var layout = new BlockLayout(g, Bs);
        Assert.Null(AttachmentGeometry.FindContact(g, Registry, layout, new Float3(2 * Bs + 0.2f, 0.1f, 0.1f), new Float3(1, 0, 0)));
        // Inside the grid (an internal boundary with solid on both sides) isn't a face either.
        Assert.Null(AttachmentGeometry.FindContact(g, Registry, layout, new Float3(0, 0.1f, 0.1f), Float3.Zero));
    }

    [Fact]
    public void RemovingSupportSlidesInward()
    {
        var g = Solid(4, 8, 4);
        var layout = new BlockLayout(g, Bs);
        var p = new Float3(2 * Bs, layout.CellCenter(3, 5, 1).Y, layout.CellCenter(3, 5, 1).Z);
        SurfaceContact c = AttachmentGeometry.FindContact(g, Registry, layout, p, new Float3(1, 0, 0))!;

        GridEdit edit = GridEditor.RemoveBlock(g, 3, 5, 1);
        SurfaceMove m = AttachmentGeometry.Resolve(c, edit, Registry, Bs);

        Assert.Equal(SurfaceSupport.Slid, m.Support);
        Assert.Equal(1, m.SlideCells);
        Near(new Float3(-Bs, 0, 0), m.Translation);
    }

    [Fact]
    public void RemovingWholeColumnLeavesPartUnsupported()
    {
        var g = new VoxelGrid(3, 1, 1);
        g.Fill(0, 0, 0, 2, 0, 0, Stone);
        var layout = new BlockLayout(g, Bs);
        var p = new Float3(1.5f * Bs, 0.1f, 0.1f);
        SurfaceContact c = AttachmentGeometry.FindContact(g, Registry, layout, p, new Float3(1, 0, 0))!;

        for (int x = 0; x < 3; x++) g.Set(x, 0, 0, BlockState.Air);
        SurfaceMove m = AttachmentGeometry.Resolve(c, new GridEdit(GridEditStatus.Edited, g, 3, 1, 1), Registry, Bs);

        Assert.Equal(SurfaceSupport.Unsupported, m.Support);
        Near(Float3.Zero, m.Translation);
    }

    [Fact]
    public void HollowWallDoesNotSlideThroughToTheFarSide()
    {
        var g = Solid(4, 8, 4);
        g.Fill(1, 1, 1, 2, 6, 2, BlockState.Air);
        var layout = new BlockLayout(g, Bs);
        var p = new Float3(2 * Bs, layout.CellCenter(3, 5, 1).Y, layout.CellCenter(3, 5, 1).Z);
        SurfaceContact c = AttachmentGeometry.FindContact(g, Registry, layout, p, new Float3(1, 0, 0))!;

        GridEdit edit = GridEditor.RemoveBlock(g, 3, 5, 1);

        Assert.Equal(SurfaceSupport.Unsupported, AttachmentGeometry.Resolve(c, edit, Registry, Bs).Support);
        SurfaceMove far = AttachmentGeometry.Resolve(c, edit, Registry, Bs, maxSlide: 4);
        Assert.Equal((SurfaceSupport.Slid, 3), (far.Support, far.SlideCells));
    }

    [Fact]
    public void NeighbourOnBoundaryStillSupports()
    {
        var g = Solid(4, 8, 4);
        var layout = new BlockLayout(g, Bs);
        var p = new Float3(0, 0, -2 * Bs);
        SurfaceContact c = AttachmentGeometry.FindContact(g, Registry, layout, p, new Float3(0, 0, -1))!;

        GridEdit edit = GridEditor.RemoveBlock(g, 1, 3, 0);
        Assert.Equal(SurfaceSupport.Supported, AttachmentGeometry.Resolve(c, edit, Registry, Bs).Support);
    }

    [Fact]
    public void ResizeMovesSurfaceChildWithBlocks()
    {
        var g = Solid(4, 8, 4);
        var layout = new BlockLayout(g, Bs);
        var p = new Float3(2 * Bs, layout.CellCenter(3, 5, 1).Y, layout.CellCenter(3, 5, 1).Z);
        SurfaceContact c = AttachmentGeometry.FindContact(g, Registry, layout, p, new Float3(1, 0, 0))!;

        GridEdit edit = GridEditor.SetBlock(g, 0, -2, 0, Stone);   // grows down by two
        SurfaceMove m = AttachmentGeometry.Resolve(c, edit, Registry, Bs);

        Assert.Equal(SurfaceSupport.Supported, m.Support);
        Near(edit.Displacement(Bs), m.Translation);
        // The attach point is still on the same block's face in the new layout.
        var newLayout = new BlockLayout(edit.Grid, Bs);
        Near(new Float3(newLayout.CellMin(4, 0, 0).X, newLayout.CellCenter(3, 7, 1).Y, newLayout.CellCenter(3, 7, 1).Z), p + m.Translation);
    }
}
