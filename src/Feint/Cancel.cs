using System;
using HarmonyLib;
using UnityEngine;

namespace Feint
{
    // Cancels the local player's current attack when they block or dodge before the swing hits.
    //
    // A cancel waits a delay of half the time since the swing started, then stops the attack and starts
    // the dodge or block. If the swing reaches its hit during the delay, the hit still lands, at
    // reduced damage (CancelledHitDamage, by default scaled by how much of the delay had passed:
    // the longer the player had been pulling back, the weaker the hit). If the swing's animation ends first (a cancel so late it
    // would be slower than finishing the swing), the dodge/block starts then and the stamina penalty
    // is waived.
    //
    // With DodgeAfterHit, a dodge after the hit also cancels the rest of the swing (its recovery). It
    // starts right away (or on landing, if in the air), also for NonCancellableAttacks, and
    // its penalty (DodgeAfterHitPenalty) falls from the at-hit amount to nothing over the recovery.
    //
    // Vanilla gates block (Humanoid.IsBlocking) and dodge (Player.UpdateDodge) on !InAttack(), and
    // InAttack() is read from the animator's state tags.
    // - Dodge: the dodge trigger is sent directly. The animator goes from the attack state straight
    //   into the dodge, and the trigger is synced, so other players see it too.
    // - Block: the animator has no transition from attack into block, so every attack-tagged layer is
    //   cross-faded back to its pre-swing state (local only). Vanilla UpdateBlock then starts the block.
    internal static class Cancel
    {
        // The local player's current attack, and whether it has reached its hit moment.
        private static Attack s_tracked;
        private static bool s_triggered;

        // Per animator layer: the non-attack state to return to on cancel (0 = none recorded).
        private static int[] s_restoreState = new int[0];

        private const float CrossFadeSeconds = 0.15f;

        // Delay before the dodge/block, as a fraction of the time since the swing started.
        private const float DelayOfElapsed = 0.5f;

        // When the local player's current attack started.
        private static float s_attackStartTime;

        // A cancelled attack waiting for its delay before the dodge/block starts.
        private static Player s_pendingPlayer;
        private static Attack s_pendingAttack;
        private static float s_pendingSince;
        private static float s_pendingDelay;
        private static float s_pendingBaseDamage;
        private static bool s_pendingDodge;
        private static Vector3 s_pendingDodgeDir;
        private static float s_pendingAt;
        private static float s_pendingPenalty;
        // Swing progress when cancelled (0..1, -1 = unknown): toward its hit, or, for a cancel after
        // the hit, from the hit to the end of the swing.
        private static float s_pendingProgress;
        private static bool s_pendingAfterHit;
        // Whether the cancelled swing's hit landed during the delay, and how far into the delay.
        private static bool s_pendingHit;
        private static float s_pendingPulledBack;

        // Stamina penalty curve over the cancel time (ScaleStaminaPenalty). Its peak is the latest
        // cancel whose delay (DelayOfElapsed x elapsed) still ends before the hit.
        private const float PeakProgress = 1f / (1f + DelayOfElapsed);
        // Penalty for a cancel right at the hit, as a fraction of the full penalty.
        private const float PenaltyAtHit = 0.5f;

        // Vanilla's dodge queue length (Player.Dodge).
        private const float QueuedDodgeSeconds = 0.5f;

        // Set at the hit; the queued-dodge check runs on the next Update.
        private static bool s_hitQueueCheck;

        // Pending check whether the dodge trigger took the animator out of the attack.
        private static Player s_checkPlayer;
        private static int s_checkFrame;
        private static float s_checkTime;
        private const int CheckFrames = 2;
        // Covers an animator that updates on the physics step.
        private const float CheckSeconds = 0.05f;

        // quiet: no stamina flash on refusal (already flashed for this press).
        internal static bool TryCancel(Player player, bool dodge, bool quiet = false)
        {
            if (!(dodge ? Plugin.CancelWithDodge.Value : Plugin.CancelWithBlock.Value))
            {
                return false;
            }
            Attack attack = player.m_currentAttack;
            if (attack == null || attack.IsDone() || !player.InAttack() || s_pendingPlayer != null)
            {
                return false;
            }

            Track(attack);
            bool afterHit = s_triggered;
            if (afterHit && !(dodge && Plugin.DodgeAfterHit.Value))
            {
                Debug("refused: the swing has already hit");
                return false;
            }
            // Excluded attacks still allow a dodge after the hit.
            if (!afterHit && IsExcluded(attack.m_attackAnimation))
            {
                Debug($"refused: {attack.m_attackAnimation} is in NonCancellableAttacks");
                return false;
            }
            // A dodge after the hit may start in the air (e.g. the knife's jump attack): it waits for the landing.
            if (!afterHit && !player.IsOnGround())
            {
                Debug("refused: in the air");
                return false;
            }
            float elapsed = Time.time - s_attackStartTime;
            // After the hit there's nothing left to stop: dodge right away.
            float delay = afterHit ? 0f : DelayOfElapsed * elapsed;

            float penalty = afterHit && !Plugin.DodgeAfterHitPenalty.Value
                ? 0f
                : attack.GetAttackStamina() * Plugin.StaminaPenalty.Value;
            float needed = penalty + (dodge ? player.GetDodgeStaminaUse() : 0f);
            if (needed > 0f && !player.HaveStamina(needed))
            {
                if (!quiet)
                {
                    Hud.instance?.StaminaBarEmptyFlash();
                }
                Debug($"refused: not enough stamina ({needed:0.#} needed)");
                return false;
            }

            // The dodge is started by us after the delay, not by vanilla's queue.
            player.m_queuedDodgeTimer = 0f;
            float progress = afterHit ? HitProgress.Recovery(player.m_animator) : HitProgress.Get(player.m_animator);
            Debug($"cancelled {attack.m_attackAnimation} with {(dodge ? "dodge" : "block")}, " +
                $"{elapsed:0.##}s into the swing " +
                $"({progress:P0} {(afterHit ? "through the recovery" : "to the hit")}), delay {delay:0.##}s");

            s_pendingPlayer = player;
            s_pendingAttack = attack;
            s_pendingSince = Time.time;
            s_pendingDelay = delay;
            s_pendingBaseDamage = attack.m_damageMultiplier;
            s_pendingDodge = dodge;
            s_pendingDodgeDir = player.m_queuedDodgeDir;
            s_pendingPenalty = penalty;
            s_pendingProgress = progress;
            s_pendingAfterHit = afterHit;
            s_pendingHit = false;
            // Started from Update, after SetControls has applied this frame's input.
            s_pendingAt = Time.time + delay;
            return true;
        }

        // Called every frame from Plugin.Update.
        internal static void Update()
        {
            UpdateHitQueue();
            UpdatePending();
            UpdateCheck();
        }

        // A dodge pressed before the hit but refused (e.g. NonCancellableAttacks, or in the air) stays in
        // vanilla's dodge queue (0.5 s). If it's still queued when the swing hits, it becomes a dodge after
        // the hit. Pressed too early, it has expired by then; a later attack press drops it (AttackInputPatch).
        private static void UpdateHitQueue()
        {
            if (!s_hitQueueCheck)
            {
                return;
            }
            s_hitQueueCheck = false;
            Player player = Player.m_localPlayer;
            if (player == null || player.m_queuedDodgeTimer <= 0f || !Plugin.DodgeAfterHit.Value ||
                !ReferenceEquals(player.m_currentAttack, s_tracked))
            {
                return;
            }
            Debug("dodge queued before the hit");
            TryCancel(player, dodge: true, quiet: true);
        }

        private static void UpdatePending()
        {
            if (s_pendingPlayer == null)
            {
                return;
            }
            Player player = s_pendingPlayer;
            // The swing's animation ended on its own before the delay: no faster than not cancelling.
            bool swingEnded = player == Player.m_localPlayer && !player.InAttack();
            if (!swingEnded && (Time.time < s_pendingAt || s_pendingAfterHit && !player.IsOnGround()))
            {
                return;
            }
            Attack attack = s_pendingAttack;
            s_pendingPlayer = null;
            s_pendingAttack = null;
            if (player != Player.m_localPlayer || player.IsDead())
            {
                return;
            }

            attack.Stop();
            if (player.m_currentAttack == attack)
            {
                player.m_currentAttack = null;
            }
            // The next attack continues the combo only if this swing hit (KeepCombo); otherwise it starts over.
            player.m_previousAttack = KeepsCombo(s_pendingAfterHit || s_pendingHit) ? attack : null;
            ResetAttackTriggers(player.m_animator, attack);
            // Drop clicks made while waiting, so the block/dodge isn't followed by a swing.
            player.m_queuedAttackTimer = 0f;
            player.m_queuedSecondAttackTimer = 0f;

            if (swingEnded)
            {
                Debug("swing ended before the cancel delay: no penalty");
            }
            else
            {
                float penalty = s_pendingPenalty * (Plugin.ScaleStaminaPenalty.Value ? PenaltyScale() : 1f);
                if (penalty > 0f)
                {
                    player.UseStamina(penalty);
                }
                Debug($"cancel delay over: penalty {penalty:0.#} of {s_pendingPenalty:0.#}");
            }

            if (s_pendingDodge && CanDodge(player))
            {
                StartDodge(player, s_pendingDodgeDir);
                if (!swingEnded)
                {
                    s_checkPlayer = player;
                    s_checkFrame = Time.frameCount + CheckFrames;
                    s_checkTime = Time.time + CheckSeconds;
                }
            }
            else if (s_pendingDodge && swingEnded && !player.IsOnGround())
            {
                // Still in the air when the swing ended: hand the dodge back to vanilla's queue, which
                // starts it on landing.
                player.m_queuedDodgeTimer = QueuedDodgeSeconds;
                player.m_queuedDodgeDir = s_pendingDodgeDir;
            }
            else if (!swingEnded)
            {
                // Vanilla UpdateBlock starts the block once the animator has left the attack.
                CrossFadeOutOfAttack(player.m_animator);
            }
        }

        // 0 for a cancel at the start of the swing, rising quickly to 1 at the peak (the latest cancel
        // whose delay ends before the hit), then falling to PenaltyAtHit for a cancel right at the hit,
        // and on to 0 for a dodge after the hit at the end of the swing.
        private static float PenaltyScale()
        {
            if (s_pendingAfterHit)
            {
                return s_pendingProgress < 0f ? PenaltyAtHit : Mathf.Lerp(PenaltyAtHit, 0f, s_pendingProgress);
            }
            if (s_pendingHit)
            {
                return Mathf.Lerp(PenaltyAtHit, 1f, s_pendingPulledBack);
            }
            if (s_pendingProgress < 0f)
            {
                return 1f;
            }
            float x = Mathf.Clamp01(s_pendingProgress / PeakProgress);
            // Ease-out: steep at first, flattening toward the peak.
            return 1f - (1f - x) * (1f - x);
        }

        private static bool CanDodge(Player player)
        {
            if (player.IsStaggering() || !player.IsOnGround())
            {
                return false;
            }
            if (!player.HaveStamina(player.GetDodgeStaminaUse()))
            {
                Hud.instance?.StaminaBarEmptyFlash();
                return false;
            }
            return true;
        }

        // Same as the dodge in Player.UpdateDodge, which won't run while InAttack().
        private static void StartDodge(Player player, Vector3 dir)
        {
            player.ClearActionQueue();
            player.m_queuedDodgeTimer = 0f;
            player.m_dodgeInvincible = true;
            player.transform.rotation = Quaternion.LookRotation(dir);
            player.m_body.rotation = player.transform.rotation;
            player.m_zanim.SetTrigger("dodge");
            player.AddNoise(5f);
            player.UseStamina(player.GetDodgeStaminaUse());
            player.m_dodgeEffects.Create(player.transform.position, Quaternion.identity, player.transform, 1f, -1,
                player.GetZDOID());
        }

        private static void UpdateCheck()
        {
            if (s_checkPlayer == null || Time.frameCount < s_checkFrame || Time.time < s_checkTime)
            {
                return;
            }
            Player player = s_checkPlayer;
            s_checkPlayer = null;
            if (player != Player.m_localPlayer)
            {
                return;
            }

            bool stuck = false;
            Animator animator = player.m_animator;
            for (int layer = 0; layer < animator.layerCount; layer++)
            {
                if (LayerTag(animator, layer) == Humanoid.s_animatorTagAttack)
                {
                    stuck = true;
                    break;
                }
            }
            Debug("dodge cancel: " +
                (stuck ? "still in attack animation, falling back to cross-fade" : "trigger worked"));

            if (stuck)
            {
                CrossFadeOutOfAttack(animator);
            }
        }

        private static bool IsExcluded(string animation)
        {
            foreach (string name in Plugin.NonCancellableAttacks.Value.Split(','))
            {
                if (name.Trim().Equals(animation, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HitDone(Attack attack) => s_triggered && ReferenceEquals(s_tracked, attack);

        private static void Track(Attack attack)
        {
            if (!ReferenceEquals(s_tracked, attack))
            {
                s_tracked = attack;
                s_triggered = false;
            }
        }

        // KeepCombo: a swing that reached its hit keeps the combo going for ComboKeepSeconds after it
        // ends, also when a dodge or block cancels it.
        private static bool KeepsCombo(bool hitDone) => hitDone && Plugin.KeepCombo.Value;

        // Vanilla turns the player toward their aim only on a combo's first swing (Attack.Start). A swing
        // continued by KeepCombo turns the same way, but at most KeptComboMaxTurn: it can correct the aim
        // after a pause or dodge, but not swing straight behind without starting over.
        private const float KeptComboMaxTurn = 90f;

        private static void TurnKeptCombo(Player player)
        {
            Attack attack = player.m_currentAttack;
            if (attack == null || attack.m_attackType == Attack.AttackType.None || attack.m_currentAttackCainLevel == 0)
            {
                return;
            }
            Quaternion target;
            if (!player.AttackTowardsPlayerLookDir || attack.m_attackType == Attack.AttackType.Projectile)
            {
                target = player.GetLookYaw();
            }
            else
            {
                Vector3 moveDir = player.GetMoveDir();
                if (moveDir.sqrMagnitude <= 0f)
                {
                    return;
                }
                target = Quaternion.LookRotation(moveDir);
            }
            player.transform.rotation = Quaternion.RotateTowards(player.transform.rotation, target, KeptComboMaxTurn);
            player.m_body.rotation = player.transform.rotation;
        }

        // The last swing KeepCombo continued (only it is held back while its animation starts).
        private static Attack s_keptSwing;

        // How long a kept swing waits for its animation before a new swing may replace it.
        private const float SwingStartGraceSeconds = 0.5f;

        // Drop combo triggers set for this weapon, so a queued next swing doesn't start by itself.
        private static void ResetAttackTriggers(Animator animator, Attack attack)
        {
            animator.ResetTrigger(attack.m_attackAnimation);
            for (int i = 0; i < attack.m_attackChainLevels; i++)
            {
                animator.ResetTrigger(attack.m_attackAnimation + i);
            }
        }

        private static void CrossFadeOutOfAttack(Animator animator)
        {
            int count = Mathf.Min(animator.layerCount, s_restoreState.Length);
            for (int layer = 0; layer < count; layer++)
            {
                if (LayerTag(animator, layer) != Humanoid.s_animatorTagAttack)
                {
                    continue;
                }
                if (s_restoreState[layer] != 0)
                {
                    animator.CrossFadeInFixedTime(s_restoreState[layer], CrossFadeSeconds, layer);
                }
                else
                {
                    Debug($"no state recorded to return to on animator layer {layer}");
                }
            }
        }

        private static int LayerTag(Animator animator, int layer) =>
            animator.IsInTransition(layer)
                ? animator.GetNextAnimatorStateInfo(layer).tagHash
                : animator.GetCurrentAnimatorStateInfo(layer).tagHash;

        // Before a swing starts, remember each layer's current (non-attack) state; once it has started,
        // remember when. Also extends the combo window (KeepCombo).
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
        private static class StartAttackPatch
        {
            private static void Postfix(Humanoid __instance, bool __result, float __state)
            {
                if (__instance != Player.m_localPlayer)
                {
                    return;
                }
                if (__result)
                {
                    s_attackStartTime = Time.time;
                    Attack a = __instance.m_currentAttack;
                    if (Plugin.DebugLog.Value && a != null)
                    {
                        Attack p = __instance.m_previousAttack;
                        Debug($"swing {a.m_attackAnimation} level {a.m_currentAttackCainLevel}/{a.m_attackChainLevels}" +
                            (p == null ? ", no previous" : $", previous {p.m_attackAnimation} level {p.m_currentAttackCainLevel} next {p.m_nextAttackChainLevel}") +
                            $", {(__state >= 0f ? __state : __instance.m_timeSinceLastAttack):0.##}s since the last swing");
                    }
                    if (__state > 0.2f)
                    {
                        Debug($"combo kept {__state:0.##}s after the last swing");
                        s_keptSwing = __instance.m_currentAttack;
                        TurnKeptCombo((Player)__instance);
                    }
                }
                else if (__state >= 0f)
                {
                    // No swing started: a refused press mustn't extend the window.
                    __instance.m_timeSinceLastAttack = __state;
                }
            }

            // First: records the pre-swing state even when another mod's prefix replaces StartAttack
            // (e.g. EpicLoot's Throwable), and skips vanilla's StartAttack while a cancel is pending.
            // Returning false doesn't stop other mods' prefixes, so one of them may still start a swing.
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Humanoid __instance, out float __state)
            {
                __state = -1f;
                if (__instance != Player.m_localPlayer)
                {
                    return true;
                }
                // No new swing (or combo step) while a cancel is waiting to start its dodge/block.
                if (s_pendingPlayer != null)
                {
                    return false;
                }
                // KeepCombo: holding attack calls StartAttack every frame, and vanilla replaces a swing whose
                // animation hasn't started yet (after a kept combo's pause, the animator can take a moment to
                // leave the last one). The replacement restarts the combo, since the next level is set only
                // once the animation starts, while the animator still plays the replaced swing's trigger: the
                // same combo step plays twice.
                Attack current = __instance.m_currentAttack;
                if (Plugin.KeepCombo.Value && current != null && ReferenceEquals(current, s_keptSwing) &&
                    !current.m_attackDone && !current.m_wasInAttack && current.m_time < SwingStartGraceSeconds)
                {
                    return false;
                }
                Animator animator = __instance.m_animator;
                if (s_restoreState.Length != animator.layerCount)
                {
                    s_restoreState = new int[animator.layerCount];
                }
                for (int layer = 0; layer < animator.layerCount; layer++)
                {
                    if (animator.IsInTransition(layer))
                    {
                        continue;
                    }
                    AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layer);
                    if (state.tagHash != Humanoid.s_animatorTagAttack)
                    {
                        s_restoreState[layer] = state.fullPathHash;
                    }
                }
                // Vanilla resets the combo when more than 0.2 s have passed since the last attack ended.
                Attack previous = __instance.m_currentAttack ?? __instance.m_previousAttack;
                // Not across a weapon swap (vanilla keeps it within 0.2 s for the same kind of weapon).
                if (previous != null && KeepsCombo(HitDone(previous)) && previous.GetWeapon() == __instance.GetCurrentWeapon() &&
                    __instance.m_timeSinceLastAttack <= Plugin.ComboKeepSeconds.Value)
                {
                    __state = __instance.m_timeSinceLastAttack;
                    __instance.m_timeSinceLastAttack = 0f;
                }
                return true;
            }
        }

        // The hit moment of a swing (animation event). From here on the swing can't be cancelled
        // (a cancelled swing that reaches it during its delay lands at reduced damage).
        [HarmonyPatch(typeof(Attack), nameof(Attack.OnAttackTrigger))]
        private static class OnAttackTriggerPatch
        {
            private static void Prefix(Attack __instance)
            {
                if (__instance.m_character == null || __instance.m_character != Player.m_localPlayer)
                {
                    return;
                }
                // Vanilla returns early without hitting when staggered or out of ammo.
                if (__instance.m_character.IsStaggering() || !Attack.HaveAmmo(__instance.m_character, __instance.m_weapon))
                {
                    return;
                }
                Track(__instance);
                s_triggered = true;
                s_hitQueueCheck = true;
                if (ReferenceEquals(__instance, s_pendingAttack) && !s_pendingAfterHit)
                {
                    s_pendingHit = true;
                    s_pendingPulledBack = s_pendingDelay > 0f
                        ? Mathf.Clamp01((Time.time - s_pendingSince) / s_pendingDelay)
                        : 1f;
                    float min = Plugin.CancelledHitDamage.Value;
                    float scale = Plugin.ScaleCancelledHitDamage.Value ? Mathf.Lerp(1f, min, s_pendingPulledBack) : min;
                    // The Attack is a per-swing clone of the weapon's, so this only affects this swing.
                    __instance.m_damageMultiplier = s_pendingBaseDamage * scale;
                    Debug($"cancelled swing hit {s_pendingPulledBack:P0} into the cancel delay: damage x{scale:0.##}");
                }
            }
        }

        // With DodgeAfterHit, an attack press during a swing, before its hit, replaces a dodge queued earlier
        // in it: the latest press wins. Otherwise vanilla keeps the queued dodge.
        [HarmonyPatch(typeof(Player), nameof(Player.PlayerAttackInput))]
        private static class AttackInputPatch
        {
            private static void Prefix(Player __instance)
            {
                if (__instance == Player.m_localPlayer && Plugin.DodgeAfterHit.Value && Plugin.CancelWithDodge.Value &&
                    (__instance.m_attack || __instance.m_secondaryAttack) && __instance.m_queuedDodgeTimer > 0f &&
                    __instance.InAttack() && !HitDone(__instance.m_currentAttack))
                {
                    __instance.m_queuedDodgeTimer = 0f;
                    Debug("queued dodge replaced by an attack press");
                }
            }
        }

        // Dodge input during an attack. Vanilla queues the dodge for 0.5 s and fires it once the
        // attack animation ends; a cancel starts it sooner. If refused, vanilla's queue stays as is.
        [HarmonyPatch(typeof(Player), nameof(Player.Dodge))]
        private static class DodgePatch
        {
            private static void Postfix(Player __instance)
            {
                if (__instance == Player.m_localPlayer && __instance.m_queuedDodgeTimer > 0f)
                {
                    TryCancel(__instance, dodge: true);
                }
            }
        }

        private static void Debug(string message)
        {
            if (Plugin.DebugLog.Value)
            {
                Plugin.Log.LogInfo(message);
            }
        }
    }
}
