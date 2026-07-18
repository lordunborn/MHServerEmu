using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;

namespace MHServerEmu.Games.RoguesGallery
{
    /// <summary>
    /// Singleton that loads Data/Game/RoguesGallery/RogueNemesisTiers.json - per-rank (0-5) HP/damage
    /// scaling and loot pool overrides for the Rogue Encounter / Nemesis system. Deliberately a
    /// separate file from RoguesGallery.json (identity/pool data vs. tuning data that's expected to
    /// see much more frequent editing - see the design discussion on keeping tiers JSON-controlled
    /// so they can be retuned from repeated play without a rebuild).
    /// </summary>
    public class RogueNemesisTierDatabase
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private static readonly string RoguesGalleryDirectory = Path.Combine(FileHelper.DataDirectory, "Game", "RoguesGallery");

        private readonly Dictionary<int, RogueNemesisTierEntry> _tiersByRank = new();

        public static RogueNemesisTierDatabase Instance { get; } = new();

        private RogueNemesisTierDatabase() { }

        /// <summary>
        /// Loads (or reloads) the tier data file. Safe to call again at runtime - no rebuild
        /// required to retune multipliers or loot pool assignments.
        /// </summary>
        public bool Initialize()
        {
            _tiersByRank.Clear();

            string path = Path.Combine(RoguesGalleryDirectory, "RogueNemesisTiers.json");
            if (File.Exists(path) == false)
            {
                return Logger.WarnReturn(false, $"Initialize(): Rogue Nemesis tier data not found at {path} - " +
                                                 "every rank will use 1.0x scaling and default loot pools until this is added.");
            }

            JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };
            RogueNemesisTierData data = FileHelper.DeserializeJson<RogueNemesisTierData>(path, options);
            if (data == null)
                return Logger.WarnReturn(false, "Initialize(): Failed to deserialize Rogue Nemesis tier data.");

            foreach (RogueNemesisTierEntry entry in data.Tiers)
            {
                if (entry.Rank < 0 || entry.Rank > 5)
                {
                    Logger.Warn($"Initialize(): tier entry rank {entry.Rank} is outside the supported 0-5 range - skipping.");
                    continue;
                }

                _tiersByRank[entry.Rank] = entry;
            }

            Logger.Info($"[RogueNemesisTiers] Initialized: {_tiersByRank.Count} rank tier(s) loaded.");
            return true;
        }

        /// <summary>
        /// Caps the Nemesis rank a player's current avatar level can "see" in combat. The
        /// persisted rank keeps counting from real wins/losses regardless of level - this only
        /// caps what tier multiplier/loot a fight is allowed to use until the player levels up.
        /// Below level 30 -> 0 (RunRogueNemesisWave already excludes these from natural spawns;
        /// this only matters for admin force-spawns at a low level). 30-39 -> 2, 40-49 -> 3,
        /// 50-59 -> 4, 60+ -> 5. So a true rank-5 Nemesis encounter requires being level 60.
        /// </summary>
        public static int GetLevelRankCap(int avatarLevel)
        {
            if (avatarLevel < 30) return 0;
            return Math.Min(5, 2 + (avatarLevel - 30) / 10);
        }

        // TUNING-CURVE (adjust here): GetLevelBaselineScale below is the sub-60 difficulty ramp.
        // Confirmed working via 2026-07-17 curve testing. Rank 5's 1.0x ceiling may need raising
        // later depending on player feedback after more play sessions - nothing else needs to
        // change to try that, just the `if (avatarLevel >= 60) return 1.0f;` line below. Any edit
        // here only requires rebuilding MHServerEmu.Games.csproj (no data/JSON changes needed).
        /// <summary>
        /// Scales the shared Incursion/RogueNemesis baseline (<c>IncursionEnemyHealthMaxOverride</c> /
        /// <c>IncursionEnemyDamageMultiplier</c>) down for sub-60 characters. That baseline was
        /// calibrated against a geared level-60 avatar; below that, rank-0 fights were being lost as
        /// high as level 47 (see the 2026-07-15 balance batch notes) because a leveling character has
        /// nowhere near that gear/resource depth even at a synced CombatLevel. Linear ramp from 0.4x
        /// at level 30 (the floor - RunIncursionWave/RunRogueNemesisWave already exclude anyone lower)
        /// up to 1.0x at level 60, applied on top of - not instead of - the RogueNemesis rank multiplier.
        /// </summary>
        public static float GetLevelBaselineScale(int avatarLevel)
        {
            if (avatarLevel >= 60) return 1.0f;
            if (avatarLevel <= 30) return 0.4f;
            return 0.4f + 0.6f * (avatarLevel - 30) / 30f;
        }

        /// <summary>Health/toughness multiplier for the given rank (0-5), or 1.0 if no entry exists.</summary>
        public float GetHealthMultiplier(int rank) =>
            _tiersByRank.TryGetValue(rank, out RogueNemesisTierEntry entry) ? MathF.Max(0.01f, entry.HealthMult) : 1.0f;

        /// <summary>Outgoing damage multiplier for the given rank (0-5), or 1.0 if no entry exists.</summary>
        public float GetDamageMultiplier(int rank) =>
            _tiersByRank.TryGetValue(rank, out RogueNemesisTierEntry entry) ? MathF.Max(0f, entry.DamageMult) : 1.0f;

        /// <summary>
        /// Loot pool name overrides for the given rank, or null if this rank has no entry or
        /// doesn't override loot (falls back to the invader's normal loot pool selection).
        /// </summary>
        public IReadOnlyList<string> GetLootPoolNames(int rank) =>
            _tiersByRank.TryGetValue(rank, out RogueNemesisTierEntry entry) && entry.LootPools.Count > 0 ? entry.LootPools : null;
    }
}
