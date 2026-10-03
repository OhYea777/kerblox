namespace Kerblox.Core
{
    public readonly struct MassProperties
    {
        /// <summary>Tonnes.</summary>
        public readonly double Mass;

        /// <summary>Part-local metres, relative to the grid's bounding-box centre.</summary>
        public readonly Float3 CenterOfMass;

        public readonly int SolidBlocks;

        /// <summary>Occupied bounds in part-local metres (both zero when the grid is empty).</summary>
        public readonly Float3 BoundsMin, BoundsMax;

        public MassProperties(double mass, Float3 com, int solidBlocks, Float3 boundsMin, Float3 boundsMax)
        {
            Mass = mass; CenterOfMass = com; SolidBlocks = solidBlocks; BoundsMin = boundsMin; BoundsMax = boundsMax;
        }

        public static MassProperties Compute(VoxelGrid grid, BlockRegistry registry, BlockLayout layout)
        {
            double mass = 0, mx = 0, my = 0, mz = 0;
            int solid = 0;
            int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
            int maxX = -1, maxY = -1, maxZ = -1;
            double blockVolume = (double)layout.BlockSize * layout.BlockSize * layout.BlockSize;

            for (int y = 0; y < grid.SizeY; y++)
                for (int z = 0; z < grid.SizeZ; z++)
                    for (int x = 0; x < grid.SizeX; x++)
                    {
                        BlockType t = registry.Get(grid.Get(x, y, z));
                        if (t == null || !t.Solid) continue;

                        solid++;
                        if (x < minX) minX = x; if (y < minY) minY = y; if (z < minZ) minZ = z;
                        if (x > maxX) maxX = x; if (y > maxY) maxY = y; if (z > maxZ) maxZ = z;

                        double m = t.Density * blockVolume;
                        Float3 c = layout.CellCenter(x, y, z);
                        mass += m;
                        mx += m * c.X; my += m * c.Y; mz += m * c.Z;
                    }

            if (solid == 0)
                return new MassProperties(0, Float3.Zero, 0, Float3.Zero, Float3.Zero);

            Float3 com = mass > 0 ? new Float3((float)(mx / mass), (float)(my / mass), (float)(mz / mass)) : Float3.Zero;
            Float3 bMin = layout.CellMin(minX, minY, minZ);
            Float3 bMax = layout.CellMin(maxX + 1, maxY + 1, maxZ + 1);
            return new MassProperties(mass, com, solid, bMin, bMax);
        }
    }
}
