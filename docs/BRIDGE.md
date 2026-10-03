# Minecraft bridge protocol

**Status: specification v1.0, transport proposed and awaiting user sign-off**
(roadmap item P3.1). The wire format below is complete and doesn't depend on
the transport choice. Sections marked *proposed* still need the user. Everything
else is the contract P3.2 (C#) and P3.4 (Java) implement.

Numeric constants (message types, error codes, limits) are also kept in
[`protocol/bridge-constants.json`](../protocol/bridge-constants.json). The tables
here and that file must agree. P3.2 and P3.4 each add a test that checks their
constants against it.

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
  identical bytes, which the protocol uses for content hashes.
- **Deltas**: `VoxelGrid.Changed` events carry `(x, y, z, old, new, revision)`.
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
conflict rules below depend on in-order delivery. Two candidates:

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
  messages larger than the ring (a worst-case 256³ full sync is tens of MB), so
  either chunking or very large rings. It also needs a wakeup strategy, death
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
| `varint` | unsigned LEB128, at most 5 bytes, value ≤ 2³²−1 (exactly `GridCodec`'s varint) |
| `str` | `varint` byte length, then UTF-8 bytes, no terminator |
| `state` | a `str` holding a canonical block state (`ns:block[k=v,...]`, properties sorted by name, brackets omitted when empty), at most 1024 bytes (`GridCodec.MaxStateLength`) |

The Java side must produce the same canonical strings as `BlockState`:
registry name, then each property as `name=Property#getName(value)`, sorted by
property name. P3.4 adds shared test vectors.

### Frame header (16 bytes)

```
offset size field
0      u32  length   bytes that follow this field (12 + payload). 12 ≤ length ≤ maxFrame − 4
4      u16  type     message type (table below)
6      u16  flags    bit 0 ACK_REQUESTED; other bits reserved: send 0, ignore on receipt
8      u32  grid     grid handle; 0 = connection-level message
12     u32  seq      per-direction frame counter: HELLO is 1, +1 per frame, wraps to 0
16     ...  payload
```

- `maxFrame` is what the *receiver* announced in HELLO (default and minimum
  64 MiB, enough for a worst-case 256³ `GridCodec` payload). A frame larger
  than that is a protocol violation.
- **Payloads are extensible:** a receiver ignores bytes after the last field it
  knows. Minor versions may only append fields. (This is unlike `GridCodec`,
  which rejects trailing bytes. A nested codec blob always has its own length
  prefix.)
- `seq` lets `ERROR` and `BLOCK_DELTA` refer to a specific frame.

### Worked example

`BLOCK_DELTA` on grid 1, seq 7, revision 4 → 5, setting cell (2, 0, 3) to
`minecraft:stone`:

```
39 00 00 00  01 02  00 00  01 00 00 00  07 00 00 00     length=57 type=0x0201 flags=0 grid=1 seq=7
04 00 00 00 00 00 00 00                                 baseRev=4
05 00 00 00 00 00 00 00                                 rev=5
00 00 00 00                                             originSeq=0 (sequencer's own edit)
01  0F 6D 69 6E 65 63 72 61 66 74 3A 73 74 6F 6E 65     1 state: "minecraft:stone"
01  02 00 00 00 03 00  00                               1 change: x=2 y=0 z=3 state#0
```

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
  `GridCodec` doesn't bump the protocol major.
- The header layout and HELLO's fields up to `maxFrame` are frozen for all
  versions, so any two builds can at least exchange HELLO and fail cleanly.

## Revisions and the sequencer

Each open grid has a **protocol revision** `rev` (`u64`), owned by the bridge
and separate from `VoxelGrid.Revision`. `VoxelGrid.Revision` counts local edits
and differs between the two sides. `rev` counts sequenced messages, which both
sides apply in the same order:

- The opener's `FULL_SYNC` right after `GRID_OPEN` seeds the grid at `rev = 0`.
- After that, exactly one side is the **sequencer** for the grid:
  - **editor** mode: **KSP**. The craft file is the source of truth, and KSP
    is the side that saves it.
  - **flight** mode: **Minecraft** *(proposed, see open decision 3)*. KSP never
    edits a flight grid (AGENTS.md: grids are frozen in flight).
- Only the sequencer sends `BLOCK_DELTA` and (after the seed) `FULL_SYNC`. Each
  one advances `rev` by exactly one, even when a delta changes nothing.
- The other side sends **proposals** (`BLOCK_EDIT`) and applies them locally
  right away (a Minecraft player's placement has already happened in the world).
  The sequencer applies proposals in arrival order, then sends a `BLOCK_DELTA`
  with `originSeq` set to the proposal's `seq`.

**Why this converges:** the channel is ordered, so the non-sequencer receives
every sequenced delta in `rev` order. A KSP edit and a Minecraft edit can race
on the same cell. Minecraft has already applied its own edit, then receives
KSP's delta (overwriting it), then the echo of its own proposal (re-applying
it). Both sides end on the proposal, which is the sequencer's order.
Concurrent edits resolve as last-writer-wins *in sequencer order*, which for a
single builder on each side is what people expect.

**Rejections:** the sequencer may reject a proposal (out of bounds, a grid
that isn't editable right now, a policy refusal). It still sends a
`BLOCK_DELTA` with `originSeq` set. That delta carries the authoritative
current state of every cell the proposal touched, so the proposer's optimistic
edit is reverted. It also sends `ERROR(edit_rejected)` with `refSeq`, so the
Minecraft side can tell the player.

**Flight-mode deltas** (proposed): Minecraft's deltas in flight change what KSP
*renders* (a lamp lights up, a repeater's `powered` flips), never KSP's
geometry. Mass, colliders and attach nodes stay those of the frozen copy taken
at launch. Ownership is Minecraft for block state and KSP for geometry.

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

Deltas carry only the new state. Divergence is detected by `rev`, and by
content hash on demand (`ACK`), not by per-cell old states.

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
| `0x0300` | SIGNAL_OUTPUT | MC → KSP (flight) | handle |
| `0x0301` | SIGNAL_INPUT | KSP → MC (flight) | handle |
| `0x0302` | SIM_STATE | KSP → MC (flight) | handle |

Ranges: `0x00xx` connection, `0x01xx` grid lifecycle, `0x02xx` grid content,
`0x03xx` signals (energy and fluids will be added to this range later).

### Connection

**HELLO** is the first frame in each direction. Both sides send it immediately
on connect without waiting for the other, and nothing else may come before it.

```
u32  magic          "KBLX" (4B 42 4C 58)
u16  major          1
u16  minor          0
u8   role           1 = KSP side, 2 = Minecraft side (fakes use the role they stand in for)
u8   gridCodecMax   highest GridCodec version this side decodes (2)
u32  maxFrame       largest frame this side accepts, header included (≥ 64 MiB)
--- fields above are frozen across all versions ---
u64  capabilities   bit set, none defined in 1.0 (send 0)
str  software       e.g. "Kerblox 0.3.0 / KSP 1.12.5" or "kerblox-neoforge 0.1.0 / NeoForge 21.1.x"
str  instance       random id per process start; a changed id after reconnect means the peer restarted
```

The receiver checks magic, major and role (a peer with the same role is
refused). On failure it sends `ERROR` with a fatal code and closes.

**BYE**: `u16 reason` (0 shutdown, 1 replaced, 2 error), `str message`. The
sender closes after writing it.

**ERROR**: `u32 refSeq` (the offending frame's seq, or 0), `u16 code`,
`str message`. Codes `1`–`2` are fatal and the sender closes the connection.
Codes from `3` up are informational and the connection stays up.

| Code | Name | Fatal | Meaning |
| --- | --- | --- | --- |
| 1 | incompatible_version | yes | magic, major or role mismatch |
| 2 | protocol_violation | yes | bad header, oversize frame, frame before HELLO |
| 3 | unknown_grid | no | `grid` names no open grid |
| 4 | bad_payload | no | a known message failed to decode; the receiver also resyncs the grid |
| 5 | edit_rejected | no | the sequencer refused a `BLOCK_EDIT` (see rejections) |
| 6 | grid_lost | no | Minecraft lost the grid's region; KSP should close and re-open it |
| 7 | internal | no | unexpected failure on the sender's side |

**PING** `u64 nonce, u64 sentAt` (the sender's monotonic clock, opaque to the
receiver). **PONG** echoes both fields. A side sends PING when it has sent
nothing for 1 s, and treats the peer as dead after 5 s with nothing received.
On a dead peer it closes the connection, and KSP starts reconnecting.

### Grid lifecycle

**GRID_OPEN** (KSP → MC):

```
str  key       stable identity of the grid part, ≤ 256 bytes, opaque to Minecraft (P3.6 defines it)
u16  sizeX, sizeY, sizeZ
u8   mode      1 = editor, 2 = flight
```

KSP follows it immediately with `FULL_SYNC(rev 0)`. Minecraft picks a region
based on `(key, mode)`, so a flight copy never overwrites the editor design. It
then replies:

**GRID_READY** (MC → KSP): `u8 status` (0 ok, 1 refused), `str dimension`
(e.g. `minecraft:overworld`), `i32 originX, originY, originZ` (world position of
cell (0,0,0); grid axes map to world +X/+Y/+Z), `str message`. This shape fits
both world-mapping options (open decision 2). KSP shows the location so the
player can `/tp` there.

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

**FULL_SYNC**: `u64 rev`, `u32 codecLength`, `codecLength` bytes of `GridCodec`
data. Dimensions must match `GRID_OPEN`. The receiver replaces its whole grid
and sets its `rev`. The sender always sets `ACK_REQUESTED`.

**BLOCK_DELTA** (sequencer):

```
u64  baseRev     rev before this delta
u64  rev         baseRev + 1
u32  originSeq   seq of the BLOCK_EDIT this sequences, 0 for the sequencer's own edits
changes
```

**BLOCK_EDIT** (non-sequencer):

```
u64  seenRev     last rev the proposer had applied (for logging conflicts only)
changes
```

**changes** block, shared by both:

```
varint  stateCount   1 … 65536
state × stateCount   distinct canonical states in this message
varint  changeCount  ≥ 1
changeCount × { u16 x, u16 y, u16 z, varint stateIndex }
```

Rules: coordinates are in bounds. Each cell appears at most once per message,
and senders must not repeat one. `stateIndex < stateCount`. States that aren't
canonical are `bad_payload` and aren't fixed up silently. A delta applies
atomically. When an edit would touch a large share of the grid, the sequencer
may send `FULL_SYNC` instead (it also advances `rev` by one).

**Receiving a delta:** if `baseRev` equals the receiver's `rev`, apply it and
set `rev`. Otherwise, or if it fails to decode, send `RESYNC_REQUEST` and
discard further deltas for that grid until a `FULL_SYNC` arrives.

**ACK** (non-sequencer): `u64 rev` (the receiver's current rev), `u64
contentHash` (FNV-1a 64 of the grid's `GridCodec` v2 bytes, or 0 if not
computed). It's sent in reply to any frame with `ACK_REQUESTED`. The hash is
only computed when that frame was a `FULL_SYNC`, or when the sequencer sets the
flag on a delta to audit (editor mode, at most every 5 s). Hashing re-encodes
the whole grid, which is cheap for typical grids and too expensive to do every
tick. The hash is well-defined because v2 encoding is canonical. If the
sequencer sees a mismatched hash, or a `rev` ahead of its own, it sends
`FULL_SYNC`.

**RESYNC_REQUEST** (non-sequencer): `u64 haveRev`, `u8 reason` (0 rev gap,
1 decode error, 2 hash mismatch, 3 local corruption or restart). The sequencer
replies with `FULL_SYNC` at its current `rev`. Proposals the requester sent
earlier arrive at the sequencer first, so they're included. Proposals sent
later get sequenced after the sync, so nothing is lost.

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
count × { u16 x, u16 y, u16 z, u8 face, u8 level }
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
after `GRID_OPEN(flight)` is running.

## Threading notes for implementers

- **KSP:** socket I/O runs on a background thread, never on Unity's main
  thread. Received frames go into a queue that `ModuleBlockGrid` drains in
  `Update`. Outgoing frames are queued and written by the I/O thread. No Unity
  object is touched off the main thread.
- **Minecraft:** I/O runs on its own thread, and every world read or write is
  handed to the server thread (verify the NeoForge 1.21.1 API for this in P3.4).
  Grid edits made by players are observed on the server thread and turned into
  `BLOCK_EDIT` (editor) or `BLOCK_DELTA` (flight).

## Open decisions

1. **Transport**: *proposed*: Unix domain socket with a TCP loopback fallback
   (see [Recommendation](#recommendation)). Awaiting user sign-off. The wire
   format above doesn't change either way.
2. **World mapping**: one region per grid part (e.g. spaced plots in a void
   world), or one dimension per craft. `GRID_READY` carries a dimension and an
   origin, so either fits without a protocol change.
3. **Ownership during flight**: *proposed by this spec*: Minecraft is the
   sequencer and authoritative for block state, and KSP stays authoritative for
   geometry (no geometry changes in flight).
