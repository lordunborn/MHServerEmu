using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData;

namespace MHServerEmu.Games.RoguesGallery
{
    /// <summary>
    /// Loads Data/Game/RoguesGallery/IncursionPowerOverrides.json - live-tunable per-hero,
    /// per-power damage scale/enabled overrides for Incursion and Rogue Nemesis invaders.
    /// Every hero already has a hardcoded PowerTable baked into its own IncursionEnemyXxx.cs
    /// (63 files, requires a rebuild to retune) - this sits in front of that table so a specific
    /// power on a specific hero can be corrected from real log evidence without a rebuild.
    /// An entry here always wins over the hardcoded table; heroes/powers with no entry fall
    /// through to the hardcoded value unchanged. Keyed by <see cref="IncursionEntity.IncursionEnemyController.CleanDisplayName"/>
    /// (e.g. "Spiderman"), case-insensitive.
    /// </summary>
    public class IncursionPowerOverrideDatabase
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private static readonly string RoguesGalleryDirectory = Path.Combine(FileHelper.DataDirectory, "Game", "RoguesGallery");

        private readonly Dictionary<string, Dictionary<PrototypeId, IncursionPowerOverrideEntry>> _overridesByHero = new(System.StringComparer.OrdinalIgnoreCase);

        public static IncursionPowerOverrideDatabase Instance { get; } = new();

        private IncursionPowerOverrideDatabase() { }

        /// <summary>
        /// Loads (or reloads) the override file. Safe to call again at runtime - no rebuild
        /// required to add or correct a power's damage scale.
        /// </summary>
        public bool Initialize()
        {
            _overridesByHero.Clear();

            string path = Path.Combine(RoguesGalleryDirectory, "IncursionPowerOverrides.json");
            if (File.Exists(path) == false)
                return Logger.WarnReturn(false, $"Initialize(): Incursion power override data not found at {path} - every hero uses its hardcoded PowerTable unchanged.");

            JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };
            IncursionPowerOverrideData data = FileHelper.DeserializeJson<IncursionPowerOverrideData>(path, options);
            if (data == null)
                return Logger.WarnReturn(false, "Initialize(): Failed to deserialize Incursion power override data.");

            int entryCount = 0;
            foreach (IncursionHeroPowerOverrides hero in data.Heroes)
            {
                if (string.IsNullOrEmpty(hero.Hero)) continue;

                if (_overridesByHero.TryGetValue(hero.Hero, out var powerMap) == false)
                {
                    powerMap = new Dictionary<PrototypeId, IncursionPowerOverrideEntry>();
                    _overridesByHero[hero.Hero] = powerMap;
                }

                foreach (IncursionPowerOverrideEntry entry in hero.Powers)
                {
                    PrototypeId powerRef = GameDatabase.GetPrototypeRefByName(entry.Power);
                    if (powerRef == PrototypeId.Invalid)
                    {
                        Logger.Warn($"Initialize(): could not resolve power '{entry.Power}' for hero '{hero.Hero}' - skipping.");
                        continue;
                    }

                    powerMap[powerRef] = entry;
                    entryCount++;
                }
            }

            Logger.Info($"[IncursionPowerOverrides] Initialized: {entryCount} override(s) across {_overridesByHero.Count} hero(es) loaded.");
            return true;
        }

        /// <summary>Looks up an override entry for the given hero/power pair, if one exists.</summary>
        public bool TryGetOverride(string heroKey, PrototypeId powerRef, out IncursionPowerOverrideEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(heroKey)) return false;
            if (_overridesByHero.TryGetValue(heroKey, out var powerMap) == false) return false;
            return powerMap.TryGetValue(powerRef, out entry);
        }
    }
}
