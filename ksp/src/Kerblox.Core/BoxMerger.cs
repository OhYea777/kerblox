using System.Collections.Generic;

namespace Kerblox.Core
{
    /// <summary>
    /// Greedy merge of solid voxels into axis-aligned boxes for the compound collider.
    /// Block type is ignored: collision only cares whether a cell is solid.
    /// Boxes never overlap and together cover exactly the solid cells.
    /// </summary>
    public static class BoxMerger
    {
        public static List<GridBox> Merge(VoxelGrid grid, BlockRegistry registry)
        {
            int sx = grid.SizeX, sy = grid.SizeY, sz = grid.SizeZ;
            var open = new bool[grid.Volume];
            for (int y = 0; y < sy; y++)
                for (int z = 0; z < sz; z++)
                    for (int x = 0; x < sx; x++)
                        open[grid.IndexOf(x, y, z)] = registry.IsSolid(grid.Get(x, y, z));

            var boxes = new List<GridBox>();
            for (int y = 0; y < sy; y++)
                for (int z = 0; z < sz; z++)
                    for (int x = 0; x < sx; x++)
                    {
                        if (!open[grid.IndexOf(x, y, z)]) continue;

                        // Grow along X, then Z (whole rows), then Y (whole slabs).
                        int w = 1;
                        while (x + w < sx && open[grid.IndexOf(x + w, y, z)]) w++;

                        int d = 1;
                        while (z + d < sz && RowOpen(grid, open, x, w, y, z + d)) d++;

                        int h = 1;
                        while (y + h < sy && SlabOpen(grid, open, x, w, y + h, z, d)) h++;

                        for (int yy = y; yy < y + h; yy++)
                            for (int zz = z; zz < z + d; zz++)
                                for (int xx = x; xx < x + w; xx++)
                                    open[grid.IndexOf(xx, yy, zz)] = false;

                        boxes.Add(new GridBox(x, y, z, w, h, d));
                    }

            return boxes;
        }

        private static bool RowOpen(VoxelGrid g, bool[] open, int x, int w, int y, int z)
        {
            for (int xx = x; xx < x + w; xx++)
                if (!open[g.IndexOf(xx, y, z)]) return false;
            return true;
        }

        private static bool SlabOpen(VoxelGrid g, bool[] open, int x, int w, int y, int z, int d)
        {
            for (int zz = z; zz < z + d; zz++)
                if (!RowOpen(g, open, x, w, y, zz)) return false;
            return true;
        }
    }
}
