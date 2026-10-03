using System;

namespace Kerblox.Core
{
    /// <summary>Integer cell coordinate. May lie outside the grid (negative or past its size).</summary>
    public readonly struct Int3 : IEquatable<Int3>
    {
        public readonly int X, Y, Z;

        public Int3(int x, int y, int z) { X = x; Y = y; Z = z; }

        public static Int3 operator +(Int3 a, Int3 b) => new Int3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public bool Equals(Int3 o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object obj) => obj is Int3 o && Equals(o);
        public override int GetHashCode() => (X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791);
        public override string ToString() => $"({X},{Y},{Z})";
    }

    /// <summary>
    /// What a ray hit on a grid: the solid cell it struck, the face it entered through
    /// (a unit axis vector pointing out of that cell), and the neighbouring cell on
    /// that side, where a placed block goes. <see cref="Place"/> can lie outside the
    /// grid; <see cref="GridEditor.SetBlock"/> grows the grid to reach it.
    /// </summary>
    public readonly struct BlockPick
    {
        public readonly Int3 Hit;
        public readonly Int3 Face;

        public BlockPick(Int3 hit, Int3 face) { Hit = hit; Face = face; }

        public Int3 Place => Hit + Face;

        public override string ToString() => $"hit {Hit} face {Face} place {Place}";
    }

    /// <summary>
    /// Turns a physics hit on the grid's colliders (point and surface normal, both in
    /// the grid's local space, i.e. the space <see cref="BlockLayout"/> describes) into
    /// cell coordinates. Kept out of the plugin so it can be tested without Unity.
    /// </summary>
    public static class BlockPicker
    {
        /// <summary>
        /// The hit cell is the one just behind the surface: the point is pushed half a
        /// block against the face normal before flooring, so hits on a box edge, on a
        /// merged box's interior seam or slightly off the surface (float noise) still
        /// resolve to the cell whose face was struck. The normal is snapped to its
        /// dominant axis; colliders are axis-aligned boxes, so it already is one, give
        /// or take rounding.
        /// </summary>
        public static BlockPick FromHit(BlockLayout layout, Float3 point, Float3 normal)
        {
            Int3 face = DominantAxis(normal);
            float half = layout.BlockSize / 2f;
            var inner = new Float3(point.X - face.X * half, point.Y - face.Y * half, point.Z - face.Z * half);
            var hit = new Int3(
                Cell(inner.X, layout.Origin.X, layout.BlockSize),
                Cell(inner.Y, layout.Origin.Y, layout.BlockSize),
                Cell(inner.Z, layout.Origin.Z, layout.BlockSize));
            return new BlockPick(hit, face);
        }

        /// <summary>Unit vector along the normal's largest component; ties prefer Y, then X.</summary>
        public static Int3 DominantAxis(Float3 n)
        {
            float ax = Math.Abs(n.X), ay = Math.Abs(n.Y), az = Math.Abs(n.Z);
            if (!(ax + ay + az > 0)) throw new ArgumentException("Normal is zero or NaN", nameof(n));
            if (ay >= ax && ay >= az) return new Int3(0, Math.Sign(n.Y), 0);
            if (ax >= az) return new Int3(Math.Sign(n.X), 0, 0);
            return new Int3(0, 0, Math.Sign(n.Z));
        }

        private static int Cell(float p, float origin, float blockSize) =>
            (int)Math.Floor((p - origin) / blockSize);
    }
}
