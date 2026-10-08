using System.Collections.Generic;
using HarmonyLib;
using Jotunn.Managers;

namespace TestTools
{
    // Vanilla only enables dev commands for the host (Terminal.IsCheatsEnabled checks ZNet.IsServer),
    // so an admin on a dedicated server can't spawn, fly or build for free. For testing on a local
    // dedicated server, treat an admin client as the host: after 'devcommands', cheat commands
    // run on the client. Admin-only commands are allowed; other server-only commands stay refused,
    // except the ones below, which only change the local player.
    internal static class AdminCheats
    {
        private static readonly HashSet<string> LocalOnlyServerCommands = new HashSet<string>
        {
            "debugmode", "fly", "nocost",
        };

        private static bool _logged;

        // Jotunn's flag comes straight from the server's own admin check, so it doesn't depend on
        // the admin list's ID format matching the client's platform ID.
        private static bool IsRemoteAdmin()
        {
            if (ZNet.instance == null || ZNet.instance.IsServer()) return false;
            bool admin = SynchronizationManager.Instance.PlayerIsAdmin || ZNet.instance.LocalPlayerIsAdminOrHost();
            if (!_logged && Terminal.m_cheat)
            {
                _logged = true;
                Plugin.Log.LogInfo($"AdminCheats: devcommands on, admin = {admin} " +
                                   $"(Jotunn {SynchronizationManager.Instance.PlayerIsAdmin}, admin list {ZNet.instance.LocalPlayerIsAdminOrHost()})");
            }
            return admin;
        }

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.IsCheatsEnabled))]
        private static class CheatsEnabled
        {
            private static void Postfix(ref bool __result)
            {
                if (!__result && Terminal.m_cheat && IsRemoteAdmin()) __result = true;
            }
        }

        [HarmonyPatch(typeof(Terminal.ConsoleCommand), nameof(Terminal.ConsoleCommand.IsValid))]
        private static class LocalServerOnlyCommands
        {
            private static void Postfix(Terminal.ConsoleCommand __instance, Terminal context, ref bool __result)
            {
                // Admin-only commands built with the value-returning constructor (spawn, goto...) get
                // OnlyServer = onlyServer | onlyAdmin, which turns "admin only" into "host only".
                if (__result || !__instance.OnlyServer) return;
                if (!__instance.OnlyAdmin && !LocalOnlyServerCommands.Contains(__instance.Command)) return;
                if (context.IsCheatsEnabled() && IsRemoteAdmin()) __result = true;
            }
        }
    }
}
