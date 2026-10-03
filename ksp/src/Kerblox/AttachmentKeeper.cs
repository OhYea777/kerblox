using System.Collections.Generic;
using Kerblox.Core;
using UnityEngine;

namespace Kerblox
{
    /// <summary>
    /// Keeps the craft together across one editor grid edit: <see cref="Capture"/> before
    /// the edit, <see cref="Apply"/> after <see cref="ModuleBlockGrid.Rebuild"/> has moved
    /// the stack nodes. The geometry lives in <see cref="AttachmentGeometry"/>; this class
    /// only reads and moves transforms.
    ///
    /// Relies on the editor's part hierarchy (verified in EditorLogic.attachPart, KSP 1.12.5):
    /// an attached part's transform is parented to its parent part's transform, its
    /// <c>attPos0</c> is its <c>localPosition</c> at attach time, and the node it attached
    /// with has <c>attachedPart</c> set to the parent (its <c>srfAttachNode</c> for surface
    /// attachment). So moving this part carries its whole subtree, and a child only needs
    /// the difference between where its attach point was and where it should be now.
    ///
    /// Policy:
    ///  - This part: if it hangs off a parent by a stack node, that node stays put (the
    ///    part moves by the node's old minus new position). Otherwise (editor root, loose,
    ///    or surface-attached to its parent) the pre-existing blocks stay fixed in world space.
    ///  - Stack-attached children follow their node.
    ///  - Surface-attached children follow their block, and slide inward if it was removed.
    /// Symmetry counterparts are separate parts with their own grids and keepers:
    /// <see cref="ModuleBlockGrid.SetBlockWithSymmetry"/> edits each one, and each
    /// edit's keeper moves only that part and its children. The policy is symmetric
    /// (it never depends on which side of the craft a part is on), so counterparts
    /// stay radial or mirror images of each other.
    /// </summary>
    internal sealed class AttachmentKeeper
    {
        private readonly Part part;
        private readonly Dictionary<AttachNode, Vector3> oldNodePositions = new Dictionary<AttachNode, Vector3>();
        private readonly List<Part> surfaceChildren = new List<Part>();
        private readonly List<SurfaceContact> surfaceContacts = new List<SurfaceContact>();

        private AttachmentKeeper(Part part) { this.part = part; }

        /// <summary>Records node positions and where each surface-attached child touches the grid.</summary>
        public static AttachmentKeeper Capture(Part part, VoxelGrid grid, BlockRegistry registry, float blockSize)
        {
            var keeper = new AttachmentKeeper(part);
            foreach (AttachNode node in part.attachNodes)
                keeper.oldNodePositions[node] = node.position;

            var layout = new BlockLayout(grid, blockSize);
            foreach (Part child in part.children)
            {
                if (child == null || FindNodeTo(part, child) != null) continue;
                AttachNode srf = child.srfAttachNode;
                if (srf == null || srf.attachedPart != part) continue;

                Vector3 point = part.transform.InverseTransformPoint(child.transform.TransformPoint(srf.position));
                Vector3 hint = part.transform.InverseTransformDirection(SurfaceNormal(child));
                SurfaceContact contact = AttachmentGeometry.FindContact(grid, registry, layout,
                    new Float3(point.x, point.y, point.z), new Float3(hint.x, hint.y, hint.z));
                keeper.surfaceChildren.Add(child);
                keeper.surfaceContacts.Add(contact);   // null: not on a face, just follows the blocks
            }
            return keeper;
        }

        /// <summary>Moves this part and its direct children for <paramref name="edit"/>. Call after the rebuild.</summary>
        public void Apply(GridEdit edit, BlockRegistry registry, float blockSize)
        {
            if (!edit.Changed) return;
            Float3 d = edit.Displacement(blockSize);
            Vector3 displacement = new Vector3(d.X, d.Y, d.Z);

            // Children are moved in this part's frame, which a translation of the part
            // doesn't rotate, so the order relative to MoveSelf doesn't matter.
            foreach (Part child in part.children)
            {
                if (child == null) continue;
                AttachNode node = FindNodeTo(part, child);
                if (node == null || !oldNodePositions.TryGetValue(node, out Vector3 old)) continue;
                MoveChild(child, node.position - old, $"node {node.id}");
            }

            for (int i = 0; i < surfaceChildren.Count; i++)
            {
                Part child = surfaceChildren[i];
                if (child == null) continue;
                SurfaceContact contact = surfaceContacts[i];
                if (contact == null)
                {
                    MoveChild(child, displacement, "surface (off-face)");
                    continue;
                }
                SurfaceMove move = AttachmentGeometry.Resolve(contact, edit, registry, blockSize);
                if (move.Support == SurfaceSupport.Unsupported)
                    Log.Warn($"{child.partInfo?.name} lost its block on {part.partInfo?.name} and has nothing behind it; left in place");
                else if (move.Support == SurfaceSupport.Slid)
                    Log.Info($"{child.partInfo?.name} slid {move.SlideCells} block(s) onto {part.partInfo?.name} after its block was removed");
                MoveChild(child, new Vector3(move.Translation.X, move.Translation.Y, move.Translation.Z), "surface");
            }

            MoveSelf(displacement);
        }

        private void MoveSelf(Vector3 displacement)
        {
            AttachNode anchor = part.parent != null ? FindNodeTo(part, part.parent) : null;
            bool anchored = anchor != null && oldNodePositions.TryGetValue(anchor, out _);
            Vector3 oldAnchor = anchored ? oldNodePositions[anchor] : Vector3.zero;
            Vector3 newAnchor = anchored ? anchor.position : Vector3.zero;

            Float3 t = AttachmentGeometry.SelfTranslation(new Float3(displacement.x, displacement.y, displacement.z),
                anchored, new Float3(oldAnchor.x, oldAnchor.y, oldAnchor.z), new Float3(newAnchor.x, newAnchor.y, newAnchor.z));
            var local = new Vector3(t.X, t.Y, t.Z);
            if (local.sqrMagnitude < 1e-10f) return;

            Vector3 world = part.transform.TransformVector(local);
            part.transform.position += world;
            // attPos0 is in the parent's frame (world for the root); the offset tool's
            // "reset" snaps back to it, so it has to follow the move.
            part.attPos0 += part.parent != null ? part.parent.transform.InverseTransformVector(world) : world;
            Log.Info($"Moved {part.partInfo?.name} by {local} (part space) to keep {(anchored ? "node " + anchor.id + " on its parent" : "its blocks in place")}");
        }

        /// <summary><paramref name="local"/> is in this part's frame, which is the child's parent frame.</summary>
        private void MoveChild(Part child, Vector3 local, string how)
        {
            if (local.sqrMagnitude < 1e-10f) return;
            child.transform.position += part.transform.TransformVector(local);
            child.attPos0 += local;
            Log.Info($"Moved {child.partInfo?.name} by {local} on {part.partInfo?.name} ({how})");
        }

        /// <summary>
        /// The node of <paramref name="owner"/> whose attachedPart is <paramref name="other"/>,
        /// stack nodes only. Not Part.FindAttachNodeByPart: that also returns srfAttachNode,
        /// which we handle separately.
        /// </summary>
        private static AttachNode FindNodeTo(Part owner, Part other)
        {
            foreach (AttachNode node in owner.attachNodes)
                if (node.attachedPart == other) return node;
            return null;
        }

        /// <summary>
        /// World-space normal of the surface a child was attached to. EditorLogic sets
        /// attachment.rotation = LookRotation(hitNormal, up) * LookRotation(srf.orientation, up)
        /// and the part's rotation to attachment.rotation * attRotation, so the normal is
        /// recoverable from those. Only a tie-break hint for edges: the rotate tool can skew it.
        /// </summary>
        private static Vector3 SurfaceNormal(Part child)
        {
            Vector3 o = child.srfAttachNode.orientation;
            if (o.sqrMagnitude < 1e-6f) return Vector3.zero;
            Quaternion attachment = child.transform.rotation * Quaternion.Inverse(child.attRotation);
            return attachment * Quaternion.Inverse(Quaternion.LookRotation(o, Vector3.up)) * Vector3.forward;
        }
    }
}
