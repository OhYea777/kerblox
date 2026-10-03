using Kerblox.Core;

namespace Kerblox.Core.Tests;

public class BlockPhysicsRulesTests
{
    private static BlockPhysicsNode Node(params string[] kv)
    {
        var values = new List<KeyValuePair<string, string>>();
        for (int i = 0; i < kv.Length; i += 2) values.Add(new(kv[i], kv[i + 1]));
        return new BlockPhysicsNode("test.cfg", values);
    }

    private static BlockPhysicsRules Parse(List<string> warnings, params BlockPhysicsNode[] nodes) =>
        BlockPhysicsRules.Parse(nodes, warnings);

    private static BlockPhysicsRules Parse(params BlockPhysicsNode[] nodes) => BlockPhysicsRules.Parse(nodes);

    private static readonly BlockPhysics Fallback = new(9, false);

    [Fact]
    public void ExactBeatsNamespaceBeatsGlobal()
    {
        var rules = Parse(
            Node("name", "*", "density", "0.25", "solid", "true"),
            Node("name", "mekanism:*", "density", "0.5"),
            Node("name", "mekanism:steel_casing", "density", "0.8"));

        Assert.Equal(new BlockPhysics(0.8, true), rules.Resolve("mekanism:steel_casing", Fallback));
        Assert.Equal(new BlockPhysics(0.5, true), rules.Resolve("mekanism:other", Fallback));
        Assert.Equal(new BlockPhysics(0.25, true), rules.Resolve("create:shaft", Fallback));
    }

    [Fact]
    public void PrecedenceDoesNotDependOnNodeOrder()
    {
        var rules = Parse(
            Node("name", "mekanism:steel_casing", "density", "0.8"),
            Node("name", "mekanism:*", "density", "0.5"),
            Node("name", "*", "density", "0.25"));
        Assert.Equal(0.8, rules.Resolve("mekanism:steel_casing", Fallback).Density);
    }

    [Fact]
    public void FieldsResolveIndependently()
    {
        var rules = Parse(
            Node("name", "*", "solid", "true"),
            Node("name", "mekanism:*", "density", "0.5"),
            Node("name", "mekanism:pipe", "solid", "false"));

        Assert.Equal(new BlockPhysics(0.5, false), rules.Resolve("mekanism:pipe", Fallback));
        Assert.Equal(new BlockPhysics(0.5, true), rules.Resolve("mekanism:block", Fallback));
        // Density comes from nowhere, so the fallback's.
        Assert.Equal(new BlockPhysics(9, true), rules.Resolve("create:shaft", Fallback));
    }

    [Fact]
    public void NoMatchingRuleUsesFallback()
    {
        var rules = Parse(Node("name", "mekanism:*", "density", "0.5"));
        Assert.Equal(Fallback, rules.Resolve("create:shaft", Fallback));
        Assert.Equal(Fallback, BlockPhysicsRules.Empty.Resolve("minecraft:stone", Fallback));
    }

    [Fact]
    public void PropertiesAreIgnoredWhenMatching()
    {
        var rules = Parse(Node("name", "minecraft:oak_log", "density", "0.1"));
        Assert.Equal(0.1, rules.Resolve(BlockState.Parse("minecraft:oak_log[axis=y]"), Fallback).Density);
        Assert.Equal(0.1, rules.Resolve("minecraft:oak_log[axis=x]", Fallback).Density);
    }

    [Fact]
    public void BarePathGetsMinecraftNamespace()
    {
        var rules = Parse(Node("name", "stone", "density", "0.4"));
        Assert.Equal(0.4, rules.Resolve("minecraft:stone", Fallback).Density);
    }

    [Fact]
    public void AirIsNeverAffected()
    {
        var warnings = new List<string>();
        var rules = Parse(warnings,
            Node("name", "*", "density", "1", "solid", "true"),
            Node("name", "minecraft:air", "solid", "true"));
        Assert.Equal(BlockPhysics.None, rules.Resolve(BlockState.Air, Fallback));
        Assert.Equal(BlockPhysics.None, rules.Resolve("minecraft:air", Fallback));
        Assert.Single(warnings);
    }

    [Fact]
    public void LaterNodeForSamePatternOverridesFieldByField()
    {
        var warnings = new List<string>();
        var rules = Parse(warnings,
            Node("name", "minecraft:stone", "density", "0.3", "solid", "true"),
            Node("name", "minecraft:stone", "density", "0.9"));
        Assert.Equal(new BlockPhysics(0.9, true), rules.Resolve("minecraft:stone", Fallback));
        Assert.Equal(1, rules.Count);
        Assert.Contains(warnings, w => w.Contains("overrides"));
    }

    [Fact]
    public void ParsesKspStyleValues()
    {
        // KSP writes booleans as True/False and users may pad values.
        var rules = Parse(Node("name", " minecraft:stone ", "density", " 1.5e-1 ", "solid", "False"));
        Assert.Equal(new BlockPhysics(0.15, false), rules.Resolve("minecraft:stone", Fallback));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0,5")]
    [InlineData("")]
    public void MalformedDensityIsIgnoredButRestOfNodeApplies(string density)
    {
        var warnings = new List<string>();
        var rules = Parse(warnings, Node("name", "minecraft:stone", "density", density, "solid", "true"));
        Assert.Equal(new BlockPhysics(9, true), rules.Resolve("minecraft:stone", Fallback));
        Assert.Single(warnings);
        Assert.Contains("density", warnings[0]);
        Assert.Contains("test.cfg", warnings[0]);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    public void MalformedSolidIsIgnored(string solid)
    {
        var warnings = new List<string>();
        var rules = Parse(warnings, Node("name", "minecraft:stone", "density", "0.3", "solid", solid));
        Assert.Equal(new BlockPhysics(0.3, false), rules.Resolve("minecraft:stone", Fallback));
        Assert.Single(warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Minecraft:Stone")]
    [InlineData("minecraft:st*")]
    [InlineData("*:stone")]
    [InlineData("bad ns:*")]
    [InlineData(":*")]
    [InlineData("minecraft:oak_log[axis=y]")]
    public void BadNamesSkipTheNode(string name)
    {
        var warnings = new List<string>();
        var rules = Parse(warnings, Node("name", name, "density", "1"));
        Assert.Equal(0, rules.Count);
        Assert.Single(warnings);
    }

    [Fact]
    public void MissingNameSkipsTheNode()
    {
        var warnings = new List<string>();
        var rules = Parse(warnings, Node("density", "1"));
        Assert.Equal(0, rules.Count);
        Assert.Single(warnings);
    }

    [Fact]
    public void NodeWithNoValidFieldsIsSkipped()
    {
        var warnings = new List<string>();
        var rules = Parse(warnings, Node("name", "minecraft:stone", "density", "heavy", "mass", "1"));
        Assert.Equal(0, rules.Count);
        Assert.Equal(3, warnings.Count); // bad density, unknown key, nothing set
    }

    [Fact]
    public void WarningsAreOptional()
    {
        var rules = BlockPhysicsRules.Parse(new[] { Node("name", "minecraft:stone", "density", "x") });
        Assert.Equal(0, rules.Count);
    }

    [Fact]
    public void NonSolidBlocksHaveNoMass()
    {
        Assert.Equal(0, new BlockPhysics(5, false).Mass(1));
        Assert.Equal(2.5, new BlockPhysics(5, true).Mass(0.5));
    }
}

public class BlockRegistryPhysicsTests
{
    private static readonly BlockState Stone = BlockState.Parse("minecraft:stone");
    private static readonly BlockState Iron = BlockState.Parse("minecraft:iron_block");
    private static readonly BlockState Mystery = BlockState.Parse("somemod:mystery_block[facing=north]");

    private static BlockPhysicsRules Rules(params (string Name, string? Density, string? Solid)[] rules)
    {
        var nodes = rules.Select(r =>
        {
            var v = new List<KeyValuePair<string, string>> { new("name", r.Name) };
            if (r.Density != null) v.Add(new("density", r.Density));
            if (r.Solid != null) v.Add(new("solid", r.Solid));
            return new BlockPhysicsNode("", v);
        });
        return BlockPhysicsRules.Parse(nodes);
    }

    [Fact]
    public void WithoutRulesUnknownBlocksAreAirAndKnownKeepBuiltIns()
    {
        var r = BlockRegistry.CreateDefault();
        Assert.Equal(BlockPhysics.None, r.GetPhysics(Mystery));
        Assert.False(r.IsSolid(Mystery));
        Assert.Equal(new BlockPhysics(0.30, true), r.GetPhysics(Stone));
        Assert.Equal(BlockPhysics.None, r.GetPhysics(BlockState.Air));
    }

    [Fact]
    public void RulesUpdateRegisteredTypes()
    {
        var r = BlockRegistry.CreateDefault();
        r.ApplyPhysics(Rules(("minecraft:stone", "0.9", null)));
        // Get(...).Density is what P1.5's InertiaTensor reads, so it must follow the rules.
        Assert.Equal(0.9, r.Get(Stone)!.Density);
        Assert.True(r.Get(Stone)!.Solid);
        Assert.Equal(0.60, r.Get(Iron)!.Density);
        Assert.Equal(0.9, r.BlockMass(Stone, 1));
    }

    [Fact]
    public void ReapplyingStartsFromBuiltIns()
    {
        var r = BlockRegistry.CreateDefault();
        r.ApplyPhysics(Rules(("minecraft:stone", "0.9", "false")));
        r.ApplyPhysics(BlockPhysicsRules.Empty);
        Assert.Equal(new BlockPhysics(0.30, true), r.GetPhysics(Stone));
    }

    [Fact]
    public void UnknownBlocksUseWildcardRules()
    {
        var r = BlockRegistry.CreateDefault();
        r.ApplyPhysics(Rules(("*", "0.25", "true"), ("somemod:*", "0.5", null)));
        Assert.Equal(new BlockPhysics(0.5, true), r.GetPhysics(Mystery));
        Assert.True(r.IsSolid(Mystery));
        Assert.Null(r.Get(Mystery)); // still not a renderable type
        // A global rule also covers built-ins it doesn't name more specifically.
        Assert.Equal(0.25, r.GetPhysics(Stone).Density);
    }

    [Fact]
    public void TypesRegisteredAfterApplyGetRules()
    {
        var r = new BlockRegistry();
        r.ApplyPhysics(Rules(("test:*", "2", null)));
        var t = r.Register(new BlockType("test:block", 1, true, true, 0, 0, 0));
        Assert.Equal(2, t.Density);
        Assert.Equal(1, t.BuiltInPhysics.Density);
    }

    [Fact]
    public void MassAndCollidersFollowRules()
    {
        var g = new VoxelGrid(2, 1, 1);
        g.Set(0, 0, 0, Stone);
        g.Set(1, 0, 0, Mystery);
        var layout = new BlockLayout(g, 1f);
        var r = BlockRegistry.CreateDefault();

        Assert.Equal(0.30, MassProperties.Compute(g, r, layout).Mass, 9);
        Assert.Single(BoxMerger.Merge(g, r));

        r.ApplyPhysics(Rules(("minecraft:stone", "1", null), ("*", "0.5", "true")));
        var p = MassProperties.Compute(g, r, layout);
        Assert.Equal(1.5, p.Mass, 9);
        Assert.Equal(2, p.SolidBlocks);
        int cells = BoxMerger.Merge(g, r).Sum(b => b.Volume);
        Assert.Equal(2, cells);
    }
}

public class ShippedBlocksCfgTests
{
    [Fact]
    public void ShippedCfgMatchesBuiltInDefaults()
    {
        // Minimal reader for the flat KERBLOX_BLOCK nodes in Blocks.cfg; KSP's own
        // parser is what runs in game.
        string path = Path.Combine(FindRepoRoot(), "ksp", "GameData", "Kerblox", "Blocks.cfg");
        var nodes = new List<BlockPhysicsNode>();
        List<KeyValuePair<string, string>>? cur = null;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Split("//")[0].Trim();
            if (line == "{") cur = new();
            else if (line == "}") { nodes.Add(new BlockPhysicsNode(path, cur!)); cur = null; }
            else if (cur != null && line.Contains('='))
            {
                int eq = line.IndexOf('=');
                cur.Add(new(line[..eq].Trim(), line[(eq + 1)..].Trim()));
            }
        }

        var warnings = new List<string>();
        var rules = BlockPhysicsRules.Parse(nodes, warnings);
        Assert.Empty(warnings);

        var configured = BlockRegistry.CreateDefault();
        configured.ApplyPhysics(rules);
        foreach (BlockType t in BlockRegistry.CreateDefault().Types)
            Assert.Equal(t.Physics, configured.Get(t.Name)!.Physics);
        Assert.True(configured.IsSolid(BlockState.Parse("somemod:anything")));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ROADMAP.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
