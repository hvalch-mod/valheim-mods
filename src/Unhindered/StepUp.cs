using HarmonyLib;
using UnityEngine;

namespace Unhindered
{
    // Valheim has no step-up: the player capsule only rolls over tiny lips, anything taller needs a jump.
    // After vanilla walking has set the body velocity, look for a low obstacle ahead with a walkable top
    // and room to stand, then lift the player onto it. Local player only; runs every physics tick.
    // Detection and lifting: StepUpSweep.cs.
    [HarmonyPatch(typeof(Character), nameof(Character.UpdateWalking))]
    internal static partial class StepUp
    {
        private const float MinStep = 0.05f;       // lower than this the capsule handles it by itself
        private const float MinWalkableY = 0.788f; // cos(38°), vanilla player slide angle

        private static int s_mask;
        private static int s_maskWithTerrain;
        private static int s_characterLayers;
        private static readonly RaycastHit[] s_hits = new RaycastHit[16];

        private static void Postfix(Character __instance, float dt)
        {
            if (!Plugin.StepUpEnabled.Value || !ReferenceEquals(__instance, Player.m_localPlayer))
            {
                return;
            }

            Player player = (Player)__instance;
            Rigidbody body = player.m_body;
            Vector3 dir = player.m_moveDir;
            dir.y = 0f;
            if (player.IsAttached() || player.InDodge() || player.IsStaggering() || player.IsRiding()
                || !player.CanMove() || body.isKinematic || dir.sqrMagnitude < 0.01f)
            {
                s_step.Active = false;
                return;
            }
            dir.Normalize();

            // A step in progress carries on even though lifting briefly leaves the ground.
            if (s_step.Active && ContinueStep(player, body, dir, dt))
            {
                return;
            }
            if (!player.IsOnGround())
            {
                return;
            }

            if (body.linearVelocity.y > 4f) // jumping or launched
            {
                return;
            }

            if (s_mask == 0)
            {
                s_mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "vehicle");
                s_maskWithTerrain = s_mask | LayerMask.GetMask("terrain");
                s_characterLayers = LayerMask.GetMask("character", "character_net", "character_ghost", "character_noenv");
            }
            int mask = Plugin.StepUpOnTerrain.Value ? s_maskWithTerrain : s_mask;
            int selfLayer = 1 << player.m_collider.gameObject.layer;
            float maxStep = MaxStep(player);
            if (maxStep < MinStep)
            {
                return;
            }

            Sweep(player, body, dir, dt, maxStep, mask, selfLayer);
        }

        // m_running / m_walking are set by UpdateWalking just before this postfix runs.
        private static float MaxStep(Player player)
        {
            if (player.m_running)
            {
                return Plugin.StepUpMaxHeightSprint.Value;
            }
            if (player.m_walking || player.IsCrouching() || player.IsEncumbered())
            {
                return Plugin.StepUpMaxHeightWalk.Value;
            }
            return Plugin.StepUpMaxHeightJog.Value;
        }

        // Nearest hit that actually collides with the player (skips pass-through plants).
        private static bool Raycast(Vector3 origin, Vector3 dir, float distance, int mask, int selfLayer, out RaycastHit best)
        {
            int count = Physics.RaycastNonAlloc(origin, dir, s_hits, distance, mask, QueryTriggerInteraction.Ignore);
            return Nearest(count, selfLayer, 1f, out best);
        }

        // Nearest hit among s_hits[0..count) that collides with the player and whose normal.y is at most maxNormalY.
        // Hits at distance 0 (cast started overlapping) carry no usable point/normal and are skipped.
        private static bool Nearest(int count, int selfLayer, float maxNormalY, out RaycastHit best)
        {
            best = default;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = s_hits[i];
                if (hit.distance > 0f && hit.distance < bestDistance && hit.normal.y <= maxNormalY
                    && Blocks(hit.collider, selfLayer))
                {
                    best = hit;
                    bestDistance = hit.distance;
                }
            }
            return bestDistance < float.MaxValue;
        }

        private static bool Blocks(Collider collider, int selfLayer)
        {
            Rigidbody attached = collider.attachedRigidbody;
            return (collider.excludeLayers & selfLayer) == 0
                && (attached == null || (attached.excludeLayers & selfLayer) == 0);
        }

        private static string s_lastDebug;
        private static float s_lastDebugTime;

        // Only when StepUp DebugLog is on. Logs when the message changes, or once a second while it repeats.
        private static void Debug(string what, Collider collider, string detail)
        {
            if (!Plugin.StepUpDebug.Value)
            {
                return;
            }
            string msg = $"Step-up {what}: {collider.transform.root.name}/{collider.name} " +
                         $"layer={LayerMask.LayerToName(collider.gameObject.layer)} {detail}";
            if (msg == s_lastDebug && Time.time - s_lastDebugTime < 1f)
            {
                return;
            }
            s_lastDebug = msg;
            s_lastDebugTime = Time.time;
            Plugin.Log.LogInfo(msg);
        }

        // Debug only: something is in front but wasn't treated as blocking. Says whether its layer is excluded.
        private static void DebugIgnoredLayer(Vector3 origin, Vector3 dir, float distance)
        {
            if (Plugin.StepUpDebug.Value
                && Physics.Raycast(origin, dir, out RaycastHit any, distance, ~s_characterLayers, QueryTriggerInteraction.Ignore))
            {
                int mask = Plugin.StepUpOnTerrain.Value ? s_maskWithTerrain : s_mask;
                bool layerIncluded = (mask & (1 << any.collider.gameObject.layer)) != 0;
                Debug(layerIncluded ? "not blocking (walkable slope, or pass-through)" : "ignored: layer not stepped on",
                    any.collider, $"normalY={any.normal.y:0.00}");
            }
        }
    }
}
