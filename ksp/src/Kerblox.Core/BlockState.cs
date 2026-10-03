using System;
using System.Collections.Generic;
using System.Text;

namespace Kerblox.Core
{
    /// <summary>
    /// One voxel's contents: a Minecraft block state in canonical string form,
    /// <c>namespace:block[prop=value,...]</c> with properties sorted by name and the
    /// brackets omitted when there are none. Using Minecraft's own notation lets
    /// arbitrary modded blocks round-trip through the bridge without a shared id table.
    ///
    /// Grids don't store these per cell: <see cref="VoxelGrid"/> keeps a palette of
    /// distinct states and cells hold palette indices, as Minecraft's chunk sections do.
    /// Equality is ordinal on the canonical string, so two states are equal exactly
    /// when Minecraft would consider them the same state.
    /// </summary>
    public readonly struct BlockState : IEquatable<BlockState>
    {
        public const string AirName = "minecraft:air";
        public const string DefaultNamespace = "minecraft";

        /// <summary><c>default(BlockState)</c> is air, so a zeroed array of states is all air.</summary>
        public static readonly BlockState Air = default;

        private readonly string canonical; // null means air
        private readonly int nameLength;   // length of the "ns:block" prefix

        private BlockState(string canonical, int nameLength)
        {
            this.canonical = canonical;
            this.nameLength = nameLength;
        }

        /// <summary>The full canonical state string, e.g. <c>minecraft:oak_log[axis=y]</c>.</summary>
        public string Canonical => canonical ?? AirName;

        /// <summary>The namespaced block id without properties, e.g. <c>minecraft:oak_log</c>.</summary>
        public string Name => canonical == null ? AirName : nameLength == canonical.Length ? canonical : canonical.Substring(0, nameLength);

        public bool IsAir => canonical == null;

        public bool HasProperties => canonical != null && nameLength != canonical.Length;

        /// <summary>Property pairs in canonical (name-sorted) order.</summary>
        public IList<KeyValuePair<string, string>> Properties
        {
            get
            {
                var list = new List<KeyValuePair<string, string>>();
                if (!HasProperties) return list;
                string body = canonical.Substring(nameLength + 1, canonical.Length - nameLength - 2);
                foreach (string pair in body.Split(','))
                {
                    int eq = pair.IndexOf('=');
                    list.Add(new KeyValuePair<string, string>(pair.Substring(0, eq), pair.Substring(eq + 1)));
                }
                return list;
            }
        }

        public bool TryGetProperty(string name, out string value)
        {
            foreach (var p in Properties)
                if (p.Key == name) { value = p.Value; return true; }
            value = null;
            return false;
        }

        /// <summary>
        /// Parses Minecraft block-state notation and canonicalises it: a missing
        /// namespace becomes <c>minecraft</c>, properties are sorted, and empty
        /// brackets are dropped. Throws <see cref="FormatException"/> on anything
        /// Minecraft's resource-location rules wouldn't accept.
        /// </summary>
        public static BlockState Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            string error = TryParseCore(text, out BlockState state);
            if (error != null) throw new FormatException($"Invalid block state '{text}': {error}");
            return state;
        }

        public static bool TryParse(string text, out BlockState state)
        {
            if (text != null && TryParseCore(text, out state) == null) return true;
            state = Air;
            return false;
        }

        /// <summary>Builds a state from a block id and unordered properties.</summary>
        public static BlockState Of(string name, IEnumerable<KeyValuePair<string, string>> properties = null)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            var sb = new StringBuilder(name);
            if (properties != null)
            {
                bool first = true;
                foreach (var p in properties)
                {
                    sb.Append(first ? '[' : ',').Append(p.Key).Append('=').Append(p.Value);
                    first = false;
                }
                if (!first) sb.Append(']');
            }
            return Parse(sb.ToString());
        }

        private static string TryParseCore(string text, out BlockState state)
        {
            state = Air;
            int open = text.IndexOf('[');
            string id = open < 0 ? text : text.Substring(0, open);

            int colon = id.IndexOf(':');
            string ns = colon < 0 ? DefaultNamespace : id.Substring(0, colon);
            string path = colon < 0 ? id : id.Substring(colon + 1);
            if (ns.Length == 0 || !AllValid(ns, allowSlash: false)) return "bad namespace";
            if (path.Length == 0 || !AllValid(path, allowSlash: true)) return "bad block path";
            string name = ns + ":" + path;

            var props = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (open >= 0)
            {
                if (text[text.Length - 1] != ']') return "missing ']'";
                string body = text.Substring(open + 1, text.Length - open - 2);
                if (body.Length > 0)
                {
                    foreach (string pair in body.Split(','))
                    {
                        int eq = pair.IndexOf('=');
                        if (eq <= 0 || eq == pair.Length - 1) return $"bad property '{pair}'";
                        string key = pair.Substring(0, eq), value = pair.Substring(eq + 1);
                        if (!AllPropertyChars(key) || !AllPropertyChars(value)) return $"bad property '{pair}'";
                        if (props.ContainsKey(key)) return $"duplicate property '{key}'";
                        props.Add(key, value);
                    }
                }
            }

            // Matches Minecraft, whose air block defines no properties.
            if (name == AirName && props.Count > 0) return "air has no properties";

            if (props.Count == 0)
            {
                state = name == AirName ? Air : new BlockState(name, name.Length);
                return null;
            }

            var sb = new StringBuilder(name).Append('[');
            bool first = true;
            foreach (var p in props)
            {
                if (!first) sb.Append(',');
                sb.Append(p.Key).Append('=').Append(p.Value);
                first = false;
            }
            sb.Append(']');
            state = new BlockState(sb.ToString(), name.Length);
            return null;
        }

        // Minecraft ResourceLocation: [a-z0-9_.-] in the namespace, plus '/' in the path.
        private static bool AllValid(string s, bool allowSlash)
        {
            foreach (char c in s)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-' || (allowSlash && c == '/')))
                    return false;
            return true;
        }

        // Minecraft property names and enum values: [a-z0-9_].
        private static bool AllPropertyChars(string s)
        {
            foreach (char c in s)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
                    return false;
            return true;
        }

        public bool Equals(BlockState other) => string.Equals(canonical, other.canonical, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is BlockState other && Equals(other);
        public override int GetHashCode() => canonical == null ? 0 : StringComparer.Ordinal.GetHashCode(canonical);
        public static bool operator ==(BlockState a, BlockState b) => a.Equals(b);
        public static bool operator !=(BlockState a, BlockState b) => !a.Equals(b);
        public override string ToString() => Canonical;
    }
}
