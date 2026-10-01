using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Feint
{
    // Input handling, all in Player.SetControls (called from PlayerController.FixedUpdate):
    // the dedicated dodge key, what block + jump does, and starting to block mid-attack.
    internal static class Controls
    {
        // Key presses are read in Update; SetControls runs in FixedUpdate, so hold them briefly.
        private const float PressLifetime = 0.2f;
        private static float s_dodgePressedAt = float.NegativeInfinity;

        internal static void PollDodgeKey()
        {
            if (!IsDown(Plugin.DodgeKey.Value))
            {
                return;
            }
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return;
            }
            PlayerController controller = player.GetComponent<PlayerController>();
            if (controller != null && controller.TakeInput() && !controller.InInventoryEtc() && !Hud.InRadial())
            {
                s_dodgePressedAt = Time.time;
            }
        }

        // KeyboardShortcut.IsDown() fails while any other key is held (e.g. a movement key), so only
        // check the main key and the shortcut's own modifiers.
        private static bool IsDown(KeyboardShortcut shortcut)
        {
            if (shortcut.MainKey == KeyCode.None || !UnityInput.Current.GetKeyDown(shortcut.MainKey))
            {
                return false;
            }
            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                if (!UnityInput.Current.GetKey(modifier))
                {
                    return false;
                }
            }
            return true;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
        private static class SetControlsPatch
        {
            private static void Prefix(Player __instance, bool block, bool blockHold, ref bool jump, ref bool dodge,
                out bool __state)
            {
                __state = false;
                if (__instance != Player.m_localPlayer)
                {
                    return;
                }

                if (Time.time - s_dodgePressedAt < PressLifetime)
                {
                    s_dodgePressedAt = float.NegativeInfinity;
                    dodge = true;
                }

                // Same as vanilla's m_blocking update below.
                bool blocking = __instance.m_toggleBlock ? __instance.m_blocking != block : blockHold;
                if (blocking && !__instance.m_blocking)
                {
                    Cancel.TryCancel(__instance, dodge: false);
                }

                // Vanilla: on keyboard (or the default gamepad layout), jump while blocking dodges.
                bool keyboardPath = ZInput.InputLayout == InputLayout.Default || !ZInput.IsGamepadActive();
                if (jump && !dodge && blocking && keyboardPath && Plugin.BlockJump.Value != BlockJumpAction.Dodge)
                {
                    jump = false;
                    __state = Plugin.BlockJump.Value == BlockJumpAction.Jump;
                }
            }

            private static void Postfix(Player __instance, bool __state)
            {
                if (__state)
                {
                    __instance.Jump();
                }
            }
        }
    }
}
