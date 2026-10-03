using System;

namespace Kerblox.Core
{
    /// <summary>
    /// One voxel's contents. Packed into 32 bits so the wire format (and a future
    /// shared-memory bridge) can treat a grid as a flat uint array.
    /// Low 16 bits: block type id (0 = air). High 16 bits: per-block state
    /// (redstone power, facing, etc. once the Minecraft bridge exists).
    /// </summary>
    public readonly struct BlockState : IEquatable<BlockState>
    {
        public static readonly BlockState Air = default;

        public readonly uint Raw;

        public BlockState(uint raw) { Raw = raw; }

        public BlockState(ushort typeId, ushort data = 0) { Raw = typeId | ((uint)data << 16); }

        public ushort TypeId => (ushort)(Raw & 0xFFFF);
        public ushort Data => (ushort)(Raw >> 16);
        public bool IsAir => TypeId == 0;

        public bool Equals(BlockState other) => Raw == other.Raw;
        public override bool Equals(object obj) => obj is BlockState other && Equals(other);
        public override int GetHashCode() => (int)Raw;
        public static bool operator ==(BlockState a, BlockState b) => a.Raw == b.Raw;
        public static bool operator !=(BlockState a, BlockState b) => a.Raw != b.Raw;
        public override string ToString() => Data == 0 ? $"#{TypeId}" : $"#{TypeId}:{Data}";
    }
}
