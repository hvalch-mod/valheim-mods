using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn;
using Jotunn.Utils;

namespace Feint
{
    // What block + jump does while blocking.
    public enum BlockJumpAction
    {
        Dodge,
        Jump,
        Nothing,
    }

    // Client-side mod. Optional on the server: if the server has it, its admin-only settings are
    // pushed to clients (and locked for non-admins); if not, each client uses its own config.
    // No process filter: the dedicated server must load it to send its config.
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency(Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.NotEnforced, VersionStrictness.None)]
    [SynchronizationMode(AdminOnlyStrictness.IfOnServer)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "hvalch.Feint";
        public const string Name = "Feint";
        public const string Version = "0.2.0";

        internal static ManualLogSource Log;

        // Cancelling: server-synced.
        internal static ConfigEntry<bool> CancelWithBlock;
        internal static ConfigEntry<bool> CancelWithDodge;
        internal static ConfigEntry<float> StaminaPenalty;
        internal static ConfigEntry<bool> ScaleStaminaPenalty;
        internal static ConfigEntry<string> NonCancellableAttacks;
        internal static ConfigEntry<float> CancelledHitDamage;
        internal static ConfigEntry<bool> ScaleCancelledHitDamage;
        internal static ConfigEntry<bool> DodgeAfterHit;
        internal static ConfigEntry<bool> DodgeAfterHitPenalty;

        // Controls: local, never synced.
        internal static ConfigEntry<KeyboardShortcut> DodgeKey;
        internal static ConfigEntry<BlockJumpAction> BlockJump;
        internal static ConfigEntry<bool> DebugLog;

        private readonly Harmony _harmony = new Harmony(Guid);

        private void Awake()
        {
            Log = Logger;

            const string c = "1 - Cancel";
            CancelWithBlock = BindSynced(c, "CancelWithBlock", true,
                "Pressing block during an attack cancels it.");
            CancelWithDodge = BindSynced(c, "CancelWithDodge", true,
                "Dodging during an attack cancels it.");
            StaminaPenalty = BindSynced(c, "StaminaPenalty", 1f,
                "Extra stamina a cancel costs, as a multiple of the attack's own stamina cost " +
                "(which is already spent when the swing starts). 0 = no extra cost.",
                new AcceptableValueRange<float>(0f, 3f));
            ScaleStaminaPenalty = BindSynced(c, "ScaleStaminaPenalty", true,
                "Scale the penalty by when you cancel: nothing at the very start of the swing, rising quickly to " +
                "the full penalty for the latest cancel that still stops the hit, then down to half for a cancel " +
                "right at the hit (when the hit lands anyway). Charged when the block/dodge starts. " +
                "Off = always the full penalty. You still need the full penalty in stamina to cancel.");
            CancelledHitDamage = BindSynced(c, "CancelledHitDamage", 0.5f,
                "Damage multiplier for a cancelled swing that still reaches its hit before the block/dodge " +
                "starts. 0 = no damage, 1 = full damage. With ScaleCancelledHitDamage, this is the lowest it goes.",
                new AcceptableValueRange<float>(0f, 1f));
            ScaleCancelledHitDamage = BindSynced(c, "ScaleCancelledHitDamage", true,
                "Scale a cancelled swing's damage by how long you had been pulling back when it hit: full " +
                "damage if it hit right after the cancel, down to CancelledHitDamage if it hit just before the " +
                "block/dodge started. Off = always CancelledHitDamage.");
            DodgeAfterHit = BindSynced(c, "DodgeAfterHit", false,
                "Dodging after the swing has hit also cancels it, skipping the rest of the swing. " +
                "The dodge starts right away. Needs CancelWithDodge. Off = vanilla (the dodge waits for the swing to end).");
            DodgeAfterHitPenalty = BindSynced(c, "DodgeAfterHitPenalty", true,
                "Charge the StaminaPenalty for a dodge after the hit. With ScaleStaminaPenalty it falls from the " +
                "at-hit amount (half) right after the hit to nothing at the end of the swing. " +
                "Off = only the dodge's own stamina.");
            NonCancellableAttacks = BindSynced(c, "NonCancellableAttacks", "knife_secondary,dual_knives_secondary",
                "Comma-separated attack animation names that can never be cancelled (e.g. the knife's jump " +
                "attack). DebugLog prints the name of each attack you cancel.");

            const string k = "2 - Controls";
            DodgeKey = Config.Bind(k, "DodgeKey", KeyboardShortcut.Empty,
                "Dedicated dodge key (dodges in the direction you're moving, forward if standing still). " +
                "Same as the game's own 'AltDodge' binding. Local only, never synced.");
            BlockJump = Config.Bind(k, "BlockJump", BlockJumpAction.Dodge,
                "What jump does while blocking (keyboard): Dodge (vanilla), Jump, or Nothing. " +
                "Local only, never synced.");
            DebugLog = Config.Bind(k, "DebugLog", false,
                "Log cancel attempts and why they were refused. Local only, never synced.");

            _harmony.PatchAll();
            Log.LogInfo($"{Name} {Version} loaded");
        }

        private void Update()
        {
            Controls.PollDodgeKey();
            Cancel.Update();
        }

        private void OnDestroy() => _harmony.UnpatchSelf();

        // Admin-only entries are overwritten by the server's values when the server runs this mod.
        private ConfigEntry<T> BindSynced<T>(string section, string key, T value, string description,
            AcceptableValueBase range = null) =>
            Config.Bind(section, key, value, new ConfigDescription(description, range,
                new ConfigurationManagerAttributes { IsAdminOnly = true }));
    }
}
