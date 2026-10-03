using System;
using System.Collections.Generic;
using Kerblox.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kerblox
{
    /// <summary>
    /// A single KSP part whose geometry is a voxel grid. Owns the generated mesh,
    /// compound box collider, mass, CoM and drag cube; the grid itself is a plain
    /// <see cref="VoxelGrid"/> persisted as text in <see cref="gridData"/>.
    ///
    /// Lifecycle (verified against KSP 1.12.5 Assembly-CSharp):
    ///  - Part compilation: PartLoader calls OnLoad on the prefab *before* it renders
    ///    the part icon and assigns part.collider, and long before it renders the
    ///    stock drag cube. So building geometry in OnLoad makes all three see the grid.
    ///  - Instances are Object.Instantiate'd from the prefab. Unity copies public
    ///    serialised fields (so gridData survives) but not our private VoxelGrid,
    ///    hence the grid is always re-decoded from gridData.
    ///  - Part.CoMOffset is copied into rb.centerOfMass when KSP creates the
    ///    rigidbody; if the rb already exists we set it directly too.
    /// </summary>
    public class ModuleBlockGrid : PartModule, IPartMassModifier
    {
        private const string GeneratedRootName = "KerbloxGrid";
        private const string CollidersName = "KerbloxColliders";

        /// <summary>Edge length of one block in metres. 0.625 = two blocks per 1.25 m stack size.</summary>
        [KSPField]
        public float blockSize = 0.625f;

        /// <summary>Base64url GridCodec payload. Persisted in craft and save files.</summary>
        [KSPField(isPersistant = true)]
        public string gridData = "";

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Blocks")]
        public int blockCount;

        [KSPField(guiActive = true, guiActiveEditor = true, guiName = "Block mass", guiUnits = " t", guiFormat = "F3")]
        public float gridMass;

        [KSPField(guiActive = false, guiActiveEditor = true, guiName = "Colliders")]
        public int colliderCount;

        private static BlockRegistry registry;
        internal static BlockRegistry Registry => registry ?? (registry = BlockRegistry.CreateDefault());

        private VoxelGrid grid;
        private MassProperties massProps;
        private Mesh ownedMesh;          // only meshes we created; the prefab's is shared with clones
        private bool dragCubeDirty;

        private static bool IsCompilingParts => PartLoader.Instance == null || !PartLoader.Instance.IsReady();

        #region Lifecycle

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            // OnLoad runs twice per prefab during compilation (once from ParsePart,
            // once in CompileParts' module pass) and once per instance loaded from a
            // craft/save. Rebuild is idempotent.
            grid = null;
            Rebuild();
        }

        public override void OnStart(StartState state)
        {
            base.OnStart(state);
            if (grid == null) Rebuild();
            else dragCubeDirty = true;
            Log.Info($"{part.partInfo?.name} started in {state}: {blockCount} blocks, {gridMass:F3} t, CoM {part.CoMOffset}, {colliderCount} colliders");
        }

        public override string GetInfo()
        {
            EnsureGrid();
            var p = MassProperties.Compute(grid, Registry, new BlockLayout(grid, blockSize));
            return $"Grid: {grid.SizeX}x{grid.SizeY}x{grid.SizeZ}\nBlocks: {p.SolidBlocks}\nBlock mass: {p.Mass:F3} t";
        }

        public void FixedUpdate()
        {
            if (dragCubeDirty && ReadyForDragCube())
            {
                dragCubeDirty = false;
                UpdateDragCube();
            }
        }

        private void OnDestroy()
        {
            if (ownedMesh != null) Destroy(ownedMesh);
        }

        #endregion

        #region Grid

        private void EnsureGrid()
        {
            if (grid != null) return;
            if (!string.IsNullOrEmpty(gridData))
            {
                try
                {
                    grid = GridCodec.FromText(gridData);
                    // Re-encode so older formats (v1 fixed ids) are saved back as the
                    // current palette format; a current payload re-encodes identically.
                    string current = GridCodec.ToText(grid);
                    if (current != gridData)
                    {
                        Log.Info($"Re-encoded gridData on {part.partInfo?.name ?? part.name} as grid format v{GridCodec.Version}");
                        gridData = current;
                    }
                    return;
                }
                catch (FormatException e)
                {
                    Log.Error($"Corrupt gridData on {part?.partInfo?.name ?? part?.name}, falling back to default grid: {e.Message}");
                }
            }
            grid = DefaultGrids.Capsule();
            gridData = GridCodec.ToText(grid);
        }

        /// <summary>Regenerate everything derived from the grid.</summary>
        public void Rebuild()
        {
            EnsureGrid();
            var layout = new BlockLayout(grid, blockSize);
            massProps = MassProperties.Compute(grid, Registry, layout);
            blockCount = massProps.SolidBlocks;
            gridMass = (float)massProps.Mass;

            Transform model = GetModelTransform();
            StripPlaceholderModel(model);
            Transform root = GetOrCreateChild(model, GeneratedRootName);

            BuildMesh(root, layout);
            BuildColliders(root, layout);
            ApplyCenterOfMass();
            UpdateAttachNodes();

            part.ResetModelRenderersCache();
            part.ResetModelMeshRenderersCache();

            // During compilation KSP renders the stock drag cube itself afterwards.
            dragCubeDirty = !IsCompilingParts;
            if (IsCompilingParts)
                Log.Info($"Built prefab grid {grid.SizeX}x{grid.SizeY}x{grid.SizeZ}: {blockCount} blocks, {gridMass:F3} t, CoM {part.CoMOffset}, {colliderCount} colliders");
        }

        #endregion

        #region Editing

        /// <summary>
        /// Raised after an edit resized the grid and geometry was rebuilt, before
        /// <c>GameEvents.onEditorShipModified</c> fires.
        ///
        /// Contract: <see cref="GridEdit.ShiftX"/>/Y/Z map old cells to new ones
        /// (old (x,y,z) is now at (x+ShiftX, ...)), and
        /// <c>edit.Displacement(blockSize)</c> is how far the pre-existing blocks moved
        /// in this part's local space, in metres, because <see cref="BlockLayout"/>
        /// re-centres the grid on the part origin. Nothing is moved to compensate: the
        /// part transform, attach nodes' attached parts and children stay where they
        /// were. Keeping blocks fixed in world space is the listener's job (P2.2).
        /// </summary>
        public event Action<ModuleBlockGrid, GridEdit> GridResized;

        /// <summary>Grid size in cells. Valid coordinates for <see cref="GetBlock"/> are 0..size-1.</summary>
        public int SizeX { get { EnsureGrid(); return grid.SizeX; } }
        public int SizeY { get { EnsureGrid(); return grid.SizeY; } }
        public int SizeZ { get { EnsureGrid(); return grid.SizeZ; } }

        /// <summary>Out-of-bounds cells read as air.</summary>
        public BlockState GetBlock(int x, int y, int z)
        {
            EnsureGrid();
            return grid.Get(x, y, z);
        }

        /// <summary>
        /// Sets one cell, growing the grid when (x,y,z) lies outside it (see
        /// <see cref="GridEditor.SetBlock"/> and <see cref="GridResized"/>). Editor
        /// only: flight runs against a frozen grid, so calls in any other scene are
        /// refused with a warning. On a change it re-encodes <see cref="gridData"/>,
        /// rebuilds mesh, colliders, mass and CoM, marks the drag cube for a
        /// re-render and fires <c>GameEvents.onEditorShipModified</c>.
        /// </summary>
        public GridEdit SetBlock(int x, int y, int z, BlockState state)
        {
            EnsureGrid();
            if (!HighLogic.LoadedSceneIsEditor)
            {
                Log.Warn($"Refused grid edit at ({x},{y},{z}) on {part.partInfo?.name} outside the editor");
                return new GridEdit(GridEditStatus.NoChange, grid, grid.SizeX, grid.SizeY, grid.SizeZ);
            }

            GridEdit edit = GridEditor.SetBlock(grid, x, y, z, state);
            if (edit.Status == GridEditStatus.TooLarge)
                Log.Warn($"Grid edit at ({x},{y},{z}) would exceed {VoxelGrid.MaxDimension} blocks per axis");
            if (!edit.Changed) return edit;

            grid = edit.Grid;
            gridData = GridCodec.ToText(grid);
            Rebuild();          // also sets dragCubeDirty; FixedUpdate re-renders once the editor is ready
            part.UpdateMass();  // GetModuleMass is only polled, so push the new mass now

            if (edit.Resized)
            {
                Log.Info($"Grid on {part.partInfo?.name} resized: {edit}");
                GridResized?.Invoke(this, edit);
            }
            if (EditorLogic.fetch != null)
                GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            return edit;
        }

        /// <summary>Clears one cell to air. Never resizes the grid.</summary>
        public GridEdit RemoveBlock(int x, int y, int z) => SetBlock(x, y, z, BlockState.Air);

        #endregion

        #region Build mode

        private const string BuildModeOff = "Build blocks";
        private const string BuildModeOn = "Stop building";

        /// <summary>PAW toggle for the editor's block placement tool (<see cref="BlockBuildMode"/>).</summary>
        [KSPEvent(guiActive = false, guiActiveEditor = true, guiName = BuildModeOff)]
        public void ToggleBuildMode()
        {
            if (BlockBuildMode.IsActiveOn(this)) BlockBuildMode.Active.Close();
            else BlockBuildMode.Open(this);
        }

        internal void OnBuildModeChanged(bool active) =>
            Events[nameof(ToggleBuildMode)].guiName = active ? BuildModeOn : BuildModeOff;

        /// <summary>The transform whose local space <see cref="BlockLayout"/> describes; null before the first build.</summary>
        internal Transform GridRoot => GetModelTransform().Find(GeneratedRootName);

        /// <summary>Fills <paramref name="into"/> with the current merged box colliders.</summary>
        internal void GetGridColliders(List<BoxCollider> into)
        {
            into.Clear();
            Transform root = GridRoot;
            Transform holder = root == null ? null : root.Find(CollidersName);
            if (holder != null) holder.GetComponentsInChildren(into);
        }

        #endregion

        #region Model

        private Transform GetModelTransform()
        {
            // KSP parents MODEL{} clones under a child named "model".
            Transform model = part.transform.Find("model");
            if (model == null)
            {
                Log.Warn($"{part.name} has no 'model' transform; attaching grid to part root");
                model = part.transform;
            }
            return model;
        }

        /// <summary>
        /// KSP refuses to compile a part without a MODEL, so the cfg borrows a tiny stock
        /// one. Remove it so only our generated geometry renders and collides.
        /// DestroyImmediate during compilation: PartLoader snapshots the icon and
        /// part.collider in the same frame, so a deferred Destroy would be too late.
        /// </summary>
        private void StripPlaceholderModel(Transform model)
        {
            var doomed = new List<GameObject>();
            foreach (Transform child in model)
                if (child.name != GeneratedRootName)
                    doomed.Add(child.gameObject);

            foreach (GameObject go in doomed)
            {
                if (IsCompilingParts) DestroyImmediate(go);
                else Destroy(go);
            }
        }

        private Transform GetOrCreateChild(Transform parent, string name)
        {
            Transform t = parent.Find(name);
            if (t != null) return t;
            var go = new GameObject(name) { layer = part.gameObject.layer };
            go.transform.SetParent(parent, worldPositionStays: false);
            return go.transform;
        }

        private void BuildMesh(Transform root, BlockLayout layout)
        {
            MeshData data = GridMesher.Build(grid, Registry, layout);

            var mesh = new Mesh { name = "KerbloxGridMesh" };
            if (data.Vertices.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;

            var verts = new Vector3[data.Vertices.Count];
            var normals = new Vector3[data.Normals.Count];
            var uvs = new Vector2[data.Uvs.Count];
            for (int i = 0; i < verts.Length; i++)
            {
                Float3 v = data.Vertices[i], n = data.Normals[i];
                verts[i] = new Vector3(v.X, v.Y, v.Z);
                normals[i] = new Vector3(n.X, n.Y, n.Z);
                uvs[i] = new Vector2(data.Uvs[i].U, data.Uvs[i].V);
            }
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = data.Triangles.ToArray();
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();

            // No `??` here: it bypasses UnityEngine.Object's overloaded null check.
            MeshFilter mf = root.GetComponent<MeshFilter>();
            if (mf == null) mf = root.gameObject.AddComponent<MeshFilter>();
            MeshRenderer mr = root.GetComponent<MeshRenderer>();
            if (mr == null) mr = root.gameObject.AddComponent<MeshRenderer>();
            mf.sharedMesh = mesh;
            mr.sharedMaterial = BlockAtlas.GetMaterial(Registry);

            if (ownedMesh != null) Destroy(ownedMesh);
            ownedMesh = mesh;
        }

        private void BuildColliders(Transform root, BlockLayout layout)
        {
            Transform holder = root.Find(CollidersName);
            if (holder != null)
            {
                holder.SetParent(null);
                if (IsCompilingParts) DestroyImmediate(holder.gameObject);
                else Destroy(holder.gameObject);
            }
            holder = GetOrCreateChild(root, CollidersName);

            List<GridBox> boxes = BoxMerger.Merge(grid, Registry);
            BoxCollider first = null;
            foreach (GridBox b in boxes)
            {
                // One GameObject per box keeps them individually inspectable in a debugger;
                // multiple BoxColliders on one GameObject would also work.
                var go = new GameObject("box") { layer = part.gameObject.layer };
                go.transform.SetParent(holder, worldPositionStays: false);
                var bc = go.AddComponent<BoxCollider>();
                Float3 c = b.Center(layout), e = b.Extents(layout);
                bc.center = new Vector3(c.X, c.Y, c.Z);
                bc.size = new Vector3(e.X, e.Y, e.Z);
                if (first == null) first = bc;
            }
            colliderCount = boxes.Count;

            // part.collider is what KSP uses for e.g. highlight bounds and some
            // ground-contact checks; PartLoader sets it from the first non-trigger collider.
            if (first != null) part.collider = first;
        }

        #endregion

        #region Physics

        private void ApplyCenterOfMass()
        {
            Float3 com = massProps.CenterOfMass;
            part.CoMOffset = new Vector3(com.X, com.Y, com.Z);
            if (part.rb != null)
                part.rb.centerOfMass = part.CoMOffset;
        }

        /// <summary>
        /// Keep the stack nodes on the top and bottom faces of the occupied blocks,
        /// centred on the part axis. Surface attachment uses colliders and needs nothing here.
        /// </summary>
        private void UpdateAttachNodes()
        {
            if (massProps.SolidBlocks == 0) return;
            SetNodeY("top", massProps.BoundsMax.Y);
            SetNodeY("bottom", massProps.BoundsMin.Y);
        }

        private void SetNodeY(string id, float y)
        {
            AttachNode node = part.FindAttachNode(id);
            if (node == null) return;
            var pos = new Vector3(0f, y, 0f);
            // Moving a node with something attached would also need to move that part;
            // that matters once the grid is editable (phase 2), not for a fixed grid.
            node.position = pos;
            node.originalPosition = pos;
        }

        public float GetModuleMass(float defaultMass, ModifierStagingSituation sit) => gridMass;

        public ModifierChangeWhen GetModuleMassChangeWhen() => ModifierChangeWhen.FIXED;

        #endregion

        #region Drag

        // Same readiness rules as ROUtils' DragCubeTool (used by Procedural Parts):
        // rendering before the vessel/editor is settled produces garbage cubes.
        private bool ReadyForDragCube()
        {
            if (HighLogic.LoadedSceneIsFlight) return FlightGlobals.ready;
            if (HighLogic.LoadedSceneIsEditor)
                return part.localRoot == EditorLogic.RootPart && part.gameObject.layer != LayerMask.NameToLayer("TransparentFX");
            return false;
        }

        private void UpdateDragCube()
        {
            DragCube cube = DragCubeSystem.Instance.RenderProceduralDragCube(part);
            part.DragCubes.ClearCubes();
            part.DragCubes.Cubes.Add(cube);
            part.DragCubes.ResetCubeWeights();
            part.DragCubes.ForceUpdate(true, true, false);
            part.DragCubes.SetDragWeights();
            Log.Info($"Drag cube for {part.partInfo?.name}: size {cube.Size}, center {cube.Center}, areas [{string.Join(", ", cube.Area)}]");
        }

        #endregion
    }
}
