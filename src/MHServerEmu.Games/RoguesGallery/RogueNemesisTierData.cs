using System.Collections.Generic;

namespace MHServerEmu.Games.RoguesGallery
{
    /// <summary>
    /// One Nemesis rank's tuning: how much tougher/harder-hitting the invader becomes, and
    /// (optionally) which named IncursionEnemyController.DefaultLootPools entries it drops from
    /// instead of the normal Incursion-style pool. An empty LootPools list means "use whatever
    /// the invader would normally drop" - only set it to override, e.g. to point rank 5 at the
    /// higher-rarity Cosmic/All pools that are disabled by default for ordinary Incursion invaders.
    /// </summary>
    public class RogueNemesisTierEntry
    {
        public int Rank { get; set; }
        public float HealthMult { get; set; } = 1.0f;
        public float DamageMult { get; set; } = 1.0f;
        public List<string> LootPools { get; set; } = new();
    }

    /// <summary>JSON schema for Data/Game/RoguesGallery/RogueNemesisTiers.json.</summary>
    public class RogueNemesisTierData
    {
        public List<RogueNemesisTierEntry> Tiers { get; set; } = new();
    }
}
