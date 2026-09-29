using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ExampleMod
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInProcess("valheim.x86_64")]
    [BepInProcess("valheim_server.x86_64")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "hvalch.ExampleMod";
        public const string Name = "ExampleMod";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<float> StaminaRegenMultiplier;

        private readonly Harmony _harmony = new Harmony(Guid);

        private void Awake()
        {
            Log = Logger;
            StaminaRegenMultiplier = Config.Bind("General", "StaminaRegenMultiplier", 1.0f,
                "Multiplier applied to the player's stamina regen.");

            _harmony.PatchAll();
            Log.LogInfo($"{Name} {Version} loaded");
        }

        private void OnDestroy() => _harmony.UnpatchSelf();
    }

    [HarmonyPatch(typeof(Player), nameof(Player.Awake))]
    internal static class PlayerAwakePatch
    {
        // Player.Awake and m_staminaRegen are private in the game; the publicizer exposes them at compile time.
        private static void Postfix(Player __instance)
        {
            __instance.m_staminaRegen *= Plugin.StaminaRegenMultiplier.Value;
        }
    }
}
