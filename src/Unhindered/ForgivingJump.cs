using HarmonyLib;
using UnityEngine;

namespace Unhindered
{
    // Running over bumps lifts the player a little off the ground. Vanilla only allows a jump for 0.2 s after the
    // last ground contact (Character.IsOnGround), so a slightly longer hop eats the jump press.
    // Fix: when the local player presses jump while just above walkable ground, treat them as grounded for that
    // one Jump call. The vanilla 0.2 s grace stays. Being hit while airborne cancels both: no jump until landing.
    // Done by setting m_lastGroundTouch (and the ground normal the jump pushes along) around Character.Jump,
    // restored if no jump happened.
    [HarmonyPatch]
    internal static class ForgivingJump
    {
        private const float AirborneAfter = 0.05f;   // contact refreshes every physics tick; older than this = in the air
        private const float MaxHeight = 0.35f;       // feet at most this far above ground still count as on it
        private const float MinTimeSinceJump = 0.3f; // no second jump while still near the ground after the first
        private const float MinWalkableY = 0.5f;     // ground steeper than 60° doesn't count (no wall jumps)

        private static readonly RaycastHit[] s_hits = new RaycastHit[16];
        private static float s_lastHitTime = float.NegativeInfinity;

        private struct State
        {
            public bool Changed;
            public float LastGroundTouch;
            public Vector3 LastGroundNormal;
        }

        [HarmonyPatch(typeof(Character), nameof(Character.Jump))]
        [HarmonyPrefix]
        private static void JumpPrefix(Character __instance, out State __state)
        {
            __state = default;
            if (!Plugin.ForgivingJumpEnabled.Value || !ReferenceEquals(__instance, Player.m_localPlayer))
            {
                return;
            }

            Player player = (Player)__instance;
            float airTime = player.m_lastGroundTouch;
            if (airTime < AirborneAfter || player.m_body.IsSleeping() || player.InLiquidSwimDepth()
                || player.IsAttached() || GrapplingPoint.m_localGrappler != null)
            {
                return;
            }

            __state.Changed = true;
            __state.LastGroundTouch = airTime;
            __state.LastGroundNormal = player.m_lastGroundNormal;

            if (Time.time - s_lastHitTime <= airTime)
            {
                player.m_lastGroundTouch = 1f; // not on ground: also cancels the vanilla grace
                return;
            }
            if (player.IsOnGround() || player.m_jumpTimer < MinTimeSinceJump || !NearGround(player, out Vector3 normal))
            {
                __state.Changed = false;
                return;
            }
            player.m_lastGroundTouch = 0f;
            player.m_lastGroundNormal = normal;
        }

        // ForceJump zeroes m_jumpTimer; any other value means Jump did nothing, so undo the fake ground contact.
        [HarmonyPatch(typeof(Character), nameof(Character.Jump))]
        [HarmonyPostfix]
        private static void JumpPostfix(Character __instance, State __state)
        {
            if (__state.Changed && __instance.m_jumpTimer > 0f)
            {
                __instance.m_lastGroundTouch = __state.LastGroundTouch;
                __instance.m_lastGroundNormal = __state.LastGroundNormal;
            }
        }

        // ApplyDamage sets m_lastHit only when damage actually landed. Damage over time and falling don't count.
        [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
        [HarmonyPostfix]
        private static void ApplyDamagePostfix(Character __instance, HitData hit)
        {
            if (ReferenceEquals(__instance, Player.m_localPlayer) && ReferenceEquals(__instance.m_lastHit, hit)
                && !IsOngoing(hit.m_hitType))
            {
                s_lastHitTime = Time.time;
            }
        }

        private static bool IsOngoing(HitData.HitType type)
        {
            switch (type)
            {
                case HitData.HitType.Fall:
                case HitData.HitType.Drowning:
                case HitData.HitType.Burning:
                case HitData.HitType.Freezing:
                case HitData.HitType.Poisoned:
                case HitData.HitType.Water:
                case HitData.HitType.Smoke:
                case HitData.HitType.AshlandsOcean:
                case HitData.HitType.AshlandsLava:
                    return true;
                default:
                    return false;
            }
        }

        // Sweep a sphere slightly narrower than the capsule's bottom down from it; walkable ground within MaxHeight
        // of the feet counts. Pass-through plants are skipped.
        private static bool NearGround(Player player, out Vector3 normal)
        {
            normal = Vector3.up;
            CapsuleCollider capsule = player.m_collider;
            float radius = capsule.radius * 0.9f;
            Vector3 origin = capsule.transform.TransformPoint(capsule.center)
                             - Vector3.up * (capsule.height * 0.5f - capsule.radius);
            float distance = MaxHeight + capsule.radius - radius;
            int count = Physics.SphereCastNonAlloc(origin, radius, Vector3.down, s_hits, distance,
                Character.s_groundRayMask, QueryTriggerInteraction.Ignore);

            int selfLayer = 1 << capsule.gameObject.layer;
            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = s_hits[i];
                if (hit.distance > 0f && hit.distance < best && hit.normal.y >= MinWalkableY
                    && StepUp.Blocks(hit.collider, selfLayer))
                {
                    best = hit.distance;
                    normal = hit.normal;
                }
            }
            return best < float.MaxValue;
        }
    }
}
