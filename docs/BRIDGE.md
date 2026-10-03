# Minecraft bridge (draft)

**Status: draft**, input to roadmap item P3.1. The "Decided" section is
settled; everything under "Open decisions" needs the user.

## Goal

A NeoForge 1.21.1 **dedicated server** holds a mirror of each block-grid part.
A player can join it with a normal client to build, and watch edits appear in KSP.
While a craft is in the VAB/SPH, block edits flow in both directions. In flight,
Minecraft runs the block logic (redstone, then modded machines) against a frozen
copy and streams outputs (powered output blocks, energy, fluids) back to KSP.

## Pattern

Taken from [SkyCraft](https://github.com/chasmlol/SkyCraft):

- One protocol definition in `protocol/` that both sides implement, with its
  constants generated or checked so the C# and Java sides can't drift apart.
- A **fake stand-in for each side**, so each half can be developed and tested
  alone:
  - `tools/fake-minecraft` (C#, net10) drives the KSP side
  - `tools/fake-ksp` drives the NeoForge mod
- CI runs each real side against the other side's fake.

## What the grid model already provides

- **Full sync**: a `GridCodec` payload (magic `KBGR`, versioned, RLE).
- **Deltas**: `VoxelGrid.Changed` events carry `(x, y, z, old, new, revision)`.
- **Divergence detection**: `VoxelGrid.Revision`. On a mismatch, request a full sync.
- **Block state**: `BlockState` is Minecraft's own canonical block-state string
  (`minecraft:repeater[delay=2,facing=east,...]`), held in a per-grid palette, so
  redstone power, facing and arbitrary modded states map 1:1.

## Decided

- **NeoForge 1.21.1** (not Fabric): Mekanism, Create and AE2 overlap there, and
  capabilities give generic access to modded energy, fluids and items.
- **Dedicated server** (not a headless client): redstone and machine logic are
  server-side anyway.

Rendering data does not flow through the bridge protocol: the client writes
render packs straight to the local cache. See [RENDERING.md](RENDERING.md).

## Open decisions

1. **Transport**: a Unix domain socket (simple framing, natural
   backpressure, easy to debug with `socat`) or `/dev/shm` ring buffers
   (lower latency, the SkyCraft approach, but needs manual synchronisation).
   For editor-time sync, latency barely matters. For flight-time redstone at
   20 Hz, either is fast enough. Leaning towards a socket unless measurements say otherwise.
2. **World mapping**: one region per grid part (e.g. spaced plots in a void
   world), or one dimension per craft.
3. **Ownership during flight**: Minecraft is authoritative for block state,
   KSP for geometry (no geometry edits in flight).
