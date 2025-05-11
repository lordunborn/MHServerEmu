using System.Diagnostics;
using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Loot;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Commands.Implementations
{
    [CommandGroup("item")]
    [CommandGroupDescription("Commands for managing items.")]
    public class ItemCommands : CommandGroup
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        [Command("drop")]
        [CommandDescription("Creates and drops the specified item from the current avatar.")]
        [CommandUsage("item drop [pattern] [count]")]
        [CommandUserLevel(AccountUserLevel.Admin)]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)]
        public string Drop(string[] @params, NetClient client)
        {
            PrototypeId itemProtoRef = CommandHelper.FindPrototype(HardcodedBlueprints.Item, @params[0], client);
            if (itemProtoRef == PrototypeId.Invalid) return string.Empty;

            if (@params.Length == 1 || int.TryParse(@params[1], out int count) == false)
                count = 1;

            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;
            Avatar avatar = player.CurrentAvatar;

            LootManager lootManager = playerConnection.Game.LootManager;
            
            for (int i = 0; i < count; i++)
            {
                lootManager.SpawnItem(itemProtoRef, LootContext.Drop, player, avatar);
                Logger.Debug($"DropItem(): {itemProtoRef.GetName()} from {avatar}");
            }

            return string.Empty;
        }

        [Command("give")]
        [CommandDescription("Creates and gives the specified item to the current player.")]
        [CommandUsage("item give [pattern] [count]")]
        [CommandUserLevel(AccountUserLevel.Admin)]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)]
        public string Give(string[] @params, NetClient client)
        {
            PrototypeId itemProtoRef = CommandHelper.FindPrototype(HardcodedBlueprints.Item, @params[0], client);
            if (itemProtoRef == PrototypeId.Invalid) return string.Empty;

            if (@params.Length == 1 || int.TryParse(@params[1], out int count) == false)
                count = 1;

            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;

            LootManager lootGenerator = playerConnection.Game.LootManager;

            for (int i = 0; i < count; i++)
                lootGenerator.GiveItem(itemProtoRef, LootContext.Drop, player);
            Logger.Debug($"GiveItem(): {itemProtoRef.GetName()}[{count}] to {player}");

            return string.Empty;
        }

        [Command("destroyindestructible")]
        [CommandDescription("Destroys indestructible items contained in the player's general inventory.")]
        [CommandUsage("item destroyindestructible")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string DestroyIndestructible(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;
            Inventory general = player.GetInventory(InventoryConvenienceLabel.General);

            List<Item> indestructibleItemList = new();
            foreach (var entry in general)
            {
                Item item = player.Game.EntityManager.GetEntity<Item>(entry.Id);
                if (item == null) continue;

                if (item.ItemPrototype.CanBeDestroyed == false)
                    indestructibleItemList.Add(item);
            }

            foreach (Item item in indestructibleItemList)
                item.Destroy();

            return $"Destroyed {indestructibleItemList.Count} indestructible items.";
        }

        [Command("roll")]
        [CommandDescription("Rolls the specified loot table.")]
        [CommandUsage("item roll [pattern]")]
        [CommandUserLevel(AccountUserLevel.Admin)]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)]
        public string RollLootTable(string[] @params, NetClient client)
        {
            PrototypeId lootTableProtoRef = CommandHelper.FindPrototype(HardcodedBlueprints.LootTable, @params[0], client);
            if (lootTableProtoRef == PrototypeId.Invalid) return string.Empty;

            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;

            player.Game.LootManager.TestLootTable(lootTableProtoRef, player);

            return $"Finished rolling {lootTableProtoRef.GetName()}, see the server console for results.";
        }

        [Command("rollall")]
        [CommandDescription("Rolls all loot tables.")]
        [CommandUsage("item rollall")]
        [CommandUserLevel(AccountUserLevel.Admin)]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string RollAllLootTables(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;

            int numLootTables = 0;
            Stopwatch stopwatch = Stopwatch.StartNew();

            foreach (PrototypeId lootTableProtoRef in DataDirectory.Instance.IteratePrototypesInHierarchy<LootTablePrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                player.Game.LootManager.TestLootTable(lootTableProtoRef, player);
                numLootTables++;
            }

            stopwatch.Stop();

            return $"Finished rolling {numLootTables} loot tables in {stopwatch.Elapsed.TotalMilliseconds} ms, see the server console for results.";
        }

        [Command("creditchest")]
        [CommandDescription("Converts 500k credits to a sellable chest item.")]
        [CommandUsage("item creditchest")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string CreditChest(string[] @params, NetClient client)
        {
            const PrototypeId CreditItemProtoRef = (PrototypeId)13983056721138685632;
            const int CreditItemPrice = 500000;

            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;

            PropertyId creditsProperty = new(PropertyEnum.Currency, GameDatabase.CurrencyGlobalsPrototype.Credits);

            if (player.Properties[creditsProperty] < CreditItemPrice)
                return "You need at least 500 000 credits to use this command.";

            // Entity/Items/Crafting/Ingredients/CreditItem500k.prototype
            player.Properties.AdjustProperty(-CreditItemPrice, creditsProperty);
            player.Game.LootManager.GiveItem(CreditItemProtoRef, LootContext.CashShop, player);

            Logger.Trace($"CreditChest(): {player}");

            return $"Converted 500 000 credits to a Credit Chest.";
        }
        [Command("autosort")]
        [CommandDescription("Automatically sorts items. Usage: '!item autosort' (to stashes) or '!item autosort inventory' (within general inventory).")]
        [CommandUsage("item autosort [inventory]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string AutoSort(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            if (playerConnection == null) return "Failed to get player connection.";

            Player player = playerConnection.Player;
            if (player == null) return "Failed to get player entity.";

            if (@params.Length > 0 && @params[0].Equals("inventory", StringComparison.OrdinalIgnoreCase))
            {
                return SortInventoryInternal(player);
            }
            else
            {
                return SortInventoryToStash(player);
            }
        }

        private string SortInventoryInternal(Player player)
        {
            Inventory generalInventory = player.GetInventory(InventoryConvenienceLabel.General);
            if (generalInventory == null) return "Failed to find general inventory.";

            // --- Step 1: Consolidate Stacks (Basic Implementation) ---
            int stacksConsolidatedCount = 0;
            List<Item> itemsToCheck = new List<Item>();
            foreach (var entry in generalInventory)
            {
                Item item = player.Game.EntityManager.GetEntity<Item>(entry.Id);
                if (item != null && item.CanStack() && item.CurrentStackSize < item.Properties[PropertyEnum.InventoryStackSizeMax])
                {
                    itemsToCheck.Add(item);
                }
            }

            HashSet<ulong> processedItemIds = new HashSet<ulong>();
            foreach (Item item in itemsToCheck)
            {
                if (item == null || !item.InventoryLocation.IsValid || processedItemIds.Contains(item.Id)) continue;

                foreach (var entry in generalInventory)
                {
                    Item targetItem = player.Game.EntityManager.GetEntity<Item>(entry.Id);
                    if (targetItem != null && targetItem.Id != item.Id && item.CanStackOnto(targetItem))
                    {
                        ulong? stackEntityId = targetItem.Id;
                        InventoryResult stackResult = Inventory.ChangeEntityInventoryLocation(item, generalInventory, entry.Slot, ref stackEntityId, true);

                        if (stackResult == InventoryResult.Success)
                        {
                            stacksConsolidatedCount++;
                            processedItemIds.Add(item.Id);
                            Logger.Debug($"AutoSort Internal: Stacked {GameDatabase.GetPrototypeName(item.PrototypeDataRef)} onto item ID {targetItem.Id}.");
                            break;
                        }
                    }
                }
            }
            Logger.Debug($"AutoSort Internal: Finished stack consolidation attempt for {player.GetName()}. Consolidated: {stacksConsolidatedCount}");

            // --- Step 2: Compact Inventory (Fill Gaps & Sort by Name) ---
            Logger.Debug($"AutoSort Internal: Starting compaction for {player.GetName()}...");
            List<ulong> currentItemIds = new List<ulong>();
            foreach (var entry in generalInventory)
            {
                currentItemIds.Add(entry.Id);
            }

            List<Item> itemsToReAdd = new List<Item>();
            EntityManager entityManager = player.Game.EntityManager;

            foreach (ulong itemId in currentItemIds)
            {
                Item item = entityManager.GetEntity<Item>(itemId);
                if (item != null)
                {
                    if (item.ChangeInventoryLocation(null) == InventoryResult.Success)
                    {
                        itemsToReAdd.Add(item);
                    }
                    else
                    {
                        Logger.Warn($"AutoSort Internal Compaction: Failed to remove item {GameDatabase.GetPrototypeName(item.PrototypeDataRef)} (ID: {itemId}) for compaction.");
                    }
                }
            }

            // Sort the list of removed items by Prototype Name
            if (itemsToReAdd.Any())
            {
                itemsToReAdd = itemsToReAdd
                    .OrderBy(item => GameDatabase.GetPrototypeName(item.PrototypeDataRef))
                    // .ThenByDescending(item => item.Properties[PropertyEnum.ItemLevel]) // Optional secondary sort
                    .ToList();
                Logger.Debug($"AutoSort Internal: Sorted {itemsToReAdd.Count} items for re-adding.");
            }


            // Re-add items sequentially from slot 0 in the new sorted order
            int itemsCompacted = 0;
            uint nextSlot = 0;
            foreach (Item item in itemsToReAdd)
            {
                uint targetSlot = FindNextAvailableSlot(generalInventory, nextSlot);

                if (targetSlot == Inventory.InvalidSlot)
                {
                    Logger.Error($"AutoSort Internal Compaction: No space left in general inventory to re-add {GameDatabase.GetPrototypeName(item.PrototypeDataRef)}. Item potentially lost!");
                    // Consider sending to error recovery or notifying player more explicitly
                    continue;
                }

                ulong? stackEntityId = null;
                InventoryResult addResult = Inventory.ChangeEntityInventoryLocation(item, generalInventory, targetSlot, ref stackEntityId, false); // allowStacking = false

                if (addResult == InventoryResult.Success)
                {
                    itemsCompacted++;
                    nextSlot = targetSlot + 1;
                }
                else
                {
                    Logger.Warn($"AutoSort Internal Compaction: Failed to re-add item {GameDatabase.GetPrototypeName(item.PrototypeDataRef)} to slot {targetSlot}. Reason: {addResult}");
                }
            }
            Logger.Debug($"AutoSort Internal: Finished compaction for {player.GetName()}. Items re-added: {itemsCompacted}");

            return $"Inventory sorted internally. Consolidated {stacksConsolidatedCount} stacks, re-positioned {itemsCompacted} items by type.";
        }

        private uint FindNextAvailableSlot(Inventory inventory, uint startSlot)
        {
            int capacity = inventory.GetCapacity();
            uint maxSlot = (capacity == int.MaxValue) ? 1000 : (uint)capacity; // Using a reasonable upper search limit

            for (uint slot = startSlot; slot < maxSlot; slot++)
            {
                // Check if the slot is physically free in the inventory's internal map
                if (inventory.GetEntityInSlot(slot) == Entity.InvalidId)
                {
                    // Also verify with the owner if this slot is generally usable
                    if (inventory.Owner == null || inventory.Owner.ValidateInventorySlot(inventory, slot))
                    {
                        return slot;
                    }
                }
            }
            Logger.Warn($"FindNextAvailableSlot: No free slot found in {GameDatabase.GetPrototypeName(inventory.PrototypeDataRef)} starting from {startSlot} up to {maxSlot}");
            return Inventory.InvalidSlot;
        }

        private string SortInventoryToStash(Player player)
        {
            Inventory generalInventory = player.GetInventory(InventoryConvenienceLabel.General);
            if (generalInventory == null) return "Failed to find general inventory.";

            List<Inventory> stashInventories = new List<Inventory>();
            List<PrototypeId> stashRefs = new List<PrototypeId>();

            if (!player.GetStashInventoryProtoRefs(stashRefs, false, true))
            {
                Logger.Debug("SortInventoryToStash: GetStashInventoryProtoRefs returned false or no stash inventories defined.");
            }

            foreach (PrototypeId stashRef in stashRefs)
            {
                Inventory stash = player.GetInventoryByRef(stashRef);
                if (stash != null)
                {
                    if (stash.Category == InventoryCategory.PlayerStashGeneral ||
                        stash.Category == InventoryCategory.PlayerStashAvatarSpecific ||
                        stash.Category == InventoryCategory.PlayerStashTeamUpGear)
                    {
                        stashInventories.Add(stash);
                    }
                }
            }

            if (stashInventories.Count == 0) return "No available stash tabs found to sort into.";

            int itemsMoved = 0;
            List<Item> itemsToMove = new List<Item>();

            foreach (var entry in generalInventory)
            {
                Item item = player.Game.EntityManager.GetEntity<Item>(entry.Id);
                if (item != null && !item.IsEquipped)
                {
                    itemsToMove.Add(item);
                }
            }

            // Sort the list of items before attempting to move them to stash
            if (itemsToMove.Any())
            {
                itemsToMove = itemsToMove
                    .OrderBy(item => GameDatabase.GetPrototypeName(item.PrototypeDataRef)) // Primary sort: Item Name
                                                                                           // .ThenByDescending(item => item.Properties[PropertyEnum.ItemLevel])    // Optional secondary sort
                    .ToList();
                Logger.Debug($"SortInventoryToStash: Sorted {itemsToMove.Count} items from general inventory before moving to stash.");
            }

            foreach (Item item in itemsToMove)
            {
                if (item == null) continue;

                Inventory targetStash = null;
                uint targetSlot = Inventory.InvalidSlot;

                foreach (Inventory stash in stashInventories)
                {
                    if (stash.PassesContainmentFilter(item.PrototypeDataRef) == InventoryResult.Success)
                    {
                        InventoryResult canPlaceResult = item.CanChangeInventoryLocation(stash);
                        if (canPlaceResult == InventoryResult.Success)
                        {
                            targetSlot = stash.GetFreeSlot(item, true, true);
                            if (targetSlot != Inventory.InvalidSlot)
                            {
                                targetStash = stash;
                                break;
                            }
                        }
                    }
                }

                if (targetStash != null && targetSlot != Inventory.InvalidSlot)
                {
                    ulong? stackEntityId = null;
                    InventoryResult moveResult = Inventory.ChangeEntityInventoryLocation(item, targetStash, targetSlot, ref stackEntityId, true);

                    if (moveResult == InventoryResult.Success)
                    {
                        itemsMoved++;
                        Logger.Debug($"SortInventoryToStash: Moved {GameDatabase.GetPrototypeName(item.PrototypeDataRef)} to {GameDatabase.GetPrototypeName(targetStash.PrototypeDataRef)}, Slot: {targetSlot}" + (stackEntityId.HasValue && stackEntityId.Value != Entity.InvalidId ? $" (Stacked on {stackEntityId.Value})" : ""));
                    }
                    else
                    {
                        Logger.Warn($"SortInventoryToStash: Failed to move {GameDatabase.GetPrototypeName(item.PrototypeDataRef)} to {GameDatabase.GetPrototypeName(targetStash.PrototypeDataRef)}. Reason: {moveResult}");
                    }
                }
            }

            return $"Auto-sort to stash complete. Moved {itemsMoved} item(s).";
        }	
    }
}
