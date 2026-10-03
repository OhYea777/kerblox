using Kerblox.Core;

namespace Kerblox.Core.Tests;

public class BlockStateTests
{
    [Theory]
    [InlineData("minecraft:stone", "minecraft:stone")]
    [InlineData("stone", "minecraft:stone")]
    [InlineData("minecraft:stone[]", "minecraft:stone")]
    [InlineData("minecraft:oak_stairs[waterlogged=false,half=bottom,facing=north,shape=straight]",
                "minecraft:oak_stairs[facing=north,half=bottom,shape=straight,waterlogged=false]")]
    [InlineData("mekanism:basic_energy_cube[active=true]", "mekanism:basic_energy_cube[active=true]")]
    [InlineData("create:fluid_pipe[up=true,down=false]", "create:fluid_pipe[down=false,up=true]")]
    [InlineData("mod-x.y:sub/block_1", "mod-x.y:sub/block_1")]
    public void ParsesToCanonicalForm(string text, string canonical) =>
        Assert.Equal(canonical, BlockState.Parse(text).Canonical);

    [Fact]
    public void PropertyOrderDoesNotAffectEquality()
    {
        var a = BlockState.Parse("minecraft:repeater[delay=2,facing=east,locked=false,powered=true]");
        var b = BlockState.Parse("minecraft:repeater[powered=true,locked=false,facing=east,delay=2]");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal("minecraft:repeater", a.Name);
        Assert.Equal(new[] { "delay", "facing", "locked", "powered" }, a.Properties.Select(p => p.Key));
        Assert.True(a.TryGetProperty("facing", out string facing));
        Assert.Equal("east", facing);
        Assert.False(a.TryGetProperty("mode", out _));
    }

    [Fact]
    public void OfSortsProperties()
    {
        var s = BlockState.Of("minecraft:oak_log", new Dictionary<string, string> { ["axis"] = "x" });
        Assert.Equal("minecraft:oak_log[axis=x]", s.Canonical);
        Assert.Equal(BlockState.Parse("minecraft:lever[powered=true,face=wall]"),
                     BlockState.Of("minecraft:lever", new[] { KeyValuePair.Create("powered", "true"), KeyValuePair.Create("face", "wall") }));
    }

    [Fact]
    public void AirIsDefaultAndParsedAirIsTheSame()
    {
        Assert.True(default(BlockState).IsAir);
        Assert.Equal(BlockState.Air, BlockState.Parse("minecraft:air"));
        Assert.Equal(BlockState.Air, BlockState.Parse("air"));
        Assert.Equal("minecraft:air", BlockState.Air.Canonical);
        Assert.Equal("minecraft:air", BlockState.Air.Name);
        Assert.False(BlockState.Air.HasProperties);
        Assert.False(BlockState.Parse("minecraft:cave_air").IsAir); // only minecraft:air is palette index 0
    }

    [Theory]
    [InlineData("")]
    [InlineData(":stone")]
    [InlineData("minecraft:")]
    [InlineData("Minecraft:Stone")]
    [InlineData("minecraft:stone[")]
    [InlineData("minecraft:stone[facing]")]
    [InlineData("minecraft:stone[facing=]")]
    [InlineData("minecraft:stone[=north]")]
    [InlineData("minecraft:stone[a=1,a=2]")]
    [InlineData("minecraft:stone[a=1]x")]
    [InlineData("minecraft:stone[a=1,]")]
    [InlineData("minecraft:stone[a=1 ]")]
    [InlineData("a:b:c")]
    public void RejectsMalformed(string text)
    {
        Assert.Throws<FormatException>(() => BlockState.Parse(text));
        Assert.False(BlockState.TryParse(text, out _));
    }
}
