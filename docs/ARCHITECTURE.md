# Architecture

## Projects

| Project | Targets | References | Ships to GameData |
| --- | --- | --- | --- |
| `ksp/src/Kerblox.Core` | net48, net10.0 | nothing | yes (net48 build) |
| `ksp/src/Kerblox` | net48 | Core, KSP + Unity DLLs via KSPBuildTools | yes |
| `ksp/tests/Kerblox.Core.Tests` | net10.0 | Core, xUnit | no |

Core is multi-targeted so the same source runs inside KSP's Mono (net48) and
under modern .NET for tests and, later, the bridge's fake stand-ins. KSPBuildTools
compiles the plugin against the game's own `mscorlib`, so there is no framework
mismatch at runtime.

## Grid data model (`Kerblox.Core`)

- **`BlockState`**: a Minecraft block state in canonical string form,
  `namespace:block[prop=value,...]` with properties sorted by name (brackets
  omitted when empty, missing namespace = `minecraft`). Equality is ordinal on
  that string. `default(BlockState)` is `minecraft:air`.
- **`BlockRegistry`**: maps block names (the state without properties) to
  `BlockType` (density in t/m³, solid, opaque, colour, tile pattern). Unknown
  blocks behave as air but keep their state string in the grid. `Ids` survives
  only as the v1 id table for migrating old craft files.
- **`VoxelGrid`**: dense, fixed size up to 256³. Storage is Y-major
  (`(y * SizeZ + z) * SizeX + x`). Cells are `ushort` indices into a per-grid
  palette of `BlockState`s, index 0 = air, like Minecraft chunk sections; entries
  are appended on write and dropped by `Compact()`. Out-of-bounds reads return air. `Revision`
  increments on every effective edit, and `Changed` fires per single-block edit.
  Those are the hooks for editor undo and bridge sync.
- **`GridCodec`** (v2): magic `KBGR`, version byte, three u16 dimensions, a
  varint-counted palette of length-prefixed UTF-8 state strings (entry 0 = air,
  compacted, first-appearance order so equal grids encode identically), then
  (varint run, varint palette index) runs. v1 payloads (u32 fixed-id states) still
  decode, mapped through the old ids, and `ModuleBlockGrid` re-saves them as v2. The text form is base64url without padding,
  because KSP's ConfigNode parser treats `//` as a comment and `=` as a separator.

## Coordinates

Grid axes map directly onto the part's local axes (Unity, left-handed, +Y is
the stack axis). `BlockLayout` centres the grid's bounding box on the part
origin, so cell `(0,0,0)`'s min corner is at `-size * blockSize / 2`.
`blockSize` defaults to 0.625 m, which puts two blocks across a 1.25 m stack.

## Derived geometry

All of this is computed in Core and only copied into Unity objects by the plugin:

- **Mesh** (`GridMesher`): replays a `BlockModel` per palette entry (resolved
  once per entry, not per cell). Models hold quads grouped by cull face plus
  per-face occlusion flags; a quad with a cull face is dropped when the
  neighbour occludes the touching face, or is the same see-through block (glass
  next to glass), and quads without one always draw, as in Minecraft's chunk
  renderer. Quads are split into one submesh per `MaterialKey` (render layer,
  atlas, tint), ordered solid → cutout → translucent. Models come from a
  `BlockModelResolver` chain: `BuiltinModelSource` (one cube per registry type
  on the procedural atlas, 16 px per tile, plus a blank tile), then
  `MapColorFallback` (an opaque tinted cube) for unknown states. Triangles wind
  so that `Cross(b-a, c-a)` points outward, which is Unity's front face.
- **Colliders** (`BoxMerger`): greedy X→Z→Y merge of solid cells, ignoring
  block type. Boxes never overlap and cover exactly the solid cells.
- **Mass** (`MassProperties`): sum of density × block volume. CoM is the
  mass-weighted mean of block centres. Occupied bounds position the stack nodes.

## KSP integration (`ModuleBlockGrid`)

- The part cfg borrows a tiny stock model, because KSP won't compile a part
  without a MODEL. `rescaleFactor = 1` keeps our geometry at true size.
- `OnLoad` (prefab compile and each saved instance) and `OnStart` (instances
  created in the editor from the prefab) call `Rebuild()`:
  1. decode `gridData`, or fall back to `DefaultGrids.Capsule()`
  2. strip the placeholder model (`DestroyImmediate` during compile)
  3. build the mesh and box colliders under `model/KerbloxGrid`
  4. set `part.CoMOffset` (and `rb.centerOfMass` if a rigidbody exists)
  5. move the `top` and `bottom` stack nodes onto the occupied bounds
  6. reset the part's renderer caches so highlighting includes the new mesh
- Mass: `IPartMassModifier.GetModuleMass` returns the block mass and the cfg
  `mass` is a 0.01 t frame.
- Drag: once the part is settled (`FlightGlobals.ready` in flight, attached to
  the editor root in the VAB), `DragCubeSystem.RenderProceduralDragCube`
  replaces the part's cube list. This follows ROUtils' `DragCubeTool`, which
  Procedural Parts uses. Stock `DRAG_CUBE { procedural = True }` is
  deliberately not used, because it re-renders periodically in flight and our
  flight geometry never changes.

Verified API facts and open questions are in [KSP-API-NOTES.md](KSP-API-NOTES.md).

## Planned: rendering modded blocks

Blocks will look the same in KSP as on the Minecraft server. The NeoForge
client exports Minecraft's own baked geometry, block entity renderer output,
tints and textures into a local render pack, and KSP's mesher replays it. See
[RENDERING.md](RENDERING.md). Before that, the grid moves to per-grid
block-state palettes (P1.8), and the mesher becomes model-driven (P1.7).

## Planned: the Minecraft bridge

See [BRIDGE.md](BRIDGE.md). The grid data model already provides what the
bridge needs: a flat, versioned binary snapshot (`GridCodec`), per-edit events
with revisions (`VoxelGrid.Changed`), and Minecraft block-state strings in
`BlockState`, so redstone power, facing and modded states map 1:1. The bridge will sit in its own project beside Core and won't
modify it.
