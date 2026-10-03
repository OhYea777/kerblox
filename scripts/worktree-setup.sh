#!/usr/bin/env bash
# Prepare a fresh worktree. Run by Orca via orca.yaml; safe to run by hand:
#   scripts/worktree-setup.sh <primary-checkout> <worktree>
set -euo pipefail

root="${1:?primary checkout path}"
wt="${2:?worktree path}"

# Agent settings that are deliberately not committed.
if [[ -f "$root/.claude/settings.local.json" && ! -e "$wt/.claude/settings.local.json" ]]; then
  mkdir -p "$wt/.claude"
  cp "$root/.claude/settings.local.json" "$wt/.claude/"
fi

# KSPBuildTools auto-detects a KSP install at <solution dir>/KSP. Share the
# primary checkout's link (or KSP_ROOT) so lanes build without extra env.
if [[ ! -e "$wt/ksp/KSP" ]]; then
  if [[ -L "$root/ksp/KSP" ]]; then
    ln -s "$(readlink -f "$root/ksp/KSP")" "$wt/ksp/KSP"
  elif [[ -n "${KSP_ROOT:-}" ]]; then
    ln -s "$KSP_ROOT" "$wt/ksp/KSP"
  else
    echo "warning: no KSP install linked; the plugin won't build (Core and tests will)" >&2
  fi
fi

if ! command -v dotnet >/dev/null && [[ -x "$HOME/.dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
fi
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

cd "$wt/ksp"
dotnet restore --verbosity quiet
dotnet test tests/Kerblox.Core.Tests --verbosity quiet --nologo
