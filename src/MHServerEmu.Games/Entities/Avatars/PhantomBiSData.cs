using System;
using System.Collections.Generic;
using System.IO;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Loot;

namespace MHServerEmu.Games.Entities.Avatars
{
    /// <summary>
    /// Loads the curated best-in-slot gear table (Data/Game/PhantomHeroes/
    /// PhantomBiSGear.json, see NOTICE.md for attribution) and resolves it to
    /// a per-avatar map of EquipmentInvUISlot -> item PrototypeId. Used so
    /// level-60 friendly phantoms wear a real, known-good loadout instead of
    /// generic level-banded random rolls. Names were pre-matched to prototype
    /// refs during the scrape, so this loader just parses hex refs and
    /// resolves the hero leaf name to its AvatarPrototype ref.
    ///
    /// Deliberately does NOT include a generative fallback for heroes missing
    /// from the curated file (unlike the upstream source this was adapted
    /// from) - that generator draws from each slot's UNRESTRICTED loot pool,
    /// which would bypass this server's own Tier1Artifacts/UniqueAvatarArmor/
    /// blacklist gear filters (see Avatar.PhantomHero.cs's
    /// BuildFilteredPhantomGearPicker) for any hero not in the curated set.
    /// A hero (or slot) missing from TryGetLoadout's result just falls
    /// through to the normal restricted random-roll path instead.
    /// </summary>
    public static class PhantomBiSData
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        // avatarRef -> (uiSlot -> itemRef). Loaded from the curated BiS
        // table at Data/Game/PhantomHeroes/PhantomBiSGear.json — a mapping of
        // community build recommendations from itembase.mhbugle.com (the
        // Marvel Heroes Omega "Item Base"), used with permission. See
        // NOTICE.md alongside the JSON for full attribution.
        private static Dictionary<PrototypeId, Dictionary<EquipmentInvUISlot, PrototypeId>> s_byAvatar;
        private static readonly object s_lock = new();

        private static readonly Dictionary<string, EquipmentInvUISlot> SlotNameToUI = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Artifact1"] = EquipmentInvUISlot.Artifact01,
            ["Artifact2"] = EquipmentInvUISlot.Artifact02,
            ["Artifact3"] = EquipmentInvUISlot.Artifact03,
            ["Artifact4"] = EquipmentInvUISlot.Artifact04,
            ["Medal"]     = EquipmentInvUISlot.Medal,
            ["Relic"]     = EquipmentInvUISlot.Relic,
            ["UruForged"] = EquipmentInvUISlot.UruForged,
            ["Legendary"] = EquipmentInvUISlot.Legendary,
            ["Ring"]      = EquipmentInvUISlot.Ring,
            ["Insignia"]  = EquipmentInvUISlot.Insignia,
            ["Gear1"]     = EquipmentInvUISlot.Gear01,
            ["Gear2"]     = EquipmentInvUISlot.Gear02,
            ["Gear3"]     = EquipmentInvUISlot.Gear03,
            ["Gear4"]     = EquipmentInvUISlot.Gear04,
            ["Gear5"]     = EquipmentInvUISlot.Gear05,
        };

        // JSON shape: { "<AvatarLeaf>": { "slots": { "Artifact1": { "path":"Full/Proto/Path.prototype", "ref":"0x...", "name":... }, ... } } }
        // "path" is the primary, hand-editable form (full prototype path, exactly what build
        // guides and our own data use) - "ref" is a hex snapshot kept for a cheap integrity
        // check, not the source of truth. Older entries without "path" yet still resolve via
        // "ref" alone.
        private sealed class HeroEntry
        {
            public Dictionary<string, SlotEntry> slots { get; set; }
        }
        private sealed class SlotEntry
        {
            public string path { get; set; }
            public string @ref { get; set; }
            public string name { get; set; }
        }

        public static void EnsureLoaded()
        {
            if (s_byAvatar != null) return;
            lock (s_lock)
            {
                if (s_byAvatar != null) return;
                s_byAvatar = Load();
            }
        }

        private static Dictionary<PrototypeId, Dictionary<EquipmentInvUISlot, PrototypeId>> Load()
        {
            var result = new Dictionary<PrototypeId, Dictionary<EquipmentInvUISlot, PrototypeId>>();
            string path = Path.Combine(FileHelper.DataDirectory, "Game", "PhantomHeroes", "PhantomBiSGear.json");
            if (File.Exists(path) == false)
            {
                Logger.Warn($"[PhantomBiS] data file not found: {path} — level-60 BiS gear disabled");
                return result;
            }

            Dictionary<string, HeroEntry> raw;
            try
            {
                raw = FileHelper.DeserializeJson<Dictionary<string, HeroEntry>>(path);
            }
            catch (Exception ex)
            {
                Logger.Warn($"[PhantomBiS] parse failed: {ex.Message}");
                return result;
            }
            if (raw == null) return result;

            // Build avatar leaf -> ref index once.
            var leafToAvatarRef = new Dictionary<string, PrototypeId>(StringComparer.OrdinalIgnoreCase);
            foreach (PrototypeId avatarRef in DataDirectory.Instance
                .IteratePrototypesInHierarchy<AvatarPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                string name = GameDatabase.GetPrototypeName(avatarRef);
                if (string.IsNullOrEmpty(name)) continue;
                int slash = name.LastIndexOf('/');
                string leaf = slash >= 0 ? name[(slash + 1)..] : name;
                if (leaf.EndsWith(".prototype", StringComparison.OrdinalIgnoreCase))
                    leaf = leaf[..^".prototype".Length];
                leafToAvatarRef[leaf] = avatarRef;
            }

            int heroesResolved = 0, slotsResolved = 0, slotsMissed = 0;
            foreach (var kvp in raw)
            {
                string leaf = kvp.Key;
                if (leaf.StartsWith("_", StringComparison.Ordinal)) continue; // metadata keys (e.g. "_credit"), not a hero leaf
                if (leafToAvatarRef.TryGetValue(leaf, out PrototypeId avatarRef) == false)
                {
                    Logger.Warn($"[PhantomBiS] no avatar prototype for leaf '{leaf}'");
                    continue;
                }
                if (kvp.Value?.slots == null) continue;

                var slotMap = new Dictionary<EquipmentInvUISlot, PrototypeId>();
                foreach (var slotKvp in kvp.Value.slots)
                {
                    if (SlotNameToUI.TryGetValue(slotKvp.Key, out EquipmentInvUISlot uiSlot) == false)
                        continue;

                    // "path" (full prototype path) is the hand-editable source of truth;
                    // "ref" (hex) is only consulted for entries that predate the path field.
                    string entryPath = slotKvp.Value?.path;
                    PrototypeId itemRef = string.IsNullOrEmpty(entryPath) == false
                        ? GameDatabase.GetPrototypeRefByName(entryPath)
                        : ParseHexRef(slotKvp.Value?.@ref);
                    if (itemRef == PrototypeId.Invalid || itemRef.As<ItemPrototype>() == null)
                    {
                        slotsMissed++;
                        continue;
                    }
                    slotMap[uiSlot] = itemRef;
                    slotsResolved++;
                }
                if (slotMap.Count > 0)
                {
                    result[avatarRef] = slotMap;
                    heroesResolved++;
                }
            }

            Logger.Info($"[PhantomBiS] loaded BiS gear for {heroesResolved} heroes ({slotsResolved} slots resolved, {slotsMissed} skipped)");
            return result;
        }

        private static PrototypeId ParseHexRef(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return PrototypeId.Invalid;
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out ulong val)
                ? (PrototypeId)val : PrototypeId.Invalid;
        }

        /// <summary>
        /// Resolve a curated BiS loadout for the avatar, if one exists.
        /// <paramref name="slots"/> maps each covered equip slot to the
        /// chosen item ref. Returns false if the hero isn't in the curated
        /// file at all - callers should fall back to the normal restricted
        /// random-roll gear path in that case, not leave slots empty.
        /// </summary>
        public static bool TryGetLoadout(PrototypeId avatarRef, out Dictionary<EquipmentInvUISlot, PrototypeId> slots)
        {
            EnsureLoaded();
            return s_byAvatar.TryGetValue(avatarRef, out slots);
        }
    }
}
