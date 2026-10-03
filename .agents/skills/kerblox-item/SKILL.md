---
name: kerblox-item
description: "Implements one Kerblox ROADMAP.md item end to end in the current worktree: branch, code with tests, KSP API verification, build gates, PR. Use when dispatched by kerblox-orchestrate, or when the user says \"do P2.1\", \"implement roadmap item X\", or \"build the next roadmap item here\"."
---

# Implement one roadmap item

One item, one branch, one PR. If you were dispatched by Orca, the injected
preamble is authoritative for asks, heartbeats and `worker_done`. Load
`orca skills get orchestration --reference references/worker-contract.md` if the
preamble leaves you unsure.

## 1. Orient

- Read `AGENTS.md`, your item in `ROADMAP.md`, `docs/ARCHITECTURE.md`, and
  `docs/KSP-API-NOTES.md`.
- Check that the item's dependencies are `done` on main. If not, ask (or
  report) instead of starting.
- Branch: `git switch -c feat/<id-lower>-<slug>`, or rename Orca's branch with
  `git branch -m`. Example: `feat/p2.1-grid-editing-api`.
- Set your item's Status to `in-progress` in ROADMAP.md. Touch no other item.

## 2. Build it

- **Core first.** Anything that doesn't strictly need Unity or KSP goes in
  `ksp/src/Kerblox.Core` with xUnit tests in `ksp/tests/Kerblox.Core.Tests`.
  The plugin (`ksp/src/Kerblox`) only adapts Core output to Unity and KSP.
- **Verify every KSP member** you haven't seen used in this repo with the
  `ksp-api-verify` skill before relying on it. Add what you learned to
  `docs/KSP-API-NOTES.md` with its status (Verified / Unverified / In-game).
- Keep to the item's Scope. If you notice other needed work, note it for the PR
  body instead of doing it.
- If the item hits a design fork its Scope doesn't settle, ask the coordinator
  (or the user). Don't pick silently.

## 3. Gates (all must pass)

```sh
dotnet build ksp -c Release      # 0 errors, 0 warnings
dotnet test ksp                  # all green
git diff --check                 # no whitespace errors
```

Never run `scripts/deploy.sh`, and never write under `$KSP_ROOT`. Other lanes
share that install.

## 4. Ship

- Status: `review` if the item's Gate is `in-game`. Otherwise `done`, since
  merging the PR makes it true.
- Commit with a plain message (`P2.1: grid editing API`), no AI attribution,
  never `--no-verify`.
- `git push -u origin HEAD`, then:

```sh
gh pr create --assignee @me --base main --title "<ID>: <title>" --body "$(cat <<'EOF'
## What
<2-4 bullets>

## Verification
<test counts, build output summary, anything measured>

## In-game checks
<only for Gate: in-game: numbered steps and expected results, in the style of docs/TESTING-IN-GAME.md>

## Follow-ups
<out-of-scope findings, or "none">
EOF
)"
```

## 5. Finish

If you were dispatched, send `worker_done` exactly once, with
`--outcome succeeded|failed`, a three-sentence summary that includes the PR URL,
and `--files-modified`. Then idle. If the user invoked you directly, reply with
the PR link and the in-game checks, if any.
