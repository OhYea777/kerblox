using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Kerblox.Core
{
    /// <summary>
    /// Compact binary form of a <see cref="VoxelGrid"/>, plus a text wrapper that is
    /// safe inside KSP ConfigNode values (craft/save files).
    ///
    /// Binary layout (little-endian), version 2 (written):
    ///   bytes 0-3  magic "KBGR"
    ///   byte  4    format version (2)
    ///   u16 x3     SizeX, SizeY, SizeZ
    ///   varint     palette count N (at least 1)
    ///   N x        (varint byteLength, UTF-8 canonical block state); entry 0 is "minecraft:air"
    ///   runs...    (varint runLength, varint paletteIndex) until Volume cells are covered,
    ///              in VoxelGrid storage order (Y-major).
    /// The palette is compacted and ordered by first appearance, so equal grids
    /// encode to identical bytes.
    ///
    /// Version 1 (read only): same header, then (varint runLength, u32 rawBlockState)
    /// runs, where the low 16 bits of the state were a fixed type id
    /// (<see cref="BlockRegistry.LegacyIdToName"/>) and the high 16 bits unused data.
    ///
    /// This is the payload the Minecraft bridge will send for full-grid syncs.
    /// </summary>
    public static class GridCodec
    {
        private static readonly byte[] Magic = { (byte)'K', (byte)'B', (byte)'G', (byte)'R' };
        public const byte Version = 2;

        /// <summary>Longest palette entry accepted, in UTF-8 bytes. Real states are far shorter.</summary>
        public const int MaxStateLength = 1024;

        public static byte[] ToBytes(VoxelGrid grid)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write((ushort)grid.SizeX);
                w.Write((ushort)grid.SizeY);
                w.Write((ushort)grid.SizeZ);

                ushort[] remap = grid.BuildCompactRemap(out List<BlockState> palette);
                WriteVarint(w, (uint)palette.Count);
                foreach (BlockState state in palette)
                {
                    byte[] utf8 = Encoding.UTF8.GetBytes(state.Canonical);
                    WriteVarint(w, (uint)utf8.Length);
                    w.Write(utf8);
                }

                ushort[] cells = grid.RawCells;
                int i = 0;
                while (i < cells.Length)
                {
                    ushort value = cells[i];
                    int run = 1;
                    while (i + run < cells.Length && cells[i + run] == value) run++;
                    WriteVarint(w, (uint)run);
                    WriteVarint(w, remap[value]);
                    i += run;
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        public static VoxelGrid FromBytes(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            using (var ms = new MemoryStream(data))
            using (var r = new BinaryReader(ms))
            {
                try
                {
                    byte[] magic = r.ReadBytes(4);
                    if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3])
                        throw new FormatException("Not a Kerblox grid (bad magic)");
                    byte version = r.ReadByte();
                    if (version != 1 && version != 2)
                        throw new FormatException($"Unsupported grid format version {version}");

                    int sx = r.ReadUInt16(), sy = r.ReadUInt16(), sz = r.ReadUInt16();
                    var grid = new VoxelGrid(sx, sy, sz);
                    List<BlockState> palette = version == 1 ? ReadV1Cells(r, grid) : ReadV2Cells(r, ms, grid);
                    if (ms.Position != ms.Length)
                        throw new FormatException("Trailing bytes after grid data");
                    grid.LoadPalette(palette);
                    return grid;
                }
                catch (EndOfStreamException e)
                {
                    throw new FormatException("Truncated grid data", e);
                }
                catch (ArgumentOutOfRangeException e)
                {
                    throw new FormatException("Invalid grid dimensions", e);
                }
            }
        }

        private static List<BlockState> ReadV2Cells(BinaryReader r, MemoryStream ms, VoxelGrid grid)
        {
            uint count = ReadVarint(r);
            if (count == 0 || count > VoxelGrid.MaxPaletteSize)
                throw new FormatException($"Bad palette size {count}");

            var palette = new List<BlockState>((int)count);
            var seen = new HashSet<BlockState>();
            for (uint p = 0; p < count; p++)
            {
                uint len = ReadVarint(r);
                if (len == 0 || len > MaxStateLength || len > ms.Length - ms.Position)
                    throw new FormatException($"Bad palette entry length {len}");
                string text = Encoding.UTF8.GetString(r.ReadBytes((int)len));
                if (!BlockState.TryParse(text, out BlockState state))
                    throw new FormatException($"Bad palette entry '{text}'");
                if (p == 0 && !state.IsAir)
                    throw new FormatException("Palette entry 0 must be air");
                if (p > 0 && (state.IsAir || !seen.Add(state)))
                    throw new FormatException($"Duplicate palette entry '{text}'");
                palette.Add(state);
            }

            ushort[] cells = grid.RawCells;
            int i = 0;
            while (i < cells.Length)
            {
                uint run = ReadVarint(r);
                uint index = ReadVarint(r);
                if (run == 0 || run > (uint)(cells.Length - i))
                    throw new FormatException($"Bad run length {run} at cell {i}");
                if (index >= count)
                    throw new FormatException($"Palette index {index} out of range at cell {i}");
                for (uint k = 0; k < run; k++) cells[i++] = (ushort)index;
            }
            return palette;
        }

        /// <summary>
        /// Version 1 stored raw 32-bit states. Map each distinct type id to its block
        /// name; the high 16 bits never carried meaning and are dropped.
        /// </summary>
        private static List<BlockState> ReadV1Cells(BinaryReader r, VoxelGrid grid)
        {
            var palette = new List<BlockState> { BlockState.Air };
            var byState = new Dictionary<BlockState, ushort> { { BlockState.Air, 0 } };
            var byTypeId = new Dictionary<ushort, ushort>();

            ushort[] cells = grid.RawCells;
            int i = 0;
            while (i < cells.Length)
            {
                uint run = ReadVarint(r);
                uint raw = r.ReadUInt32();
                if (run == 0 || run > (uint)(cells.Length - i))
                    throw new FormatException($"Bad run length {run} at cell {i}");

                ushort typeId = (ushort)(raw & 0xFFFF);
                if (!byTypeId.TryGetValue(typeId, out ushort index))
                {
                    BlockState state = typeId == 0 ? BlockState.Air : BlockState.Parse(BlockRegistry.LegacyIdToName(typeId));
                    if (!byState.TryGetValue(state, out index))
                    {
                        index = (ushort)palette.Count;
                        palette.Add(state);
                        byState.Add(state, index);
                    }
                    byTypeId.Add(typeId, index);
                }
                for (uint k = 0; k < run; k++) cells[i++] = index;
            }
            return palette;
        }

        /// <summary>
        /// URL-safe base64 without padding. KSP's ConfigNode parser treats "//" as a
        /// comment and "=" as a key/value separator, so standard base64 is not safe.
        /// </summary>
        public static string ToText(VoxelGrid grid) => EncodeBase64Url(ToBytes(grid));

        public static VoxelGrid FromText(string text) => FromBytes(DecodeBase64Url(text));

        internal static string EncodeBase64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        internal static byte[] DecodeBase64Url(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            string s = text.Trim().Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: throw new FormatException("Invalid base64url length");
            }
            return Convert.FromBase64String(s);
        }

        private static void WriteVarint(BinaryWriter w, uint value)
        {
            while (value >= 0x80)
            {
                w.Write((byte)(value | 0x80));
                value >>= 7;
            }
            w.Write((byte)value);
        }

        private static uint ReadVarint(BinaryReader r)
        {
            uint result = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                byte b = r.ReadByte();
                // The fifth byte has room for only 4 more bits of a u32.
                if (shift == 28 && (b & 0xF0) != 0)
                    throw new FormatException("Varint overflows 32 bits");
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
            }
            throw new FormatException("Varint too long");
        }
    }
}
