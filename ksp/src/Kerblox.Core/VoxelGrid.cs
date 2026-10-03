using System;
using System.Collections.Generic;

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
    ///
    /// Cells hold indices into a per-grid palette of <see cref="BlockState"/>s, like
    /// a Minecraft chunk section. Index 0 is always air. Entries are appended as new
    /// states are written and are only dropped by <see cref="Compact"/>, so indices
    /// are an internal detail: they aren't stable across compaction or encoding.
    /// </summary>
    public sealed class VoxelGrid
    {
        public const int MaxDimension = 256;

        /// <summary>Cells are 16-bit, so this many distinct states can be live at once.</summary>
        public const int MaxPaletteSize = ushort.MaxValue + 1;

        private readonly ushort[] cells;
        private readonly List<BlockState> palette = new List<BlockState> { BlockState.Air };
        private readonly List<int> counts = new List<int>();                 // cells using each palette entry
        private readonly Dictionary<BlockState, ushort> lookup = new Dictionary<BlockState, ushort>();

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
            cells = new ushort[sizeX * sizeY * sizeZ];
            counts.Add(cells.Length);
            lookup.Add(BlockState.Air, 0);
        }

        private static void CheckDimension(int value, string name)
        {
            if (value < 1 || value > MaxDimension)
                throw new ArgumentOutOfRangeException(name, value, $"Grid dimensions must be 1..{MaxDimension}");
        }

        public bool InBounds(int x, int y, int z) =>
            (uint)x < (uint)SizeX && (uint)y < (uint)SizeY && (uint)z < (uint)SizeZ;

        public int IndexOf(int x, int y, int z) => (y * SizeZ + z) * SizeX + x;

        /// <summary>
        /// Palette entries, index 0 = air. May contain entries no cell uses any more
        /// until <see cref="Compact"/> runs.
        /// </summary>
        public IReadOnlyList<BlockState> Palette => palette;

        /// <summary>Out-of-bounds reads return air, which keeps face culling at the edges trivial.</summary>
        public BlockState Get(int x, int y, int z) =>
            InBounds(x, y, z) ? palette[cells[IndexOf(x, y, z)]] : BlockState.Air;

        /// <returns>True if the cell changed.</returns>
        public bool Set(int x, int y, int z, BlockState state)
        {
            if (!InBounds(x, y, z))
                throw new ArgumentOutOfRangeException($"({x},{y},{z}) outside {SizeX}x{SizeY}x{SizeZ}");
            int i = IndexOf(x, y, z);
            BlockState old = palette[cells[i]];
            if (old == state) return false;
            ushort idx = IndexFor(state);
            counts[cells[i]]--;
            counts[idx]++;
            cells[i] = idx;
            Revision++;
            Changed?.Invoke(new GridChange(x, y, z, old, state, Revision));
            return true;
        }

        /// <summary>Fills an inclusive box. One revision bump, no per-cell events.</summary>
        public void Fill(int x0, int y0, int z0, int x1, int y1, int z1, BlockState state)
        {
            ushort idx = IndexFor(state);
            for (int y = Math.Max(0, y0); y <= Math.Min(SizeY - 1, y1); y++)
                for (int z = Math.Max(0, z0); z <= Math.Min(SizeZ - 1, z1); z++)
                    for (int x = Math.Max(0, x0); x <= Math.Min(SizeX - 1, x1); x++)
                    {
                        int i = IndexOf(x, y, z);
                        counts[cells[i]]--;
                        counts[idx]++;
                        cells[i] = idx;
                    }
            Revision++;
        }

        public int CountNonAir() => cells.Length - counts[0];

        /// <summary>Number of cells holding <paramref name="state"/>.</summary>
        public int CountOf(BlockState state) => lookup.TryGetValue(state, out ushort idx) ? counts[idx] : 0;

        /// <summary>
        /// Copies this grid into a new grid of the given size, with each old cell
        /// (x,y,z) landing at (x+offsetX, y+offsetY, z+offsetZ). Cells that land
        /// outside are dropped; new cells are air. Offsets are in cells and may be
        /// negative (crop) or positive (grow on the low side). The palette carries
        /// over (use counts recomputed), and the copy's <see cref="Revision"/> is one
        /// past this grid's so a resize always reads as an edit. <see cref="Changed"/>
        /// subscribers stay on this grid.
        /// </summary>
        public VoxelGrid Resized(int sizeX, int sizeY, int sizeZ, int offsetX, int offsetY, int offsetZ)
        {
            var copy = new VoxelGrid(sizeX, sizeY, sizeZ);
            // Only the overlap of the old box, shifted, and the new box needs copying.
            int x0 = Math.Max(0, -offsetX), x1 = Math.Min(SizeX, sizeX - offsetX);
            int y0 = Math.Max(0, -offsetY), y1 = Math.Min(SizeY, sizeY - offsetY);
            int z0 = Math.Max(0, -offsetZ), z1 = Math.Min(SizeZ, sizeZ - offsetZ);
            for (int y = y0; y < y1; y++)
                for (int z = z0; z < z1; z++)
                {
                    int src = IndexOf(x0, y, z), dst = copy.IndexOf(x0 + offsetX, y + offsetY, z + offsetZ);
                    if (x1 > x0) Array.Copy(cells, src, copy.cells, dst, x1 - x0);
                }

            var newCounts = new int[palette.Count];
            foreach (ushort c in copy.cells) newCounts[c]++;
            copy.ReplacePalette(palette, newCounts);
            copy.Revision = Revision + 1;
            return copy;
        }

        /// <summary>
        /// Inclusive bounds of the non-air cells, or false if the grid is all air.
        /// </summary>
        public bool TryGetOccupiedBounds(out GridBox bounds)
        {
            int minX = SizeX, minY = SizeY, minZ = SizeZ, maxX = -1, maxY = -1, maxZ = -1;
            for (int y = 0; y < SizeY; y++)
                for (int z = 0; z < SizeZ; z++)
                    for (int x = 0; x < SizeX; x++)
                    {
                        if (cells[IndexOf(x, y, z)] == 0) continue;
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                        if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                    }
            if (maxX < 0) { bounds = default; return false; }
            bounds = new GridBox(minX, minY, minZ, maxX - minX + 1, maxY - minY + 1, maxZ - minZ + 1);
            return true;
        }

        /// <summary>
        /// Drops palette entries no cell uses and renumbers the rest in order of first
        /// appearance in storage order (air stays 0). Content and <see cref="Revision"/>
        /// are unchanged: this is bookkeeping, not an edit.
        /// </summary>
        public void Compact()
        {
            ushort[] remap = BuildCompactRemap(out List<BlockState> newPalette);
            for (int i = 0; i < cells.Length; i++)
                cells[i] = remap[cells[i]];

            var newCounts = new int[newPalette.Count];
            for (int old = 0; old < palette.Count; old++)
                newCounts[remap[old]] += counts[old]; // unused entries map to 0 with a count of 0
            ReplacePalette(newPalette, newCounts);
        }

        /// <summary>
        /// Maps current palette indices to a compacted, first-appearance ordering
        /// without mutating anything. Unused entries map to 0. The codec writes this
        /// ordering, so equal grids always encode to identical bytes.
        /// </summary>
        internal ushort[] BuildCompactRemap(out List<BlockState> newPalette)
        {
            var remap = new ushort[palette.Count];
            var seen = new bool[palette.Count];
            newPalette = new List<BlockState> { BlockState.Air };
            seen[0] = true;
            int live = 1;
            for (int i = 1; i < counts.Count; i++) if (counts[i] > 0) live++;

            for (int i = 0; i < cells.Length && newPalette.Count < live; i++)
            {
                ushort c = cells[i];
                if (seen[c]) continue;
                seen[c] = true;
                remap[c] = (ushort)newPalette.Count;
                newPalette.Add(palette[c]);
            }
            return remap;
        }

        private ushort IndexFor(BlockState state)
        {
            if (lookup.TryGetValue(state, out ushort idx)) return idx;
            if (palette.Count == MaxPaletteSize)
            {
                Compact();
                if (palette.Count == MaxPaletteSize)
                    throw new InvalidOperationException($"Grid palette is full ({MaxPaletteSize} distinct block states)");
            }
            idx = (ushort)palette.Count;
            palette.Add(state);
            counts.Add(0);
            lookup.Add(state, idx);
            return idx;
        }

        public VoxelGrid Clone()
        {
            var copy = new VoxelGrid(SizeX, SizeY, SizeZ);
            Array.Copy(cells, copy.cells, cells.Length);
            copy.ReplacePalette(palette, counts);
            copy.Revision = Revision;
            return copy;
        }

        private void ReplacePalette(IList<BlockState> states, IList<int> stateCounts)
        {
            palette.Clear(); palette.AddRange(states);
            counts.Clear(); counts.AddRange(stateCounts);
            lookup.Clear();
            for (int i = 0; i < palette.Count; i++) lookup.Add(palette[i], (ushort)i);
        }

        /// <summary>Raw cell access for the codec. Same layout as <see cref="IndexOf"/>.</summary>
        internal ushort[] RawCells => cells;

        /// <summary>
        /// Codec bulk load: installs a palette (index 0 must be air, no duplicates) for
        /// cells the caller has already written into <see cref="RawCells"/>.
        /// </summary>
        internal void LoadPalette(IList<BlockState> states)
        {
            var stateCounts = new int[states.Count];
            foreach (ushort c in cells) stateCounts[c]++;
            ReplacePalette(states, stateCounts);
            Revision++;
        }

        public bool ContentEquals(VoxelGrid other)
        {
            if (other == null || other.SizeX != SizeX || other.SizeY != SizeY || other.SizeZ != SizeZ) return false;
            // Palettes may order the same states differently, so compare through a mapping.
            var map = new int[palette.Count];
            for (int i = 0; i < map.Length; i++)
                map[i] = other.lookup.TryGetValue(palette[i], out ushort o) ? o : -1;
            for (int i = 0; i < cells.Length; i++)
                if (map[cells[i]] != other.cells[i]) return false;
            return true;
        }
    }
}
