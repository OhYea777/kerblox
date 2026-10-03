#!/usr/bin/env bash
# Show what matters from the last KSP session: Kerblox lines, part compiler and
# assembly loader messages about Kerblox, drag cube output, and every exception.
#
#   KSP_ROOT=... scripts/ksp-logs.sh [--all]
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ -z "${KSP_ROOT:-}" && -L "$repo/ksp/KSP" ]]; then KSP_ROOT="$(readlink -f "$repo/ksp/KSP")"; fi
ksp_log="${KSP_ROOT:?KSP_ROOT is not set and ksp/KSP is not linked}/KSP.log"
player_log="${XDG_CONFIG_HOME:-$HOME/.config}/unity3d/Squad/Kerbal Space Program/Player.log"

pattern='\[Kerblox\]|kerbloxBlockGrid|ModuleBlockGrid|Kerblox\.(Core\.)?dll|AssemblyLoader.*(Kerblox|Exception)|PartCompiler|DragCubeSystem.*kerblox'
[[ "${1:-}" == --all ]] && pattern="$pattern|Exception|\[ERR"

for log in "$ksp_log" "$player_log"; do
  echo "===== $log"
  if [[ -f "$log" ]]; then
    grep -nE "$pattern" "$log" || echo "(no matches)"
    echo "-- exceptions mentioning Kerblox (with 3 lines of stack):"
    grep -nE -A3 'Exception' "$log" | grep -B1 -A3 -i kerblox || echo "(none)"
  else
    echo "(missing)"
  fi
done
