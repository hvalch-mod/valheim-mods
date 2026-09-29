# Valheim mod dev

BepInEx 5 plugins for Valheim, built with the .NET SDK on Linux and deployed straight into the Gale `Server` profile.

## Layout

- `Directory.Build.props` — paths + references shared by every mod (BepInEx, Harmony, game + Unity assemblies). Game assemblies are **publicized** at build time, so private members (`Player.Awake`, `m_staminaRegen`, ...) compile directly.
- `Directory.Build.targets` — after build, copies `<Mod>.dll` + `.pdb` to `profiles/Server/BepInEx/plugins/<Mod>/`.
- `src/<Mod>/` — one project per mod.
- `scripts/new-mod.sh <Name> [--jotunn]` — scaffold a new mod and add it to the solution.

## Paths

Defaults: game at `/home/games/steamapps/common/Valheim`, profile at `~/.local/share/com.kesomannen.gale/valheim/profiles/Server`.
Override by creating `Environment.props` (gitignored):

```xml
<Project>
  <PropertyGroup>
    <ValheimDir>/other/path/Valheim</ValheimDir>
    <ProfileDir>/other/profile</ProfileDir>
  </PropertyGroup>
</Project>
```

## Workflow

```sh
dotnet build                             # build all + deploy to profile
dotnet build -p:DeployToProfile=false    # build only
dotnet build src/Foo                     # one mod
```

Then launch Valheim through Gale (Server profile). Logs: `profiles/Server/BepInEx/LogOutput.log`.
Config files appear in `profiles/Server/BepInEx/config/<GUID>.cfg` after first run.

Anything deployed into the profile goes to the dedicated server on the next server sync. Remove `BepInEx/plugins/<Mod>/` from the profile if a mod should stay local.

## How Valheim mods work (short)

- **BepInEx 5** loads every `BaseUnityPlugin` in `BepInEx/plugins`. `[BepInPlugin(guid, name, version)]` identifies it; `Awake()` is the entry point; `Config.Bind` makes `.cfg` entries.
- **Harmony** patches game methods at runtime: `[HarmonyPatch(typeof(Player), nameof(Player.Awake))]` + `Prefix`/`Postfix`/`Transpiler`. `__instance`, `__result`, `___privateField` are magic parameter names.
- **Game code** lives in `valheim_Data/Managed/assembly_valheim.dll`. Browse it with ILSpy / `ilspycmd` / dnSpy to find what to patch. Key classes: `Player`, `Character`, `Humanoid`, `ZNet`/`ZDO`/`ZRoutedRpc` (networking), `ObjectDB` (items/recipes), `ZNetScene` (prefabs), `Piece`, `Container`.
- **Jotunn** (`--jotunn`) is the modding library for adding content — items, pieces, recipes, localization, config sync, commands. Add `[BepInDependency(Jotunn.Main.ModGuid)]`. Docs: https://valheim-modding.github.io/Jotunn/
- **Multiplayer**: logic that must be authoritative runs on the server (`ZNet.instance.IsServer()`). If clients need the mod too, set `[NetworkCompatibility]` (Jotunn) so version mismatch blocks joining.

## Publishing (later)

Thunderstore zip = `manifest.json` + `icon.png` (256x256) + `README.md` + the dll, at zip root.
