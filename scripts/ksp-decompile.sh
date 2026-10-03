#!/usr/bin/env bash
# Decompile KSP's Assembly-CSharp.dll once per game build into a shared cache,
# then print the cache path. Used by the ksp-api-verify skill.
#
#   scripts/ksp-decompile.sh            # prints the decompiled source directory
#   scripts/ksp-decompile.sh Part       # prints Part.cs with obfuscator noise removed
#
# The cache lives outside the repo so every worktree reuses it.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ -z "${KSP_ROOT:-}" && -L "$repo/ksp/KSP" ]]; then KSP_ROOT="$(readlink -f "$repo/ksp/KSP")"; fi
: "${KSP_ROOT:?KSP_ROOT is not set and ksp/KSP is not linked}"
managed="$KSP_ROOT/KSP_Data/Managed"

if ! command -v dotnet >/dev/null && [[ -x "$HOME/.dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
fi
export PATH="$HOME/.dotnet/tools:$PATH" DOTNET_ROLL_FORWARD=Major DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
if ! command -v ilspycmd >/dev/null; then
  dotnet tool install -g ilspycmd >&2
fi

build="$(sed -n 's/^build id = //p' "$KSP_ROOT/buildID64.txt" "$KSP_ROOT/buildID.txt" 2>/dev/null | head -1 || true)"
out="${XDG_CACHE_HOME:-$HOME/.cache}/kerblox/decompiled/ksp-${build:-unknown}"
if [[ ! -f "$out/.done" ]]; then
  mkdir -p "$out"
  echo "Decompiling Assembly-CSharp.dll (build ${build:-unknown}) into $out ..." >&2
  ilspycmd -p -o "$out" -r "$managed" "$managed/Assembly-CSharp.dll" >/dev/null
  touch "$out/.done"
fi

if [[ $# -eq 0 ]]; then
  echo "$out"
else
  # KSP's assembly is run through an obfuscator that injects dead
  # `while (true) { switch (N) { case 0: continue; } break; }` blocks and
  # `__ldtoken` markers. Strip those lines so the real logic is readable.
  file="$out/$1.cs"
  [[ -f "$file" ]] || { echo "no such type file: $file" >&2; exit 1; }
  grep -nvE '^\s*(while \(true\)|\{|\}|switch \([0-9]+\)|case 0:|continue;|break;|if \(1 == 0\)|__ldtoken.*)\s*$' "$file"
fi
