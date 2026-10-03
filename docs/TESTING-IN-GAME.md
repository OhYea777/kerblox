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
| Left click on the outer face of the top, bottom or a side | The grid grows to take the block (log: `[Kerblox] Grid on ... resized`). Existing blocks stay put (P2.2 compensates for the recentring). |
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

## P2.2: attached parts follow grid edits

Needs a way to edit a grid in the VAB, i.e. P2.3's build mode. Before each
edit, note where the existing blocks are against the VAB background grid. After
each edit run `scripts/ksp-logs.sh`: every compensation logs a
`[Kerblox] Moved ...` line.

Build: Kerblox grid as the **root**, a stock tank on its top node, a stock
engine on its bottom node, and a radial part (e.g. a fin or a small RCS thruster)
surface-attached to the side of the solid iron base (bottom layer), with symmetry off.

| Check | Expected |
| --- | --- |
| Add a block above the top layer (grow +Y) | Existing blocks don't move. The tank rises one block (0.625 m) and stays on the node. Engine and fin don't move. |
| Add a block below the bottom layer (grow -Y) | Existing blocks don't move. The engine drops one block. Tank and fin don't move. |
| Add a block beside the grid (grow ±X, then ±Z) | Existing blocks don't move. Tank and engine shift half a block sideways with the nodes, which stay centred on the widened grid. The fin doesn't move. |
| Remove the block directly under the fin | The fin slides inward one block onto the next solid block and stays attached: picking up the grid takes the fin with it. `[Kerblox] ... slid 1 block(s)` in the log. |
| Attach a second fin to the hollow stone wall and remove the block under it | Nothing solid is directly behind it (the capsule is hollow), so the fin stays where it was and the log warns `lost its block ... left in place`. |
| Remove the whole top layer | The tank drops onto the new top face. |

Now make the Kerblox grid a **child**: a stock probe core as root, the grid on
its bottom node via the grid's top node.

| Check | Expected |
| --- | --- |
| Grow the grid downward | The grid stays attached under the probe core; its bottom (and the engine on it) moves down one block. |
| Grow the grid upward | The grid stays attached; existing blocks and the engine move down one block (the top node stays at the probe core). |
| Offset tool reset (Offset gizmo, then reset) on the tank after an edit | The tank snaps to its node, not to where it was before the edit. |
| Save and reload the craft after edits | Parts load at their moved positions, still attached. |

## P2.4: undo, symmetry and craft round-trip

Build mode records one editor undo step when it closes. Undo and redo reload
the whole craft from a saved snapshot, which rebuilds each grid from its
`gridData` (`OnLoad`). Edits in build mode apply to every symmetry counterpart:
radial counterparts get the same cell, mirror counterparts the mirror-image cell.
After each section run `scripts/ksp-logs.sh`; there should be no exceptions and
no `[Kerblox] Can't relate ... mirror counterpart` warnings.

### Undo and redo (VAB)

Kerblox grid as the root, a stock tank on its top node.

| Check | Expected |
| --- | --- |
| Build blocks, place three blocks, **Done**, then Ctrl+Z | All three blocks disappear in one step. PAW Blocks and mass return to their old values and the button reads **Build blocks** (not Stop building). |
| Ctrl+Y (redo) | The three blocks come back. |
| Build blocks, add a block above the top layer (the tank rises), **Done**, Ctrl+Z | The grid shrinks back and the tank returns to the top node, still attached. |
| Two build sessions in a row, then Ctrl+Z twice | The first Ctrl+Z undoes only the second session; the second undoes the first. |
| Ctrl+Z while build mode is open | Nothing happens (undo is locked while building). |

### Radial symmetry (VAB)

A large stock tank (e.g. Rockomax X200-32) as the root. With 4x radial symmetry,
surface-attach four Kerblox grids to its side.

| Check | Expected |
| --- | --- |
| Build blocks on one grid; place a block on its outward face | All four grids get the block in the same place relative to the tank: the craft stays 4-way symmetric. |
| Shift+click a block on one grid | That block disappears on all four. |
| Grow one grid upward and sideways | All four grow the same way; existing blocks don't move and all four stay attached to the tank. |
| Attach a winglet to one grid with symmetry, then edit under it | Every grid's winglet follows (P2.2), one per grid. |
| **Done**, then Ctrl+Z | One undo step reverts all four grids. |

### Mirror symmetry (SPH)

A Mk1 fuselage as the root. With mirror symmetry, surface-attach two Kerblox
grids to its left and right sides. Then repeat with grids rotated before
placing (rotate the held part with W/S/A/D/Q/E).

| Check | Expected |
| --- | --- |
| Build blocks on the left grid; place a block on one corner, toward the nose | The right grid gets one on the mirror-image corner, also toward the nose: seen from above, the craft is mirror-symmetric about the fuselage. |
| Place a block on its outward face (away from the fuselage) | The right grid grows outward too, not toward the fuselage. |
| Remove a block, then grow along the fuselage axis | Mirrored on the other grid both times; both stay attached. |
| Same checks with the rotated grids | Still mirror images. If not, the log has a `Can't relate` warning or the wrong side changed: report which. |

Blocks with a facing (e.g. an eyedropped stair) are copied with the same
facing, not turned to match the mirror. That is expected for now.

### Save and load

| Check | Expected |
| --- | --- |
| Save each craft above, go to the Space Center, come back and load it | Same shapes, masses and attachments. |
| After loading, edit one grid of a symmetric set | All counterparts still change together (symmetry survives the round trip). |
| Launch the radial craft, then revert to the VAB | Same shapes. |
| In flight, F5 then F9 | Same shapes. |

### Maximum gridData length

`gridData` has no length limit in KSP's file format (see
[KSP-API-NOTES.md](KSP-API-NOTES.md)); this checks how big a grid the game
handles. Generate worst-case crafts (64³ cubes of noise that defeat the codec's
run-length encoding):

```sh
scripts/stress-crafts.sh            # writes to ~/.cache/kerblox/stress-crafts
scripts/stress-crafts.sh "" 64,128  # also 128³ (14 MB craft files)
```

Copy the `.craft` files into `saves/<your save>/Ships/VAB/` yourself.

| Craft | gridData | File |
| --- | --- | --- |
| `Kerblox stress 64-4` (4 block types) | 699,191 chars | 0.7 MB |
| `Kerblox stress 64-65535` (65,535 block states, the palette limit) | 4,855,584 chars | 4.9 MB |
| `Kerblox stress 128-4` | 5,592,546 chars | 5.6 MB |
| `Kerblox stress 128-65535` | 14,025,316 chars | 14 MB |

| Check | Expected |
| --- | --- |
| Open the VAB Load menu | The stress crafts are listed. Note how long the list takes to appear. |
| Load `Kerblox stress 64-4` | A solid 40 m cube of mixed stone, planks, iron and wool. PAW Blocks: **262,144**. Note the load time. |
| Load `Kerblox stress 64-65535` | Same cube shape and block count. Note the load time. |
| On either: place one block (build mode), **Done**, Ctrl+Z, Ctrl+Y | Works; note any hitch per step (each undo step stores a full copy of `gridData`). |
| Save it under a new name, reload it | Same cube. The new file is about the same size as the generated one. |
| (Optional) the 128³ crafts | Note load time and memory; failures here set the practical grid size limit for P2.5. |

Report the load times; they decide whether P2.5 needs to cap grid size.

## Reporting back

Paste the output of `scripts/ksp-logs.sh` and note each failing row in the
tables above. That's enough to move P1.3 to `done` or to file the fixes.
