---
name: kerblox-orchestrate
description: "Works through Kerblox ROADMAP.md items in parallel: picks the ready items, gives each its own Orca worktree and supervised worker, gates each PR through review, merges the clean ones and reports one digest. Use when the user says \"work the roadmap\", \"run the next roadmap items\", \"do P2.1 and P2.3 in parallel\", \"orchestrate phase 2\", or names roadmap ids to build."
---

# Kerblox roadmap orchestrator

You are the **coordinator**. You select roadmap items, start one supervised
Orca worker per item in its own worktree, review what comes back, merge what's
clean, and report once at the end. You do not write feature code yourself.

## Before anything

1. Load the Orca orchestration guide for the exact binary you'll use. Inside an
   Orca terminal the CLI is `orca`. On Linux outside Orca it's `orca-ide`;
   never run bare `orca` there, because it's the GNOME screen reader.
   ```sh
   orca skills get orchestration
   orca skills get orchestration --reference references/placement-and-remote.md
   ```
   Follow its loop, worker-accounting and safety rules exactly. This skill only
   adds Kerblox policy on top of it and never overrides it.
2. Read `AGENTS.md` and `ROADMAP.md` on an up-to-date `main`
   (`git switch main && git pull --ff-only`).

## 1. Select the batch

- **Ready** means the item's Status is `todo` and every id in "Depends on" is
  `done`. If the user named ids, use those, but stop and report if one isn't ready.
- Default batch: every ready item, capped at **4** parallel lanes.
- Don't parallelise two items that will edit the same files heavily (for
  example two items that both rewrite `ModuleBlockGrid.cs`). Chain them with a
  dependency instead.
- Show the user the batch (ids, titles, gates, and which items will be held for
  in-game checks) in one short message, then proceed without waiting unless
  they object.

## 2. Create the run and tasks

```sh
orca orchestration run-create --objective "Kerblox roadmap: <ids>" --json
orca orchestration task-create --task-title "<id>: <title>" --spec "<brief>" --deps '[<orca task ids of in-batch deps>]' --json
```

Each brief must satisfy the orchestration Task-spec contract. Use this shape:

```
Implement Kerblox roadmap item <ID> "<title>" in this worktree. Load the
`kerblox-item` skill first and follow it.
Target: <files/components from the item's Scope>.
Change: <the item's Scope, verbatim>.
Constraints: AGENTS.md architecture rules; Kerblox.Core stays Unity-free;
verify any KSP API with the ksp-api-verify skill; no new mod dependencies;
never run scripts/deploy.sh or touch $KSP_ROOT.
Ownership: only this item's files, plus this item's Status line in ROADMAP.md
and new entries in docs/KSP-API-NOTES.md. Branch feat/<id-lower>-<slug>.
Acceptance: <the item's Acceptance>; `dotnet build ksp -c Release` has 0
warnings; `dotnet test ksp` passes; a PR is open against main. Gate: <none|in-game>.
```

## 3. Start one worker per item, each in its own worktree

```sh
orca orchestration worker-start --task <task_id> --worktree new-top-level \
  --name <id-lower>-<slug> --base-branch main --agent claude --setup run --json
```

- Every item gets its own worktree. `orca.yaml` runs
  `scripts/worktree-setup.sh`, which links KSP and confirms the tree is green.
- Pass `--model`/`--effort` only if the user asked for them.
- Start the whole ready wave before the first wait.

## 4. Supervise

Use the guide's `check --wait` loop. Answer worker questions from ROADMAP,
AGENTS.md and the docs. If a question is a genuine product or design fork
(for example the transport choice in P3.1), don't guess. Park that item,
keep the others running, and put the question in the digest.

## 5. Review gate (each `worker_done` with `--outcome succeeded`)

1. Get the PR: `gh pr list --head <branch> --json number,url`.
2. Start a **review worker** in `--worktree current` with a read-only brief:
   review `gh pr diff <n>` against the item's Acceptance and AGENTS.md, check
   KSP API claims against `scripts/ksp-decompile.sh`, and report blocking
   findings only. No checkout, no edits, no PR comments.
3. Blocking findings go back to the original lane: reuse its terminal for a
   follow-up dispatch (see the guide's same-terminal reuse) with the findings as
   the brief. Then review again. Stop after two fix rounds and report.
4. Wait for CI: `gh pr checks <n> --watch`.

## 6. Merge policy

- **Gate `none`, review clean, CI green**: `gh pr merge <n> --squash --delete-branch`,
  then set the item to `done` on main if the PR didn't already.
- **Gate `in-game`**: never merge. Leave the PR open with the item in `review`.
  The digest lists its In-game checks for the user. If the user says the
  checks passed, merge and mark `done`.
- If the user said "don't merge" at kickoff, merge nothing and list the PRs.
- After each merge, re-check readiness. Newly ready items can join the run as
  a new wave if lanes are free.

## 7. Close out

Settle every worker per the guide's completion accounting (release or retain).
Then write **one digest**, outcome first:

- One line per item: id, outcome (merged / PR held for in-game / parked / failed), PR link.
- In-game checks the user needs to run, grouped by PR.
- Parked questions, each one a decision the user can answer in a word.
