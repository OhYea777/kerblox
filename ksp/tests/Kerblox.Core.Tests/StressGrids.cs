using System.Globalization;
using System.Text;
using Kerblox.Core;

namespace Kerblox.Core.Tests;

/// <summary>
/// Grids built to make <see cref="GridCodec"/> output as long as possible, for
/// testing how big a <c>gridData</c> value KSP's craft and save files tolerate.
/// </summary>
internal static class StressGrids
{
    /// <summary>Solid, opaque blocks (no glass), so the mesh stays a hull and every cell has mass.</summary>
    private static readonly string[] Bases =
        { "minecraft:stone", "minecraft:oak_planks", "minecraft:iron_block", "minecraft:white_wool" };

    /// <summary>
    /// <paramref name="paletteSize"/> distinct non-air states. Variants of real blocks with
    /// a dummy property, so the registry still treats them as solid stone, planks, etc.
    /// </summary>
    public static BlockState[] Palette(int paletteSize)
    {
        var states = new BlockState[paletteSize];
        for (int i = 0; i < paletteSize; i++)
            states[i] = paletteSize <= Bases.Length
                ? BlockState.Parse(Bases[i])
                : BlockState.Parse($"{Bases[i % Bases.Length]}[kerblox_noise={i.ToString(CultureInfo.InvariantCulture)}]");
        return states;
    }

    /// <summary>
    /// A completely filled cube where no cell repeats its predecessor in storage order,
    /// so run-length encoding gains nothing, and every palette entry is used. Palette
    /// indices are spread at random, so most need the widest varint the palette allows.
    /// Deterministic for a given seed.
    /// </summary>
    public static VoxelGrid Noisy(int size, int paletteSize, int seed = 1)
    {
        if (paletteSize < 2) throw new ArgumentOutOfRangeException(nameof(paletteSize));
        BlockState[] palette = Palette(paletteSize);
        var grid = new VoxelGrid(size, size, size);
        var rng = new Random(seed);
        int prev = -1, i = 0;
        for (int y = 0; y < size; y++)
            for (int z = 0; z < size; z++)
                for (int x = 0; x < size; x++, i++)
                {
                    // Walk the palette first so every entry appears, then pick at random.
                    int next = i < paletteSize ? i : rng.Next(paletteSize);
                    if (next == prev) next = (next + 1) % paletteSize;
                    grid.Set(x, y, z, palette[next]);
                    prev = next;
                }
        return grid;
    }

    /// <summary>
    /// Upper bound on <see cref="GridCodec.ToText"/> length for a grid of this volume
    /// and palette: header, palette entries, and one (run, index) pair per cell with
    /// the widest varints, as base64 (4 chars per 3 bytes).
    /// </summary>
    public static long MaxTextLength(long volume, IReadOnlyList<BlockState> palette)
    {
        long bytes = 4 + 1 + 6 + VarintLength((ulong)palette.Count);
        foreach (BlockState s in palette)
        {
            int len = Encoding.UTF8.GetByteCount(s.Canonical);
            bytes += VarintLength((ulong)len) + len;
        }
        bytes += volume * (1 + VarintLength((ulong)palette.Count - 1));
        return (bytes * 4 + 2) / 3;
    }

    public static int VarintLength(ulong v)
    {
        int n = 1;
        while (v >= 0x80) { v >>= 7; n++; }
        return n;
    }

    /// <summary>
    /// A minimal VAB craft file with one Kerblox grid part as the root, laid out the
    /// way <c>ShipConstruct.SaveShip</c> writes one (a real 1.12.5 craft was the
    /// template). Loadable from the editor's Load menu.
    /// </summary>
    public static string Craft(string shipName, string gridData, double modMass)
    {
        var sb = new StringBuilder();
        void Line(string indent, string text) => sb.Append(indent).Append(text).Append("\r\n");
        string m = modMass.ToString("R", CultureInfo.InvariantCulture);

        Line("", $"ship = {shipName}");
        Line("", "version = 1.12.5");
        Line("", "description = Kerblox gridData stress test (P2.4)");
        Line("", "type = VAB");
        Line("", "size = 1,1,1");
        Line("", "steamPublishedFileId = 0");
        Line("", "persistentId = 0");
        Line("", "rot = 0,0,0,0");
        Line("", "missionFlag = Squad/Flags/default");
        Line("", "vesselType = Debris");
        Line("", "PART");
        Line("", "{");
        string t = "\t";
        Line(t, "part = kerbloxBlockGrid_4294000001");
        Line(t, "partName = Part");
        Line(t, "persistentId = 0");
        Line(t, "pos = 0,15,0");
        Line(t, "attPos = 0,0,0");
        Line(t, "attPos0 = 0,15,0");
        Line(t, "rot = 0,0,0,1");
        Line(t, "attRot = 0,0,0,1");
        Line(t, "attRot0 = 0,0,0,1");
        Line(t, "mir = 1,1,1");
        Line(t, "symMethod = Radial");
        Line(t, "autostrutMode = Off");
        Line(t, "rigidAttachment = False");
        Line(t, "istg = -1");
        Line(t, "resPri = 0");
        Line(t, "dstg = 0");
        Line(t, "sidx = -1");
        Line(t, "sqor = -1");
        Line(t, "sepI = -1");
        Line(t, "attm = 0");
        Line(t, "sameVesselCollision = False");
        Line(t, "modCost = 0");
        Line(t, $"modMass = {m}");
        Line(t, "modSize = 0,0,0");
        Line(t, "MODULE");
        Line(t, "{");
        Line(t + t, "name = ModuleBlockGrid");
        Line(t + t, "isEnabled = True");
        Line(t + t, $"gridData = {gridData}");
        Line(t + t, "stagingEnabled = True");
        Line(t, "}");
        Line("", "}");
        return sb.ToString();
    }
}
