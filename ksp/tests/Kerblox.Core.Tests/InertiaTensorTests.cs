using Kerblox.Core;

namespace Kerblox.Core.Tests;

public class InertiaTensorTests
{
    private static readonly BlockRegistry Registry = BlockRegistry.CreateDefault();
    private static readonly BlockState Stone = BlockState.Parse("minecraft:stone");
    private static readonly BlockState Iron = BlockState.Parse("minecraft:iron_block");

    private static InertiaTensor Compute(VoxelGrid g, float size) =>
        InertiaTensor.Compute(g, Registry, new BlockLayout(g, size));

    /// <summary>Solid box a×b×c of mass m about its centre: I_xx = m(b²+c²)/12 etc.</summary>
    private static (double x, double y, double z) Box(double m, double a, double b, double c) =>
        (m * (b * b + c * c) / 12, m * (a * a + c * c) / 12, m * (a * a + b * b) / 12);

    private static void Near(double expected, double actual, double relTol = 1e-5) =>
        Assert.True(Math.Abs(expected - actual) <= relTol * Math.Max(1e-9, Math.Abs(expected)),
            $"expected {expected}, got {actual}");

    /// <summary>Rebuilds the full tensor from R·diag(I)·Rᵀ and compares it with the components.</summary>
    private static void AssertDecompositionMatches(InertiaTensor t)
    {
        var axes = new[] { new Float3(1, 0, 0), new Float3(0, 1, 0), new Float3(0, 0, 1) };
        var moments = new[] { t.PrincipalMoments.X, t.PrincipalMoments.Y, t.PrincipalMoments.Z };
        var full = new double[3, 3];
        for (int k = 0; k < 3; k++)
        {
            Float3 e = t.PrincipalRotation.Rotate(axes[k]);
            var ev = new double[] { e.X, e.Y, e.Z };
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    full[i, j] += moments[k] * ev[i] * ev[j];
        }
        double scale = Math.Max(t.Ixx, Math.Max(t.Iyy, t.Izz));
        void Close(double a, double b) => Assert.True(Math.Abs(a - b) <= 1e-5 * scale, $"expected {a}, got {b}");
        Close(t.Ixx, full[0, 0]); Close(t.Iyy, full[1, 1]); Close(t.Izz, full[2, 2]);
        Close(t.Ixy, full[0, 1]); Close(t.Ixz, full[0, 2]); Close(t.Iyz, full[1, 2]);
        Close(t.Ixy, full[1, 0]); Close(t.Ixz, full[2, 0]); Close(t.Iyz, full[2, 1]);
    }

    [Fact]
    public void EmptyGridIsZero()
    {
        var t = Compute(new VoxelGrid(2, 2, 2), 1f);
        Assert.Equal(0, t.Mass);
        Assert.Equal(Float3.Zero, t.PrincipalMoments);
    }

    [Fact]
    public void SingleBlockIsACube()
    {
        var g = new VoxelGrid(1, 1, 1);
        g.Set(0, 0, 0, Stone);
        const float s = 0.625f;
        var t = Compute(g, s);
        double m = 0.30 * s * s * s;
        Near(m, t.Mass);
        Near(m * s * s / 6, t.Ixx); Near(m * s * s / 6, t.Iyy); Near(m * s * s / 6, t.Izz);
        Assert.Equal(0, t.Ixy); Assert.Equal(0, t.Ixz); Assert.Equal(0, t.Iyz);
    }

    [Fact]
    public void UniformCuboidMatchesAnalyticBox()
    {
        var g = new VoxelGrid(3, 5, 2);
        g.Fill(0, 0, 0, 2, 4, 1, Stone);
        const float s = 0.5f;
        var t = Compute(g, s);
        double m = 0.30 * 30 * s * s * s;
        var (ix, iy, iz) = Box(m, 3 * s, 5 * s, 2 * s);
        Near(ix, t.Ixx); Near(iy, t.Iyy); Near(iz, t.Izz);
        Near(ix, t.PrincipalMoments.X); Near(iy, t.PrincipalMoments.Y); Near(iz, t.PrincipalMoments.Z);
        Assert.True(Math.Abs(t.Ixy) < 1e-9 && Math.Abs(t.Ixz) < 1e-9 && Math.Abs(t.Iyz) < 1e-9);
    }

    [Fact]
    public void IronBottomedStackMatchesTwoBoxesWithParallelAxis()
    {
        // 2x4x2: bottom 2x1x2 iron slab, 2x3x2 stone above. Unity's uniform-density
        // tensor would treat this as one 2x4x2 box about its centre; it isn't.
        var g = new VoxelGrid(2, 4, 2);
        g.Fill(0, 0, 0, 1, 0, 1, Iron);
        g.Fill(0, 1, 0, 1, 3, 1, Stone);
        const float s = 0.625f;
        var t = Compute(g, s);

        double v = s * s * s;
        double mIron = 0.60 * 4 * v, mStone = 0.30 * 12 * v, m = mIron + mStone;
        double yIron = -1.5 * s, yStone = 0.5 * s;                    // box centres, grid centred on 0
        double com = (mIron * yIron + mStone * yStone) / m;
        Near(com, t.CenterOfMass.Y);

        var a = Box(mIron, 2 * s, s, 2 * s);
        var b = Box(mStone, 2 * s, 3 * s, 2 * s);
        double dI = yIron - com, dS = yStone - com;
        double ixx = a.x + mIron * dI * dI + b.x + mStone * dS * dS;
        double iyy = a.y + b.y;
        Near(ixx, t.Ixx); Near(iyy, t.Iyy); Near(ixx, t.Izz);

        // And it really differs from the uniform-density answer for the same total mass.
        var uniform = Box(m, 2 * s, 4 * s, 2 * s);
        Assert.True(Math.Abs(uniform.x - t.Ixx) / t.Ixx > 0.01);
    }

    [Fact]
    public void DiagonalPairHasProductsOfInertiaAndRotatedPrincipalAxes()
    {
        // Two blocks on the XY diagonal: principal axes are rotated 45° about Z.
        var g = new VoxelGrid(2, 2, 1);
        g.Set(0, 0, 0, Stone);
        g.Set(1, 1, 0, Stone);
        const float s = 1f;
        var t = Compute(g, s);

        double m = 0.30;
        // Centres at ±(0.5, 0.5, 0): Σm·x·y = 2·m·0.25.
        Near(-0.5 * m, t.Ixy);
        Near(2 * m / 6 + 2 * m * 0.25, t.Ixx);
        Near(2 * m / 6 + 2 * m * 0.5, t.Izz);

        AssertDecompositionMatches(t);

        // One principal axis lies along the diagonal (1,1,0)/√2 with moment 2m/6 (no lever arm).
        double expectedAlongDiagonal = 2 * m / 6;
        var axes = new[] { new Float3(1, 0, 0), new Float3(0, 1, 0), new Float3(0, 0, 1) };
        var moments = new[] { t.PrincipalMoments.X, t.PrincipalMoments.Y, t.PrincipalMoments.Z };
        bool found = false;
        for (int k = 0; k < 3; k++)
        {
            Float3 e = t.PrincipalRotation.Rotate(axes[k]);
            if (Math.Abs(Math.Abs(e.X + e.Y) / Math.Sqrt(2) - 1) < 1e-4 && Math.Abs(moments[k] - expectedAlongDiagonal) < 1e-5)
                found = true;
        }
        Assert.True(found, $"no principal axis along the diagonal: {t}");
    }

    [Fact]
    public void DefaultCapsuleDecomposesConsistently()
    {
        var g = DefaultGrids.Capsule();
        var t = Compute(g, 0.625f);
        var p = MassProperties.Compute(g, Registry, new BlockLayout(g, 0.625f));
        Near(p.Mass, t.Mass);
        Near(p.CenterOfMass.Y, t.CenterOfMass.Y);
        AssertDecompositionMatches(t);
        // Rotationally symmetric about Y: Ixx == Izz, and Y is the short axis of a tall capsule.
        Near(t.Ixx, t.Izz);
        Assert.True(t.Iyy < t.Ixx);
    }

    [Fact]
    public void AsymmetricGridDecomposesConsistently()
    {
        var g = new VoxelGrid(4, 3, 5);
        g.Fill(0, 0, 0, 3, 0, 4, Stone);
        g.Fill(0, 1, 0, 0, 2, 1, Iron);
        g.Set(3, 2, 4, Iron);
        AssertDecompositionMatches(Compute(g, 0.625f));
    }

    [Fact]
    public void BlockStatePropertiesAndUnknownBlocksResolveThroughThePalette()
    {
        // Properties don't change density; unknown blocks weigh nothing, like air.
        var g = new VoxelGrid(3, 1, 1);
        g.Set(0, 0, 0, BlockState.Parse("minecraft:stone"));
        g.Set(1, 0, 0, BlockState.Parse("minecraft:iron_block[foo=bar]"));
        g.Set(2, 0, 0, BlockState.Parse("somemod:mystery_block"));
        var mass = InertiaTensor.RegistryCellMass(g, Registry, new BlockLayout(g, 1f));
        Near(0.30, mass(0, 0, 0));
        Near(0.60, mass(1, 0, 0));
        Assert.Equal(0, mass(2, 0, 0));
    }

    [Fact]
    public void CellMassCallbackIsUsedDirectly()
    {
        // The callback is the seam P1.8's palette grids plug into.
        var layout = new BlockLayout(new VoxelGrid(2, 1, 1), 1f);
        var t = InertiaTensor.Compute(2, 1, 1, layout, (x, y, z) => x == 0 ? 3.0 : 1.0);
        Near(4.0, t.Mass);
        Near(-0.25, t.CenterOfMass.X);   // (3·−0.5 + 1·0.5) / 4
    }
}
