using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace TestTools
{
    public enum MacroCancel
    {
        Dodge,
        Block,
    }

    // A key that swings the current weapon (primary attack), then presses dodge or block a set
    // time later, to test Feint cancels at exact timings.
    internal static class AttackMacro
    {
        // How long block is held after the cancel press.
        private const float BlockHoldSeconds = 0.6f;

        private static float s_attackAt = -1f;
        private static bool s_cancelPressed;
        private static float s_blockUntil = -1f;
        private static bool s_blockPressFrame;

        internal static void Update()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                s_attackAt = -1f;
                return;
            }

            if (s_attackAt < 0f && IsDown(Plugin.MacroKey.Value) && TakesInput(player))
            {
                if (player.StartAttack(null, secondaryAttack: false))
                {
                    s_attackAt = Time.time;
                    s_cancelPressed = false;
                }
                else
                {
                    Plugin.Log.LogInfo("Attack macro: the attack didn't start");
                }
                return;
            }

            if (s_attackAt < 0f)
            {
                return;
            }
            if (!s_cancelPressed && Time.time >= s_attackAt + Plugin.MacroCancelOffset.Value)
            {
                s_cancelPressed = true;
                Plugin.Log.LogInfo($"Attack macro: {Plugin.MacroCancelWith.Value} at " +
                    $"{Time.time - s_attackAt:0.000}s");
                if (Plugin.MacroCancelWith.Value == MacroCancel.Dodge)
                {
                    // Like block + jump with no direction held: dodge backwards.
                    Vector3 dir = player.m_moveDir;
                    if (dir.magnitude < 0.1f)
                    {
                        dir = -player.m_lookDir;
                        dir.y = 0f;
                        dir.Normalize();
                    }
                    player.Dodge(dir);
                }
                else
                {
                    s_blockUntil = Time.time + BlockHoldSeconds;
                    s_blockPressFrame = true;
                }
            }
            if (s_cancelPressed && Time.time >= s_blockUntil)
            {
                s_attackAt = -1f;
            }
        }

        private static bool TakesInput(Player player)
        {
            PlayerController controller = player.GetComponent<PlayerController>();
            return controller != null && controller.TakeInput() && !controller.InInventoryEtc();
        }

        // KeyboardShortcut.IsDown() fails while any other key is held, so only check the main key
        // and the shortcut's own modifiers.
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

        // Holds block through the input path, so Feint sees a real block press. Runs before Feint's
        // own SetControls prefix, which reads these arguments.
        [HarmonyPatch(typeof(Player), nameof(Player.SetControls))]
        private static class SetControlsPatch
        {
            [HarmonyPriority(Priority.First)]
            [HarmonyBefore("hvalch.Feint")]
            private static void Prefix(Player __instance, ref bool block, ref bool blockHold)
            {
                if (__instance != Player.m_localPlayer || Time.time >= s_blockUntil)
                {
                    return;
                }
                blockHold = true;
                if (s_blockPressFrame)
                {
                    block = true;
                    s_blockPressFrame = false;
                }
            }
        }
    }
}
