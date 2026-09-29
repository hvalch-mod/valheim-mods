#!/usr/bin/env bash
# Usage: scripts/package.sh [ModName ...]   (no args: every src/<Mod>/ that has a manifest.json)
# Builds each mod and zips it as a Thunderstore package: dist/<Author>-<ModName>-<Version>.zip
# Needs in src/<ModName>/: manifest.json (version_number is stamped from the csproj), README.md,
# icon.png (256x256), and optionally CHANGELOG.md.
# Import the zip in Gale (Import > Local mod) so Gale lists and manages it. Thunderstore takes the same zip.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
root="$PWD"
author="$(grep -oP "<Author[^>]*>\K[^<]+" Directory.Build.props)"

if (( $# == 0 )); then
  for m in src/*/manifest.json; do set -- "$@" "$(basename "$(dirname "$m")")"; done
  (( $# )) || { echo "no src/*/manifest.json found" >&2; exit 1; }
fi

package() {
  local name="$1" dir="src/$1"
  local version plugin_version stage out files=(manifest.json README.md icon.png "$1.dll")
  version="$(grep -oP "<Version>\K[^<]+" "$dir/$name.csproj")"
  plugin_version="$(grep -oP 'const string Version = "\K[^"]+' "$dir/Plugin.cs")"
  [[ "$version" == "$plugin_version" ]] || { echo "$name: csproj Version $version != Plugin.Version $plugin_version" >&2; return 1; }
  for f in manifest.json README.md icon.png; do
    [[ -f "$dir/$f" ]] || { echo "$name: missing $dir/$f" >&2; return 1; }
  done

  dotnet build "$dir" -c Release -p:DeployToProfile=false -v quiet -nologo

  stage="$(mktemp -d)"
  jq --arg v "$version" '.version_number = $v' "$dir/manifest.json" > "$stage/manifest.json"
  cp "$dir/README.md" "$dir/icon.png" "$dir/bin/Release/$name.dll" "$stage/"
  if [[ -f "$dir/CHANGELOG.md" ]]; then
    cp "$dir/CHANGELOG.md" "$stage/"
    files+=(CHANGELOG.md)
  fi

  mkdir -p dist
  out="dist/$author-$name-$version.zip"
  rm -f "$out"
  (cd "$stage" && python3 -m zipfile -c "$root/$out" "${files[@]}")
  rm -rf "$stage"
  echo "Packaged $out"
}

for name in "$@"; do package "$name"; done
