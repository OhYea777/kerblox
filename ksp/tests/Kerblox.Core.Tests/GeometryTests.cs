using Kerblox.Core;

namespace Kerblox.Core.Tests;

public class GeometryTests
{
    private static readonly BlockRegistry Registry = BlockRegistry.CreateDefault();
    private static readonly BlockState Stone = BlockState.Parse("minecraft:stone");
    private static readonly BlockState Glass = BlockState.Parse("minecraft:glass");
    private static readonly BlockState Iron = BlockState.Parse("minecraft:iron_block");

    private static MeshData Mesh(VoxelGrid g, float size = 1f) =>
        GridMesher.Build(g, Registry, new BlockLayout(g, size));

    [Fact]
    public void SingleBlockHasSixFaces()
    {
        var g = new VoxelGrid(1, 1, 1);
        g.Set(0, 0, 0, Stone);
        var m = Mesh(g);
        Assert.Equal(6, m.FaceCount);
        Assert.Equal(24, m.Vertices.Count);
    }

    [Fact]
    public void SolidCuboidOnlyHasSurfaceFaces()
    {
        var g = new VoxelGrid(3, 4, 5);
        g.Fill(0, 0, 0, 2, 3, 4, Stone);
        Assert.Equal(2 * (3 * 4 + 4 * 5 + 3 * 5), Mesh(g).FaceCount);
    }

    [Fact]
    public void AdjacentGlassCullsSharedFaceButShowsThroughToStone()
    {
        var g = new VoxelGrid(3, 1, 1);
        g.Set(0, 0, 0, Glass);
        g.Set(1, 0, 0, Glass);
        g.Set(2, 0, 0, Stone);
        // glass|glass shared face hidden both ways; glass->stone hidden (stone opaque);
        // stone face toward glass visible (glass not opaque).
        Assert.Equal(6 * 3 - 2 - 1, Mesh(g).FaceCount);
    }

    [Fact]
    public void TrianglesWindOutward()
    {
        var g = DefaultGrids.Capsule();
        var m = Mesh(g, 0.625f);
        var tris = m.AllTriangles();
        for (int t = 0; t < tris.Count; t += 3)
        {
            Float3 a = m.Vertices[tris[t]], b = m.Vertices[tris[t + 1]], c = m.Vertices[tris[t + 2]];
            Float3 n = Float3.Cross(b - a, c - a);
            Float3 expected = m.Normals[tris[t]];
            Assert.True(Float3.Dot(n, expected) > 0, $"triangle {t / 3} winds inward");
        }
    }

    [Fact]
    public void UvsStayInsideTheBlocksTile()
    {
        var g = new VoxelGrid(1, 1, 1);
        g.Set(0, 0, 0, Iron);
        var m = Mesh(g);
        int tile = Registry.Get(Iron)!.TileIndex;
        int tiles = new BuiltinModelSource(Registry).AtlasTileCount;
        float lo = (float)tile / tiles, hi = (float)(tile + 1) / tiles;
        Assert.All(m.Uvs, uv => Assert.InRange(uv.U, lo, hi));
    }

    [Fact]
    public void BoxMergeOfCuboidIsOneBox()
    {
        var g = new VoxelGrid(4, 4, 4);
        g.Fill(0, 0, 0, 3, 3, 3, Stone);
        g.Fill(0, 0, 0, 3, 0, 3, Iron); // type changes must not split boxes
        var box = Assert.Single(BoxMerger.Merge(g, Registry));
        Assert.Equal(64, box.Volume);
    }

    [Fact]
    public void BoxMergeCoversExactlySolidCellsWithoutOverlap()
    {
        var g = DefaultGrids.Capsule();
        var boxes = BoxMerger.Merge(g, Registry);

        for (int y = 0; y < g.SizeY; y++)
            for (int z = 0; z < g.SizeZ; z++)
                for (int x = 0; x < g.SizeX; x++)
                {
                    int covering = boxes.Count(b => b.Contains(x, y, z));
                    Assert.Equal(Registry.IsSolid(g.Get(x, y, z)) ? 1 : 0, covering);
                }

        Assert.True(boxes.Count < g.CountNonAir() / 4, $"expected real merging, got {boxes.Count} boxes");
    }

    [Fact]
    public void MassAndComOfUniformCuboidIsCentred()
    {
        var g = new VoxelGrid(2, 2, 2);
        g.Fill(0, 0, 0, 1, 1, 1, Stone);
        var p = MassProperties.Compute(g, Registry, new BlockLayout(g, 0.5f));
        Assert.Equal(8 * 0.125 * 0.30, p.Mass, 6);
        Assert.Equal(0, p.CenterOfMass.X, 5);
        Assert.Equal(0, p.CenterOfMass.Y, 5);
        Assert.Equal(0, p.CenterOfMass.Z, 5);
        Assert.Equal(new Float3(-0.5f, -0.5f, -0.5f), p.BoundsMin);
        Assert.Equal(new Float3(0.5f, 0.5f, 0.5f), p.BoundsMax);
    }

    [Fact]
    public void ComShiftsTowardDenserBlocks()
    {
        var g = new VoxelGrid(1, 2, 1);
        g.Set(0, 0, 0, Iron);  // 0.60 t/m3, centre y = -0.5
        g.Set(0, 1, 0, Stone); // 0.30 t/m3, centre y = +0.5
        var p = MassProperties.Compute(g, Registry, new BlockLayout(g, 1f));
        Assert.Equal(0.9, p.Mass, 6);
        Assert.Equal((-0.5 * 0.6 + 0.5 * 0.3) / 0.9, p.CenterOfMass.Y, 5);
    }

    [Fact]
    public void DefaultCapsuleHasPlausibleNumbers()
    {
        var g = DefaultGrids.Capsule();
        var p = MassProperties.Compute(g, Registry, new BlockLayout(g, 0.625f));
        Assert.Equal(16 + 12 * 5 + 16 + 4, p.SolidBlocks);
        Assert.InRange(p.Mass, 5, 10);
        Assert.True(p.CenterOfMass.Y < 0, "iron base should pull CoM below centre");
        Assert.Equal(0, p.CenterOfMass.X, 5);
        Assert.Equal(0, p.CenterOfMass.Z, 5);
    }
}
