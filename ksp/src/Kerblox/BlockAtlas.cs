using Kerblox.Core;
using UnityEngine;

namespace Kerblox
{
    /// <summary>
    /// Procedurally painted 16px-per-tile texture atlas, one column per block type,
    /// shared by every block-grid part. Generated in code so phase 1 needs no art
    /// assets or Unity Editor.
    /// </summary>
    internal static class BlockAtlas
    {
        public const int TilePx = 16;

        private static Material material;

        public static Material GetMaterial(BlockRegistry registry)
        {
            if (material != null) return material;

            // KSP/Diffuse is the stock opaque shader (confirmed via Shader.Find usage in
            // Assembly-CSharp). It supports KSP's part highlighting and opacity fading.
            Shader shader = Shader.Find("KSP/Diffuse");
            if (shader == null)
            {
                Log.Warn("Shader 'KSP/Diffuse' not found; falling back to 'Diffuse'");
                shader = Shader.Find("Diffuse");
            }

            material = new Material(shader) { name = "KerbloxBlocks", mainTexture = BuildTexture(registry) };
            return material;
        }

        private static Texture2D BuildTexture(BlockRegistry registry)
        {
            int tiles = Mathf.Max(1, registry.TileCount);
            var tex = new Texture2D(tiles * TilePx, TilePx, TextureFormat.RGBA32, mipChain: false)
            {
                name = "KerbloxAtlas",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            var pixels = new Color32[tex.width * tex.height];
            foreach (BlockType type in registry.Types)
                for (int py = 0; py < TilePx; py++)
                    for (int px = 0; px < TilePx; px++)
                        pixels[py * tex.width + type.TileIndex * TilePx + px] = Paint(type, px, py);

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return tex;
        }

        private static Color32 Paint(BlockType t, int x, int y)
        {
            // TileIndex + 1 equals each default block's old v1 type id, which used to be
            // the seed, so the painted tiles look exactly as they did before palettes.
            int seed = t.TileIndex + 1;
            float shade;
            bool edge = x == 0 || y == 0 || x == TilePx - 1 || y == TilePx - 1;
            switch (t.Pattern)
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
            return new Color32(Scale(t.R, shade), Scale(t.G, shade), Scale(t.B, shade), 255);
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
