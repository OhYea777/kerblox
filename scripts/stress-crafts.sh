#!/usr/bin/env bash
# Writes worst-case gridData craft files for the P2.4 in-game check
# (docs/TESTING-IN-GAME.md). Never writes into the KSP install: copy the files
# into saves/<save>/Ships/VAB yourself.
#
#   scripts/stress-crafts.sh [out-dir] [sizes]
#
# out-dir defaults to ~/.cache/kerblox/stress-crafts; sizes is a comma-separated
# list of cube edges (default 64; 128 and 256 work but are slow to load in game).
set -euo pipefail
cd "$(dirname "$0")/.."

out="${1:-$HOME/.cache/kerblox/stress-crafts}"
sizes="${2:-64}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"

KERBLOX_STRESS_CRAFT_DIR="$out" KERBLOX_STRESS_SIZES="$sizes" \
  dotnet test ksp --filter "FullyQualifiedName~GridDataLengthTests.WriteStressCraftFiles" \
  --logger "console;verbosity=detailed" | grep -E "\.craft:|Passed!|Failed" || true
ls -l "$out"/*.craft
