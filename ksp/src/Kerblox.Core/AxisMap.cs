using System;

namespace Kerblox.Core
{
    /// <summary>
    /// A signed axis permutation: maps a vector in one grid's local space to another's,
    /// <c>dst[i] = Sign[i] * src[Axis[i]]</c>. It's how an edit on one grid part
    /// carries over to a symmetry counterpart: identity for radial symmetry (the
    /// counterpart is a rotated copy with the same local frame), a reflection for
    /// mirror symmetry (KSP rotates mirror counterparts instead of scaling them, so
    /// one local axis comes out flipped).
    /// </summary>
    public readonly struct AxisMap : IEquatable<AxisMap>
    {
        private readonly byte ax, ay, az;     // source axis feeding each destination axis
        private readonly sbyte sx, sy, sz;    // ±1; 0 only in default(AxisMap)

        private AxisMap(int ax, int ay, int az, int sx, int sy, int sz)
        {
            this.ax = (byte)ax; this.ay = (byte)ay; this.az = (byte)az;
            this.sx = (sbyte)sx; this.sy = (sbyte)sy; this.sz = (sbyte)sz;
        }

        public static readonly AxisMap Identity = new AxisMap(0, 1, 2, 1, 1, 1);

        /// <summary>Reflection through the local YZ plane: x becomes -x.</summary>
        public static readonly AxisMap MirrorX = new AxisMap(0, 1, 2, -1, 1, 1);

        public bool IsValid => sx != 0;

        /// <summary>True when the map flips handedness (determinant -1).</summary>
        public bool IsReflection
        {
            get
            {
                // Sign of the permutation times the product of the axis signs.
                int inversions = (ax > ay ? 1 : 0) + (ax > az ? 1 : 0) + (ay > az ? 1 : 0);
                int det = sx * sy * sz * (inversions % 2 == 0 ? 1 : -1);
                return det < 0;
            }
        }

        public Float3 Apply(Float3 v) => new Float3(sx * Pick(v, ax), sy * Pick(v, ay), sz * Pick(v, az));

        /// <summary>
        /// The destination cell occupying the image of <paramref name="cell"/>'s centre.
        /// Works for cells outside either grid (an edit that grows the grid maps to
        /// the matching growth on the other side), because both layouts are centred
        /// on their part origins.
        /// </summary>
        public Int3 MapCell(Int3 cell, BlockLayout source, BlockLayout destination)
        {
            Float3 p = Apply(source.CellCenter(cell.X, cell.Y, cell.Z)) - destination.Origin;
            float b = destination.BlockSize;
            return new Int3((int)Math.Floor(p.X / b), (int)Math.Floor(p.Y / b), (int)Math.Floor(p.Z / b));
        }

        /// <summary>
        /// Rounds a 3x3 matrix (row-major, <c>dst = M * src</c>) to the signed axis
        /// permutation it approximates. Fails when any row or column has no entry
        /// within <paramref name="tolerance"/> of ±1, i.e. the frames aren't related by
        /// a multiple of 90 degrees and a reflection.
        /// </summary>
        public static bool TrySnap(float[] m, float tolerance, out AxisMap map)
        {
            if (m == null || m.Length != 9) throw new ArgumentException("Expected a 3x3 row-major matrix", nameof(m));
            map = default;
            var axis = new int[3];
            var sign = new int[3];
            bool[] used = new bool[3];
            for (int row = 0; row < 3; row++)
            {
                int best = -1;
                for (int col = 0; col < 3; col++)
                    if (Math.Abs(Math.Abs(m[row * 3 + col]) - 1f) <= tolerance) { best = col; break; }
                if (best < 0 || used[best]) return false;
                for (int col = 0; col < 3; col++)
                    if (col != best && Math.Abs(m[row * 3 + col]) > tolerance) return false;
                used[best] = true;
                axis[row] = best;
                sign[row] = m[row * 3 + best] > 0 ? 1 : -1;
            }
            map = new AxisMap(axis[0], axis[1], axis[2], sign[0], sign[1], sign[2]);
            return true;
        }

        /// <summary>
        /// The map from a grid to its mirror counterpart, given both grids' local axes
        /// in world space (unit vectors) and the normal of the mirror plane. Each
        /// source axis is reflected through the plane, then expressed in the
        /// counterpart's axes. Fails like <see cref="TrySnap"/> when the result isn't
        /// a signed axis permutation, or when the normal is zero.
        /// </summary>
        public static bool TryFromMirror(Float3[] sourceAxes, Float3[] counterpartAxes, Float3 planeNormal, float tolerance, out AxisMap map)
        {
            if (sourceAxes == null || sourceAxes.Length != 3) throw new ArgumentException("Expected three axes", nameof(sourceAxes));
            if (counterpartAxes == null || counterpartAxes.Length != 3) throw new ArgumentException("Expected three axes", nameof(counterpartAxes));
            map = default;
            float len = (float)Math.Sqrt(Float3.Dot(planeNormal, planeNormal));
            if (len < 1e-6f) return false;
            Float3 n = planeNormal * (1f / len);

            var m = new float[9];
            for (int col = 0; col < 3; col++)
            {
                Float3 s = sourceAxes[col];
                Float3 reflected = s - n * (2f * Float3.Dot(s, n));
                for (int row = 0; row < 3; row++)
                    m[row * 3 + col] = Float3.Dot(counterpartAxes[row], reflected);
            }
            return TrySnap(m, tolerance, out map);
        }

        private static float Pick(Float3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

        public bool Equals(AxisMap o) => ax == o.ax && ay == o.ay && az == o.az && sx == o.sx && sy == o.sy && sz == o.sz;
        public override bool Equals(object obj) => obj is AxisMap o && Equals(o);
        public override int GetHashCode() => (ax | ay << 2 | az << 4) ^ (sx << 8) ^ (sy << 10) ^ (sz << 12);

        public override string ToString()
        {
            const string names = "xyz";
            Func<int, int, string> term = (s, a) => (s < 0 ? "-" : "+") + names[a];
            return IsValid ? $"[{term(sx, ax)},{term(sy, ay)},{term(sz, az)}]" : "[invalid]";
        }
    }
}
