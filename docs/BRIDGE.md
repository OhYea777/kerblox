# Minecraft bridge (draft)

**Status: draft.** This is input to roadmap item P3.1, not a decision. Decisions
marked **open** need the user.

## Goal

A NeoForge 1.21.1 Minecraft instance holds a mirror of each block-grid part.
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
- **Block state**: the high 16 bits of `BlockState` hold per-block state such as
  redstone power and facing, and block names already match Minecraft's ids.

## Open decisions

1. **Transport**: a Unix domain socket (simple framing, natural
   backpressure, easy to debug with `socat`) or `/dev/shm` ring buffers
   (lower latency, the SkyCraft approach, but needs manual synchronisation).
   For editor-time sync, latency barely matters. For flight-time redstone at
   20 Hz, either is fast enough. Leaning towards a socket unless measurements say otherwise.
2. **Dedicated server or headless client**: redstone and modded machine
   logic run server-side. A dedicated server is the simpler headless target,
   and a player could join it with a normal client to build in Minecraft.
3. **World mapping**: one region per grid part (e.g. spaced plots in a void
   world), or one dimension per craft.
4. **Ownership during flight**: Minecraft is authoritative for block state,
   KSP for geometry (no geometry edits in flight).
