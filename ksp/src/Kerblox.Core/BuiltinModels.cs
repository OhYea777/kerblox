using System;
using System.Collections.Generic;

namespace Kerblox.Core
{
    /// <summary>
    /// Models for the blocks in a <see cref="BlockRegistry"/>: one textured cube per
    /// block type on the plugin's procedurally painted atlas (atlas index
    /// <see cref="AtlasIndex"/>, one 16 px column per type). This is what every grid
    /// looked like before models existed, and what renders until a render pack is loaded.
    ///
    /// The atlas has one extra column after the registry's types: a plain light tile
    /// that <see cref="Fallback"/> tints with a map colour for states nobody has a model for.
    /// </summary>
    public sealed class BuiltinModelSource : IBlockModelSource
    {
        public const int AtlasIndex = 0;

        /// <summary>Fraction of a tile trimmed from each UV edge to avoid atlas bleeding.</summary>
        public const float UvInset = 0.5f / 16f;

        private readonly BlockRegistry registry;
        private readonly Dictionary<string, BlockModel> byName = new Dictionary<string, BlockModel>(StringComparer.Ordinal);

        public BuiltinModelSource(BlockRegistry registry)
        {
            this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Fallback = new MapColorFallback(AtlasIndex, TileUvMin(FallbackTileIndex), TileUvMax(FallbackTileIndex));
        }

        public BlockRegistry Registry => registry;

        /// <summary>Columns in the built-in atlas: one per registered type plus the fallback tile.</summary>
        public int AtlasTileCount => registry.TileCount + 1;

        /// <summary>Column of the untextured tile the map-colour fallback uses.</summary>
        public int FallbackTileIndex => registry.TileCount;

        /// <summary>Map-colour cubes on this atlas's fallback tile.</summary>
        public MapColorFallback Fallback { get; }

        public Float2 TileUvMin(int tile) => new Float2((tile + UvInset) / AtlasTileCount, UvInset);

        public Float2 TileUvMax(int tile) => new Float2((tile + 1 - UvInset) / AtlasTileCount, 1f - UvInset);

        /// <summary>A cube for registered blocks (any state), null for unknown blocks and air.</summary>
        public BlockModel GetModel(BlockState state)
        {
            BlockType type = registry.Get(state);
            if (type == null) return null;
            if (byName.TryGetValue(type.Name, out BlockModel model)) return model;

            // Every built-in block is drawn in the solid layer, glass included: its tile
            // is fully opaque, and moving it to cutout would change how the default
            // capsule looks. See-through-ness is about culling: glass hides nothing,
            // and hides its faces toward more glass.
            model = BlockModel.Cube(AtlasIndex, TileUvMin(type.TileIndex), TileUvMax(type.TileIndex),
                RenderLayer.Solid, Rgba32.White,
                type.Opaque ? FaceMask.All : FaceMask.None, cullAgainstSameBlock: !type.Opaque);
            byName.Add(type.Name, model);
            return model;
        }

        /// <summary>The default chain: built-in cubes, then map-colour cubes for everything else.</summary>
        public BlockModelResolver CreateResolver() => new BlockModelResolver(this, Fallback);
    }

    /// <summary>
    /// Last resort for any non-air state: an opaque cube in the block's map colour,
    /// so a grid always renders even before a client has exported a render pack.
    /// The real map colour comes from the server once the bridge exists; until then
    /// a stable colour is derived from the block name.
    /// </summary>
    public sealed class MapColorFallback : IBlockModelSource
    {
        private readonly int atlas;
        private readonly Float2 uvMin, uvMax;
        private readonly Func<BlockState, Rgba32> mapColor;
        private readonly Dictionary<Rgba32, BlockModel> byColor = new Dictionary<Rgba32, BlockModel>();

        /// <param name="mapColor">Colour for a state; defaults to <see cref="NameColor"/>.</param>
        public MapColorFallback(int atlas, Float2 uvMin, Float2 uvMax, Func<BlockState, Rgba32> mapColor = null)
        {
            this.atlas = atlas;
            this.uvMin = uvMin;
            this.uvMax = uvMax;
            this.mapColor = mapColor ?? NameColor;
        }

        public BlockModel GetModel(BlockState state)
        {
            if (state.IsAir) return null;
            Rgba32 color = mapColor(state);
            if (byColor.TryGetValue(color, out BlockModel model)) return model;
            model = BlockModel.Cube(atlas, uvMin, uvMax, RenderLayer.Solid, color, FaceMask.All);
            byColor.Add(color, model);
            return model;
        }

        /// <summary>
        /// A mid-brightness colour hashed from the block name (not the state, so all
        /// states of one unknown block match). FNV-1a, so it's stable across runtimes.
        /// </summary>
        public static Rgba32 NameColor(BlockState state)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in state.Name) h = (h ^ c) * 16777619;
                return new Rgba32((byte)(64 + (h & 0x7f)), (byte)(64 + ((h >> 8) & 0x7f)), (byte)(64 + ((h >> 16) & 0x7f)));
            }
        }
    }

    /// <summary>
    /// Tries sources in order and returns the first model found. Air is always
    /// <see cref="BlockModel.Empty"/>, and a state no source knows is empty too, so
    /// put a <see cref="MapColorFallback"/> last to guarantee everything draws.
    /// </summary>
    public sealed class BlockModelResolver : IBlockModelSource
    {
        private readonly IBlockModelSource[] sources;

        public BlockModelResolver(params IBlockModelSource[] sources)
        {
            if (sources == null) throw new ArgumentNullException(nameof(sources));
            foreach (IBlockModelSource s in sources)
                if (s == null) throw new ArgumentException("Null model source", nameof(sources));
            this.sources = (IBlockModelSource[])sources.Clone();
        }

        /// <summary>Never null.</summary>
        public BlockModel GetModel(BlockState state)
        {
            if (state.IsAir) return BlockModel.Empty;
            foreach (IBlockModelSource s in sources)
            {
                BlockModel m = s.GetModel(state);
                if (m != null) return m;
            }
            return BlockModel.Empty;
        }
    }
}
