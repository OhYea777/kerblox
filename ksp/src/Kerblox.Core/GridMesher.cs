using System;
using System.Collections.Generic;

namespace Kerblox.Core
{
    /// <summary>
    /// What a submesh is drawn with. The plugin maps each key to one material:
    /// the atlas texture, the layer's shader and the tint colour.
    /// </summary>
    public readonly struct MaterialKey : IEquatable<MaterialKey>, IComparable<MaterialKey>
    {
        public readonly RenderLayer Layer;
        public readonly int Atlas;

        /// <summary>
        /// Tint is per submesh rather than per vertex because KSP's stock shaders
        /// ignore vertex colour (docs/RENDERING.md); the plugin applies it as a material colour.
        /// </summary>
        public readonly Rgba32 Tint;

        public MaterialKey(RenderLayer layer, int atlas, Rgba32 tint) { Layer = layer; Atlas = atlas; Tint = tint; }

        public bool Equals(MaterialKey o) => Layer == o.Layer && Atlas == o.Atlas && Tint == o.Tint;
        public override bool Equals(object obj) => obj is MaterialKey o && Equals(o);
        public override int GetHashCode() => ((int)Layer * 397 ^ Atlas) * 397 ^ Tint.GetHashCode();

        /// <summary>Solid before cutout before translucent, so translucent geometry draws last.</summary>
        public int CompareTo(MaterialKey o)
        {
            if (Layer != o.Layer) return Layer.CompareTo(o.Layer);
            if (Atlas != o.Atlas) return Atlas.CompareTo(o.Atlas);
            return ((uint)Tint.GetHashCode()).CompareTo((uint)o.Tint.GetHashCode());
        }

        public override string ToString() => $"{Layer}/atlas{Atlas}/{Tint}";
    }

    public sealed class SubMeshData
    {
        public MaterialKey Key { get; }

        /// <summary>Indices into the shared vertex lists of the owning <see cref="MeshData"/>.</summary>
        public readonly List<int> Triangles = new List<int>();

        public SubMeshData(MaterialKey key) { Key = key; }

        public int QuadCount => Triangles.Count / 6;
    }

    /// <summary>
    /// Engine-agnostic triangle mesh with one submesh per material. The plugin copies
    /// this into a UnityEngine.Mesh, one material slot per submesh.
    /// </summary>
    public sealed class MeshData
    {
        public readonly List<Float3> Vertices = new List<Float3>();
        public readonly List<Float3> Normals = new List<Float3>();
        public readonly List<Float2> Uvs = new List<Float2>();

        /// <summary>Ordered by <see cref="MaterialKey"/>: layer, then atlas, then tint.</summary>
        public readonly List<SubMeshData> SubMeshes = new List<SubMeshData>();

        /// <summary>Total quads across all submeshes.</summary>
        public int FaceCount
        {
            get
            {
                int n = 0;
                foreach (SubMeshData s in SubMeshes) n += s.QuadCount;
                return n;
            }
        }

        /// <summary>Every submesh's triangles, concatenated, for code that doesn't care about materials.</summary>
        public List<int> AllTriangles()
        {
            var all = new List<int>();
            foreach (SubMeshData s in SubMeshes) all.AddRange(s.Triangles);
            return all;
        }
    }

    /// <summary>
    /// Builds one combined mesh for a grid from per-state <see cref="BlockModel"/>s,
    /// culling the way Minecraft's chunk renderer does: a quad with a cull face is
    /// dropped when the neighbour in that direction occludes the touching face (or is
    /// the same see-through block), and quads without one always draw.
    ///
    /// Models are resolved once per palette entry, not per cell.
    ///
    /// Winding: for each triangle (a, b, c), Cross(b - a, c - a) points along the
    /// outward face normal, which is what Unity treats as front-facing
    /// (clockwise when viewed from outside).
    /// </summary>
    public static class GridMesher
    {
        /// <summary>Convenience overload: built-in cubes for the registry, map-colour cubes for the rest.</summary>
        public static MeshData Build(VoxelGrid grid, BlockRegistry registry, BlockLayout layout) =>
            Build(grid, new BuiltinModelSource(registry).CreateResolver(), layout);

        /// <param name="models">Null results are treated as empty (nothing drawn, nothing hidden).</param>
        public static MeshData Build(VoxelGrid grid, IBlockModelSource models, BlockLayout layout)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            if (models == null) throw new ArgumentNullException(nameof(models));

            PaletteModels resolved = Resolve(grid, models);
            var mesh = new MeshData();
            var subMeshes = new Dictionary<MaterialKey, SubMeshData>();
            ushort[] cells = grid.RawCells;

            for (int y = 0; y < grid.SizeY; y++)
                for (int z = 0; z < grid.SizeZ; z++)
                    for (int x = 0; x < grid.SizeX; x++)
                    {
                        ushort self = cells[grid.IndexOf(x, y, z)];
                        BlockModel model = resolved.Models[self];
                        if (model.IsEmpty) continue;

                        Float3 min = layout.CellMin(x, y, z);
                        foreach (BlockQuad q in model.Unculled)
                            Emit(mesh, subMeshes, q, min, layout.BlockSize);

                        foreach (Direction d in Directions.All)
                        {
                            IReadOnlyList<BlockQuad> quads = model.CulledBy(d);
                            if (quads.Count == 0) continue;
                            d.Offset(out int dx, out int dy, out int dz);
                            int nx = x + dx, ny = y + dy, nz = z + dz;
                            // Out of bounds is air: nothing hides the grid's outer faces.
                            if (grid.InBounds(nx, ny, nz) && resolved.Hides(self, cells[grid.IndexOf(nx, ny, nz)], d))
                                continue;
                            foreach (BlockQuad q in quads)
                                Emit(mesh, subMeshes, q, min, layout.BlockSize);
                        }
                    }

            mesh.SubMeshes.AddRange(subMeshes.Values);
            mesh.SubMeshes.Sort((a, b) => a.Key.CompareTo(b.Key));
            return mesh;
        }

        /// <summary>
        /// Whether a face of <paramref name="self"/> toward <paramref name="face"/>
        /// shows, given the neighbour's state. The per-cell loop in
        /// <see cref="Build(VoxelGrid, IBlockModelSource, BlockLayout)"/> applies the same rule.
        /// </summary>
        public static bool IsFaceVisible(BlockState self, BlockState neighbour, Direction face, IBlockModelSource models)
        {
            BlockModel n = models.GetModel(neighbour) ?? BlockModel.Empty;
            if (n.OccludesFace(face.Opposite())) return false;
            BlockModel s = models.GetModel(self) ?? BlockModel.Empty;
            return !(s.CullAgainstSameBlock && !neighbour.IsAir && neighbour.Name == self.Name);
        }

        private static void Emit(MeshData mesh, Dictionary<MaterialKey, SubMeshData> subMeshes, BlockQuad q, Float3 min, float size)
        {
            var key = new MaterialKey(q.Layer, q.Atlas, q.Tint);
            if (!subMeshes.TryGetValue(key, out SubMeshData sub))
            {
                sub = new SubMeshData(key);
                subMeshes.Add(key, sub);
            }

            int start = mesh.Vertices.Count;
            mesh.Vertices.Add(min + q.P0 * size);
            mesh.Vertices.Add(min + q.P1 * size);
            mesh.Vertices.Add(min + q.P2 * size);
            mesh.Vertices.Add(min + q.P3 * size);
            for (int i = 0; i < 4; i++) mesh.Normals.Add(q.Normal);
            mesh.Uvs.Add(q.Uv0);
            mesh.Uvs.Add(q.Uv1);
            mesh.Uvs.Add(q.Uv2);
            mesh.Uvs.Add(q.Uv3);

            sub.Triangles.Add(start); sub.Triangles.Add(start + 1); sub.Triangles.Add(start + 2);
            sub.Triangles.Add(start); sub.Triangles.Add(start + 2); sub.Triangles.Add(start + 3);
        }

        /// <summary>Models and block identities per palette index, so the cell loop never touches strings.</summary>
        private sealed class PaletteModels
        {
            public BlockModel[] Models;
            public int[] BlockIds; // equal ids = same block name, any state

            public bool Hides(ushort self, ushort neighbour, Direction face)
            {
                if (Models[neighbour].OccludesFace(face.Opposite())) return true;
                return Models[self].CullAgainstSameBlock && neighbour != 0 && BlockIds[neighbour] == BlockIds[self];
            }
        }

        private static PaletteModels Resolve(VoxelGrid grid, IBlockModelSource models)
        {
            IReadOnlyList<BlockState> palette = grid.Palette;
            var result = new PaletteModels { Models = new BlockModel[palette.Count], BlockIds = new int[palette.Count] };
            var names = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < palette.Count; i++)
            {
                BlockState state = palette[i];
                result.Models[i] = (state.IsAir ? null : models.GetModel(state)) ?? BlockModel.Empty;
                string name = state.Name;
                if (!names.TryGetValue(name, out int id))
                {
                    id = names.Count;
                    names.Add(name, id);
                }
                result.BlockIds[i] = id;
            }
            return result;
        }
    }
}
