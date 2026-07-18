using System.Collections.Generic;

namespace MHServerEmu.Games.RoguesGallery
{
    /// <summary>
    /// JSON schema for Data/Game/RoguesGallery/RoguesGallery.json. All entries are incursion
    /// enemy shorthand names (e.g. "Loki", "GreenGoblin") - the same identifiers already used
    /// by the IncursionExcludeEnemies config and !incursion commands, not raw prototype paths.
    /// </summary>
    public class RoguesGalleryData
    {
        public List<string> FallbackVillainPool { get; set; } = new();
        public Dictionary<string, List<string>> HeroRogues { get; set; } = new();
        public List<string> VillainFlavoredAvatars { get; set; } = new();
        public Dictionary<string, List<string>> VillainHunters { get; set; } = new();
    }
}
