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

        /// <summary>
        /// Tonnes per cubic metre (KSP mass units). Game-balanced, not realistic.
        /// Overridden by <c>KERBLOX_BLOCK</c> rules once the registry applies them.
        /// </summary>
        public double Density { get; private set; }

        /// <summary>Contributes mass and collision. Overridable like <see cref="Density"/>.</summary>
        public bool Solid { get; private set; }

        /// <summary>The values passed to the constructor, which rules fall back to.</summary>
        public BlockPhysics BuiltInPhysics { get; }

        public BlockPhysics Physics => new BlockPhysics(Density, Solid);

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
            BuiltInPhysics = new BlockPhysics(density, solid);
            R = r; G = g; B = b; Pattern = pattern;
        }

        internal void SetPhysics(BlockPhysics physics)
        {
            Density = physics.Density;
            Solid = physics.Solid;
        }

        public override string ToString() => Name;
    }

    public sealed class BlockRegistry
    {
        private readonly Dictionary<string, BlockType> byName = new Dictionary<string, BlockType>(StringComparer.Ordinal);
        private readonly List<BlockType> ordered = new List<BlockType>();
        private BlockPhysicsRules rules = BlockPhysicsRules.Empty;

        /// <summary>The physics rules last passed to <see cref="ApplyPhysics"/>.</summary>
        public BlockPhysicsRules PhysicsRules => rules;

        public IReadOnlyList<BlockType> Types => ordered;
        public int TileCount => ordered.Count;

        public BlockType Register(BlockType type)
        {
            if (!BlockState.TryParse(type.Name, out BlockState parsed) || parsed.HasProperties || parsed.Name != type.Name)
                throw new ArgumentException($"'{type.Name}' is not a canonical namespaced block id", nameof(type));
            if (type.Name == BlockState.AirName) throw new ArgumentException("Air can't be registered", nameof(type));
            if (byName.ContainsKey(type.Name)) throw new ArgumentException($"Duplicate block name {type.Name}", nameof(type));
            type.TileIndex = ordered.Count;
            type.SetPhysics(rules.Resolve(type.Name, type.BuiltInPhysics));
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

        /// <summary>
        /// Replaces the physics rules. Registered types take their
        /// <see cref="BlockType.Density"/> and <see cref="BlockType.Solid"/> from the rules
        /// (falling back to their built-in values), and unknown blocks resolve through
        /// <see cref="GetPhysics"/>. Applying again starts from the built-in values.
        /// </summary>
        public void ApplyPhysics(BlockPhysicsRules physicsRules)
        {
            rules = physicsRules ?? BlockPhysicsRules.Empty;
            foreach (BlockType t in ordered)
                t.SetPhysics(rules.Resolve(t.Name, t.BuiltInPhysics));
        }

        /// <summary>
        /// Mass and collision properties for any state, including blocks the registry
        /// doesn't know (which get the matching wildcard rule, else
        /// <see cref="BlockPhysics.None"/>). This is the lookup mass and collider code
        /// should use; <see cref="Get(BlockState)"/> covers known types only.
        /// </summary>
        public BlockPhysics GetPhysics(BlockState state)
        {
            if (state.IsAir) return BlockPhysics.None;
            BlockType t = Get(state.Name);
            return t != null ? t.Physics : rules.Resolve(state.Name, BlockPhysics.None);
        }

        /// <summary>Mass in tonnes of one block of <paramref name="state"/>; zero unless solid.</summary>
        public double BlockMass(BlockState state, double blockVolume) => GetPhysics(state).Mass(blockVolume);

        public bool IsSolid(BlockState state) => GetPhysics(state).Solid;
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

        /// <summary>
        /// The built-in blocks with their built-in physics. GameData/Kerblox/Blocks.cfg
        /// ships the same values; keep the two in sync.
        /// </summary>
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
