using System.Globalization;
using Kerblox.Core;
using Xunit.Abstractions;

namespace Kerblox.Core.Tests;

/// <summary>
/// How long <c>gridData</c> gets, and whether a value that long survives KSP's
/// ConfigNode text format. KSP 1.12.5 (decompiled, see docs/KSP-API-NOTES.md)
/// has no length limit on a value: <c>ConfigNode.Load</c> reads whole lines with
/// <c>File.ReadAllLines</c>, <c>PreFormatConfig</c> cuts at "//", splits lines at
/// "{" and "}" and trims them, <c>CustomEqualSplit</c> splits at the first "=", and
/// the writer emits <c>indent + name + " = " + value</c> on one line after
/// <c>CleanupInput</c> drops CR/LF and turns tabs into spaces.
/// </summary>
public class GridDataLengthTests
{
    private readonly ITestOutputHelper output;

    public GridDataLengthTests(ITestOutputHelper output) => this.output = output;

    /// <summary>A model of KSP's write-then-parse of one ConfigNode value line (see class doc).</summary>
    private static string ConfigNodeRoundTrip(string name, string value)
    {
        string cleaned = value.Replace("\n", "").Replace("\r", "").Replace("\t", " ");
        string line = "\t\t" + name + " = " + cleaned;

        int comment = line.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0) line = line.Remove(comment);
        line = line.Trim();
        Assert.DoesNotContain("{", line);   // would be split into a node opener
        Assert.DoesNotContain("}", line);   // would close the enclosing node
        int eq = line.IndexOf('=');
        Assert.True(eq > 0);
        Assert.Equal(name, line.Substring(0, eq).Trim());
        return line.Substring(eq + 1).Trim();
    }

    [Theory]
    [InlineData(64, 4)]        // the shipped block types: runs never longer than 1
    [InlineData(64, 4096)]     // a big modded palette: two-byte indices
    [InlineData(64, 65535)]    // the most states a grid can hold: three-byte indices
    public void WorstCaseGridsRoundTripThroughConfigNodeText(int size, int paletteSize)
    {
        VoxelGrid grid = StressGrids.Noisy(size, paletteSize);
        string text = GridCodec.ToText(grid);

        Assert.Equal(text, ConfigNodeRoundTrip("gridData", text));
        Assert.True(GridCodec.FromText(text).ContentEquals(grid));

        long bound = StressGrids.MaxTextLength(grid.Volume, grid.Palette);
        Assert.InRange(text.Length, 1, bound);
        output.WriteLine($"{size}^3, {paletteSize} states: gridData {text.Length:N0} chars (bound {bound:N0})");
    }

    [Fact]
    public void NoisyGridDefeatsRunLengthEncoding()
    {
        VoxelGrid grid = StressGrids.Noisy(16, 300);
        Assert.Equal(grid.Volume, grid.CountNonAir());
        Assert.Equal(301, grid.Palette.Count);
        // Each of 4096 cells costs at least a one-byte run and a two-byte index.
        Assert.True(GridCodec.ToBytes(grid).Length >= 4096 * 3);
    }

    [Fact]
    public void MaxTextLengthForTheLargestGridIsWithinDotNetStringLimits()
    {
        // 256^3 cells, a full palette of the longest states the codec accepts.
        var palette = Enumerable.Range(0, VoxelGrid.MaxPaletteSize)
            .Select(i => BlockState.Parse("minecraft:stone[n=" + i.ToString(CultureInfo.InvariantCulture) + "]")).ToList();
        long max = StressGrids.MaxTextLength(256L * 256 * 256, palette)
                   + (long)VoxelGrid.MaxPaletteSize * (GridCodec.MaxStateLength - 32);
        output.WriteLine($"Theoretical maximum gridData: {max:N0} chars");
        // Far beyond what's sensible to put in a craft file, but a single .NET string can hold it.
        Assert.True(max < int.MaxValue / 2);
    }

    /// <summary>
    /// Writes stress-test craft files for the in-game check (docs/TESTING-IN-GAME.md,
    /// P2.4) when KERBLOX_STRESS_CRAFT_DIR is set; does nothing otherwise. Sizes come
    /// from KERBLOX_STRESS_SIZES (comma-separated cube edges, default 64).
    /// </summary>
    [Fact]
    public void WriteStressCraftFiles()
    {
        string? dir = Environment.GetEnvironmentVariable("KERBLOX_STRESS_CRAFT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        string sizes = Environment.GetEnvironmentVariable("KERBLOX_STRESS_SIZES") ?? "64";
        BlockRegistry registry = BlockRegistry.CreateDefault();

        foreach (int size in sizes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture)))
            foreach (int paletteSize in new[] { 4, 65535 })
            {
                VoxelGrid grid = StressGrids.Noisy(size, paletteSize);
                string text = GridCodec.ToText(grid);
                double mass = MassProperties.Compute(grid, registry, new BlockLayout(grid, 0.625f)).Mass;
                string name = $"Kerblox stress {size}-{paletteSize}";
                File.WriteAllText(Path.Combine(dir, name + ".craft"), StressGrids.Craft(name, text, mass));
                output.WriteLine($"{name}.craft: gridData {text.Length:N0} chars");
            }
    }
}
