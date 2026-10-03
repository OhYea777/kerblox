using System;

namespace Kerblox.Core
{
    public readonly struct GridChange
    {
        public readonly int X, Y, Z;
        public readonly BlockState Old, New;
        public readonly ulong Revision;

        public GridChange(int x, int y, int z, BlockState oldState, BlockState newState, ulong revision)
        {
            X = x; Y = y; Z = z; Old = oldState; New = newState; Revision = revision;
        }
    }

    /// <summary>
    /// Fixed-size dense voxel grid. Plain data: no Unity, no KSP.
    /// Axes follow Unity/KSP part space: +Y is the stack axis ("up" on a rocket).
    /// Storage is Y-major (index = (y * SizeZ + z) * SizeX + x) so a horizontal
    /// slice is contiguous, which is also how the codec and future bridge lay it out.
    /// </summary>
    public sealed class VoxelGrid
    {
        public const int MaxDimension = 256;

        private readonly uint[] cells;

        public int SizeX { get; }
        public int SizeY { get; }
        public int SizeZ { get; }
        public int Volume => cells.Length;

        /// <summary>Bumped on every effective edit. The bridge will use this to detect divergence.</summary>
        public ulong Revision { get; private set; }

        /// <summary>Raised after each effective single-block edit (not for bulk loads).</summary>
        public event Action<GridChange> Changed;

        public VoxelGrid(int sizeX, int sizeY, int sizeZ)
        {
            CheckDimension(sizeX, nameof(sizeX));
            CheckDimension(sizeY, nameof(sizeY));
            CheckDimension(sizeZ, nameof(sizeZ));
            SizeX = sizeX; SizeY = sizeY; SizeZ = sizeZ;
            cells = new uint[sizeX * sizeY * sizeZ];
        }

        private static void CheckDimension(int value, string name)
        {
            if (value < 1 || value > MaxDimension)
                throw new ArgumentOutOfRangeException(name, value, $"Grid dimensions must be 1..{MaxDimension}");
        }

        public bool InBounds(int x, int y, int z) =>
            (uint)x < (uint)SizeX && (uint)y < (uint)SizeY && (uint)z < (uint)SizeZ;

        public int IndexOf(int x, int y, int z) => (y * SizeZ + z) * SizeX + x;

        /// <summary>Out-of-bounds reads return air, which keeps face culling at the edges trivial.</summary>
        public BlockState Get(int x, int y, int z) =>
            InBounds(x, y, z) ? new BlockState(cells[IndexOf(x, y, z)]) : BlockState.Air;

        /// <returns>True if the cell changed.</returns>
        public bool Set(int x, int y, int z, BlockState state)
        {
            if (!InBounds(x, y, z))
                throw new ArgumentOutOfRangeException($"({x},{y},{z}) outside {SizeX}x{SizeY}x{SizeZ}");
            int i = IndexOf(x, y, z);
            var old = new BlockState(cells[i]);
            if (old == state) return false;
            cells[i] = state.Raw;
            Revision++;
            Changed?.Invoke(new GridChange(x, y, z, old, state, Revision));
            return true;
        }

        /// <summary>Fills an inclusive box. One revision bump, no per-cell events.</summary>
        public void Fill(int x0, int y0, int z0, int x1, int y1, int z1, BlockState state)
        {
            for (int y = Math.Max(0, y0); y <= Math.Min(SizeY - 1, y1); y++)
                for (int z = Math.Max(0, z0); z <= Math.Min(SizeZ - 1, z1); z++)
                    for (int x = Math.Max(0, x0); x <= Math.Min(SizeX - 1, x1); x++)
                        cells[IndexOf(x, y, z)] = state.Raw;
            Revision++;
        }

        public int CountNonAir()
        {
            int n = 0;
            foreach (uint c in cells)
                if ((c & 0xFFFF) != 0) n++;
            return n;
        }

        public VoxelGrid Clone()
        {
            var copy = new VoxelGrid(SizeX, SizeY, SizeZ);
            Array.Copy(cells, copy.cells, cells.Length);
            copy.Revision = Revision;
            return copy;
        }

        /// <summary>Raw cell access for the codec. Same layout as <see cref="IndexOf"/>.</summary>
        internal uint[] RawCells => cells;

        internal void MarkBulkLoaded() => Revision++;

        public bool ContentEquals(VoxelGrid other)
        {
            if (other == null || other.SizeX != SizeX || other.SizeY != SizeY || other.SizeZ != SizeZ) return false;
            for (int i = 0; i < cells.Length; i++)
                if (cells[i] != other.cells[i]) return false;
            return true;
        }
    }
}
