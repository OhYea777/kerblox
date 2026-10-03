namespace Kerblox.Core
{
    /// <summary>Hardcoded grids for phase 1, before in-editor building exists.</summary>
    public static class DefaultGrids
    {
        /// <summary>
        /// 4x8x4 capsule: iron base (pulls the CoM down so it's visible in the VAB),
        /// hollow stone walls with a glass window band, plank cap, wool nose.
        /// </summary>
        public static VoxelGrid Capsule()
        {
            var g = new VoxelGrid(4, 8, 4);
            var iron = BlockState.Parse("minecraft:iron_block");
            var stone = BlockState.Parse("minecraft:stone");
            var glass = BlockState.Parse("minecraft:glass");
            var planks = BlockState.Parse("minecraft:oak_planks");
            var wool = BlockState.Parse("minecraft:white_wool");

            g.Fill(0, 0, 0, 3, 0, 3, iron);
            for (int y = 1; y <= 5; y++)
            {
                g.Fill(0, y, 0, 3, y, 3, y == 3 ? glass : stone);
                g.Fill(1, y, 1, 2, y, 2, BlockState.Air);
            }
            g.Fill(0, 6, 0, 3, 6, 3, planks);
            g.Fill(1, 7, 1, 2, 7, 2, wool);
            return g;
        }
    }
}
