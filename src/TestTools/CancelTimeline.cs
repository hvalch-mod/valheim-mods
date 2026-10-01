using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TestTools
{
    // On-screen timeline of the local player's last attack and its Feint cancel, drawn like the
    // stamina bar. Shown from the start of an attack; hidden when the attack ends without a cancel;
    // after a cancel it stays until the next attack starts.
    //
    // Feint is read by reflection (its Cancel class is internal), so it's an optional dependency:
    // without it only the swing and its hit are shown.
    internal static class CancelTimeline
    {
        private static bool s_active;
        private static float s_start;
        private static string s_attackName;
        private static float s_hitAt = -1f;
        private static float s_damageScale = -1f;

        private static bool s_cancelled;
        private static bool s_dodge;
        private static float s_cancelAt;
        private static float s_followUpDue;
        private static float s_progress = -1f;
        private static float s_fullPenalty;
        private static float s_followUpAt = -1f;
        private static bool s_swingEnded;
        private static float s_penaltyScale = -1f;

        // Feint.Cancel private statics.
        private static FieldInfo s_fPendingPlayer;
        private static FieldInfo s_fPendingAt;
        private static FieldInfo s_fPendingProgress;
        private static FieldInfo s_fPendingPenalty;
        private static FieldInfo s_fPendingBaseDamage;
        private static float s_peakProgress = 2f / 3f;

        private static readonly Color Background = new Color(0f, 0f, 0f, 0.6f);
        private static readonly Color SwingColor = new Color(0.75f, 0.75f, 0.75f, 0.9f);
        private static readonly Color DelayColor = new Color(1f, 0.85f, 0.2f, 0.9f);
        private static readonly Color DodgeColor = new Color(0.3f, 0.85f, 1f, 1f);
        private static readonly Color BlockColor = new Color(0.4f, 1f, 0.4f, 1f);
        private static readonly Color HitColor = new Color(1f, 0.25f, 0.25f, 1f);
        private static readonly Color EstimatedHitColor = new Color(1f, 0.25f, 0.25f, 0.45f);
        private static readonly Color PeakColor = new Color(1f, 0.55f, 0.1f, 1f);

        private static GUIStyle s_label;

        internal static void PatchFeint(Harmony harmony)
        {
            System.Type cancel = AccessTools.TypeByName("Feint.Cancel");
            if (cancel == null)
            {
                Plugin.Log.LogInfo("Feint not loaded: cancel timeline shows swings only");
                return;
            }
            s_fPendingPlayer = AccessTools.Field(cancel, "s_pendingPlayer");
            s_fPendingAt = AccessTools.Field(cancel, "s_pendingAt");
            s_fPendingProgress = AccessTools.Field(cancel, "s_pendingProgress");
            s_fPendingPenalty = AccessTools.Field(cancel, "s_pendingPenalty");
            s_fPendingBaseDamage = AccessTools.Field(cancel, "s_pendingBaseDamage");
            FieldInfo peak = AccessTools.Field(cancel, "PeakProgress");
            if (peak != null)
            {
                s_peakProgress = (float)peak.GetValue(null);
            }

            Patch(harmony, cancel, "TryCancel", postfix: nameof(TryCancelPostfix));
            Patch(harmony, cancel, "UpdatePending", nameof(UpdatePendingPrefix), nameof(UpdatePendingPostfix));
            Patch(harmony, cancel, "PenaltyScale", postfix: nameof(PenaltyScalePostfix));
        }

        private static void Patch(Harmony harmony, System.Type type, string method, string prefix = null,
            string postfix = null)
        {
            MethodInfo original = AccessTools.Method(type, method);
            if (original == null)
            {
                Plugin.Log.LogWarning($"Feint.Cancel.{method} not found: the cancel timeline may be incomplete");
                return;
            }
            harmony.Patch(original,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(CancelTimeline), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(CancelTimeline), postfix));
        }

        private static void TryCancelPostfix(bool __result, bool dodge)
        {
            if (!__result || !s_active)
            {
                return;
            }
            s_cancelled = true;
            s_dodge = dodge;
            s_cancelAt = Time.time;
            s_followUpDue = (float)s_fPendingAt.GetValue(null);
            s_progress = s_fPendingProgress != null ? (float)s_fPendingProgress.GetValue(null) : -1f;
            s_fullPenalty = (float)s_fPendingPenalty.GetValue(null);
        }

        private static void UpdatePendingPrefix(out bool __state) =>
            __state = s_fPendingPlayer.GetValue(null) != null;

        private static void UpdatePendingPostfix(bool __state)
        {
            if (!__state || s_fPendingPlayer.GetValue(null) != null || !s_cancelled)
            {
                return;
            }
            s_followUpAt = Time.time;
            s_swingEnded = Time.time < s_followUpDue;
            if (s_swingEnded)
            {
                s_penaltyScale = 0f;
            }
            else if (s_penaltyScale < 0f)
            {
                // ScaleStaminaPenalty off: PenaltyScale isn't called.
                s_penaltyScale = 1f;
            }
        }

        private static void PenaltyScalePostfix(float __result) => s_penaltyScale = __result;

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
        private static class StartAttackPatch
        {
            private static void Postfix(Humanoid __instance, bool __result)
            {
                if (!__result || __instance != Player.m_localPlayer)
                {
                    return;
                }
                s_active = true;
                s_start = Time.time;
                s_attackName = __instance.m_currentAttack?.m_attackAnimation ?? "?";
                s_hitAt = -1f;
                s_damageScale = -1f;
                s_cancelled = false;
                s_followUpAt = -1f;
                s_swingEnded = false;
                s_penaltyScale = -1f;
                s_progress = -1f;
            }
        }

        [HarmonyPatch(typeof(Attack), nameof(Attack.OnAttackTrigger))]
        private static class OnAttackTriggerPatch
        {
            // After Feint's prefix has set the damage multiplier for a cancelled swing.
            private static void Postfix(Attack __instance)
            {
                if (!s_active || s_hitAt >= 0f || __instance.m_character != Player.m_localPlayer)
                {
                    return;
                }
                s_hitAt = Time.time;
                if (s_cancelled && s_fPendingBaseDamage != null)
                {
                    float baseDamage = (float)s_fPendingBaseDamage.GetValue(null);
                    s_damageScale = baseDamage > 0f ? __instance.m_damageMultiplier / baseDamage : -1f;
                }
            }
        }

        internal static void Update()
        {
            if (!s_active || s_cancelled)
            {
                return;
            }
            Player player = Player.m_localPlayer;
            if (player == null || (Time.time - s_start > 0.15f && !player.InAttack()))
            {
                s_active = false;
            }
        }

        internal static void Draw()
        {
            if (!s_active || !Plugin.ShowCancelTimeline.Value || Player.m_localPlayer == null)
            {
                return;
            }
            if (s_label == null)
            {
                s_label = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.UpperCenter };
            }

            float now = Time.time - s_start;
            float cancel = s_cancelAt - s_start;
            float followUp = s_followUpAt >= 0f ? s_followUpAt - s_start : -1f;
            float hit = s_hitAt >= 0f ? s_hitAt - s_start : -1f;
            // Without a landed hit, estimate it from the progress toward it at the cancel.
            float estimatedHit = hit < 0f && s_cancelled && s_progress > 0f ? cancel / s_progress : -1f;
            float windup = hit >= 0f ? hit : estimatedHit;
            float peak = windup > 0f ? windup * s_peakProgress : -1f;

            float end = Mathf.Max(followUp >= 0f ? followUp : now, Mathf.Max(hit, estimatedHit));
            float span = Mathf.Max(0.5f, end * 1.15f);

            float width = Screen.width * 0.4f;
            const float height = 14f;
            float x = (Screen.width - width) / 2f;
            float y = Screen.height * 0.78f;
            float Px(float t) => x + width * Mathf.Clamp01(t / span);

            Fill(new Rect(x - 2f, y - 2f, width + 4f, height + 4f), Background);
            float swingEnd = s_cancelled ? cancel : now;
            Fill(new Rect(x, y, Px(swingEnd) - x, height), SwingColor);
            if (s_cancelled)
            {
                float delayEnd = followUp >= 0f ? followUp : now;
                Fill(new Rect(Px(cancel), y, Px(delayEnd) - Px(cancel), height), DelayColor);
            }

            if (peak > 0f)
            {
                Tick(Px(peak), y, height, PeakColor, $"peak {peak:0.00}", below: false);
            }
            if (hit >= 0f)
            {
                Tick(Px(hit), y, height, HitColor, $"hit {hit:0.00}", below: true);
            }
            else if (estimatedHit > 0f)
            {
                Tick(Px(estimatedHit), y, height, EstimatedHitColor, $"hit~ {estimatedHit:0.00}", below: true);
            }
            if (s_cancelled)
            {
                Tick(Px(cancel), y, height, Color.white, $"cancel {cancel:0.00}", below: false);
            }
            if (followUp >= 0f)
            {
                Tick(Px(followUp), y, height, s_dodge ? DodgeColor : BlockColor,
                    $"{(s_dodge ? "dodge" : "block")} {followUp:0.00}", below: true);
            }

            string summary = s_attackName;
            if (s_cancelled)
            {
                summary += $"  |  cancel at {cancel:0.00}s";
                if (s_progress >= 0f)
                {
                    summary += $" ({s_progress:P0} to hit)";
                }
                summary += $", delay {s_followUpDue - s_cancelAt:0.00}s";
                if (followUp >= 0f)
                {
                    summary += s_swingEnded
                        ? "  |  swing ended first: no penalty"
                        : $"  |  penalty {s_fullPenalty * s_penaltyScale:0.#} of {s_fullPenalty:0.#} ({s_penaltyScale:P0})";
                }
                if (s_damageScale >= 0f)
                {
                    summary += $"  |  damage x{s_damageScale:0.00}";
                }
                else if (followUp >= 0f && hit < 0f)
                {
                    summary += "  |  no hit";
                }
            }
            GUI.color = Color.white;
            GUI.Label(new Rect(x - 200f, y - 42f, width + 400f, 20f), summary, s_label);
        }

        private static void Tick(float px, float y, float height, Color color, string label, bool below)
        {
            Fill(new Rect(px - 1.5f, y - 5f, 3f, height + 10f), color);
            GUI.color = color;
            GUI.Label(new Rect(px - 60f, below ? y + height + 5f : y - 22f, 120f, 20f), label, s_label);
            GUI.color = Color.white;
        }

        private static void Fill(Rect rect, Color color)
        {
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
        }
    }
}
