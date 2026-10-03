using System.Collections.Generic;
using Kerblox.Core;

namespace Kerblox
{
    /// <summary>
    /// Adapts <c>KERBLOX_BLOCK</c> nodes from KSP's GameDatabase to
    /// <see cref="BlockPhysicsRules"/>. All parsing and precedence live in Core.
    ///
    /// Timing (verified against KSP 1.12.5): PartLoader compiles parts from
    /// <c>GameDatabase.Instance.GetConfigs("PART")</c>, so the database has loaded
    /// every GameData cfg before the first <see cref="ModuleBlockGrid"/> OnLoad asks
    /// for the registry.
    /// </summary>
    internal static class BlockConfig
    {
        /// <summary>
        /// The default registry with GameData rules applied, or null if the
        /// GameDatabase doesn't exist yet (callers should retry later).
        /// </summary>
        public static BlockRegistry CreateRegistry()
        {
            if (GameDatabase.Instance == null)
            {
                Log.Warn("GameDatabase not ready; using built-in block physics for now");
                return null;
            }

            var nodes = new List<BlockPhysicsNode>();
            foreach (UrlDir.UrlConfig cfg in GameDatabase.Instance.GetConfigs(BlockPhysicsRules.NodeName))
            {
                var values = new List<KeyValuePair<string, string>>();
                foreach (ConfigNode.Value v in cfg.config.values)
                    values.Add(new KeyValuePair<string, string>(v.name, v.value));
                nodes.Add(new BlockPhysicsNode(cfg.parent.url, values));
            }

            var warnings = new List<string>();
            BlockPhysicsRules rules = BlockPhysicsRules.Parse(nodes, warnings);
            foreach (string w in warnings) Log.Warn(w);

            var registry = BlockRegistry.CreateDefault();
            registry.ApplyPhysics(rules);
            Log.Info($"Loaded {rules.Count} block physics rules from {nodes.Count} {BlockPhysicsRules.NodeName} nodes");
            foreach (BlockType t in registry.Types)
                Log.Info($"  {t.Name}: {t.Physics}");
            return registry;
        }
    }
}
