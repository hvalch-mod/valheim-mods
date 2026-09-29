using UnityEngine;

namespace Unhindered
{
    // The usual kinematic-controller step. Sweeps the player's real capsule shape:
    //   forward (blocked by something steep?) -> up (ceiling?) -> forward again, raised (clear = low enough)
    //   -> down (find the top of the step).
    // Then commits to the step (s_step): lifts the body by position, at most Speed * dt per tick, to just above
    // the top, and holds that height (no falling) while walking carries the body forward until its center is over
    // the obstacle. Without the hold the rounded bottom ends up perched on the edge, where vanilla's slide logic
    // (ground steeper than 38°) pushes it back off.
    internal static partial class StepUp
    {
        private struct StepState
        {
            public bool Active;
            public Vector3 Start;   // body position when the step began
            public Vector3 Dir;     // horizontal move direction when the step began
            public float TargetY;   // body y standing just above the top
            public float Travel;    // forward distance until the body center is over the top
            public float Timer;
            public Collider Obstacle;
        }

        private const float StepTimeout = 0.6f;   // give up a step that hasn't finished by then
        private static StepState s_step;

        private const float Skin = 0.03f;           // casts use a slightly smaller capsule so touching things isn't "inside"
        private const float SweepBackoff = 0.15f;   // forward sweep starts this far behind: pressed against something,
                                                    // the capsule overlaps it, and casts ignore what they start inside
        private const float SweepLookAhead = 0.15f; // minimum forward distance checked per tick
        private const float SweepOnto = 0.15f;      // how far past the blocking point to look for the top
        private const float SurfaceInset = 0.15f;   // how far past an edge contact to sample the real top surface

        private static void Sweep(Player player, Rigidbody body, Vector3 dir, float dt, float maxStep, int mask, int selfLayer)
        {
            CapsuleCollider capsule = player.m_collider;
            Transform t = capsule.transform;
            float radius = capsule.radius * Mathf.Max(Mathf.Abs(t.lossyScale.x), Mathf.Abs(t.lossyScale.z));
            float height = Mathf.Max(capsule.height * Mathf.Abs(t.lossyScale.y), radius * 2f);

            // Capsule sphere centers, from the body position (the transform can be interpolated).
            Vector3 center = body.position + (t.TransformPoint(capsule.center) - t.position);
            Vector3 bottom = center - Vector3.up * (height * 0.5f - radius);
            Vector3 top = center + Vector3.up * (height * 0.5f - radius);
            float castRadius = radius - Skin;

            Vector3 flatVel = player.m_currentVel;
            flatVel.y = 0f;
            float ahead = Mathf.Max(flatVel.magnitude * dt * 2f, SweepLookAhead);

            // 1. Forward: blocked by something steeper than walkable?
            Vector3 back = -dir * SweepBackoff;
            int count = Physics.CapsuleCastNonAlloc(bottom + back, top + back, castRadius, dir, s_hits,
                SweepBackoff + ahead + Skin, mask, QueryTriggerInteraction.Ignore);
            if (!Nearest(count, selfLayer, MinWalkableY, out RaycastHit wall))
            {
                DebugIgnoredLayer(bottom - Vector3.up * (radius - 0.1f), dir, radius + ahead);
                return;
            }
            float wallDistance = Mathf.Max(0f, wall.distance - SweepBackoff);

            // 2. Up: how far can we rise before hitting a ceiling?
            float raise = maxStep + Skin;
            count = Physics.CapsuleCastNonAlloc(bottom, top, castRadius, Vector3.up, s_hits, raise, mask, QueryTriggerInteraction.Ignore);
            if (Nearest(count, selfLayer, 1f, out RaycastHit ceiling))
            {
                raise = ceiling.distance - Skin;
            }
            if (raise < MinStep)
            {
                Debug("rejected: no headroom", wall.collider, $"ceiling={ceiling.collider.name}");
                return;
            }

            // 3. Forward again from the raised position: still blocked means it's a wall, not a step.
            Vector3 up = Vector3.up * raise;
            float onto = wallDistance + SweepOnto;
            count = Physics.CapsuleCastNonAlloc(bottom + up, top + up, castRadius, dir, s_hits, onto, mask, QueryTriggerInteraction.Ignore);
            if (Nearest(count, selfLayer, MinWalkableY, out RaycastHit high))
            {
                Debug("rejected: taller than MaxHeight", wall.collider, $"blocked at raise={raise:0.00} by {high.collider.name}");
                return;
            }

            // 4. Down onto the top of the step.
            Vector3 over = up + dir * onto;
            count = Physics.CapsuleCastNonAlloc(bottom + over, top + over, castRadius, Vector3.down, s_hits, raise, mask, QueryTriggerInteraction.Ignore);
            if (!Nearest(count, selfLayer, 1f, out RaycastHit landing))
            {
                Debug("rejected: nothing to stand on past it", wall.collider, "");
                return;
            }
            float rise = raise - landing.distance; // how far the capsule must go up to stand on the landing
            if (rise < MinStep)
            {
                return; // vanilla rolls over it
            }
            if (!Walkable(landing, dir, mask, selfLayer))
            {
                Debug("rejected: top too steep", wall.collider, $"rise={rise:0.00} normalY={landing.normal.y:0.00}");
                return;
            }

            // 5. Commit: lift and carry over the edge over the next ticks.
            s_step = new StepState
            {
                Active = true,
                Start = body.position,
                Dir = dir,
                TargetY = body.position.y + rise + Skin,
                Travel = wallDistance + radius,
                Obstacle = wall.collider,
            };
            Debug("STEP", wall.collider, $"rise={rise:0.00} travel={s_step.Travel:0.00}");
            ContinueStep(player, body, dir, dt);
        }

        // One tick of a committed step. False once it's finished or abandoned.
        private static bool ContinueStep(Player player, Rigidbody body, Vector3 dir, float dt)
        {
            s_step.Timer += dt;
            float traveled = Vector3.Dot(body.position - s_step.Start, s_step.Dir);
            if (traveled >= s_step.Travel || s_step.Timer > StepTimeout || Vector3.Dot(dir, s_step.Dir) < 0.5f)
            {
                if (traveled < s_step.Travel && s_step.Obstacle != null)
                {
                    Debug("step abandoned", s_step.Obstacle, $"traveled={traveled:0.00}/{s_step.Travel:0.00} time={s_step.Timer:0.00}");
                }
                s_step.Active = false;
                return false;
            }

            // Rise by position (capped per tick so the camera rises instead of popping), then hold the height.
            Vector3 position = body.position;
            if (position.y < s_step.TargetY)
            {
                position.y = Mathf.Min(s_step.TargetY, position.y + Plugin.StepUpSpeed.Value * dt);
                body.position = position;
            }
            Vector3 velocity = body.linearVelocity;
            if (velocity.y < 0f)
            {
                velocity.y = 0f;
                body.linearVelocity = velocity;
            }
            // Count as grounded: vanilla goes airborne 0.2 s after the last ground contact (air control 10%,
            // falling animation), which would stall the carry over the edge.
            player.m_lastGroundTouch = 0f;
            return true;
        }

        // Landing on an edge (a box's rim, the rounded side of a log), a capsule cast's normal points from the
        // contact to the sphere center, so it looks steep even on a flat top. Sample the real surface a little
        // further in: it must be walkable and not lower than the contact (lower = a thin edge with a drop behind).
        private static bool Walkable(RaycastHit landing, Vector3 dir, int mask, int selfLayer)
        {
            if (landing.normal.y >= MinWalkableY)
            {
                return true;
            }
            Vector3 origin = landing.point + dir * SurfaceInset + Vector3.up * 0.3f;
            return Raycast(origin, Vector3.down, 0.6f, mask, selfLayer, out RaycastHit surface)
                && surface.normal.y >= MinWalkableY
                && surface.point.y >= landing.point.y - 0.05f;
        }
    }
}
