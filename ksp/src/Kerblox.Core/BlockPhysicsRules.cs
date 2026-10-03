using System;
using System.Collections.Generic;
using System.Globalization;

namespace Kerblox.Core
{
    /// <summary>The physical properties of one block, as used for mass and collision.</summary>
    public readonly struct BlockPhysics : IEquatable<BlockPhysics>
    {
        /// <summary>Unknown blocks with no matching rule behave like air.</summary>
        public static readonly BlockPhysics None = new BlockPhysics(0, false);

        /// <summary>Tonnes per cubic metre (KSP mass units).</summary>
        public readonly double Density;

        /// <summary>Contributes mass and collision.</summary>
        public readonly bool Solid;

        public BlockPhysics(double density, bool solid)
        {
            Density = density; Solid = solid;
        }

        /// <summary>Mass of one block of this kind; zero unless solid.</summary>
        public double Mass(double blockVolume) => Solid ? Density * blockVolume : 0;

        public bool Equals(BlockPhysics other) => Density.Equals(other.Density) && Solid == other.Solid;
        public override bool Equals(object obj) => obj is BlockPhysics other && Equals(other);
        public override int GetHashCode() => Density.GetHashCode() * 2 + (Solid ? 1 : 0);
        public override string ToString() => $"density {Density.ToString(CultureInfo.InvariantCulture)}, solid {Solid}";
    }

    /// <summary>
    /// One <c>KERBLOX_BLOCK {}</c> config node, reduced to its key/value pairs in file
    /// order. The plugin builds these from KSP's ConfigNode so Core never sees KSP types.
    /// </summary>
    public sealed class BlockPhysicsNode
    {
        /// <summary>Where the node came from (e.g. a GameData url), for warnings only.</summary>
        public string Source { get; }

        public IList<KeyValuePair<string, string>> Values { get; }

        public BlockPhysicsNode(string source, IList<KeyValuePair<string, string>> values)
        {
            Source = source ?? "";
            Values = values ?? throw new ArgumentNullException(nameof(values));
        }
    }

    /// <summary>
    /// Per-block physical properties loaded from <c>KERBLOX_BLOCK</c> nodes:
    /// <code>
    /// KERBLOX_BLOCK
    /// {
    ///     name = mekanism:*     // exact block id, "namespace:*", or "*"
    ///     density = 0.5         // t/m³, finite and non-negative
    ///     solid = true
    /// }
    /// </code>
    /// Rules match the block id only; block-state properties are ignored. Each field
    /// resolves on its own, most specific rule first: exact id, then namespace
    /// wildcard, then <c>*</c>, then the caller's fallback (the built-in values for a
    /// registered block, <see cref="BlockPhysics.None"/> for an unknown one). So a
    /// rule that sets only <c>density</c> keeps the solidity from a broader rule.
    /// When several nodes name the same pattern, later ones override earlier ones
    /// field by field, which lets a second cfg patch the shipped one without
    /// ModuleManager.
    /// </summary>
    public sealed class BlockPhysicsRules
    {
        public const string NodeName = "KERBLOX_BLOCK";
        public const string Wildcard = "*";

        public static readonly BlockPhysicsRules Empty = new BlockPhysicsRules();

        private sealed class Rule
        {
            public double? Density;
            public bool? Solid;
        }

        private readonly Dictionary<string, Rule> rules = new Dictionary<string, Rule>(StringComparer.Ordinal);

        /// <summary>Number of distinct patterns with at least one field set.</summary>
        public int Count => rules.Count;

        private BlockPhysicsRules() { }

        /// <summary>
        /// Parses rule nodes. Malformed nodes or values never throw: they're skipped
        /// and described in <paramref name="warnings"/> (if given), so one bad cfg
        /// can't stop parts from loading.
        /// </summary>
        public static BlockPhysicsRules Parse(IEnumerable<BlockPhysicsNode> nodes, ICollection<string> warnings = null)
        {
            if (nodes == null) throw new ArgumentNullException(nameof(nodes));
            var result = new BlockPhysicsRules();
            foreach (BlockPhysicsNode node in nodes)
                result.ParseNode(node, warnings);
            return result;
        }

        private void ParseNode(BlockPhysicsNode node, ICollection<string> warnings)
        {
            string where = node.Source.Length > 0 ? $"{NodeName} in {node.Source}" : NodeName;
            void Warn(string msg) => warnings?.Add($"{where}: {msg}");

            string rawName = null;
            foreach (var kv in node.Values)
                if (kv.Key == "name")
                {
                    if (rawName != null) Warn($"more than one name; using '{kv.Value}'");
                    rawName = kv.Value;
                }
            if (string.IsNullOrWhiteSpace(rawName))
            {
                Warn("no name; node ignored");
                return;
            }

            string pattern = NormalizePattern(rawName.Trim(), out string error);
            if (pattern == null)
            {
                Warn($"name '{rawName}' {error}; node ignored");
                return;
            }
            where = $"{NodeName} '{pattern}'" + (node.Source.Length > 0 ? $" in {node.Source}" : "");

            var parsed = new Rule();
            foreach (var kv in node.Values)
            {
                string value = kv.Value?.Trim() ?? "";
                switch (kv.Key)
                {
                    case "name":
                        break;
                    case "density":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                            && !double.IsNaN(d) && !double.IsInfinity(d) && d >= 0)
                            parsed.Density = d;
                        else
                            Warn($"density '{kv.Value}' is not a finite non-negative number; ignored");
                        break;
                    case "solid":
                        if (bool.TryParse(value, out bool s)) parsed.Solid = s;
                        else Warn($"solid '{kv.Value}' is not true or false; ignored");
                        break;
                    default:
                        Warn($"unknown key '{kv.Key}' ignored");
                        break;
                }
            }

            if (parsed.Density == null && parsed.Solid == null)
            {
                Warn("sets no valid density or solid; node ignored");
                return;
            }

            if (rules.TryGetValue(pattern, out Rule existing))
            {
                Warn("overrides an earlier rule for the same name");
                if (parsed.Density != null) existing.Density = parsed.Density;
                if (parsed.Solid != null) existing.Solid = parsed.Solid;
            }
            else
            {
                rules.Add(pattern, parsed);
            }
        }

        /// <summary>
        /// Canonical pattern, or null with a reason. A bare path gets the
        /// <c>minecraft</c> namespace, as in Minecraft's own commands.
        /// </summary>
        private static string NormalizePattern(string name, out string error)
        {
            error = null;
            if (name == Wildcard) return Wildcard;

            if (name.EndsWith(":*", StringComparison.Ordinal))
            {
                string ns = name.Substring(0, name.Length - 2);
                // Validate the namespace by parsing a dummy id in it.
                if (!BlockState.TryParse(ns + ":x", out _) || ns.IndexOf(':') >= 0 || ns.IndexOf('[') >= 0)
                {
                    error = "has an invalid namespace";
                    return null;
                }
                return ns + ":*";
            }

            if (name.IndexOf('*') >= 0)
            {
                error = "uses '*' other than as '*' or 'namespace:*'";
                return null;
            }
            if (!BlockState.TryParse(name, out BlockState state))
            {
                error = "is not a valid block id";
                return null;
            }
            if (state.HasProperties)
            {
                error = "has block-state properties, which rules don't match on";
                return null;
            }
            if (state.IsAir)
            {
                error = "is air, which is never solid";
                return null;
            }
            return state.Name;
        }

        /// <summary>
        /// Physics for a block id (properties, if any, are ignored), each field taken
        /// from the most specific rule that sets it, else from <paramref name="fallback"/>.
        /// Air is always <see cref="BlockPhysics.None"/>.
        /// </summary>
        public BlockPhysics Resolve(string blockName, BlockPhysics fallback)
        {
            if (blockName == null) throw new ArgumentNullException(nameof(blockName));
            int bracket = blockName.IndexOf('[');
            if (bracket >= 0) blockName = blockName.Substring(0, bracket);
            if (blockName == BlockState.AirName) return BlockPhysics.None;

            double? density = null;
            bool? solid = null;
            Apply(blockName, ref density, ref solid);
            int colon = blockName.IndexOf(':');
            if (colon > 0) Apply(blockName.Substring(0, colon) + ":*", ref density, ref solid);
            Apply(Wildcard, ref density, ref solid);

            return new BlockPhysics(density ?? fallback.Density, solid ?? fallback.Solid);
        }

        public BlockPhysics Resolve(BlockState state, BlockPhysics fallback) =>
            state.IsAir ? BlockPhysics.None : Resolve(state.Name, fallback);

        private void Apply(string pattern, ref double? density, ref bool? solid)
        {
            if (!rules.TryGetValue(pattern, out Rule r)) return;
            if (density == null) density = r.Density;
            if (solid == null) solid = r.Solid;
        }
    }
}
