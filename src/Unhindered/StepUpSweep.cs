using UnityEngine;

namespace Unhindered
{
    // The usual kinematic-controller step, using the player's real capsule shape:
    //   1. sweep forward: blocked by something steeper than walkable?
    //   2. sweep up: how high can we rise before a ceiling?
    //   3. probe down along the path, past the blocking point: the obstacle's highest point is where the body's
    //      center will end up (the stand point). Any part taller than MaxHeight rejects the step; steepness
    //      doesn't matter (logs and rocks are all curves and steep facets).
    //   4. sweep forward again, raised, up to the stand point: still blocked means it's a wall, not a step.
    //   5. sweep down at the stand point: how high the capsule must rise to stand there.
    // Then commits to the step (s_step): lifts the body by position, at most Speed * dt per tick, to just above
    // that height, and holds it (no falling) while walking carries the body forward onto the stand point.
    // Stopping earlier leaves the rounded bottom perched on the edge, where vanilla's slide logic (ground
    // steeper than 38°) pushes it back off.
    internal static partial class StepUp
    {
        private struct StepState
        {
            public bool Active;
            public Vector3 Start;   // body position when the step began
            public Vector3 Dir;     // horizontal move direction when the step began
            public float TargetY;   // body y standing just above the step
            public float Travel;    // forward distance until the body center is over the stand point
            public float Timer;
            public Collider Obstacle;
        }

        private const float StepTimeout = 0.6f;     // give up a step that hasn't finished by then
        private static StepState s_step;

        private const float Skin = 0.03f;           // casts use a slightly smaller capsule so touching things isn't "inside"
        private const float SweepBackoff = 0.15f;   // forward sweep starts this far behind: pressed against something,
                                                    // the capsule overlaps it, and casts ignore what they start inside
        private const float SweepLookAhead = 0.15f; // minimum forward distance checked per tick
        private const float ProbeStep = 0.05f;      // spacing of the stand-point probes
        private const float ProbeBeyond = 0.6f;     // how far past the body's front edge to look for a stand point

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
            Vector3 feet = bottom - Vector3.up * radius;
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

            // 3. Stand point: the obstacle's highest point along the path.
            if (!FindStandPoint(feet, dir, wallDistance, radius, raise, maxStep, mask, selfLayer, out float standDistance, out string why))
            {
                Debug(why, wall.collider, "");
                return;
            }

            // 4. Forward again from the raised position, all the way to the stand point.
            Vector3 up = Vector3.up * raise;
            count = Physics.CapsuleCastNonAlloc(bottom + up, top + up, castRadius, dir, s_hits, standDistance, mask, QueryTriggerInteraction.Ignore);
            if (Nearest(count, selfLayer, MinWalkableY, out RaycastHit high))
            {
                Debug("rejected: taller than MaxHeight", wall.collider, $"blocked at raise={raise:0.00} by {high.collider.name}");
                return;
            }

            // 5. Down at the stand point: how far must the capsule rise to stand there?
            Vector3 over = up + dir * standDistance;
            count = Physics.CapsuleCastNonAlloc(bottom + over, top + over, castRadius, Vector3.down, s_hits, raise, mask, QueryTriggerInteraction.Ignore);
            float rise = Nearest(count, selfLayer, 1f, out RaycastHit landing) ? raise - landing.distance : 0f;
            if (rise < MinStep)
            {
                return; // vanilla rolls over it
            }

            // 6. Commit: lift and carry onto the stand point over the next ticks.
            s_step = new StepState
            {
                Active = true,
                Start = body.position,
                Dir = dir,
                TargetY = body.position.y + rise + Skin,
                Travel = standDistance,
                Obstacle = wall.collider,
            };
            Debug("STEP", wall.collider, $"rise={rise:0.00} travel={standDistance:0.00}");
            ContinueStep(player, body, dir, dt);
        }

        private static readonly Collider[] s_probeOverlaps = new Collider[8];

        // Probes straight down every ProbeStep along dir, from just past the body's current position to ProbeBeyond
        // past its front edge, and returns the distance of the obstacle's highest point. Ground-level hits before
        // the obstacle are skipped; ground again after it means we've crossed it. A probe starting inside something
        // means that part is taller than the step allows.
        private static bool FindStandPoint(Vector3 feet, Vector3 dir, float wallDistance, float radius, float raise,
            float maxStep, int mask, int selfLayer, out float distance, out string why)
        {
            distance = 0f;
            float best = MinStep;
            float end = wallDistance + radius + ProbeBeyond;
            for (float d = ProbeStep; d <= end; d += ProbeStep)
            {
                Vector3 origin = feet + dir * d + Vector3.up * (raise + Skin);
                if (StartsInside(origin, mask, selfLayer))
                {
                    why = "rejected: taller than MaxHeight";
                    return false;
                }
                if (!Raycast(origin, Vector3.down, raise + Skin + MinStep, mask, selfLayer, out RaycastHit hit))
                {
                    continue; // nothing solid here (gap); keep looking
                }
                float h = hit.point.y - feet.y;
                if (h < MinStep)
                {
                    if (distance > 0f)
                    {
                        break; // back on the ground: crossed the obstacle
                    }
                    continue; // still in front of the obstacle
                }
                if (h > maxStep)
                {
                    why = "rejected: taller than MaxHeight";
                    return false;
                }
                if (h > best)
                {
                    best = h;
                    distance = d;
                }
            }
            why = distance > 0f ? null : "rejected: no top found";
            return distance > 0f;
        }

        private static bool StartsInside(Vector3 point, int mask, int selfLayer)
        {
            int count = Physics.OverlapSphereNonAlloc(point, 0.01f, s_probeOverlaps, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                if (Blocks(s_probeOverlaps[i], selfLayer))
                {
                    return true;
                }
            }
            return false;
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
    }
}
