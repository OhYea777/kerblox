using System;
using System.IO;

namespace Kerblox.Core
{
    /// <summary>
    /// Compact binary form of a <see cref="VoxelGrid"/>, plus a text wrapper that is
    /// safe inside KSP ConfigNode values (craft/save files).
    ///
    /// Binary layout (little-endian), version 1:
    ///   bytes 0-3  magic "KBGR"
    ///   byte  4    format version (1)
    ///   u16 x3     SizeX, SizeY, SizeZ
    ///   runs...    (varint runLength, u32 rawBlockState) until Volume cells are covered,
    ///              in VoxelGrid storage order (Y-major).
    ///
    /// This is the payload the Minecraft bridge will send for full-grid syncs.
    /// </summary>
    public static class GridCodec
    {
        private static readonly byte[] Magic = { (byte)'K', (byte)'B', (byte)'G', (byte)'R' };
        public const byte Version = 1;

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

                uint[] cells = grid.RawCells;
                int i = 0;
                while (i < cells.Length)
                {
                    uint value = cells[i];
                    int run = 1;
                    while (i + run < cells.Length && cells[i + run] == value) run++;
                    WriteVarint(w, (uint)run);
                    w.Write(value);
                    i += run;
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        public static VoxelGrid FromBytes(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var r = new BinaryReader(ms))
            {
                try
                {
                    byte[] magic = r.ReadBytes(4);
                    if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3])
                        throw new FormatException("Not a Kerblox grid (bad magic)");
                    byte version = r.ReadByte();
                    if (version != Version)
                        throw new FormatException($"Unsupported grid format version {version}");

                    int sx = r.ReadUInt16(), sy = r.ReadUInt16(), sz = r.ReadUInt16();
                    var grid = new VoxelGrid(sx, sy, sz);
                    uint[] cells = grid.RawCells;
                    int i = 0;
                    while (i < cells.Length)
                    {
                        uint run = ReadVarint(r);
                        uint value = r.ReadUInt32();
                        if (run == 0 || run > (uint)(cells.Length - i))
                            throw new FormatException($"Bad run length {run} at cell {i}");
                        for (uint k = 0; k < run; k++) cells[i++] = value;
                    }
                    if (ms.Position != ms.Length)
                        throw new FormatException("Trailing bytes after grid data");
                    grid.MarkBulkLoaded();
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
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
            }
            throw new FormatException("Varint too long");
        }
    }
}
