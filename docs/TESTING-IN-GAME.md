# Testing in game

These steps verify roadmap item P1.3: the block grid part loads, builds, and
flies with correct mass, CoM and drag. Agents can't run KSP, so a human does
this and reports back.

## 1. Install

```sh
export KSP_ROOT="$HOME/.local/share/Steam/steamapps/common/Kerbal Space Program"
scripts/deploy.sh
```

You should now have:

```
$KSP_ROOT/GameData/Kerblox/Plugins/Kerblox.dll
$KSP_ROOT/GameData/Kerblox/Plugins/Kerblox.Core.dll
$KSP_ROOT/GameData/Kerblox/Parts/BlockGrid/BlockGrid.cfg
```

## 2. Loading screen

Start KSP from Steam (the native Linux build). Once it reaches the main menu,
run `scripts/ksp-logs.sh` in another terminal. Expect:

```
AssemblyLoader: Loading assembly at .../GameData/Kerblox/Plugins/Kerblox.Core.dll
AssemblyLoader: Loading assembly at .../GameData/Kerblox/Plugins/Kerblox.dll
PartLoader: Compiling Part 'Kerblox/Parts/BlockGrid/BlockGrid/kerbloxBlockGrid'
[Kerblox] Built prefab grid 4x8x4: 96 blocks, 6.856 t, CoM (0.0, -0.8, 0.0), 6 colliders
```

The `[Kerblox] Built prefab grid` line probably appears twice, because KSP
loads part modules twice during compilation.

## 3. VAB checks

Start a **Sandbox** game, open the VAB, and search the parts list for `kerblox`.
The part is **Kerblox Block Grid**, under Structural.

| Check | Expected |
| --- | --- |
| Part icon in the list | A small blocky capsule, not the stock micronode |
| Place it as the root part | A 2.5 m × 5 m blocky capsule: iron base, grey stone, light-blue glass band, plank cap, white wool nose. Pixelated block textures. |
| No see-through faces | Orbit the camera; no missing faces from outside. Looking through the glass band you see the inside walls of the hollow interior. |
| Mouseover highlight | The whole capsule highlights (green or orange rim). |
| Right-click (PAW) | Blocks: **96**, Block mass: **6.856 t**, Colliders: **6** |
| Engineer's Report / mass readout | Total ≈ **6.866 t** (6.856 t of blocks plus a 0.01 t frame) |
| Centre-of-mass overlay (bottom-left button) | The CoM ball sits **0.84 m below** the geometric centre, pulled toward the iron base |
| Stack nodes | Attach nodes on the top and bottom faces, centred. Attach a 2.5 m engine (e.g. Skipper) below and a probe core or command pod on top. |
| Surface attach | Fins and radial parts attach flush to the side faces, including onto the glass band. |
| Save and reload the craft | Same shape and numbers after loading. |

## 4. Flight checks

Add an engine and a probe core (or pod), then launch.

| Check | Expected |
| --- | --- |
| On the pad | Sits still: no jitter, explosion, or sinking into the pad. |
| Liftoff | Flies straight under thrust and doesn't wobble apart (it's one rigidbody). |
| Drag | Turn on Alt+F12 → Physics → Aero → "Display Aero Data in Action Menus", then right-click the grid part in flight. Drag and area values are non-zero and similar to a stock 2.5 m part of the same length. |
| CoM in flight | Same behaviour as in the VAB. A side-mounted engine produces the torque you'd expect from a low CoM. |
| Revert to launch, and quicksave/quickload (F5/F9) | The part rebuilds identically. |

## P2.3: Block placement tool

In the VAB, with the grid part placed as the root:

| Check | Expected |
| --- | --- |
| Right-click the part (PAW) | A **Build blocks** button. Clicking it opens a **Kerblox blocks** window listing stone, oak planks, iron block, glass and white wool, and the button now reads **Stop building**. The editor switches to place mode. |
| Hover the part | A translucent green cube shows where a block would go, on the face under the mouse. Holding Shift turns it red over the block that would be removed; Ctrl turns it blue. |
| Left click on a face | A block of the selected type appears there. PAW Blocks and Block mass go up; the Engineer's Report mass follows. |
| Left click on the outer face of the top, bottom or a side | The grid grows to take the block (log: `[Kerblox] Grid on ... resized`). The rest of the grid shifts by half a block in part space; that's expected until P2.2. |
| Shift+left click a block | It disappears. Removing the last block is refused with a message in the window. |
| Ctrl+left click a block | That block becomes the selected type (shown as "Picked: ..." if it isn't in the list). |
| Pick a block in the window, then place | The new type is placed. Clicks on the window itself never place blocks. |
| Left click on another part, or on the grid part, while building | The part is **not** picked up. Keys 2/3/4 (offset/rotate/root) do nothing; Delete does nothing; Ctrl+Z does nothing. |
| Camera while building | Right-drag orbits, scroll zooms, middle-drag pans, exactly as normal. No left click moves the camera. |
| Build behind another part | Blocks hidden behind another part can still be targeted (only the grid's own colliders are raycast). |
| **Stop building** (PAW) or **Done** (window) | The window and ghost disappear. Part pickup, gizmo tools and undo work again. One Ctrl+Z undoes the whole build session (stock undo snapshot taken on exit). |
| Start building, then exit to the Space Center | No stuck input locks: in the next VAB visit, parts pick up normally. The log shows `[Kerblox] Build mode off` before the scene change. |
| Build mode on one grid part, then **Build blocks** on a second one | The first one's session ends; only the second is edited. |
| Save, reload the craft | The edited shape and numbers survive. |

Log lines: `[Kerblox] Build mode on for kerbloxBlockGrid` and `[Kerblox] Build mode off`.

## 5. Logs

| Log | Where | What it contains |
| --- | --- | --- |
| `KSP.log` | `$KSP_ROOT/KSP.log` | KSP's own log: assembly loading, part compiler, `[Kerblox]` lines. Rewritten on every launch. |
| `Player.log` | `~/.config/unity3d/Squad/Kerbal Space Program/Player.log` | Unity's log: everything above plus native crashes, shader errors, and full exception stacks. |

`scripts/ksp-logs.sh` greps both logs for Kerblox lines and exceptions;
`--all` adds every exception and error. What specific failures look like:

| Symptom in the log | Meaning |
| --- | --- |
| No `AssemblyLoader: Loading assembly at .../Kerblox.dll` | DLLs aren't in GameData. Re-run `scripts/deploy.sh`. |
| `AssemblyLoader: Exception loading 'Kerblox'` or `ReflectionTypeLoadException` | Assembly mismatch, such as building against the wrong KSP. Check `KSP_ROOT`. |
| `Cannot find a PartModule of typename 'ModuleBlockGrid'` | The DLL failed to load. Look for the AssemblyLoader exception above it. |
| `PartCompiler: Cannot clone model 'Squad/Parts/Structural/structuralMicronode/model'` | The borrowed stock model is missing. Verify the game files. |
| `[Kerblox] Corrupt gridData ...` | A craft file holds an unreadable grid. The part falls back to the default capsule. |
| `[Kerblox] Shader 'KSP/Diffuse' not found` | Shader lookup failed. The part renders with Unity's fallback. |
| `NullReferenceException` with `ModuleBlockGrid` in the stack | A bug. Send the stack from `Player.log`. |
| `[Kerblox] Drag cube for kerbloxBlockGrid: size ..., areas [...]` | Normal: a drag cube was rendered, once in the VAB when attached to the root and once on entering flight. |

## Reporting back

Paste the output of `scripts/ksp-logs.sh` and note each failing row in the
tables above. That's enough to move P1.3 to `done` or to file the fixes.
