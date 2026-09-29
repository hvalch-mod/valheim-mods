using HarmonyLib;
using UnityEngine;

namespace Footloose
{
    // Valheim has no step-up: the player capsule only rolls over tiny lips, anything taller needs a jump.
    // After vanilla walking has set the body velocity, look for a low obstacle ahead with a walkable top
    // and enough headroom, then lift the player onto it. Local player only; runs every physics tick.
    [HarmonyPatch(typeof(Character), nameof(Character.UpdateWalking))]
    internal static class StepUp
    {
        private const float ProbeHeight = 0.1f;   // forward ray height above the feet
        private const float LookAhead = 0.3f;     // how far past the capsule edge to look
        private const float ProbeInset = 0.2f;    // how far into the obstacle to look for its top
        private const float MinStep = 0.05f;      // lower than this the capsule handles it by itself
        private const float Clearance = 0.03f;    // lift slightly above the top
        private const float MinWalkableY = 0.788f; // cos(38°), vanilla player slide angle
        private const float MaxWallY = 0.7f;      // forward hit must be steeper than this to count as blocking

        private static int s_mask;
        private static int s_maskWithTerrain;
        private static readonly RaycastHit[] s_hits = new RaycastHit[16];
        private static readonly Collider[] s_overlaps = new Collider[16];

        private static void Postfix(Character __instance)
        {
            if (!Plugin.StepUpEnabled.Value || !ReferenceEquals(__instance, Player.m_localPlayer))
            {
                return;
            }

            Player player = (Player)__instance;
            Rigidbody body = player.m_body;
            if (!player.IsOnGround() || player.IsAttached() || player.InDodge() || player.IsStaggering()
                || player.IsRiding() || !player.CanMove() || body.isKinematic)
            {
                return;
            }

            Vector3 dir = player.m_moveDir;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f)
            {
                return;
            }
            dir.Normalize();

            float maxStep = Plugin.StepUpMaxHeight.Value;
            Vector3 velocity = body.linearVelocity;
            if (velocity.y > Plugin.StepUpSpeed.Value) // jumping or launched
            {
                return;
            }

            if (s_mask == 0)
            {
                s_mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "vehicle");
                s_maskWithTerrain = s_mask | LayerMask.GetMask("terrain");
            }
            int mask = Plugin.StepUpOnTerrain.Value ? s_maskWithTerrain : s_mask;

            CapsuleCollider capsule = player.m_collider;
            int selfLayer = 1 << capsule.gameObject.layer;
            float radius = capsule.radius;
            Vector3 feet = body.position;

            // 1. Something steep right in front of the feet?
            Vector3 probeOrigin = feet + Vector3.up * ProbeHeight;
            if (!Raycast(probeOrigin, dir, radius + LookAhead, mask, selfLayer, out RaycastHit wall)
                || wall.normal.y > MaxWallY)
            {
                return;
            }

            // 2. Its top: cast down from just above max step height, a little way into the obstacle.
            // If the origin is inside something taller, the ray ignores it and finds the ground (height ~0): rejected.
            Vector3 topOrigin = wall.point + dir * ProbeInset;
            topOrigin.y = feet.y + maxStep + Clearance;
            if (!Raycast(topOrigin, Vector3.down, maxStep + Clearance, mask, selfLayer, out RaycastHit top)
                || top.normal.y < MinWalkableY)
            {
                return;
            }
            float rise = top.point.y - feet.y;
            if (rise < MinStep || rise > maxStep)
            {
                return;
            }

            // 3. Room for the whole capsule standing on top?
            Vector3 raised = feet + Vector3.up * (rise + Clearance) + dir * radius;
            float height = Mathf.Max(capsule.height, radius * 2f);
            int overlaps = Physics.OverlapCapsuleNonAlloc(raised + Vector3.up * (radius + 0.05f),
                raised + Vector3.up * (height - radius), radius * 0.9f, s_overlaps, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < overlaps; i++)
            {
                if (Blocks(s_overlaps[i], selfLayer))
                {
                    return;
                }
            }

            // 4. Lift. Cap speed at what gravity can stop by the top, so there's no hop once the step ends.
            float g = Physics.gravity.magnitude;
            float lift = Mathf.Min(Plugin.StepUpSpeed.Value, Mathf.Sqrt(2f * g * (rise + Clearance)));
            if (velocity.y < lift)
            {
                velocity.y = lift;
                body.linearVelocity = velocity;
            }
        }

        // Nearest hit that actually collides with the player (skips pass-through plants).
        private static bool Raycast(Vector3 origin, Vector3 dir, float distance, int mask, int selfLayer, out RaycastHit best)
        {
            best = default;
            float bestDistance = float.MaxValue;
            int count = Physics.RaycastNonAlloc(origin, dir, s_hits, distance, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                if (s_hits[i].distance < bestDistance && Blocks(s_hits[i].collider, selfLayer))
                {
                    best = s_hits[i];
                    bestDistance = best.distance;
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
    }
}
