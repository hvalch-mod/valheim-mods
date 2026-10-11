using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;

namespace Unhindered
{
    // Walking into another character (enemy, player) sticks instead of sliding around it like a tree.
    // Cause is friction: an idle character's body material is 0.8 with Maximum combine (Character.UpdateBodyFriction),
    // which beats the player's 0.4 Multiply, and walking presses into them every tick, so friction eats the sideways
    // part. Trees combine to about 0.24 (moving player's 0.4 Multiply x Unity's default material 0.6).
    // Fix: cap the friction of contacts between the local player's capsule and an untamed character's capsule at
    // Friction, a bit below a tree, via contact modification (lower friction, e.g. airborne, is left alone).
    // Only that friction changes: blocking, pushing, ships and knockback stay vanilla. Tames keep their grip.
    [HarmonyPatch]
    internal static class CharacterSlide
    {
        private const float Friction = 0.2f; // picked in testing

        // Capsule instance IDs of every live untamed character (tames keep their grip). Written on the main thread
        // (Awake, CustomFixedUpdate, OnDestroy), read in the contact callback, which runs inside the physics step
        // while the main thread waits on it.
        private static readonly HashSet<int> s_characters = new HashSet<int>();
        private static Collider s_playerCollider; // local player's capsule (main thread only)
        private static int s_player;              // its instance ID for the callback, 0 when none
        private static bool s_enabled;            // cached from config so the callback doesn't touch it

        internal static void Init()
        {
            s_enabled = Plugin.CharacterSlideEnabled.Value;
            Physics.ContactModifyEvent += OnContactModify;
        }

        internal static void Shutdown()
        {
            Physics.ContactModifyEvent -= OnContactModify;
            SetEnabled(false);
        }

        // Opting in routes every player contact (terrain, pieces...) through the callback, so only while enabled.
        internal static void SetEnabled(bool enabled)
        {
            s_enabled = enabled;
            if (s_playerCollider != null)
            {
                s_playerCollider.hasModifiableContacts = enabled;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Character), nameof(Character.Awake))]
        private static void Awake(Character __instance) => Track(__instance);

        // Taming can happen at any time; runs before each physics step.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Character), nameof(Character.CustomFixedUpdate))]
        private static void CustomFixedUpdate(Character __instance) => Track(__instance);

        private static void Track(Character character)
        {
            if (character.m_collider == null)
            {
                return;
            }
            int id = character.m_collider.GetInstanceID();
            if (character.IsTamed())
            {
                s_characters.Remove(id);
            }
            else
            {
                s_characters.Add(id);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Character), nameof(Character.OnDestroy))]
        private static void OnDestroy(Character __instance)
        {
            if (__instance.m_collider != null)
            {
                int id = __instance.m_collider.GetInstanceID();
                s_characters.Remove(id);
                if (id == s_player)
                {
                    s_player = 0;
                    s_playerCollider = null;
                }
            }
        }

        // Contacts are only reported for modification when one of the colliders opts in; just the local player does.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), nameof(Player.SetLocalPlayer))]
        private static void SetLocalPlayer(Player __instance)
        {
            if (__instance.m_collider != null)
            {
                s_playerCollider = __instance.m_collider;
                s_player = s_playerCollider.GetInstanceID();
                s_playerCollider.hasModifiableContacts = s_enabled;
            }
        }

        // No Unity API in here: only instance IDs and the contact pair.
        private static void OnContactModify(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs)
        {
            int player = s_player;
            if (!s_enabled || player == 0)
            {
                return;
            }
            for (int p = 0; p < pairs.Length; p++)
            {
                ModifiableContactPair pair = pairs[p];
                int self = pair.colliderInstanceID;
                int other = pair.otherColliderInstanceID;
                if (!(self == player && s_characters.Contains(other)) && !(other == player && s_characters.Contains(self)))
                {
                    continue;
                }
                for (int i = 0; i < pair.contactCount; i++)
                {
                    pair.SetStaticFriction(i, Math.Min(pair.GetStaticFriction(i), Friction));
                    pair.SetDynamicFriction(i, Math.Min(pair.GetDynamicFriction(i), Friction));
                }
            }
        }
    }
}
