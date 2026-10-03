---
name: ksp-api-verify
description: "Confirms how a KSP 1.12 API member actually behaves by decompiling the installed game's Assembly-CSharp.dll, instead of trusting memory, forum posts or other mods. Use before relying on any KSP class, field, method, event or lifecycle ordering not already used in this repo, or when the user asks \"does KSP do X\", \"when is OnLoad called\", \"what's the signature of Y\"."
---

# Verify a KSP API fact

KSP has almost no official API docs, and community knowledge often describes
older versions or mods built against publicized assemblies. The installed game
is the only reliable source.

## Steps

1. **Check the notes first.** `docs/KSP-API-NOTES.md` may already answer it.
2. **Get the source.** This decompiles once per game build into
   `~/.cache/kerblox/decompiled/ksp-<build>/`, shared by all worktrees:
   ```sh
   src="$(scripts/ksp-decompile.sh)"        # first run takes about 20 s
   ```
3. **Find the member:**
   ```sh
   grep -n "public .* FindAttachNode(" "$src/Part.cs"
   grep -rln "class DragCubeSystem\b" "$src"
   grep -rn "GetModuleMass(" "$src" --include=*.cs | head
   ```
4. **Read the logic without obfuscator noise.** KSP's DLL is full of dead
   `while (true) { switch (N) { case 0: continue; } break; }` blocks and
   `__ldtoken` lines. This prints a type with them stripped:
   ```sh
   scripts/ksp-decompile.sh Part | grep -A40 'public void UpdateMass()'
   ```
   For lifecycle questions ("is X called before Y"), find the caller and read
   the order of calls. `PartLoader.ParsePart` and `CompileParts` are the usual
   ones for load-time questions.
5. **Check accessibility.** Code from other mods often uses lowercase private
   fields through a publicized assembly. If the decompile shows
   `private float[] area` and `public float[] Area => area`, use `Area`.
   Compiling against the real DLL (`dotnet build ksp`) settles it.
6. **Record the result** in `docs/KSP-API-NOTES.md` under the right section:
   the fact, then `Verified` (with type/method) / `Unverified` / `In-game`.
   Behaviour that depends on runtime state (physics, rendering, editor input)
   is `In-game` even if the code looks clear; list it in the PR's In-game checks.

## Reference mods worth reading

Read these for patterns, then verify the KSP calls they make as above:

- Procedural Parts (`KSP-RO/ProceduralParts`) and ROUtils (`KSP-RO/ROUtils`),
  especially `ProceduralTools/DragCubeTool.cs`: runtime meshes, colliders,
  drag cubes, attach node moves.
- KSPCommunityFixes (`KSPModdingLibs/KSPCommunityFixes`): documents many stock
  bugs and their exact causes.

## Don't

- Don't state a KSP API fact in code comments, docs or PRs that you haven't
  verified. Write "unverified" instead.
- Don't copy decompiled KSP code into this repo. Describe the behaviour and
  cite the type/method.
