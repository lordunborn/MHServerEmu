using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.System.Time;

namespace MHServerEmu.Games.Populations
{
    /// <summary>
    /// One villain's persistent obsession with a specific player. A villain isn't a "nemesis" yet
    /// on a first fight - Rank climbs (capped at 5) each time the PLAYER defeats them, representing
    /// a growing vendetta. When the villain gets their revenge (defeats the player) it only knocks
    /// Rank down by one rather than settling the vendetta entirely - see RogueNemesisManager.
    /// RecordNemesisLoss - so a player stuck below a rank they can't beat yet doesn't have to
    /// re-grind every rank below it.
    /// </summary>
    public class NemesisEntry
    {
        public string EnemyShorthand { get; set; }
        public int Rank { get; set; }

        /// <summary>
        /// Wall-clock Unix milliseconds (see RogueNemesisPlayerData.CooldownUntilUnixTimeMs for why
        /// this is wall-clock rather than Game.CurrentTime) this villain becomes eligible to spawn
        /// for this player again after being defeated at rank 5 - see
        /// RogueNemesisManager.RecordNemesisWin and the RogueNemesisTier5DefeatCooldown* config.
        /// 0 = no cooldown active. Only ever set for a rank-5 defeat; every other rank is unaffected.
        /// </summary>
        public long Tier5DefeatCooldownUntilUnixTimeMs { get; set; }

        public bool IsOnTier5DefeatCooldown =>
            Tier5DefeatCooldownUntilUnixTimeMs > 0 && Clock.UnixTime.TotalMilliseconds < Tier5DefeatCooldownUntilUnixTimeMs;
    }

    /// <summary>
    /// Per-player Rogue Encounter / Nemesis state: opt-in preference, post-encounter cooldown,
    /// and the persistent Nemesis rank list. This CANNOT live in RogueNemesisManager's in-memory
    /// dictionaries - this server provisions a separate Game instance per region instance (see
    /// WorldManager/GameHandle), so a player's session can hop between several different Game
    /// objects over time, each with its own empty manager. Real per-player data has to live
    /// independent of any one Game instance, the same way PlayerLootFilter does.
    /// </summary>
    public class RogueNemesisPlayerData
    {
        public bool Enabled { get; set; }

        public List<NemesisEntry> NemesisEntries { get; set; } = new();

        /// <summary>Finds an existing entry by shorthand, or null if this villain has no history yet.</summary>
        public NemesisEntry FindNemesisEntry(string enemyShorthand)
        {
            foreach (NemesisEntry entry in NemesisEntries)
                if (string.Equals(entry.EnemyShorthand, enemyShorthand, StringComparison.OrdinalIgnoreCase))
                    return entry;
            return null;
        }

        /// <summary>Finds or creates (Rank 0) an entry for the given villain shorthand.</summary>
        public NemesisEntry GetOrAddNemesisEntry(string enemyShorthand)
        {
            NemesisEntry entry = FindNemesisEntry(enemyShorthand);
            if (entry != null) return entry;

            entry = new NemesisEntry { EnemyShorthand = enemyShorthand, Rank = 0 };
            NemesisEntries.Add(entry);
            return entry;
        }

        /// <summary>
        /// Incursion enemy shorthand (e.g. "DrDoom") of the hunt currently in progress, or null
        /// if none. Set when an encounter starts, cleared when it legitimately ends (kill, death,
        /// safe zone, or a follow-respawn giving up). Exists so a NEW Game instance - which has
        /// no idea an old, now-unreachable Game instance still has an orphaned invader hunting
        /// this player (Game.EntityManager.GetEntity&lt;Player&gt; can't resolve across Game
        /// instances, so the old invader just waits forever and never detects the transfer) - can
        /// tell there's an unresolved hunt to resume via the same follow-delay mechanism used for
        /// an in-Game region change, instead of treating the player as fully eligible for a brand
        /// new roll. See RogueNemesisManager.RunRogueNemesisWave.
        /// </summary>
        public string ActiveEnemyShorthand { get; set; }

        /// <summary>
        /// Wall-clock Unix milliseconds (Clock.UnixTime, NOT Game.CurrentTime - that's relative
        /// to whichever Game instance's own start, meaningless once compared across Game
        /// instances or a server restart). 0 = no cooldown.
        /// </summary>
        public long CooldownUntilUnixTimeMs { get; set; }

        public bool IsOnCooldown => CooldownUntilUnixTimeMs > 0 && Clock.UnixTime.TotalMilliseconds < CooldownUntilUnixTimeMs;

        public void SetCooldown(TimeSpan duration)
        {
            CooldownUntilUnixTimeMs = (long)(Clock.UnixTime + duration).TotalMilliseconds;
        }
    }

    /// <summary>
    /// Loads and saves <see cref="RogueNemesisPlayerData"/> to disk, one JSON file per player
    /// keyed by their persistent DatabaseUniqueId - same storage shape as PlayerLootFilterStorage.
    /// </summary>
    public static class RogueNemesisPlayerDataStorage
    {
        private static readonly Logger Logger = LogManager.CreateLogger();
        private static readonly string BaseDir = Path.Combine(Directory.GetCurrentDirectory(), "Data", "RogueNemesisPlayers");
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        public static RogueNemesisPlayerData Load(ulong playerDbId)
        {
            string path = GetPath(playerDbId);
            if (File.Exists(path) == false)
                return new RogueNemesisPlayerData();

            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                    return new RogueNemesisPlayerData();

                return JsonSerializer.Deserialize<RogueNemesisPlayerData>(json) ?? new RogueNemesisPlayerData();
            }
            catch (Exception e)
            {
                Logger.Warn($"Failed to load Rogue Encounter data for player {playerDbId}: {e.Message}");
                return new RogueNemesisPlayerData();
            }
        }

        public static void Save(ulong playerDbId, RogueNemesisPlayerData data)
        {
            try
            {
                Directory.CreateDirectory(BaseDir);
                string json = JsonSerializer.Serialize(data, WriteOptions);
                File.WriteAllText(GetPath(playerDbId), json);
            }
            catch (Exception e)
            {
                Logger.Warn($"Failed to save Rogue Encounter data for player {playerDbId}: {e.Message}");
            }
        }

        private static string GetPath(ulong playerDbId) => Path.Combine(BaseDir, $"{playerDbId}.json");
    }
}
