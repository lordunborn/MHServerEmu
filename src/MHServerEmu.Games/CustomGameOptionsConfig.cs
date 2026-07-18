using MHServerEmu.Core.Config;

namespace MHServerEmu.Games
{
    public class CustomGameOptionsConfig : ConfigContainer
    {
        public int AutosaveIntervalMinutes { get; private set; } = 15;
        public float ESCooldownOverrideMinutes { get; private set; } = -1f;
        public bool CombineESStacks { get; private set; } = false;
        public bool AutoUnlockAvatars { get; private set; } = false;
        public bool AutoUnlockTeamUps { get; private set; } = false;
        public bool DisableMovementPowerChargeCost { get; private set; } = false;
        public bool AllowSameGroupTalents { get; private set; } = false;
        public bool EnableCreditChestConversion { get; private set; } = false;
        public float CreditChestConversionMultiplier { get; private set; } = 2f;
        public bool DisableInstancedLoot { get; private set; } = false;
        public float LootSpawnGridCellRadius { get; private set; } = 20f;
        public float TrashedItemExpirationTimeMultiplier { get; private set; } = 1f;
        public bool DisableAccountBinding { get; private set; } = false;
        public bool DisableCharacterBinding { get; private set; } = false;
        public bool DisableMissionXPBonuses { get; private set; } = false;
        public bool UsePrestigeLootTable { get; private set; } = false;
        public bool EnableUltimatePrestige { get; private set; } = false;
        public bool ApplyHiddenPvPDamageModifiers { get; private set; } = false;

        // LOOT FILTER
        public bool LootFilterEnable { get; private set; } = true;
        public bool LootFilterCharacterSpecificEnable { get; private set; } = true;
        public bool LootFilterLoggingEnable { get; private set; } = false;

        // Item Auto Pickup
        public bool EnableItemAutoPickup { get; private set; } = true;
        public float ItemAutoPickupRadius { get; private set; } = 1400f;
        public int ItemAutoPickupIntervalMs { get; private set; } = 1500;
        public bool EnableCraftingIngredientAutoPickup { get; private set; } = true;
        public bool CraftingIngredientAutoPickupToStash { get; private set; } = true;
        public bool EnableCraftingIngredientAutoPickupVerboseLogging { get; private set; } = false;
        public bool EnableGlyphAutoPickup { get; private set; } = true;
        public bool GlyphAutoPickupToStash { get; private set; } = true;
        public bool EnableRelicAutoPickup { get; private set; } = true;
        public bool RelicAutoPickupToStash { get; private set; } = true;
        public bool RelicAutoPickupEquipIfSameTypeEquipped { get; private set; } = true;

        // Stash Affinity
        public bool StashAffinityEnable { get; private set; } = true;
        public bool StashAffinityLoggingEnable { get; private set; } = false;

        // Phantom Heroes
        public bool PhantomHeroesEnable { get; private set; } = true;
        public bool PhantomHeroesDespawnOnDeath { get; private set; } = true;
        public int PhantomHeroesMaxActive { get; private set; } = 9;
        public string PhantomGearItemBlacklist { get; private set; } = "";

        // Throwable Options
        public bool DisableInteractiveThrowables { get; private set; } = true;
        public bool AutoCancelThrowableOnPowerUse { get; private set; } = true;
        public bool AutoThrowOnMovementPower { get; private set; } = true;

        // Item Chest Auto Open
        public bool EnableItemChestAutoOpen { get; private set; } = true;
        public int ItemChestAutoOpenCooldownMs { get; private set; } = 1000;
        public string ItemChestAutoOpenWhitelist { get; private set; } = "Chest,Crate,LootBox,Giftbox,GiftBox";
        public bool EnableItemChestAutoOpenVerboseLogging { get; private set; } = false;

        // Incursion
        public bool IncursionEnable { get; private set; } = false;
        public int IncursionIntervalMs { get; private set; } = 180000;
        public int IncursionRandomIntervalMaxMs { get; private set; } = 360000;
        public int IncursionMaxActiveInvaders { get; private set; } = 10;
        public int IncursionMaxLifetimeMs { get; private set; } = 1200000; // 20 minutes
        public int IncursionIdleTimeoutMs { get; private set; } = 120000; // 2 minutes
        public int IncursionDeathGracePeriodMs { get; private set; } = 20000;
        public float IncursionEnemyDamageTakenMultiplier { get; private set; } = 2.0f;
        public float IncursionEnemyVisualScale { get; private set; } = 1.5f;
        public float IncursionEnemyDamageMultiplier { get; private set; } = 0.4f;
        public float IncursionMaxSingleHitPctOfTargetHealth { get; private set; } = 0.15f;
        public long IncursionEnemyHealthMaxOverride { get; private set; } = 500;
        public string IncursionExcludeEnemies { get; private set; } = "";
        public string IncursionEnemyPrototype { get; private set; } = "";
        public string IncursionAllowedRegions { get; private set; } = "";
        public string IncursionDisguiseChassisPrototypes { get; private set; } = "";
        public int IncursionRecentlyHuntedCooldownMs { get; private set; } = 300000; // 5 minutes
        public string IncursionDeathRevealMobPrototypes { get; private set; } = "Entity/Characters/Mobs/HandSkrull/HandSkrullNinjaBase.prototype";
        public int IncursionDeathVaporizeDelayMs { get; private set; } = 600;
        public bool IncursionCommandsRequireAdmin { get; private set; } = true;
        public bool IncursionLoggingEnable { get; private set; } = false;
        public bool IncursionLogVerboseEnable { get; private set; } = false;
        public bool IncursionLogAllDamageTargetsEnable { get; private set; } = false;
        public bool IncursionLogCollatorEnable { get; private set; } = false;

        // RogueNemesis
        public bool RogueNemesisEnable { get; private set; } = false;
        public int RogueNemesisCheckIntervalMs { get; private set; } = 60000;
        public float RogueNemesisRollChance { get; private set; } = 0.25f;
        public int RogueNemesisCooldownMs { get; private set; } = 300000; // 5 minutes
        public int RogueNemesisMaxSpawns { get; private set; } = 1;
        public string RogueNemesisExcludedRegions { get; private set; } = "";
        public int RogueNemesisFollowDelayMs { get; private set; } = 30000; // 30 seconds
        public int RogueNemesisTargetUnreachableTimeoutMs { get; private set; } = 60000; // 1 minute
        public float RogueNemesisRankWeightMultiplier { get; private set; } = 2.0f;
        public bool RogueNemesisTier5DefeatCooldownEnable { get; private set; } = true;
        public int RogueNemesisTier5DefeatCooldownResetHour { get; private set; } = 6;
        public bool RogueNemesisCommandsRequireAdmin { get; private set; } = true;
        public bool RogueNemesisLoggingEnable { get; private set; } = false;
        public bool RogueNemesisLogVerboseEnable { get; private set; } = false;
    }
}
