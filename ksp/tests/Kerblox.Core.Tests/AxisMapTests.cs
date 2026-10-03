using Kerblox.Core;

namespace Kerblox.Core.Tests;

public class AxisMapTests
{
    private const float Bs = 0.625f;
    private const float Tol = 1e-3f;

    private static Float3 Norm(Float3 v) => v * (1f / MathF.Sqrt(Float3.Dot(v, v)));

    /// <summary>Unity-style frame from forward and up (like Quaternion.LookRotation): x = up × forward.</summary>
    private static Float3[] LookFrame(Float3 forward, Float3 up)
    {
        Float3 z = Norm(forward);
        Float3 x = Norm(Float3.Cross(up, z));
        Float3 y = Float3.Cross(z, x);
        return new[] { x, y, z };
    }

    private static Float3 Reflect(Float3 v, Float3 n) => v - n * (2f * Float3.Dot(v, n));

    private static Float3 ToWorld(Float3[] frame, Float3 origin, Float3 local) =>
        origin + frame[0] * local.X + frame[1] * local.Y + frame[2] * local.Z;

    private static void AssertClose(Float3 a, Float3 b)
    {
        Assert.InRange(a.X - b.X, -Tol, Tol);
        Assert.InRange(a.Y - b.Y, -Tol, Tol);
        Assert.InRange(a.Z - b.Z, -Tol, Tol);
    }

    [Fact]
    public void IdentityMapsEveryCellToItself()
    {
        var layout = new BlockLayout(4, 8, 4, Bs);
        for (int x = -1; x <= 4; x++)
            Assert.Equal(new Int3(x, 3, 2), AxisMap.Identity.MapCell(new Int3(x, 3, 2), layout, layout));
        Assert.False(AxisMap.Identity.IsReflection);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void MirrorXFlipsTheXIndexForEvenAndOddWidths(int sizeX)
    {
        var layout = new BlockLayout(sizeX, 3, 2, Bs);
        for (int x = 0; x < sizeX; x++)
            Assert.Equal(new Int3(sizeX - 1 - x, 1, 0), AxisMap.MirrorX.MapCell(new Int3(x, 1, 0), layout, layout));
        Assert.True(AxisMap.MirrorX.IsReflection);
    }

    [Fact]
    public void GrowthMapsToGrowthOnTheMirroredSide()
    {
        var layout = new BlockLayout(4, 2, 2, Bs);
        Assert.Equal(new Int3(4, 0, 1), AxisMap.MirrorX.MapCell(new Int3(-1, 0, 1), layout, layout));
        Assert.Equal(new Int3(-1, 0, 1), AxisMap.MirrorX.MapCell(new Int3(4, 0, 1), layout, layout));
        // Growth on an axis the mirror doesn't flip stays on the same side.
        Assert.Equal(new Int3(3, 2, 1), AxisMap.MirrorX.MapCell(new Int3(0, 2, 1), layout, layout));
    }

    [Fact]
    public void SnapRecognisesPermutationsAndRejectsSkew()
    {
        // dst.x = -src.z, dst.y = src.x, dst.z = src.y
        Assert.True(AxisMap.TrySnap(new[] { 0f, 0f, -1f, 1f, 0f, 0f, 0f, 1f, 0f }, Tol, out AxisMap m));
        AssertClose(new Float3(-3, 1, 2), m.Apply(new Float3(1, 2, 3)));
        Assert.True(m.IsReflection);   // a cyclic permutation with one flipped axis
        Assert.True(AxisMap.TrySnap(new[] { 0f, 0f, -1f, -1f, 0f, 0f, 0f, 1f, 0f }, Tol, out m));
        Assert.False(m.IsReflection);

        float c = MathF.Cos(0.3f), s = MathF.Sin(0.3f);
        Assert.False(AxisMap.TrySnap(new[] { c, -s, 0f, s, c, 0f, 0f, 0f, 1f }, Tol, out _));
        // Two rows picking the same source axis is not a permutation.
        Assert.False(AxisMap.TrySnap(new[] { 1f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f }, Tol, out _));
    }

    /// <summary>
    /// EditorLogic.UpdateSymmetry builds a mirror counterpart by reflecting the
    /// source's up and forward through the mirror plane and calling LookRotation,
    /// so the counterpart is a rotation, not a reflection. Whatever the source's
    /// orientation, that leaves local x flipped.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f, 1f, 0f, 1f, 0f)]       // upright
    [InlineData(0.3f, -0.2f, 0.9f, 0.1f, 1f, 0.25f)] // tilted and yawed
    [InlineData(1f, 0f, 0f, 0f, 0f, 1f)]       // lying on its side
    public void KspMirrorPlacementMapsToMirrorX(float fx, float fy, float fz, float ux, float uy, float uz)
    {
        Float3 normal = new Float3(1, 0, 0); // the root part's right axis
        Float3[] src = LookFrame(new Float3(fx, fy, fz), new Float3(ux, uy, uz));
        Float3[] dst = LookFrame(Reflect(src[2], normal), Reflect(src[1], normal));

        Assert.True(AxisMap.TryFromMirror(src, dst, normal, Tol, out AxisMap map));
        Assert.Equal(AxisMap.MirrorX, map);

        // Geometric check: every block centre lands on the mirror image of the source's.
        var layout = new BlockLayout(3, 4, 2, Bs);
        Float3 srcOrigin = new Float3(2.5f, 1f, -0.7f);
        Float3 dstOrigin = Reflect(srcOrigin, normal);
        for (int y = 0; y < 4; y++)
            for (int z = 0; z < 2; z++)
                for (int x = 0; x < 3; x++)
                {
                    Int3 mapped = map.MapCell(new Int3(x, y, z), layout, layout);
                    Float3 a = Reflect(ToWorld(src, srcOrigin, layout.CellCenter(x, y, z)), normal);
                    Float3 b = ToWorld(dst, dstOrigin, layout.CellCenter(mapped.X, mapped.Y, mapped.Z));
                    AssertClose(a, b);
                }
    }

    [Fact]
    public void FlippedSurfaceMirrorCounterpartMapsToMirrorZ()
    {
        // For some surface attachments KSP also turns the counterpart 180° about its up axis.
        Float3 normal = new Float3(1, 0, 0);
        Float3[] src = LookFrame(new Float3(0.2f, 0f, 1f), new Float3(0f, 1f, 0f));
        Float3[] dst = LookFrame(Reflect(src[2], normal), Reflect(src[1], normal));
        dst = new[] { dst[0] * -1f, dst[1], dst[2] * -1f };

        Assert.True(AxisMap.TryFromMirror(src, dst, normal, Tol, out AxisMap map));
        Assert.True(map.IsReflection);
        AssertClose(new Float3(1, 2, -3), map.Apply(new Float3(1, 2, 3)));
    }

    [Fact]
    public void MirrorWithoutAPlaneFails()
    {
        Float3[] f = LookFrame(new Float3(0, 0, 1), new Float3(0, 1, 0));
        Assert.False(AxisMap.TryFromMirror(f, f, Float3.Zero, Tol, out _));
    }
}
