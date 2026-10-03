# KSP API notes

Facts about KSP 1.12.5's API that this mod relies on. Each entry records where
it was verified. **Verified** means it was read in the decompiled
`Assembly-CSharp.dll` (KSP 1.12.5.3190, Linux), or confirmed by compiling against
it. **Unverified** means it's believed true but not yet confirmed.
**In-game** means only a running game can settle it.

To check something yourself, use the `ksp-api-verify` skill. Line numbers refer
to `ilspycmd -p` output and are only a rough guide.

## Toolchain

| Fact | Status |
| --- | --- |
| KSP 1.12.5.3190 runs on Unity 2019.4.18f1 (`Player.log`: "Initialize engine version") | Verified |
| `Assembly-CSharp.dll` targets .NET Framework 4.x runtime `v4.0.30319` | Verified |
| `KSP_Data/Managed` has no `netstandard.dll`, so netstandard2.0 libraries won't load | Verified (directory listing) |
| KSPBuildTools 1.1.1 reads `KSP_ROOT` and references `KSP_Data/Managed/*.dll`, including the game's `mscorlib` | Verified (KSPCommon.targets) |
| KSPBuildTools has no Steam auto-detect on Linux | Verified (KSPCommon.props sets `_KSPBT_SteamGameRoot` only for Windows/OSX) |
| HarmonyKSP latest is 2.2.1.0 (2022); ModuleManager latest is 4.2.3 | Verified (GitHub releases, 2026-10) |

## Part compilation (`PartLoader`)

| Fact | Status |
| --- | --- |
| A part must have a MODEL; with none, compilation fails with "PartCompiler: Cannot compile model" | Verified (`ParsePart`) |
| `rescaleFactor` defaults to 1.25 and scales the model transform | Verified (`ParsePart`: `num = 1.25f`) |
| Order in `ParsePart`: modules are added and loaded, then `CreatePartIcon`, then `part.collider = GetPartCollider(...)` | Verified |
| `GetPartCollider` picks the first non-trigger collider that isn't tagged Airlock or Ladder | Verified |
| `CompileParts` loads modules a second time, then renders drag cubes (`DragCubeSystem.SetupDragCubeCoroutine`) | Verified |
| `PartLoader.Instance.IsReady()` is false during compilation | Verified (ROUtils relies on it; method exists) |

## Mass and CoM

| Fact | Status |
| --- | --- |
| `IPartMassModifier { float GetModuleMass(float defaultMass, ModifierStagingSituation); ModifierChangeWhen GetModuleMassChangeWhen(); }` | Verified |
| `Part.UpdateMass` sums `GetModuleMass(prefabMass, CURRENT)` into `mass = prefabMass + moduleMass`, clamped to `partInfo.MinimumMass` | Verified |
| `Part.CoMOffset` is a public field; `rb.centerOfMass = Vector3.zero + CoMOffset` is set when the rigidbody is created | Verified (`Part` ~line 8024) |
| Whether KSP re-applies `CoMOffset` to an existing rigidbody later | Unverified, so the module sets `rb.centerOfMass` itself |
| Unity's inertia tensor from colliders assumes uniform density | Unity behaviour, see P1.5 |
| `Part.Start` (flight) creates the rigidbody, yields one frame, then snapshots `rb.inertiaTensor / Mathf.Max(1f, rb.mass)` into the private field `Part.inertiaTensor` (Vector3), before `ModulesBeforePartAttachJoint` and long before `ModulesOnStartFinished` | Verified (`Part.Start`; `PromoteToPhysicalPart` snapshots the same way) |
| `Part.FixedUpdate` in flight (once `started`) calls `ValidateInertiaTensor()` every frame: if `rb.inertiaTensor != inertiaTensor * Mathf.Max(1f, rb.mass)` and every component of the field is > 1e-6, it writes that value back. So setting `rb.inertiaTensor` alone is undone next physics frame; the private field must be updated too (the module does this by reflection) | Verified (`Part.ValidateInertiaTensor`, `Part.FixedUpdate`) |
| Nothing in stock code writes `inertiaTensorRotation` on a part's `rb` (only `ModuleRoboticServoRotor` on its moving-part rb); `VesselPrecalculate` reads both `rb.inertiaTensor` and `rb.inertiaTensorRotation` to build the vessel MOI | Verified (grep of the decompile) |
| `FlightIntegrator.UpdateMassStats` sets `rb.mass = max(MinimumRBMass, part.mass + resourceMass + physicsless child mass)` (halved with a servo rb), so rb.mass can change in flight | Verified |
| `PartModule.OnStartFinished(StartState)` is virtual and called from `Part.Start` via `ModulesOnStartFinished` | Verified |
| No stock pack/unpack path calls `ResetInertiaTensor` on part rigidbodies (only `ModuleCargoPart`, `ModuleGroundPart` and VehiclePhysics do, on their own parts) | Verified (grep of the decompile) |
| Once `inertiaTensor`/`inertiaTensorRotation` are set explicitly, Unity keeps them rather than recomputing from colliders | Unity behaviour, in-game (P1.5) |

## Drag cubes

| Fact | Status |
| --- | --- |
| `DragCubeSystem.Instance.RenderProceduralDragCube(Part)` returns a `DragCube` | Verified |
| `DragCubeList`: `Cubes`, `ClearCubes()`, `ResetCubeWeights()`, `ForceUpdate(bool weights, bool occlusion, bool resetProcTiming = false)`, `SetDragWeights()` | Verified |
| `DragCube` fields are private; public members are `Area`, `Center`, `Size`, `Name`, `Weight` | Verified (compile error with lowercase) |
| Stock `DRAG_CUBE { procedural = True }` re-renders every `ProceduralDragUpdateInterval` in flight | Verified (`DragCubeList` ~line 373) |
| Re-rendering is safe only when `FlightGlobals.ready` (flight) or when the part is attached to `EditorLogic.RootPart` and not on layer TransparentFX (editor) | Taken from ROUtils `DragCubeTool`; in-game |
| `RenderProceduralDragCube` clones the live part: `Object.Instantiate(p, Vector3.zero, Quaternion.identity)`, then `SetupPartForRender` sets `enabled = false` on every `MonoBehaviour` under it, then `Object.Destroy(clone)` before returning. So every component on the part GameObject gets `Awake` (during `Instantiate`) and `OnDestroy` (end of frame) on a copy whose non-serialized private fields are default; `Update`/`OnGUI` never run. Extra components on the part must stay inert when not initialised (see `BlockBuildMode`) | Verified (`DragCubeSystem.RenderProceduralDragCube` ~line 2593, `SetupPartForRender` ~line 1987); log symptom seen in game |

## Editor events

| Fact | Status |
| --- | --- |
| `GameEvents.onEditorShipModified` is `EventData<ShipConstruct>`; stock code fires it as `.Fire(EditorLogic.fetch.ship)` after editor-side part changes (`UIPartActionFieldItem`, `ModuleProceduralFairing`) | Verified (`GameEvents` ~line 761) |
| `EditorLogic.fetch` (static) and `EditorLogic.ship` (`ShipConstruct`) are public fields | Verified (`EditorLogic` ~lines 535, 543) |
| `HighLogic.LoadedSceneIsEditor` is a public static bool, true when the scene is `GameScenes.EDITOR` | Verified (`HighLogic` ~line 1251) |
| `Part.UpdateMass()` is public and re-polls every `IPartMassModifier`; the editor also calls it per part in `ShipConstruct.GetShipMass` | Verified |
| `ModifierChangeWhen` (`FIXED`/`STAGED`/`CONSTANTLY`) is only read by `DeltaVPartInfo` (skips `STAGED`); it doesn't gate `UpdateMass` | Verified |
| Listeners (engineer report, dV, `ModuleCargoBay`) pick up a grid edit from `onEditorShipModified` | In-game, P2.3 |

## Rendering

| Fact | Status |
| --- | --- |
| Stock code uses `Shader.Find("KSP/Diffuse")`, so the shader exists at runtime | Verified |
| `Part.ResetModelRenderersCache()` and `ResetModelMeshRenderersCache()` exist; highlight lists come from `FindModelRenderersCached()` | Verified |
| `KSP/Alpha/Cutoff` (with `_Cutoff`) and `KSP/Alpha/Translucent` exist at runtime: `PartTools` caches them as `shaderCutout`/`shaderAlpha`, and `PartReader` builds alpha-cutout and alpha materials from them | Verified (`PartTools`, `PartReader.ReadMaterial`) |
| Whether `KSP/Diffuse`, `KSP/Alpha/Cutoff` and `KSP/Alpha/Translucent` have a `_Color` that tints `_MainTex` | Unverified: stock code sets `_Color` only on other shaders, and the compiled shader assets don't expose property names. `BlockAtlas` checks `HasProperty("_Color")` and logs a warning; in-game |
| Generated mesh highlights correctly on mouseover | In-game |
| Highlighting and part fading work with several materials on one generated renderer | In-game |

## Attach nodes

| Fact | Status |
| --- | --- |
| `AttachNode` has public `id`, `position`, `originalPosition`, `orientation`, `size` | Verified |
| `Part.FindAttachNode(string)` exists | Verified by compile |
| `AttachNode` also has public `attachedPart`, `owner`, `nodeType` (`Stack`/`Surface`/`Dock`), `offset` and `nodeTransform` | Verified (`AttachNode`) |
| `Part.attachNodes` (`List<AttachNode>`), `srfAttachNode`, `parent`, `children`, `attachMode` (`AttachModes.STACK`/`SRF_ATTACH`) are public fields | Verified (`Part` ~lines 1981-2507) |
| `Part.FindAttachNodeByPart(p)` scans `attachNodes`, then returns `srfAttachNode` if *its* `attachedPart` is `p`; it dereferences `srfAttachNode` without a null check, so `AttachmentKeeper` scans `attachNodes` itself | Verified (`Part.FindAttachNodeByPart`) |
| `srfAttachNode` is the `node_attach` node (`FindAttachNode("attach")`, or `"srfAttach"`); a part without one has none | Verified (`Part` ~line 17626, `PartLoader` ~line 3888) |
| Editor attach (`EditorLogic.attachPart`): `part.setParent(parent)`, `part.transform.parent = parent.transform`, `callerPartNode.attachedPart = parent`, `otherPartNode.attachedPart = part`, then `attPos0 = transform.localPosition`, `attRotation0 = localRotation` | Verified |
| So in the editor every attached part's transform is a child of its parent part's transform: moving a part carries its subtree | Verified (`attachPart`); in-game, P2.2 |
| For surface attach the caller node is the child's `srfAttachNode` and `otherPartNode` is null: the parent's nodes don't reference surface children | Verified (`EditorLogic` ~line 11086) |
| Surface attach placement: `attachment.rotation = LookRotation(hit.normal, parentUp) * LookRotation(srfAttachNode.orientation, up)`, `attachment.position = hit.point - attachment.rotation * attRotation * srfAttachNode.position`, part rotation = `attachment.rotation * attRotation`. So the hit point is `transform.TransformPoint(srfAttachNode.position)` at unit scale, and the surface normal can be recovered from the rotation | Verified (`EditorLogic` ~lines 11092, 3869) |
| Stack attach point is `parent.transform.TransformPoint(node.position + node.offset)` | Verified (`EditorLogic` ~line 10778) |
| `attPos` is the offset-tool offset, `localPosition - attPos0`; offset "reset" sets `localPosition = attPos0`. Both are written to craft files (`ShipConstruct`), so code that moves an attached part must also move `attPos0` | Verified (`EditorLogic` ~lines 7533, 7616; `ShipConstruct` ~line 263) |
| Setting `AttachNode.position` doesn't move the part attached there | Verified (plain field; Procedural Parts moves attached parts itself, see below) |
| Procedural Parts (`ProceduralAbstractShape`, 2026-10): on a length change it moves each stack node; if the node's part is a child it translates it by the node delta, if it's the parent it translates itself by the negated delta. Surface children are moved with `MovePartByAttachNode`, which translates `node.owner.transform` by the world delta of the attach point. It doesn't update `attPos0` | Read from GitHub source, not a KSP fact |
| Moved parts stay attached, render in place, and save/load at the new positions | In-game, P2.2 |

## Editor input and build mode (P2.3)

| Fact | Status |
| --- | --- |
| `InputLockManager.SetControlLock(ControlTypes, string id)` stores the mask under the id (replacing an existing one) and ORs all ids into `lockMask`; `RemoveControlLock(id)` ignores unknown ids; `GetControlLock(id)` returns `ControlTypes.None` for unknown ids | Verified (`InputLockManager`) |
| Editor part pickup in place mode (`on_partPicked`) and Delete-key part deletion are gated on `InputLockManager.IsUnlocked(ControlTypes.EDITOR_PAD_PICK_PLACE)` | Verified (`EditorLogic.SetupFSM`, `DeleteInputUpdate` ~line 9250) |
| The offset/rotate/root modes' left-click selection (`on_offsetSelect`, `on_offsetDeselect`, ...) picks parts without checking any lock, so a tool that owns left click must keep the editor in place mode | Verified (`EditorLogic.SetupFSM`) |
| `EditorLogic.fetch.toolsUI` is a public `EditorToolsUI`; `SetMode(ConstructionMode, bool updateUI = true)` refuses Move/Rotate while `EDITOR_GIZMO_TOOLS` is locked and Root while `EDITOR_ROOT_REFLOW` is locked; Place is always allowed unless a held part isn't in the ship. Hotkeys 1-4 go through `SetMode` | Verified (`EditorToolsUI.Update`, `SetMode`) |
| Switching to Place with `selectedPart` in the ship goes to `st_idle`; with a held part not in the ship it stays in `st_place` | Verified (`on_goToModePlace`) |
| `EditorLogic.SelectedPart` (static) returns `fetch.selectedPart` or null | Verified |
| Undo/redo input is gated on `EDITOR_UNDO_REDO` | Verified (`EditorLogic.UndoRedoInputUpdate`) |
| `EditorLogic.SetBackup()` is public, snapshots the ship (`ShipConstruction.CreateBackup`), truncates redo states, and fires `onEditorSetBackup` and `onEditorShipModified` | Verified |
| `VABCamera`/`SPHCamera` only move while `CAMERACONTROLS` is unlocked and never read the left mouse button (orbit via `AXIS_CAMERA_HDG/PITCH`, pan on button 2, zoom on the wheel) | Verified (`VABCamera`, `SPHCamera`) |
| `EditorLogic.editorCamera` is the public editor `Camera` (from `FindObjectOfType<EditorCamera>()`) | Verified (`EditorLogic.Awake` area ~line 1139) |
| `EditorLogic` skips clicks when `EventSystem.current.IsPointerOverGameObject()` (PAWs, part list, toolbar) | Verified |
| `ControlTypes` editor bits: `EDITOR_ICON_PICK`, `EDITOR_PAD_PICK_PLACE`, `EDITOR_PAD_PICK_COPY`, `EDITOR_GIZMO_TOOLS`, `EDITOR_ROOT_REFLOW`, `EDITOR_UNDO_REDO`, `EDITOR_MODE_SWITCH` exist | Verified (`ControlTypes`) |
| `KSPEvent` has public `guiActive`, `guiActiveEditor`, `guiName`; `PartModule.Events[string]` returns the `BaseEvent`, whose `guiName` is settable | Verified (`KSPEvent`, `BaseEvent`, `BaseEventList`) |
| Changing `BaseEvent.guiName` updates an open PAW's button label | In-game, P2.3 |
| `HighLogic.Skin` is a public static `GUISkin` | Verified |
| `ScreenMessages.PostScreenMessage(string, float, ScreenMessageStyle)` exists | Verified |
| `GameEvents.onGameSceneLoadRequested` is `EventData<GameScenes>` | Verified (`GameEvents` ~line 442) |
| `KSP/Alpha/Unlit Transparent` takes its tint from `_Color` | Verified (`PartReader` sets `_Color` on it); looks right in-game: In-game, P2.3 |
| Other `EditorLogic` left-click handlers in idle place mode do nothing harmful while `EDITOR_PAD_PICK_PLACE` is locked | In-game, P2.3 |

## Open questions

- Maximum length of a ConfigNode value in craft and save files (`gridData`). See P2.4.
- Whether editor raycasts for surface attach hit child box colliders on the part's layer. In-game, P1.3.
