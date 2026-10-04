using HarmonyLib;
using UnityEngine;

namespace Unhindered
{
    // Tracks the cart the local player is pulling so step-up ignores it (stepping onto it breaks the hitch).
    // AttachTo/Detach run on the cart's owner, which is the hauling client. A joint that snaps from force
    // is cleaned up by Vagon.Update calling Detach on the next frame.
    [HarmonyPatch]
    internal static class HauledCart
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Vagon), nameof(Vagon.AttachTo))]
        private static void AttachTo(Vagon __instance, GameObject go)
        {
            if (Player.m_localPlayer != null && go == Player.m_localPlayer.gameObject)
            {
                StepUp.s_hauledCart = __instance.m_body;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Vagon), nameof(Vagon.Detach))]
        private static void Detach(Vagon __instance)
        {
            if (ReferenceEquals(StepUp.s_hauledCart, __instance.m_body))
            {
                StepUp.s_hauledCart = null;
            }
        }
    }
}
