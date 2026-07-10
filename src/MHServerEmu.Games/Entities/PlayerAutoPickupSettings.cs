using System.IO;
using System.Text.Json;
using MHServerEmu.Core.Logging;

namespace MHServerEmu.Games.Entities
{
    /// <summary>
    /// Per-player overrides for the server's item auto-pickup categories. The server's
    /// <see cref="MHServerEmu.Games.CustomGameOptionsConfig"/> toggles decide which categories exist
    /// at all; these settings let a player opt out of a category the server allows, or choose
    /// stash vs. general inventory for their own pickups. A <see langword="null"/> value means
    /// "use the server default" for that setting.
    /// </summary>
    public class PlayerAutoPickupSettings
    {
        public bool? CurrencyEnabled { get; set; }
        public bool? CraftingEnabled { get; set; }
        public bool? CraftingToStash { get; set; }
        public bool? GlyphEnabled { get; set; }
        public bool? GlyphToStash { get; set; }
        public bool? RelicEnabled { get; set; }
        public bool? RelicToStash { get; set; }
    }

    /// <summary>
    /// Handles loading and saving <see cref="PlayerAutoPickupSettings"/> to disk.
    /// </summary>
    public static class PlayerAutoPickupSettingsStorage
    {
        private static readonly Logger Logger = LogManager.CreateLogger();
        private static readonly string BaseDir = Path.Combine(Directory.GetCurrentDirectory(), "Data", "PlayerAutoPickupSettings");
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        public static PlayerAutoPickupSettings Load(ulong playerDbId)
        {
            string path = GetPath(playerDbId);
            if (File.Exists(path) == false)
                return new PlayerAutoPickupSettings();

            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                    return new PlayerAutoPickupSettings();

                return JsonSerializer.Deserialize<PlayerAutoPickupSettings>(json) ?? new PlayerAutoPickupSettings();
            }
            catch (Exception e)
            {
                Logger.Warn($"Failed to load auto-pickup settings for player {playerDbId}: {e.Message}");
                return new PlayerAutoPickupSettings();
            }
        }

        public static void Save(ulong playerDbId, PlayerAutoPickupSettings settings)
        {
            try
            {
                Directory.CreateDirectory(BaseDir);
                string json = JsonSerializer.Serialize(settings, WriteOptions);
                File.WriteAllText(GetPath(playerDbId), json);
            }
            catch (Exception e)
            {
                Logger.Warn($"Failed to save auto-pickup settings for player {playerDbId}: {e.Message}");
            }
        }

        private static string GetPath(ulong playerDbId) => Path.Combine(BaseDir, $"{playerDbId}.json");
    }
}
