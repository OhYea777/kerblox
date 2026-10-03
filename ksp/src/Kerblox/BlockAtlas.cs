using System.Collections.Generic;
using Kerblox.Core;
using UnityEngine;

namespace Kerblox
{
    /// <summary>
    /// Textures and materials for the grid mesh. Atlas 0 is the procedurally painted
    /// built-in atlas (16 px per tile, one column per block type plus the fallback
    /// tile, laid out by <see cref="BuiltinModelSource"/>), generated in code so phase 1
    /// needs no art assets or Unity Editor. Materials are shared by every block-grid
    /// part, one per <see cref="MaterialKey"/> (layer shader, atlas, tint).
    /// </summary>
    internal static class BlockAtlas
    {
        public const int TilePx = 16;

        // Fallback tile base colour: light grey, so a tint reads as roughly that colour.
        private const byte FallbackGrey = 240;

        private static readonly Dictionary<MaterialKey, Material> materials = new Dictionary<MaterialKey, Material>();
        private static Texture2D builtinTexture;
        private static bool warnedAtlas, warnedTint;

        public static Material GetMaterial(MaterialKey key, BuiltinModelSource builtin)
        {
            if (materials.TryGetValue(key, out Material material) && material != null) return material;

            material = new Material(LayerShader(key.Layer))
            {
                name = $"KerbloxBlocks/{key}",
                mainTexture = GetAtlasTexture(key.Atlas, builtin),
            };
            // Minecraft's cutout layer discards alpha below 0.1.
            if (key.Layer == RenderLayer.Cutout && material.HasProperty("_Cutoff"))
                material.SetFloat("_Cutoff", 0.1f);
            if (key.Tint != Rgba32.White)
            {
                // KSP's shaders ignore vertex colour, so tint goes on the material. Whether
                // the KSP shaders honour _Color is unverified (in-game check).
                if (material.HasProperty("_Color"))
                    material.SetColor("_Color", new Color32(key.Tint.R, key.Tint.G, key.Tint.B, key.Tint.A));
                else if (!warnedTint)
                {
                    warnedTint = true;
                    Log.Warn($"Shader '{material.shader.name}' has no _Color; block tints won't show");
                }
            }
            materials[key] = material;
            return material;
        }

        /// <summary>
        /// Stock shader per layer, the same ones PartReader/PartTools pick for opaque,
        /// alpha-cutout and alpha-blended part materials.
        /// </summary>
        private static Shader LayerShader(RenderLayer layer)
        {
            string name;
            switch (layer)
            {
                case RenderLayer.Cutout: name = "KSP/Alpha/Cutoff"; break;
                case RenderLayer.Translucent: name = "KSP/Alpha/Translucent"; break;
                default: name = "KSP/Diffuse"; break;
            }
            Shader shader = Shader.Find(name);
            if (shader == null)
            {
                Log.Warn($"Shader '{name}' not found; falling back to 'Diffuse'");
                shader = Shader.Find("Diffuse");
            }
            return shader;
        }

        /// <summary>Only the built-in atlas exists until render packs load (P3.10).</summary>
        private static Texture2D GetAtlasTexture(int atlas, BuiltinModelSource builtin)
        {
            if (atlas != BuiltinModelSource.AtlasIndex && !warnedAtlas)
            {
                warnedAtlas = true;
                Log.Warn($"No texture for atlas {atlas}; using the built-in atlas");
            }
            if (builtinTexture == null) builtinTexture = BuildTexture(builtin);
            return builtinTexture;
        }

        private static Texture2D BuildTexture(BuiltinModelSource builtin)
        {
            int tiles = builtin.AtlasTileCount;
            var tex = new Texture2D(tiles * TilePx, TilePx, TextureFormat.RGBA32, mipChain: false)
            {
                name = "KerbloxAtlas",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            var pixels = new Color32[tex.width * tex.height];
            foreach (BlockType type in builtin.Registry.Types)
                PaintTile(pixels, tex.width, type.TileIndex, type.R, type.G, type.B, type.Pattern);
            PaintTile(pixels, tex.width, builtin.FallbackTileIndex, FallbackGrey, FallbackGrey, FallbackGrey, TilePattern.Noise);

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return tex;
        }

        private static void PaintTile(Color32[] pixels, int width, int tile, byte r, byte g, byte b, TilePattern pattern)
        {
            for (int py = 0; py < TilePx; py++)
                for (int px = 0; px < TilePx; px++)
                    pixels[py * width + tile * TilePx + px] = Paint(tile, r, g, b, pattern, px, py);
        }

        private static Color32 Paint(int tile, byte r, byte g, byte b, TilePattern pattern, int x, int y)
        {
            // TileIndex + 1 equals each default block's old v1 type id, which used to be
            // the seed, so the painted tiles look exactly as they did before palettes.
            int seed = tile + 1;
            float shade;
            bool edge = x == 0 || y == 0 || x == TilePx - 1 || y == TilePx - 1;
            switch (pattern)
            {
                case TilePattern.Planks:
                    shade = (y % 4 == 0) ? 0.72f : 0.92f + 0.08f * Noise(seed, x / 4, y);
                    if ((y / 4) % 2 == 0 ? x == 7 : x == 15) shade = 0.75f;
                    break;
                case TilePattern.Bevel:
                    shade = edge ? 0.7f : (x == 1 || y == TilePx - 2) ? 1.05f : 0.95f + 0.05f * Noise(seed, x, y);
                    break;
                case TilePattern.Frame:
                    shade = edge ? 1.0f : (x == y && x > 2 && x < 6) ? 1.1f : 0.82f;
                    break;
                default:
                    shade = 0.85f + 0.15f * Noise(seed, x, y);
                    break;
            }
            return new Color32(Scale(r, shade), Scale(g, shade), Scale(b, shade), 255);
        }

        private static byte Scale(byte c, float s) => (byte)Mathf.Clamp(c * s, 0, 255);

        /// <summary>Deterministic per-pixel hash in [0,1].</summary>
        private static float Noise(int seed, int x, int y)
        {
            unchecked
            {
                uint h = (uint)(seed * 374761393 + x * 668265263 + y * 2147483647);
                h = (h ^ (h >> 13)) * 1274126177;
                return (h ^ (h >> 16)) / (float)uint.MaxValue;
            }
        }
    }
}
