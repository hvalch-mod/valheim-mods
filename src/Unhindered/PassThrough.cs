using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Unhindered
{
    // Small vegetation: exclude character layers from its colliders' contacts. Only physics contacts
    // are affected; raycasts and overlap queries (interacting, melee hits, building) still see it.
    // Applied to prefabs on world load, and re-applied to prefabs + spawned objects when config changes
    // (server sync arrives after the world has started loading).
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
    internal static class PassThrough
    {
        private static int s_characterLayers;
        private static bool s_dirty;

        // Prefabs whose colliders already exclude characters in vanilla. Never touched, so disabling can't break them.
        // Checked once per prefab, before we ever modify it (prefabs survive across world loads).
        private static readonly HashSet<string> s_untouchable = new HashSet<string>();
        private static readonly HashSet<string> s_checked = new HashSet<string>();

        [HarmonyPriority(Priority.Last)] // after mods (e.g. PlantEverything) register their prefabs
        private static void Postfix(ZNetScene __instance)
        {
            s_characterLayers = LayerMask.GetMask("character", "character_net", "character_ghost", "character_noenv");
            foreach (GameObject prefab in AllPrefabs(__instance))
            {
                if (s_checked.Add(prefab.name)
                    && prefab.GetComponentsInChildren<Collider>(true).Any(c => (c.excludeLayers & s_characterLayers) != 0))
                {
                    s_untouchable.Add(prefab.name);
                }
            }
            s_dirty = false;
            Refresh(__instance, includeSpawned: false);
        }

        internal static void MarkDirty() => s_dirty = true;

        internal static void RefreshIfDirty()
        {
            if (!s_dirty || ZNetScene.instance == null)
            {
                return;
            }
            s_dirty = false;
            Refresh(ZNetScene.instance, includeSpawned: true);
        }

        private static void Refresh(ZNetScene scene, bool includeSpawned)
        {
            bool enabled = Plugin.PassThroughEnabled.Value;
            string[] names = Split(Plugin.PassThroughNames.Value);
            string[] exclude = Split(Plugin.PassThroughExclude.Value);

            // Prefab name -> should characters pass through it.
            var passable = new Dictionary<string, bool>();
            foreach (GameObject prefab in AllPrefabs(scene))
            {
                if (s_untouchable.Contains(prefab.name) || passable.ContainsKey(prefab.name))
                {
                    continue;
                }
                bool pass = enabled && Matches(prefab, names, exclude);
                passable[prefab.name] = pass;
                foreach (Collider collider in prefab.GetComponentsInChildren<Collider>(true))
                {
                    Set(collider, pass);
                }
            }

            int spawned = includeSpawned ? RefreshSpawned(passable) : 0;

            List<string> matched = passable.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
            Plugin.Log.LogInfo(enabled
                ? $"Pass-through applied to {matched.Count} prefabs" + (includeSpawned ? $" ({spawned} spawned colliders updated)" : "")
                : "Pass-through disabled");
            if (enabled && Plugin.PassThroughLogMatches.Value)
            {
                matched.Sort(StringComparer.OrdinalIgnoreCase);
                Plugin.Log.LogInfo("Pass-through prefabs: " + string.Join(", ", matched));
            }
        }

        // Objects already in the world are copies of the prefabs, so update them too. Only runs on a
        // config change; walks every loaded collider once.
        private static int RefreshSpawned(Dictionary<string, bool> passable)
        {
            int count = 0;
            foreach (Collider collider in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                // The outermost ancestor named like a prefab is the spawned object (vegetation without a
                // ZNetView is parented under its zone's root, which isn't a prefab).
                bool? pass = null;
                for (Transform t = collider.transform; t != null; t = t.parent)
                {
                    if (passable.TryGetValue(Utils.GetPrefabName(t.gameObject), out bool p))
                    {
                        pass = p;
                    }
                }
                if (pass.HasValue && Set(collider, pass.Value))
                {
                    count++;
                }
            }
            return count;
        }

        private static bool Set(Collider collider, bool pass)
        {
            if (collider.isTrigger)
            {
                return false;
            }
            int before = collider.excludeLayers;
            int after = pass ? before | s_characterLayers : before & ~s_characterLayers;
            if (after == before)
            {
                return false;
            }
            collider.excludeLayers = after;
            return true;
        }

        private static IEnumerable<GameObject> AllPrefabs(ZNetScene scene) =>
            scene.m_prefabs.Concat(scene.m_nonNetViewPrefabs).Where(p => p != null);

        private static bool Matches(GameObject prefab, string[] names, string[] exclude)
        {
            string name = prefab.name;
            if (ContainsAny(name, exclude))
            {
                return false;
            }
            return (Plugin.PassThroughPickables.Value && prefab.GetComponent<Pickable>() != null)
                || (Plugin.PassThroughPlants.Value && prefab.GetComponent<Plant>() != null)
                || ContainsAny(name, names);
        }

        private static bool ContainsAny(string name, string[] parts)
        {
            foreach (string part in parts)
            {
                if (name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static string[] Split(string list) =>
            list.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
    }
}
