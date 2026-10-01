using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn;
using Jotunn.Entities;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;

namespace TestTools
{
    // Testing aid, never released. Adds console commands (F5) that work without devcommands:
    //   noon               - jump to 12:00 (next midday). Needs the mod on the server and admin rights there.
    //   give <item> [n] [q] - put n of an item (quality q) in your inventory. Tab completes item names.
    //   items [search]      - list items (prefab and display name) matching search, case insensitive.
    //   weapons             - a random craftable weapon of each type, plus 100 ammo for ranged ones.
    //   food [n]            - n (default 10) of each of the 3 best stamina foods.
    // Feint testing (config section "Feint"):
    //   an on-screen timeline of each attack and its cancel (CancelTimeline), and
    //   a key that attacks, then dodges or blocks after a set offset (AttackMacro).
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency(Main.ModGuid)]
    [BepInDependency("hvalch.Feint", BepInDependency.DependencyFlags.SoftDependency)]
    [NetworkCompatibility(CompatibilityLevel.NotEnforced, VersionStrictness.None)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "hvalch.TestTools";
        public const string Name = "TestTools";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> ShowCancelTimeline;
        internal static ConfigEntry<KeyboardShortcut> MacroKey;
        internal static ConfigEntry<float> MacroCancelOffset;
        internal static ConfigEntry<MacroCancel> MacroCancelWith;

        private readonly Harmony _harmony = new Harmony(Guid);

        private const string NoonRpc = "hvalch.TestTools.Noon";
        private static bool _rpcRegistered;

        private void Awake()
        {
            Log = Logger;
            CommandManager.Instance.AddConsoleCommand(new NoonCommand());
            CommandManager.Instance.AddConsoleCommand(new GiveCommand());
            CommandManager.Instance.AddConsoleCommand(new ItemsCommand());
            CommandManager.Instance.AddConsoleCommand(new WeaponsCommand());
            CommandManager.Instance.AddConsoleCommand(new FoodCommand());

            const string f = "Feint";
            ShowCancelTimeline = Config.Bind(f, "ShowCancelTimeline", true,
                "Show a timeline bar of each attack and its cancel: swing (grey), cancel delay (yellow), " +
                "hit (red; faded = estimated), penalty peak (orange), dodge (blue) / block (green).");
            MacroKey = Config.Bind(f, "MacroKey", new KeyboardShortcut(KeyCode.K),
                "Attacks with the current weapon, then dodges or blocks after MacroCancelOffset.");
            MacroCancelOffset = Config.Bind(f, "MacroCancelOffset", 0.3f,
                new ConfigDescription("Seconds after the attack starts to press dodge/block.",
                    new AcceptableValueRange<float>(0f, 2f)));
            MacroCancelWith = Config.Bind(f, "MacroCancelWith", MacroCancel.Dodge,
                "What the macro presses: Dodge or Block.");

            _harmony.PatchAll();
            CancelTimeline.PatchFeint(_harmony);
            Log.LogInfo($"{Name} {Version} loaded");
        }

        private void OnDestroy() => _harmony.UnpatchSelf();

        private void OnGUI() => CancelTimeline.Draw();

        // ZRoutedRpc is recreated per session, so register lazily on first use / server start.
        private void Update()
        {
            CancelTimeline.Update();
            AttackMacro.Update();

            if (ZRoutedRpc.instance == null)
            {
                _rpcRegistered = false;
                return;
            }
            if (_rpcRegistered) return;
            ZRoutedRpc.instance.Register(NoonRpc, RPC_Noon);
            _rpcRegistered = true;
        }

        // Runs on the server: the time there is authoritative and pushed to clients every 2s.
        private static void RPC_Noon(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            if (sender != ZNet.GetUID())
            {
                ZNetPeer peer = ZNet.instance.GetPeer(sender);
                if (peer == null || !ZNet.instance.IsAdmin(peer.m_socket.GetHostName()))
                {
                    Log.LogWarning($"Rejected noon request from non-admin {peer?.m_playerName ?? sender.ToString()}");
                    return;
                }
            }

            long dayLength = EnvMan.instance != null ? EnvMan.instance.m_dayLengthSec : 1200L;
            double now = ZNet.instance.GetTimeSeconds();
            double noon = System.Math.Floor(now / dayLength) * dayLength + dayLength * 0.5;
            if (noon <= now) noon += dayLength;
            ZNet.instance.SetNetTime(noon);
            Log.LogInfo($"Time set to noon ({now:0} -> {noon:0}s)");
        }

        // Split into stacks; anything that doesn't fit is dropped at the player's feet.
        private static void AddToInventory(Player player, GameObject prefab, int amount, int quality = 1)
        {
            int maxStack = System.Math.Max(1, prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_maxStackSize);
            Inventory inventory = player.GetInventory();
            for (int left = amount; left > 0; left -= maxStack)
            {
                inventory.AddItem(prefab.name, System.Math.Min(left, maxStack), quality, 0,
                    player.GetPlayerID(), player.GetPlayerName(), cheated: true);
            }
        }

        private class NoonCommand : ConsoleCommand
        {
            public override string Name => "noon";
            public override string Help => "Skip forward to 12:00 (needs TestTools on the server and admin)";

            public override void Run(string[] args)
            {
                if (ZRoutedRpc.instance == null || ZNet.instance == null)
                {
                    Console.instance.Print("Not in a world");
                    return;
                }
                ZRoutedRpc.instance.InvokeRoutedRPC(NoonRpc);
                Console.instance.Print("Requested noon");
            }
        }

        private class WeaponsCommand : ConsoleCommand
        {
            private const int AmmoAmount = 100;

            private static readonly HashSet<ItemDrop.ItemData.ItemType> WeaponItemTypes = new HashSet<ItemDrop.ItemData.ItemType>
            {
                ItemDrop.ItemData.ItemType.OneHandedWeapon,
                ItemDrop.ItemData.ItemType.TwoHandedWeapon,
                ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft,
                ItemDrop.ItemData.ItemType.Bow,
            };

            // Tools share weapon item types; skip them.
            private static readonly HashSet<Skills.SkillType> ToolSkills = new HashSet<Skills.SkillType>
            {
                Skills.SkillType.Pickaxes,
                Skills.SkillType.WoodCutting,
            };

            public override string Name => "weapons";
            public override string Help => $"Give a random weapon of each type (sword, axe, bow, staff...) plus {AmmoAmount} ammo for ranged ones";

            public override void Run(string[] args)
            {
                Player player = Player.m_localPlayer;
                if (player == null || ObjectDB.instance == null)
                {
                    Console.instance.Print("No local player");
                    return;
                }

                // Only craftable items, so debug/cheat items and monster attacks are left out.
                List<ItemDrop> craftable = ObjectDB.instance.m_recipes
                    .Where(r => r != null && r.m_enabled && r.m_item != null)
                    .Select(r => r.m_item)
                    .Distinct()
                    .ToList();

                var weaponsByType = craftable
                    .Where(i => WeaponItemTypes.Contains(i.m_itemData.m_shared.m_itemType)
                        && !ToolSkills.Contains(i.m_itemData.m_shared.m_skillType))
                    .GroupBy(i => i.m_itemData.m_shared.m_skillType)
                    .OrderBy(g => g.Key.ToString());

                foreach (var group in weaponsByType)
                {
                    List<ItemDrop> options = group.ToList();
                    ItemDrop weapon = options[Random.Range(0, options.Count)];
                    AddToInventory(player, weapon.gameObject, 1);
                    string line = $"{group.Key}: {weapon.name}";

                    string ammoType = weapon.m_itemData.m_shared.m_ammoType;
                    if (!string.IsNullOrEmpty(ammoType))
                    {
                        List<ItemDrop> ammo = craftable
                            .Where(i => i.m_itemData.m_shared.m_ammoType == ammoType
                                && (i.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo
                                    || i.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.AmmoNonEquipable))
                            .ToList();
                        if (ammo.Count > 0)
                        {
                            ItemDrop pick = ammo[Random.Range(0, ammo.Count)];
                            AddToInventory(player, pick.gameObject, AmmoAmount);
                            line += $" + {AmmoAmount} x {pick.name}";
                        }
                    }
                    Console.instance.Print(line);
                }
            }
        }

        private class FoodCommand : ConsoleCommand
        {
            private const int FoodSlots = 3;

            public override string Name => "food";
            public override string Help => $"[amount] - give the {FoodSlots} best stamina foods (more stamina than health), 10 each by default";

            public override void Run(string[] args)
            {
                Player player = Player.m_localPlayer;
                if (player == null || ObjectDB.instance == null)
                {
                    Console.instance.Print("No local player");
                    return;
                }

                int amount = args.Length > 0 && int.TryParse(args[0], out int a) ? System.Math.Max(1, a) : 10;

                // Cooked food comes from cooking stations, not recipes, so search all items.
                List<ItemDrop> foods = ObjectDB.instance.m_items
                    .Where(i => i != null)
                    .Select(i => i.GetComponent<ItemDrop>())
                    .Where(i => i != null
                        && i.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable
                        && i.m_itemData.m_shared.m_icons.Length > 0
                        && i.m_itemData.m_shared.m_foodStamina > i.m_itemData.m_shared.m_food)
                    .OrderByDescending(i => i.m_itemData.m_shared.m_foodStamina)
                    .Take(FoodSlots)
                    .ToList();

                foreach (ItemDrop food in foods)
                {
                    AddToInventory(player, food.gameObject, amount);
                    var shared = food.m_itemData.m_shared;
                    Console.instance.Print($"{amount} x {food.name}  (stamina {shared.m_foodStamina:0}, health {shared.m_food:0}, eitr {shared.m_foodEitr:0})");
                }
                if (foods.Count == 0) Console.instance.Print("No stamina food found");
            }
        }

        private class ItemsCommand : ConsoleCommand
        {
            private const int MaxShown = 200;

            public override string Name => "items";
            public override string Help => "[search] - list items whose prefab or display name contains search (case insensitive)";

            public override void Run(string[] args)
            {
                if (ObjectDB.instance == null)
                {
                    Console.instance.Print("ObjectDB not loaded");
                    return;
                }

                string search = string.Join(" ", args);
                var matches = ObjectDB.instance.m_items
                    .Where(i => i != null && i.GetComponent<ItemDrop>() != null)
                    .Select(i => (prefab: i.name,
                        display: Localization.instance.Localize(i.GetComponent<ItemDrop>().m_itemData.m_shared.m_name)))
                    .Where(m => search.Length == 0
                        || m.prefab.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0
                        || m.display.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(m => m.prefab, System.StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var m in matches.Take(MaxShown))
                {
                    Console.instance.Print($"{m.prefab}  ({m.display})");
                }
                Console.instance.Print(matches.Count > MaxShown
                    ? $"{matches.Count} matches, showing first {MaxShown}. Narrow the search."
                    : $"{matches.Count} matches");
            }
        }

        private class GiveCommand : ConsoleCommand
        {
            public override string Name => "give";
            public override string Help => "<item> [amount] [quality] - add an item to your inventory";

            public override void Run(string[] args)
            {
                Player player = Player.m_localPlayer;
                if (player == null || ObjectDB.instance == null)
                {
                    Console.instance.Print("No local player");
                    return;
                }
                if (args.Length < 1)
                {
                    Console.instance.Print("Usage: give " + Help);
                    return;
                }

                GameObject prefab = FindItem(args[0]);
                if (prefab == null)
                {
                    Console.instance.Print($"Unknown item '{args[0]}'");
                    return;
                }

                int amount = args.Length > 1 && int.TryParse(args[1], out int a) ? System.Math.Max(1, a) : 1;
                int quality = args.Length > 2 && int.TryParse(args[2], out int q) ? System.Math.Max(1, q) : 1;
                AddToInventory(player, prefab, amount, quality);
                Console.instance.Print($"Gave {amount} x {prefab.name}");
            }

            public override List<string> CommandOptionList() =>
                ObjectDB.instance == null
                    ? new List<string>()
                    : ObjectDB.instance.m_items.Where(i => i != null).Select(i => i.name).ToList();

            // Exact prefab name first, then case-insensitive.
            private static GameObject FindItem(string name)
            {
                GameObject prefab = ObjectDB.instance.GetItemPrefab(name);
                if (prefab == null)
                {
                    prefab = ObjectDB.instance.m_items.FirstOrDefault(i =>
                        i != null && string.Equals(i.name, name, System.StringComparison.OrdinalIgnoreCase));
                }
                return prefab != null && prefab.GetComponent<ItemDrop>() != null ? prefab : null;
            }
        }
    }
}
