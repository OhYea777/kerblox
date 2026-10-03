using Kerblox.Core;

namespace Kerblox.Core.Tests;

public class MesherTests
{
    private static readonly BlockRegistry Registry = BlockRegistry.CreateDefault();
    private static readonly BlockState Stone = BlockState.Parse("minecraft:stone");
    private static readonly BlockState Glass = BlockState.Parse("minecraft:glass");

    private static readonly Float2 Uv0 = new(0, 0), Uv1 = new(1, 1);

    /// <summary>Models by block name, falling back to the built-in cubes.</summary>
    private sealed class TestSource : IBlockModelSource
    {
        private readonly Dictionary<string, BlockModel> models = new();
        private readonly IBlockModelSource rest = new BuiltinModelSource(Registry).CreateResolver();
        public int Calls;

        public TestSource Add(string name, BlockModel model) { models[name] = model; return this; }

        public BlockModel GetModel(BlockState state)
        {
            Calls++;
            return models.TryGetValue(state.Name, out var m) ? m : rest.GetModel(state);
        }
    }

    private static BlockModel Cube(FaceMask occludes, RenderLayer layer = RenderLayer.Solid, int atlas = 0, Rgba32? tint = null) =>
        BlockModel.Cube(atlas, Uv0, Uv1, layer, tint ?? Rgba32.White, occludes);

    private static MeshData Mesh(VoxelGrid g, IBlockModelSource models) => GridMesher.Build(g, models, new BlockLayout(g, 1f));

    private static VoxelGrid Row(params BlockState[] states)
    {
        var g = new VoxelGrid(states.Length, 1, 1);
        for (int x = 0; x < states.Length; x++) g.Set(x, 0, 0, states[x]);
        return g;
    }

    [Fact]
    public void OccludingNeighbourCullsOnlyTheTouchingFaces()
    {
        Assert.Equal(10, GridMesher.Build(Row(Stone, Stone), Registry, new BlockLayout(Row(Stone, Stone), 1f)).FaceCount);
    }

    [Fact]
    public void NonOccludingNeighbourKeepsTheFaceButIsCulledByAnOccluder()
    {
        // A bottom-slab-like block: only its bottom covers the cell boundary.
        var src = new TestSource().Add("test:slab", Cube(FaceMask.Down));
        var slab = BlockState.Parse("test:slab");

        // Side by side: stone's east face shows (slab doesn't cover its west side);
        // slab's west face hides behind stone.
        Assert.Equal(6 + 6 - 1, Mesh(Row(Stone, slab), src).FaceCount);

        // Slab on stone: each hides the other's touching face.
        var g = new VoxelGrid(1, 2, 1);
        g.Set(0, 0, 0, Stone);
        g.Set(0, 1, 0, slab);
        Assert.Equal(10, Mesh(g, src).FaceCount);

        // Stone on slab: the slab's top doesn't occlude, so stone's bottom shows;
        // stone still hides the slab's top.
        g.Set(0, 0, 0, slab);
        g.Set(0, 1, 0, Stone);
        Assert.Equal(11, Mesh(g, src).FaceCount);
    }

    [Fact]
    public void OcclusionIsPerDirection()
    {
        // Occludes only toward +X: hides the stone east of it, not the one west of it.
        var src = new TestSource().Add("test:wall", Cube(FaceMask.East));
        var g = Row(Stone, BlockState.Parse("test:wall"), Stone);
        // wall's two side faces hide behind stone; stone(0)'s east face shows; stone(2)'s west face hides.
        Assert.Equal(18 - 2 - 1, Mesh(g, src).FaceCount);
        Assert.True(GridMesher.IsFaceVisible(Stone, BlockState.Parse("test:wall"), Direction.East, src));
        Assert.False(GridMesher.IsFaceVisible(Stone, BlockState.Parse("test:wall"), Direction.West, src));
    }

    [Fact]
    public void UnculledQuadsDrawEvenWhenEnclosed()
    {
        var quad = new BlockQuad(new(0, 0, 0.5f), new(0, 1, 0.5f), new(1, 1, 0.5f), new(1, 0, 0.5f),
            Uv0, new(0, 1), Uv1, new(1, 0), new(0, 0, -1), null, RenderLayer.Cutout, 0, Rgba32.White);
        var src = new TestSource().Add("test:cross", new BlockModel(new[] { quad }, FaceMask.None));

        var g = new VoxelGrid(3, 3, 3);
        g.Fill(0, 0, 0, 2, 2, 2, Stone);
        g.Set(1, 1, 1, BlockState.Parse("test:cross"));
        var m = Mesh(g, src);

        // Outer shell, the six stone faces around the non-occluding centre, and the cross quad.
        Assert.Equal(54 + 6 + 1, m.FaceCount);
        Assert.Equal(1, m.SubMeshes.Single(s => s.Key.Layer == RenderLayer.Cutout).QuadCount);
    }

    [Fact]
    public void SeeThroughBlocksCullAgainstOtherStatesOfTheSameBlock()
    {
        var tinted = BlockState.Parse("minecraft:glass[pane=a]");
        Assert.Equal(10, Mesh(Row(Glass, tinted), new BuiltinModelSource(Registry).CreateResolver()).FaceCount);
        // Glass next to stone: glass hides its face, stone shows its face toward the glass.
        Assert.Equal(11, Mesh(Row(Glass, Stone), new BuiltinModelSource(Registry).CreateResolver()).FaceCount);
    }

    [Fact]
    public void LayersAtlasesAndTintsBecomeSeparateSubmeshesInDrawOrder()
    {
        var red = new Rgba32(255, 0, 0);
        var src = new TestSource()
            .Add("test:stained", Cube(FaceMask.None, RenderLayer.Translucent, atlas: 1))
            .Add("test:leaves", Cube(FaceMask.None, RenderLayer.Cutout, tint: red))
            .Add("test:red", Cube(FaceMask.All, tint: red));
        var g = new VoxelGrid(7, 1, 1); // spaced out so nothing culls
        g.Set(0, 0, 0, BlockState.Parse("test:stained"));
        g.Set(2, 0, 0, BlockState.Parse("test:leaves"));
        g.Set(4, 0, 0, Stone);
        g.Set(6, 0, 0, BlockState.Parse("test:red"));
        var m = Mesh(g, src);

        Assert.Equal(new[]
        {
            new MaterialKey(RenderLayer.Solid, 0, Rgba32.White),
            new MaterialKey(RenderLayer.Solid, 0, red),
            new MaterialKey(RenderLayer.Cutout, 0, red),
            new MaterialKey(RenderLayer.Translucent, 1, Rgba32.White),
        }.OrderBy(k => k).ToArray(), m.SubMeshes.Select(s => s.Key).ToArray());
        Assert.All(m.SubMeshes, s => Assert.Equal(6, s.QuadCount));
        Assert.Equal(RenderLayer.Translucent, m.SubMeshes.Last().Key.Layer);
        Assert.Equal(RenderLayer.Solid, m.SubMeshes.First().Key.Layer);

        // Every submesh's triangles stay within its own block's vertices.
        int vertsPerBlock = 24;
        Assert.Equal(4 * vertsPerBlock, m.Vertices.Count);
        Assert.All(m.SubMeshes, s => Assert.Single(s.Triangles.Select(i => i / vertsPerBlock).Distinct()));
    }

    [Fact]
    public void UnknownStatesFallBackToAMapColourCube()
    {
        var builtin = new BuiltinModelSource(Registry);
        var unknown = BlockState.Parse("create:andesite_casing");
        var g = Row(unknown, Stone);
        var m = Mesh(g, builtin.CreateResolver());

        // The fallback is opaque, so it hides stone's west face and vice versa.
        Assert.Equal(10, m.FaceCount);
        var fallback = m.SubMeshes.Single(s => s.Key.Tint != Rgba32.White);
        Assert.Equal(MapColorFallback.NameColor(unknown), fallback.Key.Tint);
        Assert.Equal(RenderLayer.Solid, fallback.Key.Layer);
        Assert.Equal(5, fallback.QuadCount);

        float lo = (float)builtin.FallbackTileIndex / builtin.AtlasTileCount;
        Assert.All(fallback.Triangles, i => Assert.InRange(m.Uvs[i].U, lo, 1f));
    }

    [Fact]
    public void FallbackColourIsPerBlockAndInjectable()
    {
        var a = BlockState.Parse("mod:thing[facing=north]");
        Assert.Equal(MapColorFallback.NameColor(a), MapColorFallback.NameColor(BlockState.Parse("mod:thing[facing=south]")));

        var blue = new Rgba32(0, 0, 255);
        var fb = new MapColorFallback(3, Uv0, Uv1, _ => blue);
        var quad = fb.GetModel(a)!.CulledBy(Direction.Up)[0];
        Assert.Equal(blue, quad.Tint);
        Assert.Equal(3, quad.Atlas);
        Assert.Null(fb.GetModel(BlockState.Air));
    }

    [Fact]
    public void ResolverTakesTheFirstSourceThatKnowsTheState()
    {
        var custom = new TestSource().Add("minecraft:stone", Cube(FaceMask.All, RenderLayer.Cutout));
        var builtin = new BuiltinModelSource(Registry);
        var resolver = new BlockModelResolver(custom, builtin, builtin.Fallback);
        Assert.Equal(RenderLayer.Cutout, resolver.GetModel(Stone).CulledBy(Direction.Up)[0].Layer);
        Assert.Same(BlockModel.Empty, resolver.GetModel(BlockState.Air));

        // Without a fallback, unknown states draw nothing.
        var bare = new BlockModelResolver(builtin);
        Assert.True(bare.GetModel(BlockState.Parse("mod:unknown")).IsEmpty);
        Assert.Equal(0, Mesh(Row(BlockState.Parse("mod:unknown")), bare).FaceCount);
    }

    [Fact]
    public void ModelsResolveOncePerPaletteEntry()
    {
        var src = new TestSource();
        var g = new VoxelGrid(8, 8, 8);
        g.Fill(0, 0, 0, 7, 3, 7, Stone);
        g.Fill(0, 4, 0, 7, 7, 7, Glass);
        Mesh(g, src);
        Assert.Equal(2, src.Calls); // air is never asked for
    }

    [Fact]
    public void DefaultCapsuleMatchesTheFixedCubeMesher()
    {
        var g = DefaultGrids.Capsule();
        var m = GridMesher.Build(g, Registry, new BlockLayout(g, 0.625f));

        // The pre-model rule: a face shows unless the neighbour is opaque or the same see-through type.
        int expected = 0;
        for (int y = 0; y < g.SizeY; y++)
            for (int z = 0; z < g.SizeZ; z++)
                for (int x = 0; x < g.SizeX; x++)
                {
                    BlockType? self = Registry.Get(g.Get(x, y, z));
                    if (self == null) continue;
                    foreach (Direction d in Directions.All)
                    {
                        d.Offset(out int dx, out int dy, out int dz);
                        BlockType? n = Registry.Get(g.Get(x + dx, y + dy, z + dz));
                        if (n == null || (!n.Opaque && n != self)) expected++;
                    }
                }

        Assert.Equal(expected, m.FaceCount);
        var only = Assert.Single(m.SubMeshes);
        Assert.Equal(new MaterialKey(RenderLayer.Solid, BuiltinModelSource.AtlasIndex, Rgba32.White), only.Key);
    }

    [Fact]
    public void DirectionHelpersAreConsistent()
    {
        foreach (Direction d in Directions.All)
        {
            Assert.Equal(d, d.Opposite().Opposite());
            Assert.NotEqual(d, d.Opposite());
            Assert.Equal(Float3.Zero, d.Normal() + d.Opposite().Normal());
        }
        Direction.East.Offset(out int ex, out _, out _);
        Direction.South.Offset(out _, out _, out int sz);
        Assert.Equal(1, ex);
        Assert.Equal(1, sz);
    }
}
