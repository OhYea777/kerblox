using System;
using System.Collections.Generic;

namespace Kerblox.Core
{
    /// <summary>How the plugin paints this block's atlas tile. Purely cosmetic.</summary>
    public enum TilePattern { Noise, Planks, Bevel, Frame }

    public sealed class BlockType
    {
        /// <summary>Stable numeric id stored in grids. Never renumber; 0 is reserved for air.</summary>
        public ushort Id { get; }

        /// <summary>Namespaced id, deliberately matching Minecraft's so the bridge can map 1:1.</summary>
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

        public BlockType(ushort id, string name, double density, bool solid, bool opaque,
                         byte r, byte g, byte b, TilePattern pattern = TilePattern.Noise)
        {
            Id = id; Name = name; Density = density; Solid = solid; Opaque = opaque;
            R = r; G = g; B = b; Pattern = pattern;
        }

        public override string ToString() => $"{Name} (#{Id})";
    }

    public sealed class BlockRegistry
    {
        private readonly Dictionary<ushort, BlockType> byId = new Dictionary<ushort, BlockType>();
        private readonly Dictionary<string, BlockType> byName = new Dictionary<string, BlockType>(StringComparer.Ordinal);
        private readonly List<BlockType> ordered = new List<BlockType>();

        public IReadOnlyList<BlockType> Types => ordered;
        public int TileCount => ordered.Count;

        public BlockType Register(BlockType type)
        {
            if (type.Id == 0) throw new ArgumentException("Id 0 is reserved for air", nameof(type));
            if (byId.ContainsKey(type.Id)) throw new ArgumentException($"Duplicate block id {type.Id}", nameof(type));
            if (byName.ContainsKey(type.Name)) throw new ArgumentException($"Duplicate block name {type.Name}", nameof(type));
            type.TileIndex = ordered.Count;
            byId.Add(type.Id, type);
            byName.Add(type.Name, type);
            ordered.Add(type);
            return type;
        }

        /// <summary>Null for air or unknown ids. Unknown ids are treated as air everywhere.</summary>
        public BlockType Get(BlockState state) => byId.TryGetValue(state.TypeId, out var t) ? t : null;

        public BlockType Get(string name) => byName.TryGetValue(name, out var t) ? t : null;

        public bool IsSolid(BlockState state) => Get(state)?.Solid ?? false;
        public bool IsOpaque(BlockState state) => Get(state)?.Opaque ?? false;

        public static class Ids
        {
            public const ushort Stone = 1;
            public const ushort OakPlanks = 2;
            public const ushort IronBlock = 3;
            public const ushort Glass = 4;
            public const ushort WhiteWool = 5;
        }

        public static BlockRegistry CreateDefault()
        {
            var r = new BlockRegistry();
            r.Register(new BlockType(Ids.Stone, "minecraft:stone", 0.30, true, true, 125, 125, 125));
            r.Register(new BlockType(Ids.OakPlanks, "minecraft:oak_planks", 0.10, true, true, 162, 130, 78, TilePattern.Planks));
            r.Register(new BlockType(Ids.IronBlock, "minecraft:iron_block", 0.60, true, true, 220, 220, 220, TilePattern.Bevel));
            r.Register(new BlockType(Ids.Glass, "minecraft:glass", 0.20, true, false, 190, 225, 235, TilePattern.Frame));
            r.Register(new BlockType(Ids.WhiteWool, "minecraft:white_wool", 0.02, true, true, 234, 236, 237));
            return r;
        }
    }
}
