using System.Collections.Generic;

namespace Kerblox.Core
{
    /// <summary>Engine-agnostic triangle mesh. The plugin copies this into a UnityEngine.Mesh.</summary>
    public sealed class MeshData
    {
        public readonly List<Float3> Vertices = new List<Float3>();
        public readonly List<Float3> Normals = new List<Float3>();
        public readonly List<Float2> Uvs = new List<Float2>();
        public readonly List<int> Triangles = new List<int>();

        public int FaceCount => Triangles.Count / 6;
    }

    /// <summary>
    /// Builds one combined mesh for a grid, emitting a block face only when the
    /// neighbouring cell doesn't hide it (Minecraft-style face culling).
    ///
    /// Winding: for each triangle (a, b, c), Cross(b - a, c - a) points along the
    /// outward face normal, which is what Unity treats as front-facing
    /// (clockwise when viewed from outside).
    /// </summary>
    public static class GridMesher
    {
        /// <summary>Fraction of a tile trimmed from each UV edge to avoid atlas bleeding.</summary>
        public const float UvInset = 0.5f / 16f;

        private struct Face
        {
            public int Dx, Dy, Dz;
            public Float3 Normal;
            public Float3[] Corners; // unit-cube corners, ordered bottom-left, top-left, top-right, bottom-right seen from outside
        }

        private static readonly Face[] Faces =
        {
            // +X
            new Face { Dx = 1, Normal = new Float3(1, 0, 0), Corners = new[] { P(1,0,0), P(1,1,0), P(1,1,1), P(1,0,1) } },
            // -X
            new Face { Dx = -1, Normal = new Float3(-1, 0, 0), Corners = new[] { P(0,0,1), P(0,1,1), P(0,1,0), P(0,0,0) } },
            // +Y
            new Face { Dy = 1, Normal = new Float3(0, 1, 0), Corners = new[] { P(0,1,0), P(0,1,1), P(1,1,1), P(1,1,0) } },
            // -Y
            new Face { Dy = -1, Normal = new Float3(0, -1, 0), Corners = new[] { P(0,0,1), P(0,0,0), P(1,0,0), P(1,0,1) } },
            // +Z
            new Face { Dz = 1, Normal = new Float3(0, 0, 1), Corners = new[] { P(1,0,1), P(1,1,1), P(0,1,1), P(0,0,1) } },
            // -Z
            new Face { Dz = -1, Normal = new Float3(0, 0, -1), Corners = new[] { P(0,0,0), P(0,1,0), P(1,1,0), P(1,0,0) } },
        };

        private static Float3 P(float x, float y, float z) => new Float3(x, y, z);

        public static MeshData Build(VoxelGrid grid, BlockRegistry registry, BlockLayout layout)
        {
            var mesh = new MeshData();
            int tiles = registry.TileCount;

            for (int y = 0; y < grid.SizeY; y++)
                for (int z = 0; z < grid.SizeZ; z++)
                    for (int x = 0; x < grid.SizeX; x++)
                    {
                        BlockState state = grid.Get(x, y, z);
                        BlockType type = registry.Get(state);
                        if (type == null) continue;

                        Float3 min = layout.CellMin(x, y, z);
                        float u0 = (float)type.TileIndex / tiles + UvInset / tiles;
                        float u1 = (float)(type.TileIndex + 1) / tiles - UvInset / tiles;
                        const float v0 = UvInset, v1 = 1f - UvInset;

                        foreach (Face f in Faces)
                        {
                            BlockState neighbour = grid.Get(x + f.Dx, y + f.Dy, z + f.Dz);
                            if (!IsFaceVisible(state, neighbour, registry)) continue;

                            int start = mesh.Vertices.Count;
                            foreach (Float3 c in f.Corners)
                            {
                                mesh.Vertices.Add(min + c * layout.BlockSize);
                                mesh.Normals.Add(f.Normal);
                            }
                            mesh.Uvs.Add(new Float2(u0, v0));
                            mesh.Uvs.Add(new Float2(u0, v1));
                            mesh.Uvs.Add(new Float2(u1, v1));
                            mesh.Uvs.Add(new Float2(u1, v0));

                            mesh.Triangles.Add(start); mesh.Triangles.Add(start + 1); mesh.Triangles.Add(start + 2);
                            mesh.Triangles.Add(start); mesh.Triangles.Add(start + 2); mesh.Triangles.Add(start + 3);
                        }
                    }

            return mesh;
        }

        /// <summary>
        /// A face shows unless the neighbour is opaque, or is the same see-through
        /// block (so a glass pane wall doesn't render its internal faces).
        /// </summary>
        public static bool IsFaceVisible(BlockState self, BlockState neighbour, BlockRegistry registry)
        {
            BlockType n = registry.Get(neighbour);
            if (n == null) return true;
            if (n.Opaque) return false;
            return n != registry.Get(self);
        }
    }
}
