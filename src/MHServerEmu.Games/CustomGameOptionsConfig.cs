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
        public int PhantomHeroesDownedGracePeriodMs { get; private set; } = 30000;
        public int PhantomHeroesReviveCooldownMs { get; private set; } = 60000;

        // Throwable Options
        public bool DisableInteractiveThrowables { get; private set; } = true;
        public bool AutoCancelThrowableOnPowerUse { get; private set; } = true;
        public bool AutoThrowOnMovementPower { get; private set; } = true;

        // Item Chest Auto Open
        public bool EnableItemChestAutoOpen { get; private set; } = true;
        public int ItemChestAutoOpenCooldownMs { get; private set; } = 1000;
        public string ItemChestAutoOpenWhitelist { get; private set; } = "Chest,Crate,LootBox,Giftbox,GiftBox";
        public bool EnableItemChestAutoOpenVerboseLogging { get; private set; } = false;
    }
}
