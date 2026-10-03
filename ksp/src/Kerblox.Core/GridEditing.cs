using System;

namespace Kerblox.Core
{
    public enum GridEditStatus
    {
        /// <summary>The cell already held that state, or air was "placed" outside the grid.</summary>
        NoChange,
        /// <summary>A cell inside the existing bounds changed; the grid object is the same.</summary>
        Edited,
        /// <summary>The grid was resized (a new grid object) and the edit applied to it.</summary>
        Resized,
        /// <summary>Growing to reach the cell would exceed <see cref="VoxelGrid.MaxDimension"/>; nothing changed.</summary>
        TooLarge,
    }

    /// <summary>
    /// Outcome of a grid edit. When <see cref="Status"/> is <see cref="GridEditStatus.Resized"/>,
    /// <see cref="Grid"/> is a new object and every old cell (x,y,z) is now at
    /// (x+ShiftX, y+ShiftY, z+ShiftZ). Otherwise <see cref="Grid"/> is the grid passed in
    /// and the shift is zero.
    /// </summary>
    public readonly struct GridEdit
    {
        public readonly GridEditStatus Status;
        public readonly VoxelGrid Grid;
        public readonly int ShiftX, ShiftY, ShiftZ;
        /// <summary>Grid size before the edit, needed to work out the part-space displacement.</summary>
        public readonly int OldSizeX, OldSizeY, OldSizeZ;

        public GridEdit(GridEditStatus status, VoxelGrid grid, int oldSizeX, int oldSizeY, int oldSizeZ,
            int shiftX = 0, int shiftY = 0, int shiftZ = 0)
        {
            Status = status; Grid = grid;
            OldSizeX = oldSizeX; OldSizeY = oldSizeY; OldSizeZ = oldSizeZ;
            ShiftX = shiftX; ShiftY = shiftY; ShiftZ = shiftZ;
        }

        public bool Changed => Status == GridEditStatus.Edited || Status == GridEditStatus.Resized;

        public bool Resized => Status == GridEditStatus.Resized;

        /// <summary>
        /// How far the pre-existing blocks moved in part-local metres. <see cref="BlockLayout"/>
        /// centres the grid's box on the part origin, so growing on one side moves the
        /// centre and with it every old block, by half a block per added layer. Add this to
        /// an old part-space position to get where that point is now; to keep the blocks
        /// fixed in world space, move the part by the negated vector (in part space).
        /// </summary>
        public Float3 Displacement(float blockSize) => new Float3(
            Axis(ShiftX, OldSizeX, Grid.SizeX),
            Axis(ShiftY, OldSizeY, Grid.SizeY),
            Axis(ShiftZ, OldSizeZ, Grid.SizeZ)) * blockSize;

        // Old min corner of cell x: -old/2 + x. New: -new/2 + x + shift. Difference in blocks:
        private static float Axis(int shift, int oldSize, int newSize) => shift - (newSize - oldSize) / 2f;

        public override string ToString() =>
            Resized ? $"{Status} {OldSizeX}x{OldSizeY}x{OldSizeZ} -> {Grid.SizeX}x{Grid.SizeY}x{Grid.SizeZ}, shift ({ShiftX},{ShiftY},{ShiftZ})"
                    : Status.ToString();
    }

    /// <summary>
    /// Edits that may change the grid's bounds. Coordinates are in the grid's current
    /// cell space and may lie outside it: placing a block there grows the grid to
    /// include the cell, and the returned <see cref="GridEdit"/> reports the shift.
    /// </summary>
    public static class GridEditor
    {
        public static GridEdit SetBlock(VoxelGrid grid, int x, int y, int z, BlockState state)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            int sx = grid.SizeX, sy = grid.SizeY, sz = grid.SizeZ;

            if (grid.InBounds(x, y, z))
                return new GridEdit(grid.Set(x, y, z, state) ? GridEditStatus.Edited : GridEditStatus.NoChange, grid, sx, sy, sz);

            // Outside is already air.
            if (state.IsAir)
                return new GridEdit(GridEditStatus.NoChange, grid, sx, sy, sz);

            int minX = Math.Min(0, x), minY = Math.Min(0, y), minZ = Math.Min(0, z);
            // long: a wild coordinate mustn't overflow into a "valid" size.
            long nx = (long)Math.Max(sx - 1, x) - minX + 1;
            long ny = (long)Math.Max(sy - 1, y) - minY + 1;
            long nz = (long)Math.Max(sz - 1, z) - minZ + 1;
            if (nx > VoxelGrid.MaxDimension || ny > VoxelGrid.MaxDimension || nz > VoxelGrid.MaxDimension)
                return new GridEdit(GridEditStatus.TooLarge, grid, sx, sy, sz);

            VoxelGrid grown = grid.Resized((int)nx, (int)ny, (int)nz, -minX, -minY, -minZ);
            grown.Set(x - minX, y - minY, z - minZ, state);
            return new GridEdit(GridEditStatus.Resized, grown, sx, sy, sz, -minX, -minY, -minZ);
        }

        public static GridEdit RemoveBlock(VoxelGrid grid, int x, int y, int z) =>
            SetBlock(grid, x, y, z, BlockState.Air);

        /// <summary>
        /// Shrinks the grid to the bounds of its non-air cells. An all-air grid shrinks
        /// to 1x1x1. Not done automatically on removal: every trim moves the blocks in
        /// part space, which the caller has to compensate for.
        /// </summary>
        public static GridEdit Trim(VoxelGrid grid)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            int sx = grid.SizeX, sy = grid.SizeY, sz = grid.SizeZ;
            if (!grid.TryGetOccupiedBounds(out GridBox b))
                b = new GridBox(0, 0, 0, 1, 1, 1);
            if (b.X == 0 && b.Y == 0 && b.Z == 0 && b.SizeX == sx && b.SizeY == sy && b.SizeZ == sz)
                return new GridEdit(GridEditStatus.NoChange, grid, sx, sy, sz);

            VoxelGrid trimmed = grid.Resized(b.SizeX, b.SizeY, b.SizeZ, -b.X, -b.Y, -b.Z);
            return new GridEdit(GridEditStatus.Resized, trimmed, sx, sy, sz, -b.X, -b.Y, -b.Z);
        }
    }
}
