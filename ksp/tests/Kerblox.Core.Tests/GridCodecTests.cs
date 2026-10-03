using System.Text;
using Kerblox.Core;

namespace Kerblox.Core.Tests;

public class GridCodecTests
{
    /// <summary>
    /// DefaultGrids.Capsule() as written by the v1 codec (fixed u32 ids), captured
    /// before P1.8. Every craft saved before palettes carries exactly this string.
    /// </summary>
    private const string CapsuleV1 =
        "S0JHUgEEAAgABAAQAwAAAAUBAAAAAgAAAAACAQAAAAIAAAAACgEAAAACAAAAAAIBAAAAAgAAAAAFAQAAAAUEAAAAAgAAAAACBAAAAAIAAAAABQQAAAAF" +
        "AQAAAAIAAAAAAgEAAAACAAAAAAoBAAAAAgAAAAACAQAAAAIAAAAABQEAAAAQAgAAAAUAAAAAAgUAAAACAAAAAAIFAAAABQAAAAA";

    private static readonly string[] SomeStates =
    {
        "minecraft:stone", "minecraft:glass", "minecraft:oak_log[axis=y]",
        "minecraft:redstone_wire[east=side,north=none,power=15,south=up,west=none]",
        "mekanism:basic_energy_cube", "create:shaft[axis=z]",
    };

    [Fact]
    public void RoundTripsDefaultGrid()
    {
        var g = DefaultGrids.Capsule();
        var back = GridCodec.FromText(GridCodec.ToText(g));
        Assert.True(g.ContentEquals(back));
    }

    [Fact]
    public void RoundTripsPaletteOfModdedStates()
    {
        var g = new VoxelGrid(5, 3, 7);
        for (int i = 0; i < SomeStates.Length; i++)
            g.Set(i % 5, i % 3, i, BlockState.Parse(SomeStates[i]));
        var back = GridCodec.FromBytes(GridCodec.ToBytes(g));
        Assert.True(g.ContentEquals(back));
        Assert.Equal(BlockState.Parse(SomeStates[3]), back.Get(3, 0, 3));
        Assert.Equal(SomeStates.Length + 1, back.Palette.Count);
        Assert.Equal(BlockState.Air, back.Palette[0]);
    }

    [Fact]
    public void WritesCompactedFirstAppearancePalette()
    {
        var stone = BlockState.Parse("minecraft:stone");
        var glass = BlockState.Parse("minecraft:glass");
        var log = BlockState.Parse("minecraft:oak_log[axis=y]");

        // Same content reached through different edit histories (and stale palette entries).
        var a = new VoxelGrid(3, 1, 1);
        a.Set(2, 0, 0, stone);
        a.Set(0, 0, 0, log);
        a.Set(1, 0, 0, glass);
        a.Set(1, 0, 0, BlockState.Air);

        var b = new VoxelGrid(3, 1, 1);
        b.Set(0, 0, 0, log);
        b.Set(2, 0, 0, stone);

        Assert.Equal(GridCodec.ToBytes(a), GridCodec.ToBytes(b));
        Assert.Equal(new[] { BlockState.Air, log, stone }, GridCodec.FromBytes(GridCodec.ToBytes(a)).Palette);
        Assert.Equal(4, a.Palette.Count); // encoding doesn't mutate the grid
    }

    [Fact]
    public void WritesVersion2()
    {
        var bytes = GridCodec.ToBytes(DefaultGrids.Capsule());
        Assert.Equal("KBGR", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(2, bytes[4]);
        Assert.Equal(2, GridCodec.Version);
    }

    [Fact]
    public void MigratesV1CapsuleToNamedStates()
    {
        var g = GridCodec.FromText(CapsuleV1);
        Assert.True(DefaultGrids.Capsule().ContentEquals(g));
        Assert.Equal(BlockState.Parse("minecraft:iron_block"), g.Get(0, 0, 0));
        Assert.Equal(BlockState.Parse("minecraft:glass"), g.Get(0, 3, 0));
        Assert.Equal(BlockState.Parse("minecraft:white_wool"), g.Get(1, 7, 1));
        Assert.True(g.Get(1, 1, 1).IsAir);

        // Re-encoding upgrades to v2 and is stable from then on.
        string v2 = GridCodec.ToText(g);
        Assert.NotEqual(CapsuleV1, v2);
        Assert.Equal(GridCodec.ToText(DefaultGrids.Capsule()), v2);
        Assert.Equal(v2, GridCodec.ToText(GridCodec.FromText(v2)));
    }

    [Fact]
    public void MigratesV1UnknownIdsAndDropsDataBits()
    {
        // 1x1x2 v1 grid: cell 0 = stone with data 0x0007, cell 1 = never-shipped id 99.
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("KBGR")) { 1, 1, 0, 2, 0, 1, 0 };
        bytes.Add(1); bytes.AddRange(BitConverter.GetBytes(0x0007_0001u));
        bytes.Add(1); bytes.AddRange(BitConverter.GetBytes(99u));

        var g = GridCodec.FromBytes(bytes.ToArray());
        Assert.Equal(BlockState.Parse("minecraft:stone"), g.Get(0, 0, 0));
        Assert.Equal(BlockState.Parse("kerblox:legacy_99"), g.Get(0, 1, 0));
        Assert.Null(BlockRegistry.CreateDefault().Get(g.Get(0, 1, 0)));
    }

    [Fact]
    public void RoundTripsMaxSizeEmptyGrid()
    {
        var g = new VoxelGrid(256, 256, 256);
        var bytes = GridCodec.ToBytes(g);
        Assert.True(bytes.Length < 32, "a single run should encode tiny");
        Assert.True(g.ContentEquals(GridCodec.FromBytes(bytes)));
    }

    [Fact]
    public void TextIsConfigNodeSafe()
    {
        // Random-ish content to exercise all base64 characters.
        var g = new VoxelGrid(16, 16, 16);
        var rng = new Random(1234);
        for (int i = 0; i < 2000; i++)
            g.Set(rng.Next(16), rng.Next(16), rng.Next(16), BlockState.Parse(SomeStates[rng.Next(SomeStates.Length)]));

        string text = GridCodec.ToText(g);
        Assert.DoesNotContain("//", text);
        Assert.DoesNotContain("=", text);
        Assert.DoesNotContain("{", text);
        Assert.DoesNotContain("}", text);
        Assert.DoesNotContain("\n", text);
        Assert.True(g.ContentEquals(GridCodec.FromText(text)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAA")]
    [InlineData("S0JHUgE")] // "KBGR" + version 1, then truncated
    [InlineData("S0JHUgI")] // "KBGR" + version 2, then truncated
    [InlineData("S0JHUgM")] // "KBGR" + version 3
    public void RejectsGarbage(string text) =>
        Assert.Throws<FormatException>(() => GridCodec.FromText(text));

    [Fact]
    public void RejectsTrailingBytes()
    {
        var bytes = GridCodec.ToBytes(new VoxelGrid(1, 1, 1)).Append((byte)0).ToArray();
        Assert.Throws<FormatException>(() => GridCodec.FromBytes(bytes));
    }

    private static byte[] V2(params object[] parts)
    {
        // Header for a 2x1x1 grid, then the given palette strings and raw bytes.
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("KBGR")) { 2, 2, 0, 1, 0, 1, 0 };
        foreach (object p in parts)
        {
            if (p is string s)
            {
                bytes.Add((byte)s.Length);
                bytes.AddRange(Encoding.UTF8.GetBytes(s));
            }
            else bytes.Add(Convert.ToByte(p));
        }
        return bytes.ToArray();
    }

    [Fact]
    public void AcceptsHandWrittenV2()
    {
        var g = GridCodec.FromBytes(V2(2, "minecraft:air", "minecraft:stone", 1, 0, 1, 1));
        Assert.True(g.Get(0, 0, 0).IsAir);
        Assert.Equal(BlockState.Parse("minecraft:stone"), g.Get(1, 0, 0));
    }

    [Theory]
    [InlineData(2, "minecraft:air", "minecraft:stone", 5, 1)]                 // run past the end
    [InlineData(2, "minecraft:air", "minecraft:stone", 2, 2)]                 // index past palette
    [InlineData(0, 2, 0)]                                                     // empty palette
    [InlineData(1, "minecraft:stone", 2, 0)]                                  // entry 0 not air
    [InlineData(3, "minecraft:air", "minecraft:stone", "stone", 2, 1)]        // duplicate after canonicalising
    [InlineData(2, "minecraft:air", "minecraft:air", 2, 1)]                   // air twice
    [InlineData(2, "minecraft:air", "Bad Name", 2, 1)]                        // not a block state
    public void RejectsBadV2(params object[] parts) =>
        Assert.Throws<FormatException>(() => GridCodec.FromBytes(V2(parts)));
}
