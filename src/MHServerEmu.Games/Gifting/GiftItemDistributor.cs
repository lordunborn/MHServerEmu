using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.Loot;
using System.Text.Json;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Games.GameData.Prototypes;  // Add this for ItemPrototype
using MHServerEmu.Games.Common;
using MHServerEmu.Games.Entities;
using System.Collections.Concurrent;
using MHServerEmu.Core.System.Time;



namespace MHServerEmu.Games.Gifting
{
    public class GiftItemEntry
    {
        public ulong ItemPrototype { get; set; }  // Store as ulong
        public int Count { get; set; }
        public DateTime AddedDate { get; set; }
    }

    public static class GiftItemDistributor
    {
        private static readonly Logger Logger = LogManager.CreateLogger();
        private static readonly string PendingItemsPath = Path.Combine(FileHelper.DataDirectory, "PendingItems.json");
        private static List<GiftItemEntry> _cachedItems;

        public static bool Initialize()
        {
            Logger.Info("Initializing GiftItemDistributor");
            LoadGiftItems();
            GiftClaimStorage.Initialize();
            return true;
        }

        private static void LoadGiftItems()
        {
            Logger.Info("LoadGiftItems called");
            if (File.Exists(PendingItemsPath))
            {
                Logger.Info($"Loading gifts from {PendingItemsPath}");
                string jsonContent = File.ReadAllText(PendingItemsPath);
                _cachedItems = JsonSerializer.Deserialize<List<GiftItemEntry>>(jsonContent);
                Logger.Info($"Loaded {_cachedItems.Count} gift entries");
            }
            else
            {
                _cachedItems = new List<GiftItemEntry>();
                Logger.Info("Created new empty gift list");
            }
        }

        public static void DistributeGiftItems(Player player)
        {
            if (_cachedItems == null || _cachedItems.Count == 0)
            {
                //Logger.Info("No gifts available for distribution, skipping checks");
                return;
            }
            //Logger.Debug($"Checking gifts for player {player.GetName()}");
            var currentTime = DateTime.UtcNow;
            string playerName = player.GetName();
            int giftsDistributed = 0;

            foreach (var entry in _cachedItems)
            {
                if (entry.AddedDate > currentTime)
                {
                    //Logger.Debug($"Gift {entry.ItemPrototype} not yet available. Current time: {currentTime}, Gift available: {entry.AddedDate}");
                    continue;
                }

                if (!GiftClaimStorage.HasClaimed(playerName, entry.ItemPrototype))
                {
                    ItemPrototype itemProto = GameDatabase.GetPrototype<ItemPrototype>((PrototypeId)entry.ItemPrototype);
                    if (itemProto != null)
                    {
                        for (int i = 0; i < entry.Count; i++)
                            player.Game.LootManager.GiveItem(itemProto.DataRef, LootContext.Drop, player);
                        _ = GiftClaimStorage.SaveClaimAsync(playerName, entry.ItemPrototype);
                        giftsDistributed++;
                        //Logger.Info($"Successfully distributed {entry.Count}x {itemProto} to player {playerName}");
                    }
                }
            }
            if (giftsDistributed > 0)
                Logger.Info($"Distributed {giftsDistributed} gifts to player {playerName}");
        }
    }
}
