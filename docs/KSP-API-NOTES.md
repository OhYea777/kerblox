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

## Drag cubes

| Fact | Status |
| --- | --- |
| `DragCubeSystem.Instance.RenderProceduralDragCube(Part)` returns a `DragCube` | Verified |
| `DragCubeList`: `Cubes`, `ClearCubes()`, `ResetCubeWeights()`, `ForceUpdate(bool weights, bool occlusion, bool resetProcTiming = false)`, `SetDragWeights()` | Verified |
| `DragCube` fields are private; public members are `Area`, `Center`, `Size`, `Name`, `Weight` | Verified (compile error with lowercase) |
| Stock `DRAG_CUBE { procedural = True }` re-renders every `ProceduralDragUpdateInterval` in flight | Verified (`DragCubeList` ~line 373) |
| Re-rendering is safe only when `FlightGlobals.ready` (flight) or when the part is attached to `EditorLogic.RootPart` and not on layer TransparentFX (editor) | Taken from ROUtils `DragCubeTool`; in-game |

## Rendering

| Fact | Status |
| --- | --- |
| Stock code uses `Shader.Find("KSP/Diffuse")`, so the shader exists at runtime | Verified |
| `Part.ResetModelRenderersCache()` and `ResetModelMeshRenderersCache()` exist; highlight lists come from `FindModelRenderersCached()` | Verified |
| Generated mesh highlights correctly on mouseover | In-game |

## Attach nodes

| Fact | Status |
| --- | --- |
| `AttachNode` has public `id`, `position`, `originalPosition`, `orientation`, `size` | Verified |
| `Part.FindAttachNode(string)` exists | Verified by compile |
| Moving a node with a part attached doesn't move that part | Unverified (Procedural Parts moves attached parts manually, see P2.2) |

## Open questions

- Maximum length of a ConfigNode value in craft and save files (`gridData`). See P2.4.
- Whether editor raycasts for surface attach hit child box colliders on the part's layer. In-game, P1.3.
