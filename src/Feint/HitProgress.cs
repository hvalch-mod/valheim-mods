using System.Collections.Generic;
using UnityEngine;

namespace Feint
{
    // How far the current swing is toward its hit, from 0 (start) to 1 (hit).
    //
    // The hit isn't timed in code: each attack clip has an animation event (CharacterAnimEvent.Hit or
    // OnAttackTrigger) at the frame the weapon connects. Comparing the state's normalized time with that
    // event's position in the clip gives the progress. It's a fraction of the clip, not seconds, so it
    // holds even when Speed animation events change the animator speed mid-swing.
    internal static class HitProgress
    {
        // Clip -> fraction of the clip at which the first hit event fires (-1 = none).
        private static readonly Dictionary<AnimationClip, float> s_hitFraction = new Dictionary<AnimationClip, float>();

        // -1 if unknown (no attack state, or its clip has no hit event).
        internal static float Get(Animator animator) =>
            Find(animator, out float time, out float hit) ? Mathf.Clamp01(time / hit) : -1f;

        // How far the current swing is from its hit to the end of its clip, from 0 (hit) to 1 (end).
        // -1 if unknown.
        internal static float Recovery(Animator animator) =>
            Find(animator, out float time, out float hit)
                ? hit < 1f ? Mathf.Clamp01((time - hit) / (1f - hit)) : 1f
                : -1f;

        // The attack state's normalized time and the hit's position in its clip (0..1).
        private static bool Find(Animator animator, out float time, out float hit)
        {
            time = 0f;
            hit = -1f;
            for (int layer = 0; layer < animator.layerCount; layer++)
            {
                bool transition = animator.IsInTransition(layer);
                AnimatorStateInfo state = transition
                    ? animator.GetNextAnimatorStateInfo(layer)
                    : animator.GetCurrentAnimatorStateInfo(layer);
                if (state.tagHash != Humanoid.s_animatorTagAttack)
                {
                    continue;
                }

                AnimatorClipInfo[] clips = transition
                    ? animator.GetNextAnimatorClipInfo(layer)
                    : animator.GetCurrentAnimatorClipInfo(layer);
                AnimationClip clip = null;
                float weight = -1f;
                foreach (AnimatorClipInfo info in clips)
                {
                    if (info.weight > weight)
                    {
                        clip = info.clip;
                        weight = info.weight;
                    }
                }
                time = state.normalizedTime;
                hit = clip != null ? HitFraction(clip) : -1f;
                return hit > 0f;
            }
            return false;
        }

        private static float HitFraction(AnimationClip clip)
        {
            if (s_hitFraction.TryGetValue(clip, out float fraction))
            {
                return fraction;
            }
            float first = float.MaxValue;
            foreach (AnimationEvent e in clip.events)
            {
                if ((e.functionName == "Hit" || e.functionName == "OnAttackTrigger") && e.time < first)
                {
                    first = e.time;
                }
            }
            fraction = first == float.MaxValue || clip.length <= 0f ? -1f : first / clip.length;
            s_hitFraction[clip] = fraction;
            return fraction;
        }
    }
}
