using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn;
using Jotunn.Utils;

namespace Unhindered
{
    // Client-side mod. Optional on the server: if the server has it, its admin-only settings are
    // pushed to clients (and locked for non-admins); if not, each client uses its own config.
    // No process filter: the dedicated server must load it to send its config.
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency(Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.NotEnforced, VersionStrictness.None)]
    [SynchronizationMode(AdminOnlyStrictness.IfOnServer)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "hvalch.Unhindered";
        public const string Name = "Unhindered";
        public const string Version = "0.1.1";

        internal static ManualLogSource Log;

        // Pass-through: small plants stop blocking characters.
        internal static ConfigEntry<bool> PassThroughEnabled;
        internal static ConfigEntry<bool> PassThroughPickables;
        internal static ConfigEntry<bool> PassThroughPlants;
        internal static ConfigEntry<string> PassThroughNames;
        internal static ConfigEntry<string> PassThroughExclude;
        internal static ConfigEntry<bool> PassThroughLogMatches;

        // Step-up: walk over low obstacles (logs, rocks) without jumping.
        internal static ConfigEntry<bool> StepUpEnabled;
        internal static ConfigEntry<float> StepUpMaxHeightWalk;
        internal static ConfigEntry<float> StepUpMaxHeightJog;
        internal static ConfigEntry<float> StepUpMaxHeightSprint;
        internal static ConfigEntry<float> StepUpSpeed;
        internal static ConfigEntry<bool> StepUpOnTerrain;
        internal static ConfigEntry<bool> StepUpDebug;

        private readonly Harmony _harmony = new Harmony(Guid);

        private void Awake()
        {
            Log = Logger;

            const string pt = "1 - Pass Through";
            PassThroughEnabled = BindSynced(pt, "Enabled", true,
                "Let characters walk through small plants (bushes, mushrooms, berry bushes, crops...). " +
                "They can still be picked, chopped and hit.");
            PassThroughPickables = BindSynced(pt, "Pickables", true,
                "Include every pickable object (mushrooms, berries, thistle, flax, stones, branches...).");
            PassThroughPlants = BindSynced(pt, "Plants", true,
                "Include growing plants and saplings (crops, tree saplings).");
            PassThroughNames = BindSynced(pt, "NameContains", "bush,shrub",
                "Comma-separated, case-insensitive. Any prefab whose name contains one of these is included " +
                "(e.g. add 'FirTree_small,Beech_small' to walk through small trees).");
            PassThroughExclude = BindSynced(pt, "Exclude", "",
                "Comma-separated, case-insensitive. Prefabs whose name contains one of these are never changed. Wins over everything above.");
            PassThroughLogMatches = Config.Bind(pt, "LogMatches", false,
                "Log every prefab made pass-through (to tune the lists above). Local only, never synced.");

            const string su = "2 - Step Up";
            StepUpEnabled = BindSynced(su, "Enabled", true,
                "Automatically step up onto low obstacles (logs, rocks, ledges) while moving, instead of having to jump.");
            StepUpMaxHeightWalk = BindSynced(su, "MaxHeightWalk", 0.7f,
                "Tallest obstacle (meters) you can step onto while walking, crouching or encumbered. 0 = never step up.",
                new AcceptableValueRange<float>(0f, 2f));
            StepUpMaxHeightJog = BindSynced(su, "MaxHeightJog", 1.2f,
                "Tallest obstacle (meters) you can step onto at normal running speed. 0 = never step up.",
                new AcceptableValueRange<float>(0f, 2f));
            StepUpMaxHeightSprint = BindSynced(su, "MaxHeightSprint", 1.5f,
                "Tallest obstacle (meters) you can step onto while sprinting. 0 = never step up.",
                new AcceptableValueRange<float>(0f, 2f));
            StepUpSpeed = BindSynced(su, "Speed", 6f,
                "How fast you rise when stepping up (m/s). Higher is snappier; 20 is near-instant.",
                new AcceptableValueRange<float>(1f, 20f));
            StepUpOnTerrain = BindSynced(su, "StepOnTerrain", false,
                "Also step up terrain ledges (e.g. hoe-raised ground). Off by default so natural cliffs still need a jump.");
            StepUpDebug = Config.Bind(su, "DebugLog", false,
                "Log why each obstacle you walk into is or isn't stepped onto (to LogOutput.log). Local only, never synced.");

            // In-game config manager edits and server sync re-apply live.
            Config.SettingChanged += (_, e) =>
            {
                if (e.ChangedSetting.Definition.Section == pt)
                {
                    PassThrough.MarkDirty();
                }
            };

            _harmony.PatchAll();
            Log.LogInfo($"{Name} {Version} loaded");
        }

        private void Update() => PassThrough.RefreshIfDirty();

        private void OnDestroy() => _harmony.UnpatchSelf();

        // Admin-only entries are overwritten by the server's values when the server runs this mod.
        private ConfigEntry<T> BindSynced<T>(string section, string key, T value, string description,
            AcceptableValueBase range = null) =>
            Config.Bind(section, key, value, new ConfigDescription(description, range,
                new ConfigurationManagerAttributes { IsAdminOnly = true }));
    }
}
