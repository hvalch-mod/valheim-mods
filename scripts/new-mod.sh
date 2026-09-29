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

dotnet sln ValheimMods.sln add "$dir/$name.csproj"
echo "Created $dir"
