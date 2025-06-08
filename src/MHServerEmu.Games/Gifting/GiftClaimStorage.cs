using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Helpers;

namespace MHServerEmu.Games.Gifting
{
    public static class GiftClaimStorage
    {
        private static readonly Logger Logger = LogManager.CreateLogger();
        private static readonly string ClaimsPath = Path.Combine(FileHelper.DataDirectory, "GiftClaims.dat");
        private static readonly Dictionary<(string PlayerName, ulong ItemId), DateTime> _claims = new();
        private static readonly object _lockObject = new();

        public static void Initialize()
        {
            Logger.Info("Initializing GiftClaimStorage");
            LoadFromDisk();
        }

        public static bool HasClaimed(string playerName, ulong itemId)
        {
            lock (_lockObject)
            {
                return _claims.ContainsKey((playerName, itemId));
            }
        }

        public static void SaveClaim(string playerName, ulong itemId)
        {
            lock (_lockObject)
            {
                _claims[(playerName, itemId)] = DateTime.UtcNow;
                _ = SaveClaimAsync(playerName, itemId);
            }
        }

        public  static async Task SaveClaimAsync(string playerName, ulong itemId)
        {
            await Task.Run(() =>
            {
                lock (_lockObject)
                {
                    _claims[(playerName, itemId)] = DateTime.UtcNow;
                    SaveToDisk();
                }
            });
        }

        private static void LoadFromDisk()
        {
            if (!File.Exists(ClaimsPath))
            {
                Logger.Info("No existing claims file found");
                return;
            }

            try
            {
                using var reader = new BinaryReader(File.OpenRead(ClaimsPath));
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string playerName = reader.ReadString();
                    ulong itemId = reader.ReadUInt64();
                    long ticks = reader.ReadInt64();
                    _claims[(playerName, itemId)] = new DateTime(ticks);
                }
                Logger.Info($"Loaded {count} gift claims from storage");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading gift claims: {ex.Message}");
            }
        }

        private static void SaveToDisk()
        {
            try
            {
                using var writer = new BinaryWriter(File.Create(ClaimsPath));
                writer.Write(_claims.Count);
                foreach (var ((playerName, itemId), claimTime) in _claims)
                {
                    writer.Write(playerName);
                    writer.Write(itemId);
                    writer.Write(claimTime.Ticks);
                }
                Logger.Info($"Saved {_claims.Count} gift claims to storage");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error saving gift claims: {ex.Message}");
            }
        }
    }
}
