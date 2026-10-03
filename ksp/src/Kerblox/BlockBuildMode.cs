using System.Collections.Generic;
using Kerblox.Core;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Kerblox
{
    /// <summary>
    /// The editor's block placement tool, toggled from a <see cref="ModuleBlockGrid"/>'s
    /// PAW. While it exists, left click places the palette block on the face under the
    /// mouse, Shift+click removes the block under the mouse and Ctrl+click picks it into
    /// the palette. At most one part is in build mode at a time.
    ///
    /// Lives on the part's GameObject, so deleting the part or leaving the scene
    /// destroys it, and <see cref="OnDestroy"/> is where every input lock, the ghost
    /// GameObject and its material are released.
    ///
    /// Keeping clicks out of KSP's editor (verified against KSP 1.12.5 Assembly-CSharp,
    /// see docs/KSP-API-NOTES.md): part pickup in place mode is gated on
    /// <c>ControlTypes.EDITOR_PAD_PICK_PLACE</c>, but the offset/rotate/root modes pick
    /// parts on left click without checking any lock. So entering build mode switches
    /// the editor to place mode and locks the gizmo and root tools, which
    /// <c>EditorToolsUI.SetMode</c> honours. The camera never reads the left button,
    /// so it stays free for orbiting and zooming while building.
    /// </summary>
    internal class BlockBuildMode : MonoBehaviour
    {
        private const string LockPrefix = "Kerblox_BlockBuildMode_";
        private const ControlTypes Locks =
            ControlTypes.EDITOR_PAD_PICK_PLACE | ControlTypes.EDITOR_PAD_PICK_COPY | ControlTypes.EDITOR_ICON_PICK |
            ControlTypes.EDITOR_GIZMO_TOOLS | ControlTypes.EDITOR_ROOT_REFLOW |
            // Undo restores a ship snapshot, which would replace the part under the tool.
            ControlTypes.EDITOR_UNDO_REDO |
            // The actions/crew screens hide the parts being edited.
            ControlTypes.EDITOR_MODE_SWITCH;

        private const float MaxRayDistance = 2000f;
        private const int WindowId = 0x4B42_4D31; // "KBM1", any constant unique among IMGUI windows

        /// <summary>The block placed on left click. Remembered across build sessions.</summary>
        private static BlockState selected = BlockState.Parse("minecraft:stone");

        public static BlockBuildMode Active { get; private set; }

        private ModuleBlockGrid module;
        private readonly List<BoxCollider> colliders = new List<BoxCollider>();
        private GameObject ghost;
        private Material ghostMaterial;
        private int openedFrame;
        private bool closing;
        private bool edited;
        private string status = "";
        // Per instance, so a closing tool removing its lock at end of frame can't
        // release the lock of the one that replaced it.
        private string lockId;
        private Rect windowRect = new Rect(260f, 120f, 240f, 10f);

        #region Open and close

        public static bool IsActiveOn(ModuleBlockGrid m) => Active != null && Active.module == m;

        /// <summary>Puts <paramref name="m"/>'s part into build mode, closing any other part's.</summary>
        public static void Open(ModuleBlockGrid m)
        {
            if (!HighLogic.LoadedSceneIsEditor || EditorLogic.fetch == null) return;
            if (Active != null) Active.Close();

            // A part held on the cursor isn't in the ship; switching to place mode
            // would keep it held, and every click here would also try to drop it.
            Part held = EditorLogic.SelectedPart;
            if (held != null && !EditorLogic.fetch.ship.Contains(held))
            {
                ScreenMessages.PostScreenMessage("Drop the held part before building blocks", 3f, ScreenMessageStyle.UPPER_CENTER);
                return;
            }

            EditorLogic.fetch.toolsUI.SetMode(ConstructionMode.Place);
            var tool = m.gameObject.AddComponent<BlockBuildMode>();
            tool.module = m;
            Active = tool;
            m.OnBuildModeChanged(true);
            Log.Info($"Build mode on for {m.part.partInfo?.name}");
        }

        /// <summary>User-initiated exit: records one editor undo step if anything changed.</summary>
        public void Close()
        {
            if (closing) return;
            closing = true;
            if (edited && EditorLogic.fetch != null)
                EditorLogic.fetch.SetBackup();
            Destroy(this);
        }

        private void Awake()
        {
            openedFrame = Time.frameCount;
            lockId = LockPrefix + GetInstanceID();
            InputLockManager.SetControlLock(Locks, lockId);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
        }

        private void OnSceneLoadRequested(GameScenes scene)
        {
            // No undo snapshot while the scene is being torn down.
            closing = true;
            Destroy(this);
        }

        private void OnDestroy()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            InputLockManager.RemoveControlLock(lockId);
            if (ghost != null) Destroy(ghost);
            if (ghostMaterial != null) Destroy(ghostMaterial);
            if (Active == this) Active = null;
            if (module != null) module.OnBuildModeChanged(false);
            Log.Info("Build mode off");
        }

        #endregion

        #region Input

        private void Update()
        {
            if (closing) return;
            if (module == null || EditorLogic.fetch == null || !HighLogic.LoadedSceneIsEditor)
            {
                closing = true;
                Destroy(this);
                return;
            }

            // Something (a dialog closing, a scene reset) may have cleared every lock.
            if (InputLockManager.GetControlLock(lockId) == ControlTypes.None)
                InputLockManager.SetControlLock(Locks, lockId);

            if (!TryPick(out BlockPick pick))
            {
                SetGhostVisible(false);
                return;
            }

            bool remove = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool eyedrop = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            ShowGhost(remove || eyedrop ? pick.Hit : pick.Place, remove ? GhostRemove : eyedrop ? GhostPick : GhostPlace);

            // Skip the frame the PAW button was clicked in, and act on press so a
            // camera drag that starts on the part doesn't fire on release.
            if (!Input.GetMouseButtonDown(0) || Time.frameCount <= openedFrame + 1) return;

            if (eyedrop) Eyedrop(pick.Hit);
            else if (remove) Remove(pick.Hit);
            else Place(pick.Place);
        }

        private void Place(Int3 c)
        {
            GridEdit e = module.SetBlock(c.X, c.Y, c.Z, selected);
            if (e.Status == GridEditStatus.TooLarge) status = $"Grid can't grow past {VoxelGrid.MaxDimension} blocks";
            else if (e.Changed) { edited = true; status = ""; }
        }

        private void Remove(Int3 c)
        {
            // An empty grid has no colliders left to click on, so the part could never
            // be built on again.
            if (module.blockCount <= 1)
            {
                status = "Can't remove the last block";
                return;
            }
            if (module.RemoveBlock(c.X, c.Y, c.Z).Changed) { edited = true; status = ""; }
        }

        private void Eyedrop(Int3 c)
        {
            BlockState s = module.GetBlock(c.X, c.Y, c.Z);
            if (!s.IsAir) selected = s;
        }

        /// <summary>
        /// Raycasts only this part's box colliders, so blocks can be edited behind other
        /// parts, and resolves the nearest hit to cells with <see cref="BlockPicker"/>.
        /// </summary>
        private bool TryPick(out BlockPick pick)
        {
            pick = default;
            if (MouseOverUi()) return false;

            Camera cam = EditorLogic.fetch.editorCamera;
            Transform root = module.GridRoot;
            if (cam == null || root == null) return false;

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            module.GetGridColliders(colliders);
            bool found = false;
            RaycastHit best = default;
            foreach (BoxCollider c in colliders)
            {
                if (c.Raycast(ray, out RaycastHit h, MaxRayDistance) && (!found || h.distance < best.distance))
                {
                    best = h;
                    found = true;
                }
            }
            if (!found) return false;

            Vector3 p = root.InverseTransformPoint(best.point);
            Vector3 n = root.InverseTransformDirection(best.normal);
            var layout = new BlockLayout(module.SizeX, module.SizeY, module.SizeZ, module.blockSize);
            pick = BlockPicker.FromHit(layout, new Float3(p.x, p.y, p.z), new Float3(n.x, n.y, n.z));
            // Colliders cover exactly the solid cells, so this only fails on float edge cases.
            return !module.GetBlock(pick.Hit.X, pick.Hit.Y, pick.Hit.Z).IsAir;
        }

        private bool MouseOverUi()
        {
            // Same check EditorLogic uses before acting on a click (PAWs, part list, toolbar).
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return true;
            Vector2 guiMouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            return windowRect.Contains(guiMouse);
        }

        #endregion

        #region Ghost

        private static readonly Color GhostPlace = new Color(0.3f, 1f, 0.4f, 0.35f);
        private static readonly Color GhostRemove = new Color(1f, 0.25f, 0.2f, 0.45f);
        private static readonly Color GhostPick = new Color(0.3f, 0.6f, 1f, 0.35f);

        /// <summary>
        /// The ghost is a root-level GameObject, not a child of the part: anything under
        /// the part would be picked up by its renderer caches, highlighting and drag cube
        /// render. It has no collider, so KSP's own editor raycasts never hit it.
        /// </summary>
        private void ShowGhost(Int3 cell, Color color)
        {
            if (ghost == null)
            {
                ghost = GameObject.CreatePrimitive(PrimitiveType.Cube);
                ghost.name = "KerbloxBuildGhost";
                ghost.layer = module.part.gameObject.layer;
                DestroyImmediate(ghost.GetComponent<Collider>());

                // KSP/Alpha/Unlit Transparent takes its tint from _Color (PartReader).
                Shader shader = Shader.Find("KSP/Alpha/Unlit Transparent");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
                ghostMaterial = new Material(shader) { name = "KerbloxBuildGhost" };
                ghost.GetComponent<MeshRenderer>().sharedMaterial = ghostMaterial;
            }

            Transform root = module.GridRoot;
            var layout = new BlockLayout(module.SizeX, module.SizeY, module.SizeZ, module.blockSize);
            Float3 c = layout.CellCenter(cell.X, cell.Y, cell.Z);
            ghost.transform.position = root.TransformPoint(new Vector3(c.X, c.Y, c.Z));
            ghost.transform.rotation = root.rotation;
            // Slightly oversized so it doesn't z-fight the faces it sits on or replaces.
            ghost.transform.localScale = root.lossyScale * (module.blockSize * 1.02f);
            ghostMaterial.color = color;
            SetGhostVisible(true);
        }

        private void SetGhostVisible(bool visible)
        {
            if (ghost != null && ghost.activeSelf != visible) ghost.SetActive(visible);
        }

        #endregion

        #region Palette window

        private void OnGUI()
        {
            if (closing || module == null) return;
            GUI.skin = HighLogic.Skin;
            windowRect = GUILayout.Window(WindowId, windowRect, DrawWindow, "Kerblox blocks", GUILayout.Width(240f));
        }

        private void DrawWindow(int id)
        {
            GUILayout.Label("Click: place\nShift+click: remove\nCtrl+click: pick block");
            foreach (BlockType t in ModuleBlockGrid.Registry.Types)
            {
                bool isSelected = selected.Name == t.Name;
                string label = (isSelected ? "> " : "") + t.Name;
                if (GUILayout.Button(label))
                    selected = BlockState.Parse(t.Name);
            }
            if (ModuleBlockGrid.Registry.Get(selected) == null || selected.HasProperties)
                GUILayout.Label("Picked: " + selected.Canonical);
            if (status.Length > 0) GUILayout.Label(status);
            if (GUILayout.Button("Done")) Close();
            GUI.DragWindow();
        }

        #endregion
    }
}
