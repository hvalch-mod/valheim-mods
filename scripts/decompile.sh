#!/usr/bin/env bash
# Decompile game assemblies into decompiled/<assembly>/ (one .cs file per type) for grepping.
# Usage: scripts/decompile.sh [assembly ...]   default: assembly_valheim assembly_utils
# Re-run after a game update.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

ilspy="$(command -v ilspycmd || echo "$HOME/.dotnet/tools/ilspycmd")"
managed="$(grep -oP "<ValheimDir[^>]*>\K[^<]+" Directory.Build.props)/valheim_Data/Managed"
refs="$(grep -oP "<ProfileDir[^>]*>\K[^<]+" Directory.Build.props | sed "s|\$(HOME)|$HOME|")/BepInEx/core"

(( $# )) || set -- assembly_valheim assembly_utils
for asm in "$@"; do
  out="decompiled/$asm"
  rm -rf "$out" && mkdir -p "$out"
  echo "Decompiling $asm -> $out"
  "$ilspy" -p -o "$out" -r "$managed" -r "$refs" "$managed/$asm.dll" >/dev/null
done
