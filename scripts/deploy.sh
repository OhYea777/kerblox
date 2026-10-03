#!/usr/bin/env bash
# Build Kerblox and install it into $KSP_ROOT/GameData/Kerblox.
#
#   KSP_ROOT=/path/to/Kerbal\ Space\ Program scripts/deploy.sh [--debug] [--skip-tests] [--link]
#
#   --debug       Debug build (also copies .pdb files)
#   --skip-tests  Don't run the Core unit tests first
#   --link        Symlink GameData/Kerblox into the game instead of copying,
#                 so later builds land in the game directly
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
config=Release
run_tests=1
link=0
for arg in "$@"; do
  case "$arg" in
    --debug) config=Debug ;;
    --skip-tests) run_tests=0 ;;
    --link) link=1 ;;
    -h|--help) sed -n '2,11p' "$0"; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

if [[ -z "${KSP_ROOT:-}" && -L "$repo/ksp/KSP" ]]; then
  KSP_ROOT="$(readlink -f "$repo/ksp/KSP")"
fi
if [[ -z "${KSP_ROOT:-}" ]]; then
  echo "KSP_ROOT is not set and ksp/KSP isn't a symlink to an install. Example:" >&2
  echo "  export KSP_ROOT=\"\$HOME/.local/share/Steam/steamapps/common/Kerbal Space Program\"" >&2
  exit 1
fi
export KSP_ROOT
if [[ ! -f "$KSP_ROOT/KSP_Data/Managed/Assembly-CSharp.dll" ]]; then
  echo "KSP_ROOT=$KSP_ROOT doesn't look like a Linux KSP 1.x install (no KSP_Data/Managed/Assembly-CSharp.dll)" >&2
  exit 1
fi

# Prefer a user-local SDK (~/.dotnet) if dotnet isn't on PATH.
if ! command -v dotnet >/dev/null && [[ -x "$HOME/.dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
fi
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

cd "$repo/ksp"
if [[ "$run_tests" == 1 ]]; then
  dotnet test tests/Kerblox.Core.Tests -c "$config" --nologo -v quiet
fi
# Remove stale plugin DLLs so a renamed/removed assembly doesn't linger in GameData.
rm -rf GameData/Kerblox/Plugins
dotnet build src/Kerblox -c "$config" --nologo -v quiet

src="$repo/ksp/GameData/Kerblox"
dest="$KSP_ROOT/GameData/Kerblox"
if [[ "$link" == 1 ]]; then
  if [[ -e "$dest" && ! -L "$dest" ]]; then
    echo "$dest exists and is not a symlink; remove it first" >&2
    exit 1
  fi
  ln -sfn "$src" "$dest"
  echo "Linked $dest -> $src"
else
  if [[ -L "$dest" ]]; then rm "$dest"; fi
  mkdir -p "$dest"
  rsync -a --delete "$src/" "$dest/"
  echo "Copied $src -> $dest"
fi
