# Minecraft bridge protocol

**Status: specification v1.0, transport proposed and awaiting user sign-off**
(roadmap item P3.1). The wire format below is complete and doesn't depend on
the transport choice. Sections marked *proposed* still need the user. Everything
else is the contract P3.2 (C#) and P3.4 (Java) implement.

Shared machine-readable files live in [`protocol/`](../protocol):

- [`bridge-constants.json`](../protocol/bridge-constants.json): message types,
  error codes and limits. The tables here and that file must agree. P3.2 and
  P3.4 each test their constants against it.
- [`vectors/gridcodec-v2.json`](../protocol/vectors/gridcodec-v2.json): golden
  grid encodings with their content hashes, generated from `GridCodec`.
- [`vectors/frames.json`](../protocol/vectors/frames.json): golden frames.

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

- **Full sync**: a `GridCodec` payload (magic `KBGR`, v2: palette of canonical
  block-state strings, then RLE runs of palette indices). Equal grids encode to
  identical bytes (see [Canonical grid encoding](#canonical-grid-encoding)), which
  the protocol uses for content hashes.
- **Deltas**: `VoxelGrid.Changed` events carry `(x, y, z, old, new, revision)`.
- **Resizes** (P2.1): `GridEditor.SetBlock` grows the grid to reach a cell
  outside it, and `GridEditor.Trim` shrinks it. Both return a `GridEdit` with
  the new size and the cell shift (old `(x,y,z)` is now `(x+ShiftX, …)`), and
  `ModuleBlockGrid.GridResized` fires. The protocol carries this as `GRID_RESIZE`.
- **Divergence detection**: revisions. The protocol keeps its own per-grid
  counter (see [Revisions](#revisions-and-the-sequencer)), separate from
  `VoxelGrid.Revision`.
- **Block state**: `BlockState` is Minecraft's own canonical block-state string
  (`minecraft:repeater[delay=2,facing=east,...]`), held in a per-grid palette, so
  redstone power, facing and arbitrary modded states map 1:1.

## Decided

- **NeoForge 1.21.1** (not Fabric): Mekanism, Create and AE2 overlap there, and
  capabilities give generic access to modded energy, fluids and items.
- **Dedicated server** (not a headless client), decided 2026-10-03: redstone and
  machine logic are server-side anyway, and the user joins with a normal client
  to build. The bridge connects KSP to the **server process** only. The player's
  client talks to the server over Minecraft's normal network protocol and never
  touches the bridge.

Rendering data doesn't flow through the bridge protocol: the client writes
render packs straight to the local cache. See [RENDERING.md](RENDERING.md).

## Topology

```
 KSP (Mono, net48)                 NeoForge 1.21.1 dedicated server (Java 21)
 ┌──────────────────┐   bridge    ┌──────────────────────────────┐   MC protocol   ┌──────────────┐
 │ ModuleBlockGrid  │◀──────────▶│ Kerblox mod: one region per  │◀──────────────▶│ normal MC    │
 │ Kerblox.Bridge   │  (connects) │ open grid (listens)          │   (TCP 25565)   │ client (user)│
 └──────────────────┘             └──────────────────────────────┘                 └──────────────┘
```

- The **server listens and KSP connects.** The server is the long-lived process,
  and KSP restarts more often (crashes, mod reloads). KSP reconnects with
  backoff from 0.5 s, doubling, up to 5 s.
- The server accepts **one KSP connection at a time**. A new connection replaces
  the old one: the server sends `BYE(replaced)` on the old one and closes it.
- Both processes normally run on the same machine (P3.5 launches the server
  alongside KSP). A server on another machine is supported only through the TCP
  fallback below.

## Transport (proposed, awaiting user sign-off)

The protocol needs a **reliable, ordered, bidirectional** channel. The
conflict and resync rules below depend on in-order delivery. Two candidates:

### Option A: Unix domain socket (stream)

Length-prefixed frames over `SOCK_STREAM`.

- **Java 21:** built in since JDK 16 (JEP 380): `ServerSocketChannel.open(StandardProtocolFamily.UNIX)`
  with `UnixDomainSocketAddress`. No dependencies, works on Linux, macOS and
  Windows 10 1803+.
- **C#, net10** (fakes, tests): `UnixDomainSocketEndPoint`, built in.
- **C#, inside KSP (Mono, net48):** `UnixDomainSocketEndPoint` isn't in .NET
  Framework 4.8, and KSP's `System.dll` doesn't contain it. KSP does ship
  `KSP_Data/Managed/Mono.Posix.dll`, whose `Mono.Unix.UnixEndPoint : EndPoint`
  (AddressFamily.Unix) exists (checked by decompiling the installed copy). P3.2
  should instead carry its own ~20-line `EndPoint` subclass that serialises
  `sockaddr_un`, so one source builds for net48 and net10 with no extra
  reference. *That it actually connects under KSP's Mono runtime is unverified*
  and is P3.6's in-game check.
- **Latency:** a small message's one-way latency on a local Unix socket is
  typically 5–30 µs including the reader thread's wakeup (typical figure, not
  measured here). At 20 Hz the tick budget is 50 ms, so the socket uses under
  0.1 % of it. The real latency floor is elsewhere: KSP drains its receive queue
  once per frame (~16–20 ms) and Minecraft applies work at its next tick (≤ 50 ms).
- **Portability:** Linux and macOS natively (path limit: 107 bytes on Linux,
  103 on macOS). Windows supports AF_UNIX from 10 1803, but KSP's Mono on
  Windows is unverified. The framing is transport-agnostic, so **TCP on
  127.0.0.1** is a drop-in fallback for Windows or for a server on another
  machine: same frames, different endpoint, no protocol change.
- **Complexity:** low. Framing is ~100 lines per side. The kernel provides
  flow control (writes block when the peer stops reading), and a dead peer
  shows up as EOF or `ECONNRESET`. Debuggable with `socat -x` and testable in
  process with socket pairs.
- **Topology fit:** the server listens on a path both sides can compute.
  Nothing persists across crashes except a stale socket file, which the server
  removes at startup after a test connect fails.

### Option B: `/dev/shm` ring buffers (the SkyCraft approach)

Two single-producer single-consumer rings in a shared memory-mapped file, one
per direction, with head/tail counters updated with acquire/release ordering.

- **Java 21:** `FileChannel.map` gives a `MappedByteBuffer` (each mapping under
  2 GiB). Atomic head/tail access goes through
  `MethodHandles.byteBufferViewVarHandle` (acquire/release on aligned
  `int`/`long` in direct buffers). The Foreign Function & Memory API is still
  preview in 21 (final in 22), so no `MemorySegment` without `--enable-preview`.
  Pure Java has no futex or eventfd, so the reader must poll (spin, then
  `LockSupport.parkNanos`).
- **C#, inside KSP:** `System.IO.MemoryMappedFiles` is present in KSP's
  `System.Core.dll`. Acquire/release access to the mapped counters means
  `unsafe` pointers with `Volatile.Read`/`Write`, or `MemoryMappedViewAccessor`
  plus explicit barriers. It works, but it's fiddly and runs on a Mono JIT whose
  memory-model guarantees on mapped memory nobody here has tested.
- **Latency:** with busy polling, sub-microsecond. With polite polling (parking
  around 50–100 µs to avoid burning a core on each side), it's comparable to the
  socket or worse. Either way the per-frame and per-tick floors above dominate,
  so the gain isn't visible at 20 Hz.
- **Portability:** `/dev/shm` is Linux-only. macOS has no `/dev/shm`, and Java
  can't call `shm_open` without FFM, so a plain mmapped file under `$TMPDIR`
  would be needed. On Windows that would be a mapped file in `%TEMP%`. Neither
  covers a server on another machine.
- **Complexity:** high. It needs a versioned segment header, wrap-around, and
  messages larger than the ring (a full sync can be over 100 MiB, see
  [Size limits](#size-limits)). It also needs a wakeup strategy, death
  detection (heartbeats plus PID checks, since there's no EOF) and stale-segment
  cleanup after crashes. Both fakes have to reimplement all of it.
- **Topology fit:** works only when both processes share a machine and a
  filesystem namespace. A containerised server would need the segment
  bind-mounted.

### Recommendation

**Option A, a Unix domain socket, with TCP loopback as a configuration-only
fallback.** At 20 Hz both transports are three orders of magnitude faster than
the per-frame and per-tick latency floors, so ring buffers buy nothing
measurable. Meanwhile they cost polling CPU, a hand-rolled synchronisation
protocol on two runtimes, and Linux-only portability. The SkyCraft trade-off
made sense for high-rate streaming. Ours is low-rate, bursty and correctness-bound.
If P3.2 finds that Unix sockets don't work under KSP's Mono, the fallback is TCP
on `127.0.0.1` with the same frames.

### Endpoint discovery

Both sides read `KERBLOX_BRIDGE`:

| Value | Meaning |
| --- | --- |
| `unix:/abs/path.sock` | Unix socket at that path |
| `tcp:host:port` | TCP. The server binds `host` (default `127.0.0.1`); KSP connects to it |
| unset | `unix:$XDG_RUNTIME_DIR/kerblox/bridge.sock`, else `unix:/tmp/kerblox-<uid>/bridge.sock` |

The server creates the socket directory with mode `0700`. The TCP fallback has
no authentication, so it must bind loopback unless the user explicitly sets
another host.

## Framing

All integers are **little-endian**, matching `GridCodec`. Java's `ByteBuffer`
defaults to big-endian, so the Java side must set `ByteOrder.LITTLE_ENDIAN`.

### Primitive types

| Name | Encoding |
| --- | --- |
| `u8`, `u16`, `u32`, `u64` | unsigned little-endian, fixed width |
| `i32` | signed little-endian, two's complement |
| `varint` | unsigned LEB128, value ≤ 2³²−1, at most 5 bytes (exactly `GridCodec`'s varint). Writers emit the minimal encoding. |
| `str` | `varint` byte length, then UTF-8 bytes, no terminator |
| `state` | a `str` holding a canonical block state (`ns:block[k=v,...]`, properties sorted by name, brackets omitted when empty), 1 to 1024 bytes (`GridCodec.MaxStateLength`) |

The Java side must produce the same canonical strings as `BlockState`:
registry name, then each property as `name=Property#getName(value)`, sorted by
property name (ordinal). The golden vectors include properties.

### Frame header (16 bytes)

```
offset size field
0      u32  length   bytes that follow this field (12 + payload). 12 ≤ length ≤ maxFrame − 4
4      u16  type     message type (table below)
6      u16  flags    bit 0 ACK_REQUESTED; other bits reserved: send 0, ignore on receipt
8      u32  grid     grid handle; 0 = connection-level message
12     u32  seq      per-direction frame counter, see below
16     ...  payload
```

- **`seq`** starts at 1 (HELLO) and goes up by 1 per frame. After
  `0xFFFFFFFF` it wraps to **1, never 0**, so 0 stays free as the "none"
  sentinel in `refSeq` and `originSeq`. A reference to a frame names the most
  recent frame with that seq. At realistic rates (≤ a few thousand frames per
  second) a wrap takes weeks, so no reference is ambiguous.
- `maxFrame` is what the *receiver* announced in HELLO. A frame larger than that
  is a protocol violation. Senders keep every frame within it using the rules in
  [Size limits](#size-limits), so a large grid never causes a violation.
- **Payloads are extensible:** a receiver ignores bytes after the last field it
  knows. Minor versions may only append fields. (This is unlike `GridCodec`,
  which rejects trailing bytes. A nested codec blob always has its own length
  prefix.)

### Size limits

| Limit | Value | Why |
| --- | --- | --- |
| `maxFrame` (announced) | ≥ 1 MiB; 16 MiB recommended | bounds each read buffer; unrelated to grid size |
| `maxGridPayload` | 256 MiB | a safe overestimate of the largest legal `GridCodec` v2 payload bounds it at 134,347,802 bytes (≈ 128.1 MiB): 11 header + 3 palette count + 14 air + 65,535 × (2 + 1024) palette + 256³ × 4 (every cell a 1-byte run plus a 3-byte index). Not every term can be maxed at once (for one thing, indices below 16,384 take fewer bytes), so real payloads are smaller. The limit is about 2× the bound |
| `GRID_OPEN` / `GRID_RESIZE` sizes | 1–256 per axis | `VoxelGrid.MaxDimension` |

What a sender does when a message wouldn't fit the peer's `maxFrame`:

- **FULL_SYNC** is always sent in chunks sized to fit (see FULL_SYNC). A
  one-chunk sync is just the common case.
- **BLOCK_DELTA:** the sequencer sends a FULL_SYNC instead. If the oversized
  delta was a final echo, the FULL_SYNC (which contains the accepted changes)
  is followed by an empty echo for that proposal, so it still gets exactly one
  final echo.
- **BLOCK_EDIT:** the proposer splits it into several edits. Proposals aren't
  atomic anyway.
- Every other message has a small fixed upper bound (strings ≤ 1024 bytes). The
  exception is SIGNAL_* with absurdly many ports, which would be a sender bug.
  Those messages are within 1 MiB by construction.

### Worked example

`BLOCK_DELTA` on grid 1, seq 7, revision 4 → 5, setting cell (2, 0, 3) to
`minecraft:stone` (also in `vectors/frames.json`):

```
39 00 00 00  01 02  00 00  01 00 00 00  07 00 00 00     length=57 type=0x0201 flags=0 grid=1 seq=7
04 00 00 00 00 00 00 00                                 baseRev=4
05 00 00 00 00 00 00 00                                 rev=5
00 00 00 00                                             originSeq=0 (sequencer's own edit)
01  0F 6D 69 6E 65 63 72 61 66 74 3A 73 74 6F 6E 65     1 state: "minecraft:stone"
01  02 00 00 00 03 00  00                               1 change: x=2 y=0 z=3 state#0
```

## Canonical grid encoding

`FULL_SYNC` carries a `GridCodec` v2 payload, and content hashes are FNV-1a 64
over those bytes. So **both sides must produce byte-identical v2 encodings of
equal grids.** The Java side reproduces exactly what `GridCodec.ToBytes` emits
(`ksp/src/Kerblox.Core/GridCodec.cs`, `VoxelGrid.BuildCompactRemap`):

1. `4B 42 47 52` (`KBGR`), then the version byte `02`.
2. `u16` SizeX, SizeY, SizeZ, each 1–256.
3. **Palette:** `varint` count N, then N × (`varint` byte length, UTF-8 canonical
   state).
   - Entry 0 is always `minecraft:air`, **even if no cell is air**.
   - Then every non-air state that occurs in at least one cell, exactly once,
     **in order of first appearance in storage order** (rule 4). A state that no
     cell holds any more (placed, then removed) is not written. How the
     encoder's in-memory palette happens to be ordered has no effect.
4. **Storage order** is Y-major: cell index `(y * SizeZ + z) * SizeX + x`, so x
   varies fastest, then z, then y.
5. **Cells:** (`varint` run length ≥ 1, `varint` palette index) pairs covering
   all `SizeX·SizeY·SizeZ` cells in storage order. Runs are **maximal**: two
   consecutive pairs never have the same index. Runs freely cross row and layer
   boundaries, and a single run can cover the whole grid.
6. All varints are minimal LEB128 (no `0x80`-padded forms). Nothing follows the
   last run.

The decoder is more lenient (it accepts non-minimal varints and non-maximal
runs), but those inputs aren't canonical. Hashes are only defined over
canonical bytes.

**Content hash:** FNV-1a 64 (offset basis `0xcbf29ce484222325`, prime
`0x100000001b3`) over the canonical bytes, sent as `u64`. Revisions and the
anchor offset aren't part of it.

**Golden vectors:** `protocol/vectors/gridcodec-v2.json` lists grids (size and
non-air cells) with their expected `hex`, `base64url` and `fnv1a64`. They cover
an all-air grid, a grid with no air cell, edit order differing from storage
order, sorted properties, a modded namespace, a removed state, runs crossing
rows, and a two-byte run varint. They were generated from `GridCodec` on main and
verified to round-trip. Each side's tests must encode every vector to exactly
`hex` and decode `hex` back to the listed cells. Whenever `GridCodec` changes,
regenerate the vectors and bump the codec version.

## Versioning

- **Protocol version** is `major.minor`, currently **1.0**. Peers with different
  majors refuse to talk: each sends `ERROR(incompatible_version)` and closes.
  Within a major, the effective minor is the lower of the two. A sender must
  not use message types or appended fields newer than that.
- A receiver **ignores unknown message types** (and logs each new one once).
  This keeps a newer peer working when it slips, but it isn't a feature to rely on.
- **Grid payload version:** HELLO carries the highest `GridCodec` version each
  side decodes. Senders encode `FULL_SYNC` at or below the peer's maximum.
  Today that's 2 on both sides, and v1 is never sent over the bridge. Bumping
  `GridCodec` doesn't bump the protocol major, but content hashes are only
  compared when both sides encode the same version.
- The header layout and HELLO's fields up to `maxFrame` are frozen for all
  versions, so any two builds can at least exchange HELLO and fail cleanly.

## Coordinates

- **Cell space:** `0 … size−1` on each axis at the grid's *current* revision.
  BLOCK_DELTA, signals and the `GridCodec` payload use it. It shifts whenever a
  resize adds or removes cells on a low side.
- **Anchor space** (`i32`): a frame fixed for the whole life of a grid handle.
  At the seed, cell `(0,0,0)` is anchor `(0,0,0)`. Each side keeps an
  `anchorOffset` (initially 0) with `anchor = cell + anchorOffset`. A resize with
  shift `s` (old cell `c` becomes `c + s`) sets `anchorOffset −= s`, so every
  surviving block keeps its anchor coordinates. BLOCK_EDIT uses anchor space.
  That way a proposal stays correct even when a resize is sequenced between
  the proposer's view and the sequencer applying it. It can also name cells
  outside the current box, which grows the grid.
- **Minecraft world:** a grid's region is fixed in the world. GRID_READY
  reports the world position of anchor `(0,0,0)`, so world = anchor origin +
  anchor, and cell `(0,0,0)` sits at anchor origin + `anchorOffset`. A resize
  never moves blocks in the world. KSP keeps blocks still in *its* world by
  moving the part (P2.2), which is KSP's business.

## Revisions and the sequencer

Each open grid has a **protocol revision** `rev` (`u64`), owned by the bridge
and separate from `VoxelGrid.Revision`. `VoxelGrid.Revision` counts local edits
and differs between the two sides. `rev` counts sequenced messages, which both
sides apply in the same order:

- The opener's `FULL_SYNC` right after `GRID_OPEN` seeds the grid at `rev = 0`.
  KSP is always the opener.
- After that, exactly one side is the **sequencer** for the grid:
  - **editor** mode: **KSP**. The craft file is the source of truth, and KSP
    is the side that saves it.
  - **flight** mode: **Minecraft** *(proposed, see open decision 3)*. KSP never
    edits a flight grid (AGENTS.md: grids are frozen in flight).
- Only the sequencer sends `BLOCK_DELTA`, `GRID_RESIZE` and (after the seed)
  `FULL_SYNC`. Each one advances `rev` by exactly one, including a delta
  with no changes (an empty echo, see [Proposals](#proposals-mirror-and-live-view))
  and a delta whose changes are all no-ops. A chunked FULL_SYNC counts once.
- The sequencer assigns `rev` and enqueues the message in one atomic step.
  A FULL_SYNC snapshot is taken at the moment it's enqueued, so its position in
  the stream matches its `rev`. Its chunks are sent back to back, with no other
  content frame for that grid in between. (Frames for other grids, and PING,
  may interleave.)
- The other side sends **proposals** (`BLOCK_EDIT`). They show up in its live
  view right away (a Minecraft player's placement has already happened in the
  world), but never in its sequenced mirror (see
  [Proposals](#proposals-mirror-and-live-view)). The sequencer applies proposals
  in arrival order and answers each with exactly one **final echo**: a
  BLOCK_DELTA with `originSeq` set to the proposal's `seq`.
- ACK_REQUESTED is set **only by the sequencer**, and ACK is sent **only by the
  non-sequencer**. The opener's seed is confirmed by GRID_READY instead, which
  covers flight mode, where KSP seeds but Minecraft sequences.

### Proposals, mirror and live view

The non-sequencer keeps two views of each grid:

- The **mirror** is the grid at its current `rev`, built *only* from sequenced
  frames (seed and FULL_SYNC, BLOCK_DELTA, GRID_RESIZE). Its own proposals
  never touch it. Every protocol decision is made against the mirror: `baseRev`
  checks, ACK hashes, and anchor/cell conversion. Minecraft's mirror is an
  in-memory grid (a `short[]` plus palette, like `VoxelGrid`).
- The **live view** is what the user sees: Minecraft's world in editor mode,
  where players' unsequenced placements already exist. It's the mirror plus
  the proposer's **pending** proposals.

A proposal `s` is *pending* from the moment it's sent until its final echo
(the BLOCK_DELTA with `originSeq = s`) has been applied to the mirror. The
sequencer answers every BLOCK_EDIT it can attribute with exactly one final
echo, including malformed ones (see [Undecodable or illegal
edits](#proposals-mirror-and-live-view) below). Before the
echo it may send **at most one** GRID_RESIZE with the same `originSeq`: the
composition of all the growth the proposal caused (`SetBlock` only grows, so
the composition is itself one resize). The echo's changes are in the
post-resize cell space.

**Reconciling the live view:** after applying any sequenced frame to the
mirror, the proposer writes the mirror's state of every cell that frame changed
into the live view, *skipping cells touched by a still-pending proposal* (the
player's newer block stays visible). After applying the final echo of `s`, it
also resets every cell `s` touched to the mirror's state, again skipping cells
touched by a later pending proposal. A touched cell outside the mirror's box is
reset to air. A **reconciliation write** is exactly that: one cell set to the
state the proposer's model of the live view expects. The write itself is never
proposed back. What the world does *in response* to it can be, see
[What Minecraft proposes](#what-minecraft-proposes-in-editor-mode).

The cells a frame **changed** are compared in anchor space, treating any
position outside a box as air:

- BLOCK_DELTA: the cells it lists whose state differs from the mirror's.
- GRID_RESIZE: the non-air cells it drops. Its new cells are air, and surviving
  cells keep their anchor position and state.
- FULL_SYNC: every anchor position where the old mirror and the new mirror
  differ, a plain diff of the two grids. That covers cells that only one of the
  two boxes contains and a changed `anchorOffset`. Implementations may write
  the whole new box and clear the whole old one instead, which gives the same
  result.

**Why this converges:** the channel is ordered, so the proposer applies every
sequenced message in `rev` order, and its mirror equals the sequencer's grid at
each `rev`. Take a KSP edit and a Minecraft edit racing on cell C:

- If KSP's delta is sequenced first, Minecraft skips writing it to the world
  (C is pending), then the echo sets C to the proposal in the mirror. World and
  mirror agree.
- If the proposal is sequenced first, the echo sets C, then KSP's delta
  overwrites C in the mirror and the world.

Either way both sides end in sequencer order. Concurrent edits resolve as
last-writer-wins *in sequencer order*, which for a single builder on each side
is what people expect. Once nothing is pending, the live view equals the mirror.

**Rejections and partial acceptance:** the sequencer may refuse all or part of
a proposal (`GridEditStatus.TooLarge`, a grid that isn't editable right now, a
policy refusal). Its final echo carries **only the changes it actually made**,
possibly none: an **empty echo** (`changeCount = 0`, `stateCount = 0`). An empty
echo still advances `rev` by one, like every sequenced message, so revision
counting has no special case. The proposer's reset step then reverts exactly the
refused cells, including out-of-bounds ones that a TooLarge growth would have
needed, with no extra signalling. For each refusal the sequencer also sends
`ERROR(edit_rejected)` with `refSeq = s`, so the Minecraft side can tell the
player. A no-op change (the cell already held that state) is simply left out of
the echo.

**Undecodable or illegal edits:** "exactly one final echo" covers every
BLOCK_EDIT the sequencer can attribute, meaning its header decoded and its
`grid` names an open grid in a mode that takes proposals. If the payload fails
to decode or breaks the [change rules](#grid-content), the sequencer applies
none of it and answers with an empty final echo (`originSeq = s`) plus
`ERROR(bad_payload, refSeq = s)`. The proposer's reset step reverts every cell
it had shown for `s`, so a malformed proposal is never left pending forever.
The sequencer doesn't resync anything, because its own grid isn't in doubt.
A BLOCK_EDIT whose `grid` is unknown gets `ERROR(unknown_grid)` and no echo,
since the proposer has no open grid for it either. One sent in the wrong
direction or mode gets `ERROR(unexpected_message)` and no echo (see
[Messages](#messages)).

### What Minecraft proposes in editor mode

Minecraft keeps editor regions **frozen**: no scheduled block ticks, random
ticks or fluid ticks. It uses the same mechanism as a paused flight grid
(`SIM_STATE`, P3.4 finds it). Editor regions are always frozen, and SIM_STATE
applies to flight grids only.

**Every change Minecraft observes** to a cell in a grid's region is proposed,
whatever caused it, except its own reconciliation writes. That includes:

- player placement and breaking
- the neighbour shape updates a placement triggers in the same tick (fence and
  wall connections, stair shapes, redstone wire connecting, a door's other half)
- immediate redstone updates from a player flipping a lever
- **the neighbour shape and redstone updates a reconciliation write triggers**
- anything that leaks past the freeze

An *observation* is defined by state, not by call stack: a cell change in the
region is an observation when the world's new state differs from Minecraft's
model of the live view (mirror plus pending proposals) for that cell. A
reconciliation write updates the model and the world to the same state, so it
never matches. Anything it sets off, in a neighbour or in the written cell
itself (wire recomputing its own `power`), does. **Don't implement the
exclusion as a flag (for example a ThreadLocal) held around `level.setBlock`.**
The updates run inside that call, so such a guard would hide them: the world
would change while the mirror didn't, and audits, which hash the mirror, would
never notice.

Reconciliation writes use normal block updates (`Block.UPDATE_ALL`), not
`UPDATE_CLIENTS | UPDATE_KNOWN_SHAPE`. *Why:* KSP doesn't compute connection
shapes, so a fence, wall or wire placed from KSP arrives in whatever state KSP
gave it. With updates suppressed, the player would see a KSP-placed fence that
doesn't join the fence next to it, and redstone wire that ignores its
neighbours, which is not something Minecraft ever produces. Worse, the first
unrelated click nearby would set off the deferred updates in a burst. With
normal updates, the Minecraft world always looks like Minecraft built it, and
the shapes it settles on reach the craft a round trip later as ordinary
proposals. So the craft ends with what the player saw. This converges: the echo
of a shape proposal writes a state the world already holds, which is a no-op
and sets off nothing further. Editor regions are frozen, so the cascade is only
what the edit touches directly. Updates that reach blocks outside the grid's
region aren't observations of this grid; world mapping (open decision 2) keeps
air between regions so that doesn't happen.

Changes are coalesced into one BLOCK_EDIT per grid per server tick. If that
edit wouldn't fit the sequencer's `maxFrame`, Minecraft splits it into several
BLOCK_EDITs sent back to back in the same tick (see [Size limits](#size-limits)),
each a separate proposal with its own final echo. A non-air change outside the
current box is a growth proposal in anchor space.

*Why propose rather than exclude:* those effects are part of the block-state
string (`east=true` on a fence, `power=15` on wire). Excluding them would leave
the craft file disagreeing with what the player sees, and the next audit would
fail. Freezing limits the volume to what a player causes directly, so a
redstone clock built in the editor can't stream edits into the craft file.

In flight, Minecraft is the sequencer. It observes the same way and sends
BLOCK_DELTAs, and KSP proposes nothing. KSP's mirror is its render grid.

## Resizes

In editor mode a placement outside the box grows the grid (`GridEditor.SetBlock`),
and `GridEditor.Trim` shrinks it. The sequencer (KSP) sends **GRID_RESIZE**
with the new size and the shift. Then, as a separate message with the next
`rev`, it sends the BLOCK_DELTA for the edit that caused the growth. Applying a
resize means exactly `VoxelGrid.Resized(newSize, shift)`: each old cell `c`
moves to `c + shift`, cells landing outside are dropped, and new cells are air.
The receiver also updates `anchorOffset −= shift`. Nobody closes or re-opens
anything, and FULL_SYNC isn't needed.

A Minecraft player builds past the box with a BLOCK_EDIT in anchor
coordinates. KSP converts each change to cell space (`cell = anchor −
anchorOffset`) and runs it through `GridEditor.SetBlock`. If that resizes, KSP
sends GRID_RESIZE and then the BLOCK_DELTA, both with `originSeq`. Minecraft's
blocks don't move in the world, so applying the resize on that side changes
bookkeeping only.

If Minecraft's region can't hold the new box, it sends
`ERROR(region_exhausted)`. KSP then closes the grid and re-opens it, which gets
it a new region. World mapping (open decision 2) should reserve room for growth
to avoid this.

Flight grids never resize.

## Block deltas: block-state strings in a message-local table (decided by this spec)

A delta names each new state as a canonical **block-state string**, listed
once per message in a small table that the changes reference by index. No
palette indices are shared across messages.

Options considered:

1. **Sender's grid palette indices.** Smallest on the wire, but both sides must
   keep identical palettes forever. `VoxelGrid` appends states in edit order
   and `Compact()` renumbers them (on save, and implicitly in every
   `FULL_SYNC`). The Java mirror would have to replicate that algorithm exactly
   and sync palette changes as extra messages. Worst, a divergence is
   **silent**: an index maps to the wrong block, which neither `rev` nor bounds
   checks can detect.
2. **One string per change.** Self-contained and trivially debuggable, but it
   repeats a long string for every cell when a redstone line or a fill sets
   hundreds of cells to the same state.
3. **Message-local string table** (chosen). It's option 2 with per-message
   dedup. Each message stands alone, so there's no cross-message state to
   diverge. A bad index is a decode error caught on receipt. It mirrors how a
   `GridCodec` payload carries its own palette. Cost estimate: 1,000 cells
   flipping every tick across 3 states is ~150 bytes of strings plus 7 bytes
   per change, about 7 KB per message or 140 KB/s at 20 Hz, which is negligible
   on a local socket.

Deltas carry only the new state. Divergence is detected by `rev` and by content
hash audits (ACK), not by per-cell old states.

## Divergence: audits and resync

**Audits** catch silent divergence, such as a bug that applies a delta wrong
while `rev` still matches. **The sequencer only compares hashes of the same
revision:**

1. To audit, the sequencer sets ACK_REQUESTED on one sequenced frame with
   revision R: a BLOCK_DELTA or the last chunk of a FULL_SYNC, **never a
   GRID_RESIZE**. A resize revision may be a state the sequencer never holds:
   `GridEditor.SetBlock` returns the grown grid with the edit already applied,
   so KSP can't hash "resized but not yet edited". At that
   moment it computes the hash of its own grid at R and stores the pair (R,
   hash). **At most one audit per grid is outstanding.** A new one isn't
   started until the previous one ends: its ACK arrives, a FULL_SYNC
   supersedes it, or it times out.
   - **A FULL_SYNC supersedes the outstanding audit.** Every non-seed
     FULL_SYNC is audited, so enqueueing one replaces the stored pair with the
     FULL_SYNC's (R, hash), whatever was outstanding. This matters when the
     non-sequencer is awaiting a sync: it drops the audited delta, so that ACK
     never comes, and the FULL_SYNC it asked for is what ends the wait on both
     sides.
   - **ACK timeout:** if no ACK for the stored R arrives within 30 s, the
     sequencer abandons that audit, logs it, and may start the next. A timeout
     isn't treated as a mismatch: an ordered channel only loses an ACK through
     a bug, which resync and the next audit cover.
2. The non-sequencer applies that frame, then hashes its **mirror** (the
   sequenced state only, never pending proposals, see
   [Proposals](#proposals-mirror-and-live-view)) *before applying any later
   frame for that grid*, and replies `ACK(rev = R, hash)`.
3. The sequencer compares only when `ACK.rev` equals the stored R. Any other
   ACK (a stale one, or one from before a FULL_SYNC) is ignored.

The alternative was to accept ACKs at any revision and compare them with the
current revision. That fails under continuous change: a player building, or a
redstone clock flipping every tick in flight, means the current revision has
already moved on by the time the ACK arrives. Every audit would then "fail"
and trigger a FULL_SYNC, forever. Keeping per-revision hashes for every
revision would mean re-encoding the whole grid every tick. Snapshotting only
the audited revision costs one encode per audit on each side, and it's correct
however fast the grid changes.

Hashing the mirror, not the live view, keeps audits correct while proposals are
in flight. The live view contains unsequenced placements and would mismatch
whenever a player is mid-build, falsely tripping `hash_disagreement`. The other
option was to skip or mark an ACK dirty while the proposer has pending
proposals. It was rejected because continuous building keeps a proposal
pending nearly all the time, which would starve audits exactly when divergence
is most likely. The mirror costs one in-memory grid, which Minecraft needs
anyway for reconciliation.

Audit rate: every FULL_SYNC is audited. Beyond that, at most one audit per
5 s per grid, and only when `rev` has moved since the last one (so an idle grid
costs nothing). Flight grids may audit less often. Hashing re-encodes the whole
grid, which is cheap for typical grids.

**On a mismatch** (same R, different hash), the sequencer sends one FULL_SYNC,
which is itself audited. If *that* audit also mismatches (an audit of a
FULL_SYNC sent because of a mismatch, whichever FULL_SYNC superseded it), the two sides encode
equal grids differently. That's an encoder bug, not divergence, and another
FULL_SYNC won't fix it. The sequencer sends `ERROR(hash_disagreement)` and stops
auditing that grid for the rest of the connection. So a hash can never cause
more than one FULL_SYNC in a row.

**Resync** handles divergence the receiver notices itself:

- A non-sequencer that gets a delta or resize whose `baseRev` isn't its `rev`,
  or a frame that fails to decode, sends **one** `RESYNC_REQUEST` and enters an
  *awaiting sync* state. While waiting it silently drops every BLOCK_DELTA and
  GRID_RESIZE for that grid, **without sending further requests**. A FULL_SYNC
  of any revision ends the wait. If none arrives within 30 s (which can only
  happen through a bug, given an ordered channel), the grid is restarted. KSP
  owns that: when KSP is the one waiting (flight), it closes and re-opens the
  grid. When Minecraft is waiting (editor), it sends `ERROR(grid_lost)` for
  the grid, and KSP closes and re-opens it.
- A dropped BLOCK_DELTA can be the final echo of one of the proposer's own
  proposals. The proposer still reads the `originSeq` of every BLOCK_DELTA it
  drops, and marks that proposal *echo-dropped*. Its effect, if accepted, is
  already in the sequencer's state, so it will be in the next FULL_SYNC. When
  the FULL_SYNC is applied, every echo-dropped proposal is settled: its touched
  cells are reset to the new mirror exactly as for a final echo, and it stops
  being pending. Proposals whose echoes weren't dropped are unaffected and get
  their echoes after the sync.
- The sequencer answers each RESYNC_REQUEST with a FULL_SYNC at a new revision
  (current `rev` + 1). If a FULL_SYNC for that grid is already queued or being chunked
  out, it doesn't queue another.
- There's no livelock under continuous change. The FULL_SYNC's place in the
  stream matches its `rev`. Deltas queued before it have older revisions and
  are dropped by the waiting receiver. Deltas after it start at its `rev` and
  apply. Proposals the requester sent before its request reach the sequencer
  first, so they're included. Later ones are sequenced after the sync.

## Messages

Grid handles are chosen by KSP in `GRID_OPEN`: nonzero, unique within the
connection, never reused on that connection. Every handle dies with its
connection.

| Type | Name | Direction | `grid` |
| --- | --- | --- | --- |
| `0x0001` | HELLO | both | 0 |
| `0x0002` | BYE | both | 0 |
| `0x0003` | ERROR | both | 0 or handle |
| `0x0004` | PING | both | 0 |
| `0x0005` | PONG | both | 0 |
| `0x0100` | GRID_OPEN | KSP → MC | handle |
| `0x0101` | GRID_READY | MC → KSP | handle |
| `0x0102` | GRID_CLOSE | KSP → MC | handle |
| `0x0200` | FULL_SYNC | opener (seed) or sequencer | handle |
| `0x0201` | BLOCK_DELTA | sequencer → other | handle |
| `0x0202` | BLOCK_EDIT | non-sequencer → sequencer | handle |
| `0x0203` | ACK | non-sequencer → sequencer | handle |
| `0x0204` | RESYNC_REQUEST | non-sequencer → sequencer | handle |
| `0x0205` | GRID_RESIZE | sequencer → other (editor only) | handle |
| `0x0300` | SIGNAL_OUTPUT | MC → KSP (flight) | handle |
| `0x0301` | SIGNAL_INPUT | KSP → MC (flight) | handle |
| `0x0302` | SIM_STATE | KSP → MC (flight) | handle |

Ranges: `0x00xx` connection, `0x01xx` grid lifecycle, `0x02xx` grid content,
`0x03xx` signals (energy and fluids will be added to this range later).

A known message that breaks the Direction or `grid` columns gets
`ERROR(unexpected_message, refSeq)` and is otherwise ignored. That covers a
message from the wrong side, a sequencer-only message from the non-sequencer
(or the reverse) for that grid's mode, a GRID_RESIZE or SIGNAL_* on a grid of
the wrong mode, a connection message with `grid ≠ 0`, and a grid message with
`grid = 0`. A grid message whose nonzero handle isn't open gets
`ERROR(unknown_grid)` instead. GRID_OPEN reusing a handle on this connection
gets `GRID_READY(refused)`.

### Connection

**HELLO** is the first frame in each direction. Both sides send it immediately
on connect without waiting for the other, and nothing else may come before it.

```
u32  magic          "KBLX" (4B 42 4C 58)
u16  major          1
u16  minor          0
u8   role           1 = KSP side, 2 = Minecraft side (fakes use the role they stand in for)
u8   gridCodecMax   highest GridCodec version this side decodes (2)
u32  maxFrame       largest frame this side accepts, header included (≥ 1 MiB)
--- fields above are frozen across all versions ---
u64  capabilities   bit set, none defined in 1.0 (send 0)
str  software       e.g. "Kerblox 0.3.0 / KSP 1.12.5" or "kerblox-neoforge 0.1.0 / NeoForge 21.1.x"
str  instance       random id per process start; a changed id after reconnect means the peer restarted
```

The receiver checks magic, major and role (a peer with the same role is
refused). On failure it sends `ERROR` with a fatal code and closes.

**BYE**: `u16 reason` (0 shutdown, 1 replaced, 2 error), `str message`. The
sender closes after writing it.

**ERROR**: `u32 refSeq` (the offending frame's seq, or 0 for none), `u16 code`,
`str message`. Codes `1`–`2` are fatal and the sender closes the connection.
Codes from `3` up are informational and the connection stays up.

| Code | Name | Fatal | Meaning |
| --- | --- | --- | --- |
| 1 | incompatible_version | yes | magic, major or role mismatch |
| 2 | protocol_violation | yes | bad header, frame over `maxFrame`, frame before HELLO |
| 3 | unknown_grid | no | `grid` names no open grid |
| 4 | bad_payload | no | a known message failed to decode or broke its rules; what follows depends on the message, see below |
| 5 | edit_rejected | no | the sequencer refused a `BLOCK_EDIT` (see rejections) |
| 6 | grid_lost | no | Minecraft lost the grid's region; KSP should close and re-open it |
| 7 | internal | no | unexpected failure on the sender's side |
| 8 | region_exhausted | no | a resize doesn't fit Minecraft's region; KSP closes and re-opens the grid |
| 9 | hash_disagreement | no | an audit right after a FULL_SYNC mismatched; audits stop for this grid |
| 10 | unexpected_message | no | a known message in the wrong direction or mode, or with a `grid` field its type doesn't allow; it's ignored |

After a `bad_payload`, the receiver of the bad frame recovers by its role:

- **Non-sequencer**, bad BLOCK_DELTA, GRID_RESIZE or FULL_SYNC chunk: it can't
  trust its mirror, so it also sends `RESYNC_REQUEST(decode error)` (see
  [resync](#divergence-audits-and-resync)).
- **Sequencer**, bad BLOCK_EDIT: an empty final echo for it (see
  [Undecodable or illegal edits](#proposals-mirror-and-live-view)). The
  sequencer never sends RESYNC_REQUEST.
- **Sequencer**, bad ACK: the audit is abandoned, as on a timeout. Bad
  RESYNC_REQUEST: treated as a valid one, so a FULL_SYNC follows.
- Anything else (GRID_OPEN, signals, SIM_STATE): the frame is dropped. A bad
  GRID_OPEN is answered with `GRID_READY(refused)`.

**PING** `u64 nonce, u64 sentAt` (the sender's monotonic clock, opaque to the
receiver). **PONG** echoes both fields. A side sends PING when it has sent
nothing for 1 s, and treats the peer as dead after 5 s with nothing received.
On a dead peer it closes the connection, and KSP starts reconnecting.

PING, PONG and the liveness clock live **on the I/O thread**: it answers PING
directly and counts any received frame as liveness, without going through the
main-thread queue. A KSP scene load or a long Minecraft tick can stall the main
thread for well over 5 s, and that mustn't look like a dead peer.

### Grid lifecycle

**GRID_OPEN** (KSP → MC):

```
str  key       grid key, see below
u16  sizeX, sizeY, sizeZ    1–256 each; must equal the seed FULL_SYNC's dimensions
u8   mode      1 = editor, 2 = flight
```

The **grid key** names the grid part stably and is safe as a file name:

- 1–64 bytes matching `^[a-z0-9][a-z0-9_-]{0,63}$`. That's lowercase only, so it
  works on case-insensitive filesystems, and has no dots, so a suffix can be
  appended unambiguously.
- It's the same for the same part across KSP restarts, reconnects and editor
  sessions. It's different for different grid parts, including two grid parts
  on one craft. P3.6 defines the derivation (for example a hash of the craft's
  identity and the part's persistent id; that KSP member is unverified). A
  receiver that gets an invalid key replies `GRID_READY(refused)`.
- The **grid id** is `<key>.<mode>`, with mode `editor` or `flight`. Minecraft
  picks regions by grid id, so a flight copy never overwrites the editor design.
  P3.8's render pack uses it as the file name `grids/<grid-id>.bin`.

KSP follows GRID_OPEN immediately with the seed `FULL_SYNC(rev 0, anchorOffset 0)`.
Minecraft applies it, then replies. Regions are picked by grid id, so a region
can be reused with blocks left over from an earlier session. Applying the seed
therefore sets **every cell of the region outside the seeded box to air**, not
just the box, with reconciliation writes. Otherwise stale blocks would look like
part of the craft, and breaking them would be proposed as edits.

**GRID_READY** (MC → KSP):

```
u8   status        0 ok, 1 refused
str  dimension     e.g. "minecraft:overworld"
i32  originX, originY, originZ   world position of anchor (0,0,0); grid axes map to world +X/+Y/+Z
u64  seedHash      content hash of the seed as Minecraft decoded it (0 if refused)
str  message
```

KSP compares `seedHash` with its own hash of the seed. A mismatch means the
encoders disagree: KSP logs it and doesn't audit that grid (the same rule as
`hash_disagreement`). This is how the seed is confirmed in both modes. ACK is
never used for it. Minecraft sends no content frame for a grid before its
GRID_READY. KSP, as editor sequencer, may send deltas right after the seed;
they're applied in order. The dimension-plus-origin shape fits both
world-mapping options (open decision 2). KSP shows the location so the player
can `/tp` there.

**GRID_CLOSE** (KSP → MC): `u8 reason` (0 editor exit, 1 part removed,
2 vessel unloaded or destroyed). Whether Minecraft keeps the region is part of
open decision 2.

**Editor to flight:** launching closes the editor grid and opens a new flight
grid (new handle, `mode = 2`, seeded from the frozen copy). There's no in-place
mode switch.

**Reconnect:** handles don't survive a connection. KSP re-opens every grid
with `GRID_OPEN` plus `FULL_SYNC` from its own copy. In editor mode that's
correct by construction. In flight mode Minecraft's transient state (latches,
timers) is lost. That limitation is acceptable for v1 and noted for P3.6.

### Grid content

**FULL_SYNC** (chunked):

```
u64  rev
i32  anchorOffsetX, anchorOffsetY, anchorOffsetZ
u32  totalLength     length of the whole GridCodec payload, 1 … maxGridPayload
u32  offset          where this chunk starts in the payload
u32  chunkLength     ≥ 1
chunkLength bytes
```

- Chunks go in order from offset 0, back to back. Every chunk repeats the same
  `rev`, anchor offset and `totalLength`, and the receiver rejects
  inconsistent chunks with `bad_payload`. Chunk sizes are chosen so each frame
  fits the peer's `maxFrame`.
- The sync completes with the chunk where `offset + chunkLength = totalLength`.
  The receiver then decodes the payload, replaces its whole grid (dimensions
  come from the payload, so they may differ from GRID_OPEN after resizes), and
  sets `rev` and `anchorOffset`.
- The sequencer sets ACK_REQUESTED on the last chunk of every non-seed
  FULL_SYNC (an audit). The seed's last chunk never has it.

**BLOCK_DELTA** (sequencer):

```
u64  baseRev     rev before this delta
u64  rev         baseRev + 1
u32  originSeq   seq of the BLOCK_EDIT this sequences, 0 for the sequencer's own edits
varint stateCount; state × stateCount           distinct canonical states in this message (0 … 65536)
varint changeCount; changeCount × { u16 x, u16 y, u16 z, varint stateIndex }    cell space; 0 only in an empty echo
```

**BLOCK_EDIT** (non-sequencer):

```
u64  seenRev     last rev the proposer had applied (for logging conflicts only)
varint stateCount; state × stateCount
varint changeCount; changeCount × { i32 x, i32 y, i32 z, varint stateIndex }    anchor space
```

**GRID_RESIZE** (sequencer, editor mode only):

```
u64  baseRev
u64  rev         baseRev + 1
u32  originSeq   as in BLOCK_DELTA
u16  sizeX, sizeY, sizeZ    new size, 1–256 each
i32  shiftX, shiftY, shiftZ old cell c becomes c + shift (GridEdit.ShiftX/Y/Z)
```

Change rules: `changeCount ≥ 1` and `stateCount ≥ 1`, with one exception. A
BLOCK_DELTA with `originSeq ≠ 0` (a final echo) may have `changeCount = 0`,
and then `stateCount = 0` too. That's an empty echo, and it still advances
`rev`. An empty BLOCK_DELTA with `originSeq = 0`, or an empty BLOCK_EDIT, is
`bad_payload`. Every state in the table is referenced at least once. Delta
coordinates are in bounds. Each cell
appears at most once per message, and senders must not repeat one.
`stateIndex < stateCount`. States that aren't canonical are `bad_payload` and
aren't fixed up silently. A delta applies atomically.

**Receiving a delta or resize:** if `baseRev` equals the receiver's `rev`,
apply it and set `rev`. Otherwise follow [resync](#divergence-audits-and-resync).

**ACK** (non-sequencer, only in reply to ACK_REQUESTED): `u64 rev` (the
revision of the flagged frame), `u64 contentHash` (FNV-1a 64 of the canonical encoding of the **mirror**
right after applying that frame, without pending proposals). See
[audits](#divergence-audits-and-resync).

**RESYNC_REQUEST** (non-sequencer): `u64 haveRev`, `u8 reason` (0 rev gap,
1 decode error, 2 local state lost, for example Minecraft finding its region
changed under it). It's sent once per divergence, as above. Hash mismatches
aren't a reason: only the sequencer compares hashes, and it answers a mismatch
with a FULL_SYNC directly.

### Flight-mode block changes

In flight, Minecraft may change any cell to any state, including air to solid
and back: pistons extending, fluids flowing, blocks broken. Such changes travel
as ordinary BLOCK_DELTAs, and KSP applies them to the **render** state only.
The physical part (mass, CoM, colliders, drag cube, attach nodes) stays exactly
as it was in the frozen copy at launch, for the life of the flight grid. *This
is proposed, see open decision 3.* Physical changes are out of scope for
protocol 1.0. That includes TNT splitting a craft into separate vessels
(P4.3), and blocks that matter to physics appearing or vanishing. They'll come
as a later minor version (for example a `GRID_SPLIT` message designed with
P4.3). A block pushed out of the grid's box leaves the grid: Minecraft sends
its cell as air and doesn't track it further. Flight grids never resize.

### Signals (flight)

Redstone shows up in two ways. In-grid appearance (lamp lit, `powered=true`) is
an ordinary block state and travels as `BLOCK_DELTA`. **I/O with the craft**
travels on these messages. Which blocks act as ports (for example a future
`kerblox:output` block) is for a later roadmap item. The protocol only
addresses cells and faces.

**SIGNAL_OUTPUT** (MC → KSP) and **SIGNAL_INPUT** (KSP → MC) share a layout:

```
u64     stamp       SIGNAL_OUTPUT: Minecraft server tick; SIGNAL_INPUT: KSP counter. Increasing.
varint  count
count × { u16 x, u16 y, u16 z, u8 face, u8 level }    cell space
```

`face` is 0 down, 1 up, 2 north, 3 south, 4 west, 5 east, or 6 for the block's
strongest output or any input. `level` runs 0–15. Each message is an
**absolute snapshot** of every port on the grid, not a delta. It's sent in any
tick where a level changed, and at least once per second. Receivers keep the
newest `stamp` and drop older ones. Snapshots are idempotent and a few bytes
per port, so a missed or stale one corrects itself and needs no
acknowledgement or resync.

**SIM_STATE** (KSP → MC): `u8 state`. 1 means running. 2 means paused (game
paused, on-rails time warp, vessel packed). Minecraft should stop simulating
the grid's region while paused, or at least stop sending `SIGNAL_OUTPUT` and
ignore its changes. How it freezes a region is for P3.4 to investigate. Default
after `GRID_OPEN(flight)` is running. SIM_STATE is for flight grids only:
editor regions are always frozen (see [What Minecraft proposes](#what-minecraft-proposes-in-editor-mode)).

## Threading notes for implementers

- **KSP:** socket I/O runs on a background thread, never on Unity's main
  thread. It answers PING itself (see [Connection](#connection)). Other
  received frames go into a queue that `ModuleBlockGrid` drains in
  `Update`, in order. Outgoing frames are queued and written by the I/O thread.
  No Unity object is touched off the main thread. Revision assignment and
  enqueueing happen together on the main thread.
- **Minecraft:** I/O runs on its own thread, and every world read or write is
  handed to the server thread (verify the NeoForge 1.21.1 API for this in P3.4).
  Cell changes are observed on the server thread and turned into
  `BLOCK_EDIT` (editor, see [What Minecraft proposes](#what-minecraft-proposes-in-editor-mode))
  or `BLOCK_DELTA` (flight). Reconciliation writes are excluded by comparing
  against the live-view model, not by a guard around `setBlock`, so the updates
  they set off are still observed.

## Open decisions

1. **Transport**: *proposed*: Unix domain socket with a TCP loopback fallback
   (see [Recommendation](#recommendation)). Awaiting user sign-off. The wire
   format above doesn't change either way.
2. **World mapping**: one region per grid part (e.g. spaced plots in a void
   world), or one dimension per craft. `GRID_READY` carries a dimension and an
   origin, so either fits without a protocol change. Either way, it must leave
   room for editor-time growth (see [Resizes](#resizes)).
3. **Ownership during flight**: *proposed by this spec*: Minecraft is the
   sequencer and authoritative for block state, and KSP stays authoritative for
   geometry (no geometry changes in flight).
