# Rendering modded blocks (design)

**Goal:** a block-grid part in KSP looks like the same blocks you see when you
join the Minecraft server: same geometry, same textures, same tints, connected
textures and machine details. That includes arbitrary NeoForge mods (Mekanism,
Create, ...), not just vanilla.

**Approach:** Minecraft renders, and KSP replays the result. A client-side
exporter in the NeoForge mod asks Minecraft for the finished geometry of each
block and writes it to a local *render pack*. KSP only draws render packs. It
never parses Minecraft models itself.

Roadmap: P1.7, P1.8, P3.7–P3.12.

## Why not reimplement Minecraft's model system in C#

Parsing blockstate and model JSON from mod jars covers vanilla and simple mods
only. It misses:

- custom baked models: OBJ models, and dynamic models driven by `ModelData`,
  such as connected textures
- block entity renderers (BERs): chests, Mekanism fluid and energy displays,
  Create's kinetic parts when Flywheel is off
- client-side tints (grass, leaves, redstone wire power) and multipart quirks

Reimplementing all of that would mean chasing every mod forever.

## Pipeline

```
NeoForge client (your normal client, joined to the server)
  └─ exporter: for each block in a grid's world region
       ├─ baked model quads, queried with the block's real ModelData and neighbours
       ├─ BER output captured through a recording buffer source
       ├─ tint resolved in the block's real biome and position
       └─ sprites copied into a compact atlas (only the sprites used)
          → render pack in ~/.cache/kerblox/renderpacks/<pack-id>/

KSP (no Minecraft needed at runtime)
  └─ render pack loader → Core model-driven mesher → one mesh per part,
     with a submesh per render layer (solid / cutout / translucent)
```

### Two granularities

| Kind | Key | When |
| --- | --- | --- |
| **State model** | full block-state string, e.g. `minecraft:stone_stairs[facing=north,half=bottom,shape=straight]` | The model depends only on the block state. Exported once per pack and reused by every grid. |
| **Placed model** | grid id + cell | The model depends on world context: `ModelData` (connected textures), neighbours, or BER state. Exported per grid snapshot. |

The exporter tries the state model first and records a placed model only when
the block needs one. Detecting that reliably (non-empty `ModelData`, a
registered BER, a model that varies with neighbours) is part of P3.8.

### Face culling

Minecraft groups each block's quads by cull face: quads returned for a
direction disappear when the neighbour in that direction occludes, and the rest
always draw. The export keeps that grouping plus each state's occlusion
flags, so KSP's mesher can cull exactly as Minecraft does. The current
six-face cube mesher becomes the special case of a "full cube" model.

### Render layers

Solid, cutout (alpha-tested, e.g. leaves) and translucent (e.g. stained glass)
become separate submeshes with different KSP shaders. Shader names must be
verified by decompiling before use (`ksp-api-verify`). Translucent sorting
within one combined mesh is approximate.

## Render pack contents (spec owned by P3.7)

- `manifest.json`: format version, pack id, Minecraft and NeoForge versions,
  the mod list with versions, the resource pack list, and the export time.
  The pack id is a hash of the mod list and resource packs, so changing
  modpacks never mixes renders.
- `atlas-N.png`: compact atlases containing only the sprites used, plus
  animation metadata (frame strips, frame times).
- `states.bin`: state models, as quads in block-local space (position, UV,
  normal, tint colour, cull face, render layer, emissive flag) plus occlusion flags.
- `grids/<grid-id>.bin`: placed-model overrides for a specific grid snapshot.

## What will not match exactly

- **Lighting.** KSP lights the craft with its own sun and dynamic lighting.
  Minecraft's light levels and smooth ambient occlusion don't carry over.
  Baking AO into vertex colours is possible later, but KSP's stock shaders ignore
  vertex colour, so it would need a custom shader. Custom shaders need an asset
  bundle built in the Unity Editor.
- **Animated BERs** (spinning shafts, moving pistons) are snapshots. They are
  refreshed when the block's state changes, not animated per frame.
- **Shaders and post-processing** from client mods like Iris don't apply.

## Fallbacks

The fallbacks, in order: placed model, then state model, then map-colour cube.
A block's map colour is known server-side, so a grid always renders, even
before any client has exported anything.

## Licensing

Render packs contain Mojang's and mod authors' textures. They are generated on
the user's machine, live in a cache outside GameData, and must never be
committed to this repo or shipped with the mod.
