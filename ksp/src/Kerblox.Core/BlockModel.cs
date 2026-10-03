using System;
using System.Collections.Generic;

namespace Kerblox.Core
{
    /// <summary>
    /// The six block faces, in Minecraft's <c>Direction</c> order so render-pack
    /// exports (P3.7) can store the ordinal directly. Grid axes follow the part:
    /// east = +X, up = +Y, south = +Z, as in Minecraft.
    /// </summary>
    public enum Direction : byte { Down, Up, North, South, West, East }

    [Flags]
    public enum FaceMask : byte
    {
        None = 0,
        Down = 1 << Direction.Down,
        Up = 1 << Direction.Up,
        North = 1 << Direction.North,
        South = 1 << Direction.South,
        West = 1 << Direction.West,
        East = 1 << Direction.East,
        All = Down | Up | North | South | West | East,
    }

    public static class Directions
    {
        public const int Count = 6;

        public static readonly Direction[] All =
            { Direction.Down, Direction.Up, Direction.North, Direction.South, Direction.West, Direction.East };

        public static Direction Opposite(this Direction d) => (Direction)((int)d ^ 1);

        public static FaceMask Mask(this Direction d) => (FaceMask)(1 << (int)d);

        public static void Offset(this Direction d, out int dx, out int dy, out int dz)
        {
            dx = d == Direction.East ? 1 : d == Direction.West ? -1 : 0;
            dy = d == Direction.Up ? 1 : d == Direction.Down ? -1 : 0;
            dz = d == Direction.South ? 1 : d == Direction.North ? -1 : 0;
        }

        public static Float3 Normal(this Direction d)
        {
            d.Offset(out int dx, out int dy, out int dz);
            return new Float3(dx, dy, dz);
        }
    }

    /// <summary>
    /// Minecraft's chunk render layers. Each becomes its own submesh, because each
    /// needs a different shader: opaque, alpha-tested, or alpha-blended.
    /// </summary>
    public enum RenderLayer : byte { Solid, Cutout, Translucent }

    /// <summary>8-bit RGBA colour, so Core needs no UnityEngine.Color32.</summary>
    public readonly struct Rgba32 : IEquatable<Rgba32>
    {
        public readonly byte R, G, B, A;

        public Rgba32(byte r, byte g, byte b, byte a = 255) { R = r; G = g; B = b; A = a; }

        /// <summary>No tint: the texture's own colour.</summary>
        public static readonly Rgba32 White = new Rgba32(255, 255, 255);

        public bool Equals(Rgba32 o) => R == o.R && G == o.G && B == o.B && A == o.A;
        public override bool Equals(object obj) => obj is Rgba32 o && Equals(o);
        public override int GetHashCode() => (R << 24) | (G << 16) | (B << 8) | A;
        public static bool operator ==(Rgba32 a, Rgba32 b) => a.Equals(b);
        public static bool operator !=(Rgba32 a, Rgba32 b) => !a.Equals(b);
        public override string ToString() => $"#{R:x2}{G:x2}{B:x2}{A:x2}";
    }

    /// <summary>
    /// One textured quad in block-local space, where a full cube spans 0..1 on each
    /// axis. Corners wind so that Cross(c1 - c0, c2 - c0) points along
    /// <see cref="Normal"/> (Unity's front face); the mesher splits them into
    /// triangles (0,1,2) and (0,2,3).
    /// </summary>
    public sealed class BlockQuad
    {
        public Float3 P0 { get; }
        public Float3 P1 { get; }
        public Float3 P2 { get; }
        public Float3 P3 { get; }
        public Float2 Uv0 { get; }
        public Float2 Uv1 { get; }
        public Float2 Uv2 { get; }
        public Float2 Uv3 { get; }
        public Float3 Normal { get; }

        /// <summary>
        /// The quad is dropped when the neighbour in this direction occludes the
        /// touching face. Null means the quad always draws (Minecraft's "unculled" quads).
        /// </summary>
        public Direction? CullFace { get; }

        public RenderLayer Layer { get; }

        /// <summary>Index of the texture atlas the UVs refer to.</summary>
        public int Atlas { get; }

        /// <summary>Multiplied with the texture (grass, leaves, map-colour fallback).</summary>
        public Rgba32 Tint { get; }

        public BlockQuad(Float3 p0, Float3 p1, Float3 p2, Float3 p3,
                         Float2 uv0, Float2 uv1, Float2 uv2, Float2 uv3,
                         Float3 normal, Direction? cullFace, RenderLayer layer, int atlas, Rgba32 tint)
        {
            if (atlas < 0) throw new ArgumentOutOfRangeException(nameof(atlas));
            P0 = p0; P1 = p1; P2 = p2; P3 = p3;
            Uv0 = uv0; Uv1 = uv1; Uv2 = uv2; Uv3 = uv3;
            Normal = normal; CullFace = cullFace; Layer = layer; Atlas = atlas; Tint = tint;
        }
    }

    /// <summary>
    /// The geometry of one block state: its quads grouped by cull face, plus which
    /// of its faces fully cover the cell boundary (and so hide the neighbour's
    /// touching quads). This mirrors what Minecraft's baked models and occlusion
    /// shapes give the chunk renderer, so a render pack can be replayed as-is.
    /// Immutable and shared by every cell of the same state.
    /// </summary>
    public sealed class BlockModel
    {
        private static readonly BlockQuad[] NoQuads = new BlockQuad[0];

        /// <summary>Air: draws nothing, hides nothing.</summary>
        public static readonly BlockModel Empty = new BlockModel(NoQuads, FaceMask.None);

        private readonly BlockQuad[][] culled = new BlockQuad[Directions.Count][];

        /// <summary>Quads with no cull face: always drawn.</summary>
        public IReadOnlyList<BlockQuad> Unculled { get; }

        /// <summary>Faces that cover the whole cell side, so a neighbour's quads culled toward us are hidden.</summary>
        public FaceMask Occludes { get; }

        /// <summary>
        /// Cull quads against a neighbour of the same block (any state), like
        /// Minecraft's glass and ice, so a glass wall has no internal faces.
        /// </summary>
        public bool CullAgainstSameBlock { get; }

        public int QuadCount { get; }

        public bool IsEmpty => QuadCount == 0;

        public BlockModel(IEnumerable<BlockQuad> quads, FaceMask occludes, bool cullAgainstSameBlock = false)
        {
            if (quads == null) throw new ArgumentNullException(nameof(quads));
            var groups = new List<BlockQuad>[Directions.Count + 1];
            for (int i = 0; i < groups.Length; i++) groups[i] = new List<BlockQuad>();
            int count = 0;
            foreach (BlockQuad q in quads)
            {
                if (q == null) throw new ArgumentException("Null quad", nameof(quads));
                groups[q.CullFace.HasValue ? (int)q.CullFace.Value : Directions.Count].Add(q);
                count++;
            }
            for (int d = 0; d < Directions.Count; d++)
                culled[d] = groups[d].Count == 0 ? NoQuads : groups[d].ToArray();
            Unculled = groups[Directions.Count].Count == 0 ? NoQuads : groups[Directions.Count].ToArray();
            Occludes = occludes & FaceMask.All;
            CullAgainstSameBlock = cullAgainstSameBlock;
            QuadCount = count;
        }

        /// <summary>Quads hidden when the neighbour in <paramref name="face"/> occludes.</summary>
        public IReadOnlyList<BlockQuad> CulledBy(Direction face) => culled[(int)face];

        /// <summary>Whether this model hides the neighbouring quads touching <paramref name="face"/>.</summary>
        public bool OccludesFace(Direction face) => (Occludes & face.Mask()) != 0;

        /// <summary>
        /// A full cube with every face on the same atlas rectangle, each face culled
        /// toward its own direction. Used by the built-in source and the fallback.
        /// </summary>
        public static BlockModel Cube(int atlas, Float2 uvMin, Float2 uvMax, RenderLayer layer, Rgba32 tint,
                                      FaceMask occludes, bool cullAgainstSameBlock = false)
        {
            var quads = new List<BlockQuad>(Directions.Count);
            foreach (Direction d in Directions.All)
            {
                Float3[] c = CubeCorners(d);
                quads.Add(new BlockQuad(c[0], c[1], c[2], c[3],
                    new Float2(uvMin.U, uvMin.V), new Float2(uvMin.U, uvMax.V),
                    new Float2(uvMax.U, uvMax.V), new Float2(uvMax.U, uvMin.V),
                    d.Normal(), d, layer, atlas, tint));
            }
            return new BlockModel(quads, occludes, cullAgainstSameBlock);
        }

        /// <summary>Unit-cube corners of a face: bottom-left, top-left, top-right, bottom-right seen from outside.</summary>
        private static Float3[] CubeCorners(Direction d)
        {
            switch (d)
            {
                case Direction.East: return new[] { P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1) };
                case Direction.West: return new[] { P(0, 0, 1), P(0, 1, 1), P(0, 1, 0), P(0, 0, 0) };
                case Direction.Up: return new[] { P(0, 1, 0), P(0, 1, 1), P(1, 1, 1), P(1, 1, 0) };
                case Direction.Down: return new[] { P(0, 0, 1), P(0, 0, 0), P(1, 0, 0), P(1, 0, 1) };
                case Direction.South: return new[] { P(1, 0, 1), P(1, 1, 1), P(0, 1, 1), P(0, 0, 1) };
                default: return new[] { P(0, 0, 0), P(0, 1, 0), P(1, 1, 0), P(1, 0, 0) }; // North
            }
        }

        private static Float3 P(float x, float y, float z) => new Float3(x, y, z);
    }

    /// <summary>
    /// Supplies block models by state. A source returns null for states it knows
    /// nothing about, so sources can be chained (render pack, then built-in, then
    /// fallback; see <see cref="BlockModelResolver"/>).
    /// </summary>
    public interface IBlockModelSource
    {
        BlockModel GetModel(BlockState state);
    }
}
