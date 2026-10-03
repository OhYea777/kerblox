# Kerblox

Build Kerbal Space Program rockets out of Minecraft-style blocks, and eventually
let real Minecraft mechanics drive them: redstone first, then modded machines
(Mekanism, Create, ...) through a NeoForge bridge.

**Status:** phase 1 (a block-grid part that flies with correct mass, CoM, and drag)
is implemented and awaiting in-game verification. See [ROADMAP.md](ROADMAP.md).

KSP 1.12.x only (KSP 2 is out of scope). Developed and tested on Linux with the
native KSP build.

## How it works

Blocks are **not** KSP parts. One part (`kerbloxBlockGrid`) carries a
`ModuleBlockGrid` that stores a voxel grid and generates:

- one combined, face-culled mesh with a procedural texture atlas
- a compound collider of greedily merged boxes (6 boxes for the 96-block default)
- mass and centre of mass from per-block densities
- a drag cube, re-rendered whenever the geometry changes

The grid itself (`Kerblox.Core`) is a plain .NET library with no Unity or KSP
references. It is unit-tested on its own and serialised in a compact binary
format, which is also what the planned Minecraft bridge will send.

More detail: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Requirements

| What | Version | Notes |
| --- | --- | --- |
| KSP | 1.12.5 (Unity 2019.4.18f1) | Native Linux build. Other 1.12.x builds should work but aren't tested. |
| .NET SDK | 10.x | Builds the `net48` plugin against KSP's own `mscorlib`; Mono isn't needed. A user-local install from [dot.net/v1/dotnet-install.sh](https://dot.net/v1/dotnet-install.sh) into `~/.dotnet` works. |
| [KSPBuildTools](https://github.com/KSPModdingLibs/KSPBuildTools) | 1.1.1 | NuGet package, restored automatically. |
| rsync | any | Used by the deploy script. |

There are no mod dependencies yet: phase 1 needs neither Harmony nor ModuleManager.

## Build, test, deploy

```sh
export KSP_ROOT="$HOME/.local/share/Steam/steamapps/common/Kerbal Space Program"

cd ksp
dotnet test                 # Core unit tests (no KSP needed)
dotnet build -c Release     # plugin too (needs KSP_ROOT)
cd ..
scripts/deploy.sh           # tests + build + copy ksp/GameData/Kerblox into the game
scripts/deploy.sh --link    # symlink instead, so later builds land in the game directly
```

`KSP_ROOT` is required on Linux, because KSPBuildTools only auto-detects Steam
installs on Windows and macOS. Alternatively, symlink your install to `ksp/KSP`
(next to the solution).

## Testing in game

Follow [docs/TESTING-IN-GAME.md](docs/TESTING-IN-GAME.md): what to check in the
VAB and in flight, the expected numbers, and what to look for in `KSP.log` or
`Player.log`. To pull the relevant lines out of both logs, run
`scripts/ksp-logs.sh`.

## Repository layout

A monorepo, so that both sides of the bridge and its protocol can change in one PR.

```
ksp/                          the KSP mod (.NET; Kerblox.sln)
  src/Kerblox.Core/           pure grid library (net48 + net10.0): data model, codec, mesher, box merge, mass
  src/Kerblox/                KSP plugin (net48): ModuleBlockGrid, texture atlas
  tests/Kerblox.Core.Tests/   xUnit tests for Core
  GameData/Kerblox/           the mod as it ships (part cfg; Plugins/ is build output)
minecraft/                    (phase 3) NeoForge 1.21.1 bridge mod (Gradle)
protocol/                     (phase 3) bridge protocol spec + shared constants for both sides
tools/                        (phase 3) fake KSP and fake Minecraft stand-ins
scripts/                      deploy.sh, ksp-logs.sh, ksp-decompile.sh, worktree-setup.sh
docs/                         architecture, KSP API notes, in-game testing, bridge design
.agents/skills/               agent skills (also exposed as .claude/skills)
```

## Working with agents

[AGENTS.md](AGENTS.md) is the canonical guide for AI coding agents. Roadmap
items are built through Orca-orchestrated worktrees using the skills in
`.agents/skills/`. Start with `kerblox-orchestrate`.

## License

[MIT](LICENSE)
