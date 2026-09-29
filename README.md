# Valheim mod dev

BepInEx 5 plugins for Valheim, built with the .NET SDK on Linux and deployed straight into the Gale `Server` profile.

## Mods

| Mod | Version | What it does |
|---|---|---|
| [Unhindered](src/Unhindered/README.md) | 0.1.0 | Walk through small plants; step over logs and low rocks without jumping. |

`src/ExampleMod` is a reference sample, not released.

## Layout

- `Directory.Build.props` — paths + references shared by every mod (BepInEx, Harmony, game + Unity assemblies). Game assemblies are **publicized** at build time, so private members (`Player.Awake`, `m_staminaRegen`, ...) compile directly.
- `Directory.Build.targets` — after build, copies `<Mod>.dll` + `.pdb` to `profiles/Server/BepInEx/plugins/<Author>-<Mod>/`.
- `src/<Mod>/` — one project per mod.
- `scripts/new-mod.sh <Name> [--jotunn]` — scaffold a new mod and add it to the solution.
- `scripts/package.sh [Mod ...]` — build Thunderstore zips into `dist/` (all mods if no args).
- `scripts/decompile.sh [assembly ...]` — decompile game code into `decompiled/` (gitignored). Re-run after game updates.

## Naming

Author = `hvalch` (Thunderstore team; `<Author>` in `Directory.Build.props`). Mod names are PascalCase, no spaces.

| Thing | Pattern |
|---|---|
| Project | `src/<Name>/<Name>.csproj` |
| DLL / namespace | `<Name>` |
| GUID | `hvalch.<Name>` — never change after release (config file + dependents key on it) |
| Config | `BepInEx/config/hvalch.<Name>.cfg` |
| Profile folder | `BepInEx/plugins/hvalch-<Name>/` (matches Thunderstore installs) |

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

Anything deployed into the profile goes to the dedicated server on the next server sync. Remove `BepInEx/plugins/hvalch-<Mod>/` from the profile if a mod should stay local.

## How Valheim mods work (short)

- **BepInEx 5** loads every `BaseUnityPlugin` in `BepInEx/plugins`. `[BepInPlugin(guid, name, version)]` identifies it; `Awake()` is the entry point; `Config.Bind` makes `.cfg` entries.
- **Harmony** patches game methods at runtime: `[HarmonyPatch(typeof(Player), nameof(Player.Awake))]` + `Prefix`/`Postfix`/`Transpiler`. `__instance`, `__result`, `___privateField` are magic parameter names.
- **Game code** lives in `valheim_Data/Managed/assembly_valheim.dll`. Run `scripts/decompile.sh`, then grep `decompiled/assembly_valheim/` (one file per class) to find what to patch. Key classes: `Player`, `Character`, `Humanoid`, `ZNet`/`ZDO`/`ZRoutedRpc` (networking), `ObjectDB` (items/recipes), `ZNetScene` (prefabs), `Piece`, `Container`.
- **Jotunn** (`--jotunn`) is the modding library for adding content — items, pieces, recipes, localization, config sync, commands. Add `[BepInDependency(Jotunn.Main.ModGuid)]`. Docs: https://valheim-modding.github.io/Jotunn/
- **Multiplayer**: logic that must be authoritative runs on the server (`ZNet.instance.IsServer()`). Default for these mods: client-side, with config synced from the server when it has the mod (Jotunn admin-only entries).

## Releasing

Each mod is versioned on its own. To release `<Mod>`:

1. Bump `<Version>` in `src/<Mod>/<Mod>.csproj` and `Plugin.Version` (semver; they must match).
2. Add an entry to `src/<Mod>/CHANGELOG.md` and update the version in the table above.
3. `scripts/package.sh <Mod>` → `dist/hvalch-<Mod>-<Version>.zip` (manifest, README, CHANGELOG, icon, dll).
4. Commit, then tag `<Mod>-v<Version>` (e.g. `Unhindered-v0.1.0`).
5. Install it: Gale → Import → Local mod → the zip. Or upload the zip to Thunderstore.

Gale only lists mods it installed. The copy `dotnet build` drops into the profile still loads, but it doesn't appear in Gale.

## License

MIT. See [LICENSE](LICENSE). Use, change and share freely; keep the copyright notice.

## License

MIT. See [LICENSE](LICENSE). Use, change and share freely; keep the copyright notice.
