# Roadmap

Each item is one branch and one PR. Agents pick items whose status is `todo` and
whose dependencies are all `done`. See the `kerblox-orchestrate` skill.

**Item fields**

- **Status:** `todo` · `in-progress` · `review` (PR open) · `blocked` · `done`
- **Depends on:** item ids that must be `done` first
- **Gate:** `none`, or `in-game`: the item needs the user to verify it in KSP
  before it can be `done` (agents can't run the game)
- **Scope / Acceptance:** what to build and the observable proof that it works

An item with an in-game gate moves to `review` when its PR is open. It stays
there until the user reports the checks pass.

---

## Phase 1: a block grid part that flies

Goal: a part loads a hardcoded voxel grid, builds mesh and collider, and flies
with correct mass, CoM and drag.

### P1.1: Core grid data model and codec
- **Status:** done
- **Depends on:** none
- **Gate:** none
- **Scope:** `BlockState` (32-bit: type + state), `BlockRegistry` with stable
  ids and Minecraft names, `VoxelGrid` (Y-major, revision counter, change
  events), `GridCodec` (binary RLE, base64url text safe for ConfigNode).
- **Acceptance:** round-trip and corruption tests in `tests/Kerblox.Core.Tests`.

### P1.2: Core geometry: mesher, box merge, mass properties
- **Status:** done
- **Depends on:** P1.1
- **Gate:** none
- **Scope:** face-culled mesh with outward winding and atlas UVs, greedy
  non-overlapping box merge, mass/CoM/occupied bounds.
- **Acceptance:** tests for face counts, winding, exact box coverage, CoM.

### P1.3: KSP part and ModuleBlockGrid
- **Status:** review
- **Depends on:** P1.2
- **Gate:** in-game
- **Scope:** `kerbloxBlockGrid` part cfg (borrowed stock model, stripped at
  load), `ModuleBlockGrid`: mesh, box colliders, `IPartMassModifier`,
  `CoMOffset`, stack nodes on grid faces, drag cube re-render, grid persisted
  in `gridData`.
- **Acceptance:** the checklist in [docs/TESTING-IN-GAME.md](docs/TESTING-IN-GAME.md).

### P1.4: Build and deploy tooling
- **Status:** done
- **Depends on:** none
- **Gate:** none
- **Scope:** KSPBuildTools with `KSP_ROOT`, `scripts/deploy.sh`,
  `scripts/ksp-logs.sh`, CI for Core tests, Orca worktree setup.
- **Acceptance:** `scripts/deploy.sh` installs into a clean KSP; CI green.

### P1.5: Correct inertia tensor for mixed-density grids
- **Status:** todo
- **Depends on:** P1.3
- **Gate:** in-game
- **Scope:** Unity derives `rb.inertiaTensor` from colliders assuming uniform
  density, which is wrong for an iron-bottomed grid. Compute the tensor
  (and rotation) in Core from block masses, and apply it in the plugin.
  First verify via decompile whether KSP overwrites `inertiaTensor` (e.g. on
  pack/unpack or mass change) and hook in after it.
- **Acceptance:** Core tests against analytic box inertia; in game, an
  asymmetric grid's rotation response matches expectation (document the test).

### P1.6: Block physics from config
- **Status:** todo
- **Depends on:** P1.8
- **Gate:** in-game
- **Scope:** load per-block physical properties (density, solid) from
  `KERBLOX_BLOCK {}` ConfigNodes in GameData, keyed by Minecraft block name,
  with wildcard rules (`mekanism:*`) and a default for unknown modded blocks.
  Ship the current defaults as a cfg. The parser lives in Core and doesn't
  depend on KSP's ConfigNode (the plugin adapts it).
- **Acceptance:** Core parser and rule-precedence tests; changing a density in
  the cfg changes part mass in the VAB.

### P1.7: Model-driven mesher
- **Status:** todo
- **Depends on:** P1.8
- **Gate:** in-game
- **Scope:** generalise `GridMesher` from fixed cubes to per-state models:
  quads grouped by cull face, per-state occlusion flags, render layers
  (solid / cutout / translucent as submeshes), tint colour, multiple atlases.
  The current procedural cube and atlas become the built-in model source, and
  the map-colour cube becomes the fallback for unknown states. See
  [docs/RENDERING.md](docs/RENDERING.md).
- **Acceptance:** Core tests for cull-face culling against occluding and
  non-occluding neighbours, layer separation and fallback; the default capsule
  looks unchanged in the VAB.

### P1.8: Palette-based grid format (codec v2)
- **Status:** done
- **Depends on:** P1.3
- **Gate:** none
- **Scope:** replace fixed 16-bit type ids with a per-grid **palette** of
  canonical Minecraft block-state strings (`ns:block[prop=val,...]`,
  properties sorted). Cells store palette indices, and index 0 is
  `minecraft:air`, as in Minecraft's own chunk palettes. This is needed for
  arbitrary modded blocks. `GridCodec` v2 writes the palette then the RLE
  indices, and still decodes v1 (map the old ids to their names). Change
  events and revisions stay as they are. Do this before P2.1, while the only
  grids in the wild are the hardcoded default.
- **Acceptance:** Core tests: palette round-trip, canonical ordering, v1 → v2
  migration, palette compaction after removals; the plugin builds and loads
  an old v1 `gridData`.

---

## Phase 2: building in the editor

Goal: place and remove blocks on the grid part inside the VAB/SPH.

### P2.1: Grid editing API in ModuleBlockGrid
- **Status:** done
- **Depends on:** P1.8
- **Gate:** none
- **Scope:** `SetBlock`/`RemoveBlock` on the module that edits the grid,
  re-encodes `gridData`, rebuilds geometry, marks the drag cube dirty and fires
  `GameEvents.onEditorShipModified`. Core: grid resize and auto-grow when
  placing at the edge, with an origin shift reported so the plugin can keep the
  existing blocks fixed in world space.
- **Acceptance:** Core tests for resize/auto-grow and origin shift; the plugin
  builds; KSP API members used are recorded in KSP-API-NOTES.

### P2.2: Attach node and attached-part maintenance on edit
- **Status:** done
- **Depends on:** P2.1
- **Gate:** in-game
- **Scope:** when the grid changes, move stack nodes and any parts attached to
  them (and surface-attached children whose block was removed). Verify how
  Procedural Parts moves attached parts (`MovePartByAttachNode`).
- **Acceptance:** extending a grid upward in the VAB carries the parts above it.

### P2.3: Editor block placement tool
- **Status:** done
- **Depends on:** P2.1
- **Gate:** in-game
- **Scope:** a build mode toggled from the part's PAW. Raycast against the
  part's box colliders, place on the hit face or remove the hit block, ghost
  preview, block palette. Input must not leak into KSP's editor (part
  pickup, camera). Verify editor input-lock APIs (`InputLockManager`) by decompile.
- **Acceptance:** place/remove blocks with mouse; part pickup unaffected outside build mode.

### P2.4: Undo, symmetry and craft round-trip
- **Status:** todo
- **Depends on:** P2.3
- **Gate:** in-game
- **Scope:** confirm editor undo/redo restores grids (it snapshots craft
  ConfigNodes, so `gridData` should ride along). Define behaviour for parts in
  symmetry (edit all counterparts). Test save/load and the maximum
  `gridData` length a craft file tolerates.
- **Acceptance:** documented results; any fixes needed land here.

### P2.5: Large-grid performance
- **Status:** todo
- **Depends on:** P2.3
- **Gate:** in-game
- **Scope:** chunked or incremental remesh so single-block edits on 64³ grids
  stay under a frame budget; measure first, then optimise. Core benchmarks.
- **Acceptance:** benchmark numbers in the PR; editing stays smooth on a 64³ grid.

---

## Phase 3: Minecraft bridge

Goal: a NeoForge 1.21.1 Minecraft instance mirrors the grid and syncs edits
with KSP. NeoForge rather than Fabric so the big tech mods (Mekanism, Create,
AE2) run alongside it, and its capability API (energy, fluid and item handlers)
gives one generic way into every mod's machines.
Pattern from [SkyCraft](https://github.com/chasmlol/SkyCraft): one shared
protocol header, plus a fake stand-in for each side so each half can be tested
alone. Draft design: [docs/BRIDGE.md](docs/BRIDGE.md). Rendering modded blocks
exactly as they look in Minecraft: [docs/RENDERING.md](docs/RENDERING.md)
(P3.7–P3.12).

### P3.1: Protocol specification
- **Status:** todo
- **Depends on:** P1.8
- **Gate:** none
- **Scope:** finalise docs/BRIDGE.md: transport choice (Unix socket vs
  `/dev/shm` ring buffers), header layout, versioning, message set (hello,
  full sync = `GridCodec` payload, block delta, redstone state, ack/resync).
  Target NeoForge 1.21.1 on a **dedicated server** (decided 2026-10-03:
  redstone and modded machine logic run server-side, and the user can join
  with a normal client to build in Minecraft).
- **Acceptance:** spec merged; user has signed off on the transport.

### P3.2: C# bridge transport in Core
- **Status:** todo
- **Depends on:** P3.1
- **Gate:** none
- **Scope:** protocol implementation in a new `ksp/src/Kerblox.Bridge` project
  (net48 + net10.0, no Unity), message codec, reconnection, revision-based
  resync.
- **Acceptance:** loopback tests between two in-process endpoints.

### P3.3: Fake Minecraft stand-in
- **Status:** todo
- **Depends on:** P3.2
- **Gate:** none
- **Scope:** `tools/fake-minecraft` (net10 console app) that speaks the
  protocol, holds a grid, applies and emits edits, and toggles a fake redstone
  signal on a timer.
- **Acceptance:** scripted session against the P3.2 endpoint in CI.

### P3.4: NeoForge mod with fake KSP stand-in
- **Status:** todo
- **Depends on:** P3.1
- **Gate:** none
- **Scope:** `minecraft/` Gradle project (NeoForge 1.21.1, ModDevGradle):
  mirrors a bounded region as the grid, speaks the protocol;
  `tools/fake-ksp` stand-in that drives it.
- **Acceptance:** fake-ksp round-trip passes against a dev server in CI or a script.

### P3.5: Minecraft launcher on Linux
- **Status:** todo
- **Depends on:** P3.4
- **Gate:** none
- **Scope:** script that launches a NeoForge dedicated server with the mod
  against a local void world, with its lifecycle tied to KSP's.
- **Acceptance:** `scripts/mc-run.sh` starts and stops cleanly.

### P3.6: KSP-side bridge client
- **Status:** todo
- **Depends on:** P2.1, P3.2
- **Gate:** in-game
- **Scope:** editor-only sync between `ModuleBlockGrid` and the bridge;
  handles connect/disconnect and conflicting edits by revision.
- **Acceptance:** with FakeMinecraft, edits round-trip in the VAB; then with the real client.

### P3.7: Render pack format specification
- **Status:** todo
- **Depends on:** P1.7
- **Gate:** none
- **Scope:** specify the render pack in `protocol/render-pack.md`: manifest
  (pack id = hash of mods and resource packs), compact atlases with animation
  metadata, the state-model binary, per-grid placed-model overrides, and
  versioning. Shared constants for C# and Java. Cache location
  `~/.cache/kerblox/renderpacks/<pack-id>/`, never inside GameData.
- **Acceptance:** spec merged; C# reader and writer round-trip tests in Core
  against hand-built fixtures.

### P3.8: Client-side render exporter
- **Status:** todo
- **Depends on:** P3.4, P3.7
- **Gate:** none
- **Scope:** NeoForge client code that, when the player joins the server (and
  via a `/kerblox export` command), exports every grid region the server
  announces. For each block: baked-model quads queried with the block's real
  `ModelData`, neighbours and render type; tints resolved at the block's real
  position; sprites copied into compact atlases. Writes state models where the
  model depends only on the block state, and placed models where it depends on
  world context (connected textures, neighbours). Verify the exact NeoForge
  1.21.1 model APIs from its sources before relying on them.
- **Acceptance:** export of a test world with vanilla stairs, fences, glass
  panes, grass (tinted), leaves (cutout), stained glass (translucent) and a
  connected-texture block; golden-file tests of the emitted quads.

### P3.9: Block entity renderer capture
- **Status:** todo
- **Depends on:** P3.8
- **Gate:** none
- **Scope:** capture BER output (chests, Mekanism machines' dynamic parts,
  Create kinetics with Flywheel's backend off) by rendering into a recording
  `MultiBufferSource` and converting the triangles to placed-model geometry.
  Re-export when the block entity's state changes, throttled.
- **Acceptance:** a vanilla chest and a Mekanism energy cube export non-empty
  geometry; a fluid tank's level change produces a new snapshot.

### P3.10: KSP render pack loader and layered shaders
- **Status:** todo
- **Depends on:** P1.7, P3.7
- **Gate:** in-game
- **Scope:** load render packs at KSP startup and on change, feed them into the
  model-driven mesher, and pick the pack whose id matches the grid's server.
  Add cutout and translucent submeshes with KSP shaders verified by decompile.
  Fall back to the map-colour cube when there's no export.
- **Acceptance:** a grid exported from the test world renders in the VAB with
  correct textures, tints, culling and transparency.

### P3.11: Animated and emissive textures
- **Status:** todo
- **Depends on:** P3.10
- **Gate:** in-game
- **Scope:** play exported texture animations (frame strips and timings) by
  updating UVs or atlas regions; mark emissive quads (lit lamps, glowstone,
  machine screens) to render with KSP's emissive property.
- **Acceptance:** lava, water and a lit redstone lamp animate or glow in the VAB.

### P3.12: Fidelity check against the live server
- **Status:** todo
- **Depends on:** P3.8, P3.9, P3.10
- **Gate:** in-game
- **Scope:** side-by-side screenshots of the same build in Minecraft and in
  KSP for vanilla, Mekanism and Create; record every mismatch as a fix or a
  documented limitation (lighting and AO are known differences).
- **Acceptance:** comparison sheet in `docs/`; no unexplained mismatches.

---

## Phase 4: Minecraft mechanics drive the craft

### P4.1: Frozen flight grid and tick scheduler
- **Status:** todo
- **Depends on:** P3.6
- **Gate:** in-game
- **Scope:** snapshot the grid at launch; run block logic at Minecraft's 20 Hz
  against the snapshot, decoupled from physics frames and time warp.
- **Acceptance:** tick rate stable across physics warp; no grid edits in flight.

### P4.2: Redstone to action groups, staging and throttle
- **Status:** todo
- **Depends on:** P4.1
- **Gate:** in-game
- **Scope:** output blocks whose redstone power maps to action groups,
  staging, and throttle (power level / 15).
- **Acceptance:** a lever wired to an output block toggles an action group in flight.

### P4.3: TNT as decoupler
- **Status:** todo
- **Depends on:** P4.2
- **Gate:** in-game
- **Scope:** igniting TNT splits the grid along the blast into separate
  vessels (each its own grid part). Needs research into splitting a part at runtime.
- **Acceptance:** documented design first, then a working split.

### P4.4: Furnaces as engines
- **Status:** todo
- **Depends on:** P4.2
- **Gate:** in-game
- **Scope:** lit furnaces facing outward produce thrust via an engine module
  with generated thrust transforms.
- **Acceptance:** thrust and fuel draw visible in flight.

### P4.6: Modded machines through NeoForge capabilities
- **Status:** todo
- **Depends on:** P4.2
- **Gate:** in-game
- **Scope:** generic mapping of NeoForge capabilities on any block entity:
  `IEnergyStorage` (FE) to ElectricCharge, `IFluidHandler` tanks to KSP
  resources, `IItemHandler` to cargo. Start with Mekanism as the reference
  mod; per-mod adapters only where the generic path falls short.
- **Acceptance:** a Mekanism energy cube in the grid shows up as ElectricCharge in flight.

### P4.5: Chests and hoppers as resource storage
- **Status:** todo
- **Depends on:** P4.2
- **Gate:** in-game
- **Scope:** container blocks contribute KSP resource capacity; hoppers move
  resources between them.
- **Acceptance:** resources show in the PAW and flow as expected.
