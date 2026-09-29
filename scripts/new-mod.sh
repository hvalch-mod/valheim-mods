#!/usr/bin/env bash
# Usage: scripts/new-mod.sh <ModName> [--jotunn]
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

name="${1:?usage: new-mod.sh <ModName> [--jotunn]}"
jotunn="${2:-}"
dir="src/$name"
[[ -e "$dir" ]] && { echo "$dir exists" >&2; exit 1; }
mkdir -p "$dir"

author="$(grep -oP "<Author[^>]*>\K[^<]+" Directory.Build.props)"
guid="$author.$name"
use_jotunn=""
jotunn_using=""
jotunn_attr=""
if [[ "$jotunn" == "--jotunn" ]]; then
  use_jotunn=$'\n    <UseJotunn>true</UseJotunn>'
  jotunn_using=$'\nusing Jotunn;'
  jotunn_attr=$'\n    [BepInDependency(Main.ModGuid)]'
fi

cat > "$dir/$name.csproj" <<CSPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>$name</AssemblyName>
    <RootNamespace>$name</RootNamespace>
    <Version>0.1.0</Version>$use_jotunn
  </PropertyGroup>
</Project>
CSPROJ

cat > "$dir/Plugin.cs" <<CS
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;$jotunn_using

namespace $name
{
    [BepInPlugin(Guid, Name, Version)]$jotunn_attr
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "$guid";
        public const string Name = "$name";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;

        private readonly Harmony _harmony = new Harmony(Guid);

        private void Awake()
        {
            Log = Logger;
            _harmony.PatchAll();
            Log.LogInfo(\$"{Name} {Version} loaded");
        }

        private void OnDestroy() => _harmony.UnpatchSelf();
    }
}
CS

# Release files for scripts/package.sh. version_number is stamped from the csproj at package time.
deps='"denikson-BepInExPack_Valheim-5.4.2351"'
if [[ -n "$use_jotunn" ]]; then
  plugins="$(grep -oP "<ProfileDir[^>]*>\K[^<]+" Directory.Build.props | sed "s|\$(HOME)|$HOME|")/BepInEx/plugins"
  jv="$(jq -r .version_number "$plugins/ValheimModding-Jotunn/manifest.json" 2>/dev/null || echo 2.30.2)"
  deps+=$',\n    "ValheimModding-Jotunn-'"$jv"'"'
fi
cat > "$dir/manifest.json" <<JSON
{
  "name": "$name",
  "version_number": "0.0.0",
  "website_url": "",
  "description": "TODO: one sentence, max 250 characters.",
  "dependencies": [
    $deps
  ]
}
JSON

cat > "$dir/README.md" <<MD
# $name

TODO: what it does, config, multiplayer notes.
MD

cat > "$dir/CHANGELOG.md" <<MD
# Changelog

## 0.1.0

- Initial release.
MD

if command -v magick >/dev/null; then
  magick -size 256x256 gradient:'#2e5e3a-#8fbf6a' -gravity center -fill white \
    -font DejaVu-Sans-Bold -pointsize 36 -annotate 0 "$name" -depth 8 "$dir/icon.png"
else
  echo "magick not found: add a 256x256 $dir/icon.png before packaging" >&2
fi

dotnet sln ValheimMods.sln add "$dir/$name.csproj"
echo "Created $dir"
