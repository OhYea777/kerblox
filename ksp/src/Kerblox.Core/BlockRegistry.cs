using System;
using System.Collections.Generic;

namespace Kerblox.Core
{
    /// <summary>How the plugin paints this block's atlas tile. Purely cosmetic.</summary>
    public enum TilePattern { Noise, Planks, Bevel, Frame }

    public sealed class BlockType
    {
        /// <summary>
        /// Namespaced block id, matching Minecraft's so the bridge maps 1:1. Every state
        /// of the block (any properties) shares this type.
        /// </summary>
        public string Name { get; }

        /// <summary>Tonnes per cubic metre (KSP mass units). Game-balanced, not realistic.</summary>
        public double Density { get; }

        /// <summary>Contributes mass and collision.</summary>
        public bool Solid { get; }

        /// <summary>Hides the faces of neighbouring blocks.</summary>
        public bool Opaque { get; }

        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        public TilePattern Pattern { get; }

        /// <summary>Column in the texture atlas; assigned by the registry.</summary>
        public int TileIndex { get; internal set; }

        public BlockType(string name, double density, bool solid, bool opaque,
                         byte r, byte g, byte b, TilePattern pattern = TilePattern.Noise)
        {
            Name = name; Density = density; Solid = solid; Opaque = opaque;
            R = r; G = g; B = b; Pattern = pattern;
        }

        public override string ToString() => Name;
    }

    public sealed class BlockRegistry
    {
        private readonly Dictionary<string, BlockType> byName = new Dictionary<string, BlockType>(StringComparer.Ordinal);
        private readonly List<BlockType> ordered = new List<BlockType>();

        public IReadOnlyList<BlockType> Types => ordered;
        public int TileCount => ordered.Count;

        public BlockType Register(BlockType type)
        {
            if (!BlockState.TryParse(type.Name, out BlockState parsed) || parsed.HasProperties || parsed.Name != type.Name)
                throw new ArgumentException($"'{type.Name}' is not a canonical namespaced block id", nameof(type));
            if (type.Name == BlockState.AirName) throw new ArgumentException("Air can't be registered", nameof(type));
            if (byName.ContainsKey(type.Name)) throw new ArgumentException($"Duplicate block name {type.Name}", nameof(type));
            type.TileIndex = ordered.Count;
            byName.Add(type.Name, type);
            ordered.Add(type);
            return type;
        }

        /// <summary>
        /// Null for air or unknown blocks. Unknown blocks are treated as air everywhere,
        /// but grids keep their state strings so nothing is lost on a round trip.
        /// </summary>
        public BlockType Get(BlockState state) => state.IsAir ? null : Get(state.Name);

        public BlockType Get(string name) => byName.TryGetValue(name, out var t) ? t : null;

        public bool IsSolid(BlockState state) => Get(state)?.Solid ?? false;
        public bool IsOpaque(BlockState state) => Get(state)?.Opaque ?? false;

        /// <summary>
        /// Fixed type ids from grid format v1, kept only so <see cref="GridCodec"/> can
        /// migrate old craft files. Never renumber or reuse them.
        /// </summary>
        public static class Ids
        {
            public const ushort Stone = 1;
            public const ushort OakPlanks = 2;
            public const ushort IronBlock = 3;
            public const ushort Glass = 4;
            public const ushort WhiteWool = 5;
        }

        /// <summary>
        /// Block name for a v1 type id. Ids nobody ever shipped map to a placeholder
        /// that the registry treats as unknown (so as air), preserving the id for debugging.
        /// </summary>
        public static string LegacyIdToName(ushort id)
        {
            switch (id)
            {
                case 0: return BlockState.AirName;
                case Ids.Stone: return "minecraft:stone";
                case Ids.OakPlanks: return "minecraft:oak_planks";
                case Ids.IronBlock: return "minecraft:iron_block";
                case Ids.Glass: return "minecraft:glass";
                case Ids.WhiteWool: return "minecraft:white_wool";
                default: return "kerblox:legacy_" + id;
            }
        }

        public static BlockRegistry CreateDefault()
        {
            var r = new BlockRegistry();
            r.Register(new BlockType("minecraft:stone", 0.30, true, true, 125, 125, 125));
            r.Register(new BlockType("minecraft:oak_planks", 0.10, true, true, 162, 130, 78, TilePattern.Planks));
            r.Register(new BlockType("minecraft:iron_block", 0.60, true, true, 220, 220, 220, TilePattern.Bevel));
            r.Register(new BlockType("minecraft:glass", 0.20, true, false, 190, 225, 235, TilePattern.Frame));
            r.Register(new BlockType("minecraft:white_wool", 0.02, true, true, 234, 236, 237));
            return r;
        }
    }
}
