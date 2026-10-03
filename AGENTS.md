# AGENTS.md

Canonical instructions for every AI coding agent in this repository. Claude Code
loads it through `@AGENTS.md` in CLAUDE.md; Codex and opencode read it natively.
Skills live in `.agents/skills/` (`.claude/skills` is a symlink to it).

## What this is

A KSP 1.12 mod: one part whose shape is a voxel grid of Minecraft-style blocks,
later bridged to a NeoForge 1.21.1 Minecraft instance so redstone and modded
machines can drive the craft. Monorepo: `ksp/` is the .NET solution;
`minecraft/`, `protocol/` and `tools/` arrive in phase 3. Read [ROADMAP.md](ROADMAP.md) for what's done and what's next, and
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before changing structure.

## Commands

```sh
export KSP_ROOT="$HOME/.local/share/Steam/steamapps/common/Kerbal Space Program"
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"   # if dotnet isn't on PATH

dotnet test ksp                 # Core unit tests; must pass before any commit
dotnet build ksp -c Release     # whole solution incl. KSP plugin; 0 warnings expected
scripts/deploy.sh               # tests + build + copy into $KSP_ROOT/GameData/Kerblox
scripts/ksp-logs.sh             # Kerblox-relevant lines + exceptions from KSP.log / Player.log
```

## Architecture rules

These are deliberate decisions. Don't revisit them inside a roadmap item; raise
them with the user instead.

- **Blocks are never individual KSP parts.** One part, one `ModuleBlockGrid`,
  one combined mesh, merged box colliders.
- **`Kerblox.Core` has no UnityEngine or KSP references.** It targets
  `net48;net10.0` and every piece of logic that can live there does, with tests.
  The plugin is a thin adapter from Core output to Unity/KSP objects.
- **Grid edits happen only in the editor** (VAB/SPH, maybe EVA later), never
  under thrust. Flight logic runs against a frozen copy of the grid.
- **The grid wire format is `GridCodec`** (versioned, magic `KBGR`). Bump the
  version and keep a decoder for the old one when changing it; craft files in
  the wild depend on it.
- **Block ids are stable.** Never renumber `BlockRegistry.Ids`; 0 is air.
  Block names match Minecraft's namespaced ids so the bridge maps 1:1.
  (P1.8 replaces fixed ids with per-grid palettes of block-state strings.)
- **Minecraft renders, KSP replays.** Never parse Minecraft models in C#.
  Modded-block appearance comes from client-exported render packs
  ([docs/RENDERING.md](docs/RENDERING.md)). Render packs contain Mojang and mod
  textures: they live in `~/.cache/kerblox/` and must never be committed or
  shipped.
- **Nothing ships to GameData that targets anything but `net48`**, and nothing
  may reference `netstandard.dll` (KSP's Managed folder has no facade for it).

## KSP API: verify, don't guess

KSP's API is sparsely documented and community knowledge is often out of date.
Before using a KSP member you haven't seen used in this repo, confirm it exists
and behaves as you expect by decompiling the game's assembly. Follow the
`ksp-api-verify` skill. Record anything non-obvious you learn in
[docs/KSP-API-NOTES.md](docs/KSP-API-NOTES.md) with the source location, and
mark anything unverified as such. A compile error against the real DLL beats a
confident guess. For example, `DragCube.size` (lowercase, as used in ROUtils) is
private in the shipped DLL; the public member is `Size`.

Things only a running game can verify (rendering, physics feel, drag numbers,
editor interaction) are an **in-game gate**: say so explicitly in the PR and in
ROADMAP.md, and list the checks for the user to run. Agents can't run KSP.

## Code conventions

- C# `LangVersion latest`, but only features that compile to `net48` without
  polyfills (no `init`, records, default interface members, `Span` in shipped code).
- Match the surrounding code: XML doc comments on public types and anything
  KSP-lifecycle-sensitive; explain *why*, not *what*.
- Plugin logging goes through `Log` (prefix `[Kerblox]`) so `scripts/ksp-logs.sh` finds it.
- Unity objects: never use `??` or `?.` on `UnityEngine.Object` (they bypass
  Unity's null overload). Destroy meshes and materials you create.
- No new mod dependencies (Harmony, ModuleManager, etc.) without a roadmap item
  that justifies them.

## Git and PRs

- Default branch is `main`. Work on `feat/<roadmap-id>-<slug>` branches, e.g.
  `feat/p2.1-grid-editing-api`. One roadmap item per branch and PR.
- PR title starts with the roadmap id: `P2.1: grid editing API`.
- PR body: what changed, how it was verified (test output), and an
  **In-game checks** section if the item has an in-game gate.
- Never add AI attribution: no `Co-Authored-By:` trailers and no "Generated
  with" lines in commits or PRs.
- Never pass `--no-verify`. Never force-push `main`.
- Update ROADMAP.md item status in the same PR that changes it.

## Parallel work (Orca worktrees)

Roadmap items run in parallel Orca worktrees (see the `kerblox-orchestrate` and
`kerblox-item` skills). Each new worktree runs `scripts/worktree-setup.sh` via
`orca.yaml`. All worktrees share **one** KSP install, so:

- Only build with `dotnet build ksp`/`dotnet test ksp` in a worktree; never run
  `scripts/deploy.sh` from a lane. Deploying to the game is a user step.
- Don't touch `$KSP_ROOT` outside `GameData/Kerblox`, and treat
  `KSP_Data/Managed` as read-only.
