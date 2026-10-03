using System;

namespace Kerblox.Core
{
    /// <summary>Part-local positions of the grid's <c>top</c> and <c>bottom</c> stack nodes.</summary>
    public readonly struct StackNodes
    {
        /// <summary>False for an all-air grid, which has no faces to put nodes on.</summary>
        public readonly bool Valid;
        public readonly Float3 Top, Bottom;

        public StackNodes(Float3 top, Float3 bottom) { Valid = true; Top = top; Bottom = bottom; }

        /// <summary>
        /// Nodes sit on the top and bottom faces of the occupied bounds, centred on the
        /// part's Y axis so that stacks stay on the craft's centreline.
        /// </summary>
        public static StackNodes From(MassProperties props) =>
            props.SolidBlocks == 0
                ? default
                : new StackNodes(new Float3(0f, props.BoundsMax.Y, 0f), new Float3(0f, props.BoundsMin.Y, 0f));
    }

    /// <summary>
    /// Where a surface-attached part touches the grid, in cells: the layer of solid
    /// cells behind the attach point along <see cref="Axis"/>, and the one or two
    /// cells on each other axis that the point lies on (two when it sits on a block
    /// boundary, so either neighbour counts as support).
    /// </summary>
    public sealed class SurfaceContact
    {
        /// <summary>0 = X, 1 = Y, 2 = Z.</summary>
        public int Axis { get; }

        /// <summary>+1 or -1: the face's outward normal is <c>Sign</c> along <see cref="Axis"/>.</summary>
        public int Sign { get; }

        private readonly int[] lo, hi;

        internal SurfaceContact(int axis, int sign, int[] lo, int[] hi)
        {
            Axis = axis; Sign = sign; this.lo = lo; this.hi = hi;
        }

        /// <summary>Inclusive cell range on axis <paramref name="a"/>; on <see cref="Axis"/> it's the single supporting layer.</summary>
        public int Lo(int a) => lo[a];
        public int Hi(int a) => hi[a];

        /// <summary>Outward face normal as a unit vector.</summary>
        public Float3 Normal => new Float3(Axis == 0 ? Sign : 0, Axis == 1 ? Sign : 0, Axis == 2 ? Sign : 0);

        public override string ToString() =>
            $"{(Sign > 0 ? "+" : "-")}{"XYZ"[Axis]} face, cells [{lo[0]}..{hi[0]}, {lo[1]}..{hi[1]}, {lo[2]}..{hi[2]}]";
    }

    public enum SurfaceSupport
    {
        /// <summary>The block under the attach point is still there.</summary>
        Supported,
        /// <summary>The supporting blocks were removed; the part slid inward onto the solid layer behind them.</summary>
        Slid,
        /// <summary>Nothing solid within reach behind the attach point; the part keeps its place relative to the old blocks.</summary>
        Unsupported,
    }

    public readonly struct SurfaceMove
    {
        public readonly SurfaceSupport Support;
        /// <summary>Whole blocks slid inward (0 unless <see cref="SurfaceSupport.Slid"/>).</summary>
        public readonly int SlideCells;
        /// <summary>Part-local metres to add to the attached part's position (new part space).</summary>
        public readonly Float3 Translation;

        public SurfaceMove(SurfaceSupport support, int slideCells, Float3 translation)
        {
            Support = support; SlideCells = slideCells; Translation = translation;
        }
    }

    /// <summary>
    /// Geometry for keeping parts attached to a grid while it is edited. Everything is
    /// in the edited part's local space, in metres, with <see cref="BlockLayout"/>'s
    /// convention that the grid's box is centred on the part origin.
    /// </summary>
    public static class AttachmentGeometry
    {
        /// <summary>How close (in blocks) a point must be to a cell boundary to count as on it.</summary>
        public const float FaceTolerance = 0.05f;

        /// <summary>
        /// Translation for the edited part itself. When it hangs off a parent, the node
        /// that joins them is the anchor, so the part moves by the anchor's old minus new
        /// position and stays attached. Otherwise (editor root, or a loose part) the
        /// pre-existing blocks stay fixed in world space: the part moves by minus the
        /// edit's displacement.
        /// </summary>
        public static Float3 SelfTranslation(Float3 displacement, bool anchored, Float3 oldAnchor, Float3 newAnchor) =>
            anchored ? oldAnchor - newAnchor : displacement * -1f;

        /// <summary>
        /// Finds the face that a surface-attach point lies on, before an edit. Returns null
        /// when the point isn't on any solid face within <see cref="FaceTolerance"/> (for
        /// example after the offset tool moved the part off the surface). On an edge or
        /// corner, the candidate face whose normal best matches <paramref name="outwardHint"/>
        /// wins; pass the attach direction, or zero for the first match.
        /// </summary>
        public static SurfaceContact FindContact(VoxelGrid grid, BlockRegistry registry, BlockLayout layout,
            Float3 point, Float3 outwardHint)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            float[] u =
            {
                (point.X - layout.Origin.X) / layout.BlockSize,
                (point.Y - layout.Origin.Y) / layout.BlockSize,
                (point.Z - layout.Origin.Z) / layout.BlockSize,
            };
            float[] hint = { outwardHint.X, outwardHint.Y, outwardHint.Z };

            SurfaceContact best = null;
            float bestScore = float.NegativeInfinity;
            for (int axis = 0; axis < 3; axis++)
            {
                int boundary = (int)Math.Round(u[axis]);
                if (Math.Abs(u[axis] - boundary) > FaceTolerance) continue;

                for (int sign = 1; sign >= -1; sign -= 2)
                {
                    var lo = new int[3];
                    var hi = new int[3];
                    for (int a = 0; a < 3; a++)
                    {
                        if (a == axis) continue;
                        int nearest = (int)Math.Round(u[a]);
                        if (Math.Abs(u[a] - nearest) <= FaceTolerance) { lo[a] = nearest - 1; hi[a] = nearest; }
                        else { lo[a] = hi[a] = (int)Math.Floor(u[a]); }
                    }
                    // A +normal face is the top of the cell below the boundary.
                    lo[axis] = hi[axis] = sign > 0 ? boundary - 1 : boundary;

                    if (!AnySolid(grid, registry, lo, hi, axis, 0)) continue;
                    if (!AnyOpen(grid, registry, lo, hi, axis, sign)) continue;

                    float score = sign * hint[axis];
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = new SurfaceContact(axis, sign, lo, hi);
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// Where a surface-attached part has to go after <paramref name="edit"/>, given the
        /// <paramref name="contact"/> found before it. While a block of the contact remains
        /// the part follows the blocks (the edit's displacement). If they're all gone it
        /// slides inward, against the face normal, onto the nearest layer with a solid block
        /// in the same footprint, at most <paramref name="maxSlide"/> blocks. The cap keeps
        /// a part on a hollow wall from falling through to the far wall's inside face;
        /// removing a column block by block still walks the part down it one step per edit.
        /// With nothing in reach it stays where it was relative to the blocks.
        /// </summary>
        public static SurfaceMove Resolve(SurfaceContact contact, GridEdit edit, BlockRegistry registry, float blockSize,
            int maxSlide = 1)
        {
            if (contact == null) throw new ArgumentNullException(nameof(contact));
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            Float3 displacement = edit.Displacement(blockSize);
            VoxelGrid grid = edit.Grid;
            int[] shift = { edit.ShiftX, edit.ShiftY, edit.ShiftZ };
            var lo = new int[3];
            var hi = new int[3];
            for (int a = 0; a < 3; a++) { lo[a] = contact.Lo(a) + shift[a]; hi[a] = contact.Hi(a) + shift[a]; }

            int size = contact.Axis == 0 ? grid.SizeX : contact.Axis == 1 ? grid.SizeY : grid.SizeZ;
            for (int k = 0; ; k++)
            {
                int layer = lo[contact.Axis] - contact.Sign * k;
                if (k > maxSlide || layer < 0 || layer >= size)
                    return new SurfaceMove(SurfaceSupport.Unsupported, 0, displacement);
                if (AnySolid(grid, registry, lo, hi, contact.Axis, -contact.Sign * k))
                    return k == 0
                        ? new SurfaceMove(SurfaceSupport.Supported, 0, displacement)
                        : new SurfaceMove(SurfaceSupport.Slid, k, displacement - contact.Normal * (k * blockSize));
            }
        }

        private static bool AnySolid(VoxelGrid grid, BlockRegistry registry, int[] lo, int[] hi, int axis, int offset) =>
            Any(grid, registry, lo, hi, axis, offset, solid: true);

        // The face is exposed if at least one cell in front of the footprint isn't solid.
        private static bool AnyOpen(VoxelGrid grid, BlockRegistry registry, int[] lo, int[] hi, int axis, int offset) =>
            Any(grid, registry, lo, hi, axis, offset, solid: false);

        /// <summary>Whether any footprint cell, moved <paramref name="offset"/> cells along <paramref name="axis"/>, is (or isn't) solid.</summary>
        private static bool Any(VoxelGrid grid, BlockRegistry registry, int[] lo, int[] hi, int axis, int offset, bool solid)
        {
            int dx = axis == 0 ? offset : 0, dy = axis == 1 ? offset : 0, dz = axis == 2 ? offset : 0;
            for (int x = lo[0]; x <= hi[0]; x++)
                for (int y = lo[1]; y <= hi[1]; y++)
                    for (int z = lo[2]; z <= hi[2]; z++)
                        if (registry.IsSolid(grid.Get(x + dx, y + dy, z + dz)) == solid) return true;
            return false;
        }
    }
}
