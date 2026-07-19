using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.Populations;

namespace MHServerEmu.Games.RoguesGallery
{
    /// <summary>
    /// Singleton that loads Data/Game/RoguesGallery/RoguesGallery.json and resolves rogue spawn
    /// pools for the Rogue Encounter / Nemesis system. Every entry is validated at load time
    /// against the live incursion enemy roster (<see cref="IncursionManager.GetKnownEnemyShorthands"/>)
    /// so a typo in the data file surfaces as a startup warning instead of a silent no-op later.
    /// </summary>
    public class RoguesGalleryDatabase
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private static readonly string RoguesGalleryDirectory = Path.Combine(FileHelper.DataDirectory, "Game", "RoguesGallery");

        private readonly List<string> _fallbackVillainPool = new();
        private readonly Dictionary<string, List<string>> _heroRogues = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _villainHunters = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _villainFlavoredAvatars = new(StringComparer.OrdinalIgnoreCase);

        public static RoguesGalleryDatabase Instance { get; } = new();

        private RoguesGalleryDatabase() { }

        /// <summary>
        /// Loads (or reloads) the Rogues Gallery data file. Safe to call again at runtime
        /// (e.g. from an admin reload command) - no rebuild required to pick up data changes.
        /// </summary>
        public bool Initialize()
        {
            Clear();

            string path = Path.Combine(RoguesGalleryDirectory, "RoguesGallery.json");
            if (File.Exists(path) == false)
                return Logger.WarnReturn(false, $"Initialize(): Rogues Gallery data not found at {path}");

            JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };
            RoguesGalleryData data = FileHelper.DeserializeJson<RoguesGalleryData>(path, options);
            if (data == null)
                return Logger.WarnReturn(false, "Initialize(): Failed to deserialize Rogues Gallery data");

            HashSet<string> knownShorthands = new(IncursionManager.GetKnownEnemyShorthands(), StringComparer.OrdinalIgnoreCase);

            AddValidated(_fallbackVillainPool, data.FallbackVillainPool, "fallbackVillainPool", knownShorthands);

            foreach (var kvp in data.HeroRogues)
            {
                List<string> list = new();
                AddValidated(list, kvp.Value, $"heroRogues[{kvp.Key}]", knownShorthands);
                if (list.Count > 0)
                    _heroRogues[kvp.Key] = list;
            }

            foreach (string name in data.VillainFlavoredAvatars)
            {
                if (knownShorthands.Contains(name) == false)
                {
                    Logger.Warn($"Initialize(): villainFlavoredAvatars entry '{name}' does not match any known incursion enemy - skipping.");
                    continue;
                }
                if (IncursionManager.IsExcludedFromRandomSpawns(name))
                {
                    Logger.Warn($"Initialize(): villainFlavoredAvatars entry '{name}' matches IncursionExcludeEnemies - skipping.");
                    continue;
                }
                _villainFlavoredAvatars.Add(name);
            }

            foreach (var kvp in data.VillainHunters)
            {
                List<string> list = new();
                AddValidated(list, kvp.Value, $"villainHunters[{kvp.Key}]", knownShorthands);
                if (list.Count > 0)
                    _villainHunters[kvp.Key] = list;
            }

            Logger.Info($"[RoguesGallery] Initialized: {_fallbackVillainPool.Count} fallback villain(s), " +
                        $"{_heroRogues.Count} curated hero rogues list(s), {_villainFlavoredAvatars.Count} villain-flavored avatar(s), " +
                        $"{_villainHunters.Count} curated villain hunter list(s).");
            return true;
        }

        private static void AddValidated(List<string> dest, List<string> source, string label, HashSet<string> knownShorthands)
        {
            if (source == null) return;

            foreach (string name in source)
            {
                if (knownShorthands.Contains(name) == false)
                {
                    Logger.Warn($"Initialize(): {label} entry '{name}' does not match any known incursion enemy - skipping.");
                    continue;
                }
                if (IncursionManager.IsExcludedFromRandomSpawns(name))
                {
                    Logger.Warn($"Initialize(): {label} entry '{name}' matches IncursionExcludeEnemies - skipping.");
                    continue;
                }
                dest.Add(name);
            }
        }

        private void Clear()
        {
            _fallbackVillainPool.Clear();
            _heroRogues.Clear();
            _villainHunters.Clear();
            _villainFlavoredAvatars.Clear();
        }

        private static readonly HashSet<string> s_emptyCuratedSet = new(StringComparer.OrdinalIgnoreCase);

        public bool IsVillainFlavored(string avatarShorthand) => _villainFlavoredAvatars.Contains(avatarShorthand);

        /// <summary>
        /// Returns the rogue spawn pool for the given avatar shorthand: its own curated entry (if
        /// one exists) unioned with the global fallback pool, plus which of those names came from
        /// the curated entry so the caller can weight picks toward it (see
        /// RogueNemesisManager.PickWeightedDistinct / RogueNemesisCuratedRogueWeightShare) rather
        /// than restrict the pool to ONLY the curated names. Curated is an empty set when the
        /// avatar has no curated entry, in which case Pool is just the fallback pool unchanged.
        /// Does not invert for villain-flavored avatars; callers should check
        /// <see cref="IsVillainFlavored"/> first and use <see cref="GetHeroHunterPool"/> instead
        /// when the avatar is tagged.
        /// </summary>
        public (IReadOnlyList<string> Pool, IReadOnlySet<string> Curated) GetRoguePoolForAvatar(string avatarShorthand)
        {
            if (_heroRogues.TryGetValue(avatarShorthand, out List<string> curated) && curated.Count > 0)
                return UnionWithCurated(curated, _fallbackVillainPool);

            return (_fallbackVillainPool, s_emptyCuratedSet);
        }

        /// <summary>
        /// The "heroes hunt you" pool for the given villain-flavored avatar shorthand: its own
        /// curated entry (if one exists) unioned with the general pool of every known incursion
        /// enemy that ISN'T tagged villain-flavored, plus which names came from the curated entry
        /// (see <see cref="GetRoguePoolForAvatar"/> for why this isn't a restriction). The general
        /// pool is computed on demand from the live roster rather than stored, so it never goes
        /// stale relative to the data file.
        /// </summary>
        public (IReadOnlyList<string> Pool, IReadOnlySet<string> Curated) GetHeroHunterPool(string villainShorthand)
        {
            List<string> generalPool = IncursionManager.GetKnownEnemyShorthands()
                .Where(shorthand => _villainFlavoredAvatars.Contains(shorthand) == false
                    && IncursionManager.IsExcludedFromRandomSpawns(shorthand) == false)
                .ToList();

            if (_villainHunters.TryGetValue(villainShorthand, out List<string> curated) && curated.Count > 0)
                return UnionWithCurated(curated, generalPool);

            return (generalPool, s_emptyCuratedSet);
        }

        /// <summary>Unions a curated list with a fallback pool (dedup, curated names first) and returns the curated set alongside it.</summary>
        private static (IReadOnlyList<string> Pool, IReadOnlySet<string> Curated) UnionWithCurated(List<string> curated, IReadOnlyList<string> fallback)
        {
            HashSet<string> curatedSet = new(curated, StringComparer.OrdinalIgnoreCase);

            List<string> union = new(curated);
            foreach (string name in fallback)
            {
                if (curatedSet.Contains(name) == false)
                    union.Add(name);
            }

            return (union, curatedSet);
        }
    }
}
