using System;

namespace Kerblox.Core
{
    /// <summary>Minimal vector type so Core stays free of UnityEngine.</summary>
    public readonly struct Float3 : IEquatable<Float3>
    {
        public readonly float X, Y, Z;

        public Float3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public static readonly Float3 Zero = new Float3(0, 0, 0);

        public static Float3 operator +(Float3 a, Float3 b) => new Float3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Float3 operator -(Float3 a, Float3 b) => new Float3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Float3 operator *(Float3 a, float s) => new Float3(a.X * s, a.Y * s, a.Z * s);

        public static Float3 Cross(Float3 a, Float3 b) =>
            new Float3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public static float Dot(Float3 a, Float3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public bool Equals(Float3 o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object obj) => obj is Float3 o && Equals(o);
        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() << 2) ^ (Z.GetHashCode() >> 2);
        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
    }

    public readonly struct Float2
    {
        public readonly float U, V;
        public Float2(float u, float v) { U = u; V = v; }
    }

    /// <summary>
    /// Maps grid cells to part-local metres. The grid's bounding box is centred on
    /// the part origin, so CoM offsets and attach nodes are relative to the box centre.
    /// </summary>
    public readonly struct BlockLayout
    {
        public readonly float BlockSize;
        public readonly Float3 Origin;   // part-space position of cell (0,0,0)'s min corner

        public BlockLayout(VoxelGrid grid, float blockSize)
        {
            if (!(blockSize > 0)) throw new ArgumentOutOfRangeException(nameof(blockSize));
            BlockSize = blockSize;
            Origin = new Float3(-grid.SizeX * blockSize / 2f, -grid.SizeY * blockSize / 2f, -grid.SizeZ * blockSize / 2f);
        }

        public Float3 CellMin(int x, int y, int z) =>
            new Float3(Origin.X + x * BlockSize, Origin.Y + y * BlockSize, Origin.Z + z * BlockSize);

        public Float3 CellCenter(int x, int y, int z) =>
            CellMin(x, y, z) + new Float3(BlockSize / 2f, BlockSize / 2f, BlockSize / 2f);

        public float BlockVolume => BlockSize * BlockSize * BlockSize;
    }

    /// <summary>Axis-aligned box in grid cells: Min inclusive, Size in cells.</summary>
    public readonly struct GridBox
    {
        public readonly int X, Y, Z, SizeX, SizeY, SizeZ;

        public GridBox(int x, int y, int z, int sizeX, int sizeY, int sizeZ)
        {
            X = x; Y = y; Z = z; SizeX = sizeX; SizeY = sizeY; SizeZ = sizeZ;
        }

        public int Volume => SizeX * SizeY * SizeZ;

        public bool Contains(int x, int y, int z) =>
            x >= X && x < X + SizeX && y >= Y && y < Y + SizeY && z >= Z && z < Z + SizeZ;

        /// <summary>Centre in part-local metres.</summary>
        public Float3 Center(BlockLayout layout) =>
            layout.CellMin(X, Y, Z) + new Float3(SizeX, SizeY, SizeZ) * (layout.BlockSize / 2f);

        /// <summary>Full extents in part-local metres.</summary>
        public Float3 Extents(BlockLayout layout) => new Float3(SizeX, SizeY, SizeZ) * layout.BlockSize;

        public override string ToString() => $"[{X},{Y},{Z} +{SizeX}x{SizeY}x{SizeZ}]";
    }
}
