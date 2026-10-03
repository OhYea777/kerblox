---
name: ksp-ingame-check
description: "Runs the human-in-the-loop in-game verification for a Kerblox change: deploys one build into KSP, gives the user a short checklist, then triages KSP.log/Player.log and decides the roadmap status. Use when the user says \"test it in game\", \"I ran it, here's what happened\", \"check the KSP log\", or when a PR with an in-game gate needs sign-off."
---

# In-game check

Agents can't drive KSP. The user plays; you prepare, triage and record.

## 1. Deploy (primary checkout only)

All worktrees share one KSP install, so deploy from exactly one place. To test
a PR, check its branch out in the primary checkout, or link that worktree's
`ksp/GameData/Kerblox`:

```sh
gh pr checkout <n>                 # in the primary checkout
scripts/deploy.sh                  # tests + build + copy into $KSP_ROOT/GameData/Kerblox
```

Never deploy while the user has KSP running. Ask first. KSP loads DLLs only
at startup.

## 2. Give the user the checklist

Use the PR's **In-game checks** section, or `docs/TESTING-IN-GAME.md` for
phase 1. Send it as one short numbered list with the expected value on each
line. Remind them to quit KSP afterwards, because `KSP.log` is complete only after exit.

## 3. Triage

```sh
scripts/ksp-logs.sh          # Kerblox lines, loader/compiler messages, Kerblox exceptions
scripts/ksp-logs.sh --all    # plus every exception and error
```

Map what you find to the "Logs" table in `docs/TESTING-IN-GAME.md`. Stock KSP
logs many harmless errors. Only exceptions whose stack includes `Kerblox`, and
loader errors naming our DLLs or parts, matter.

For a confirmed bug, find the cause before proposing a fix. Read the stack in
`Player.log`, and verify any KSP behaviour involved with `ksp-api-verify`.

## 4. Record

- All checks pass: merge the PR (if the user approves) and set the item to
  `done` in ROADMAP.md.
- Failures: list each failing check with its log evidence, and open the fix as
  a follow-up on the same branch, or as a new roadmap item if it's out of scope.
- Add anything learned about runtime behaviour to `docs/KSP-API-NOTES.md`
  (status `Verified (in-game)`).
