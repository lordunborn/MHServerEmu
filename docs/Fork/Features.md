# Fork Features

This repository's lineage is [Crypto137/MHServerEmu](https://github.com/Crypto137/MHServerEmu) → [mtzimas92/MHServerEmu](https://github.com/mtzimas92/MHServerEmu) → this fork. It stays synced with the original upstream project and carries custom, non-upstream additions from both this fork and mtzimas92's. See [CREDITS.md](../../CREDITS.md) for full attribution.

## Added in this fork

- **Phantom Heroes** — spawn server-side AI-controlled hero companions (`!phantom spawn`) that fight alongside a solo player as a synthetic party, up to 9 active phantoms per player (configurable). Includes idle-follow formation locomotion, level-scaled damage and gear (armor/artifact pool restrictions, a configurable bad-item blacklist, level-scaled relic stacks), ultimates and charge-release power support, death/despawn handling, and full party-HUD integration (health bars, leave/kick/disband/convert-to-raid). Based on and adapted from [TruSkillzzRuns's fork](https://github.com/TruSkillzzRuns/MHServerEmu).

- **Rogue Nemesis** — an opt-in (`!rogue enable`), per-player villain ambush system. Eligible players periodically get ambushed by a rogue drawn from a curated gallery of hero-to-villain matchups (weighted, with a fallback pool so it doesn't become fully predictable), which builds up a 0-5 Nemesis rank against you the more you fight it, with difficulty and loot quality scaling with rank. Rank 4/5 loot is gated behind your own damage share in the fight (so summoned companions can't farm top-tier rewards for you), and rank-5 kills are capped to once per day to prevent loot farming. Check your status with `!rogue status`. Based on and adapted from [TruSkillzzRuns's fork](https://github.com/TruSkillzzRuns/MHServerEmu).

- **Incursion** — an experimental, toggleable random invader system: a hostile impostor enemy (skinned as a random hero variant, drawn from a large roster) periodically hunts a player in eligible patrol zones. Configurable spawn cadence, damage/visual scaling, lifetime, and a per-region allow-list. Controlled via `!incursion start` / `stop` / `status`, off by default. Based on and adapted from [CorvaeOboro's fork](https://corvaeoboro.github.io/MHServerEmu/).

- **Server-Side Player Loot Filter** — players can configure filters for loot they don't want, and the server simply won't spawn those items for them. Based on [CorvaeOboro's fork](https://corvaeoboro.github.io/MHServerEmu/).

- **Item Auto-Pickup** — configurable server-side auto-pickup for currency, crafting ingredients, Runeword Glyphs, and Relics within a radius, with per-category stash-vs-inventory routing. Players can opt out of individual categories or override routing for themselves via `!autopickup`, independent of the server-wide defaults. Based on CorvaeOboro's fork.

- **Stash Affinity** — manually moving an item into a stash redirects it to the best-matching tab automatically (a character-specific stash for bound items, or a tab whose name matches the item's type) if one exists with space. Based on CorvaeOboro's fork.

- **Throwable Options** — server toggles to disable interactive throwable pickups, auto-cancel a held throwable when another power is used, and auto-throw a held object before a movement power activates. Based on CorvaeOboro's fork.

- **Item Chest Auto-Open** — automatically opens chest-type items in inventory on a cooldown, with a configurable name whitelist. Based on CorvaeOboro's fork.

- **`!stash` command overhaul** — collapsed from a command group with subcommands into a single, more flexible command with a sensible default action. Based on [mtzimas92's fork](https://github.com/mtzimas92/MHServerEmu).

- **Automated Live-Tuning-driven leaderboards** — leaderboards tied to a scheduled Live Tuning event now automatically activate and deactivate in sync with that event's active window, instead of requiring manual leaderboard-schedule edits. Based on work done by @Omega in the MHServerEmu Discord.

- **Changed `DayOfWeekRotation`** — changed LiveTuning event rotation to actually cycle weekly on the configured weekday, instead of firing all rotation events simultaneously on the month's 5th occurrence. Based on [mtzimas92's fork](https://github.com/mtzimas92/MHServerEmu).

- **Stackable loot boxes and Fortune Cards** — chests and cards like Midtown Madness Chests, Odin's Bounty, Worldstone Caches/Giftboxes, Reliquaries, and Fortune Cards can now stack in inventory instead of taking a separate slot each, with the level requirement removed so stacks aren't locked to the level they first dropped at. Each individual item opened from a stack still rolls its own independent, level-appropriate reward rather than repeating whatever the first one in the stack rolled. Based on a fix contributed by @sillyotter in the MHServerEmu Discord.

- **In-game news page** — the client's login news popup (and any other embedded-browser window) can point at server-hosted static content instead of an external URL, hot-reloadable via `!server reloadnews`.

- **Reorganized Avengers Tower** — see [below](#avengers-tower-overhaul) for the full writeup.

- **Custom gameplay data (Patches & LiveTuning)** — this fork ships with a substantial set of custom `Data/Game/Patches` JSON patches (custom loot tables, vendor overhauls, difficulty tuning, stackable-box wiring, and full standalone content like the **Dinos Invade Manhattan** wave-battle event) and `Data/Game/LiveTuning` event scheduling (weekly rotations, seasonal events, XP bonus weeks). These are active gameplay content, not just code, so building from source gets the same experience as the live server.

- **Stability fixes and admin tooling** — fixed a null-reference crash in `Teleporter.CanTeleport()` that could take down a whole game instance on the CH0906 Loki boss region, added admin debug commands that dump item, power, and orb prototype data to JSON (`!debug dumpitems` / `dumppower` / `listorbs`), tuned several server config defaults (Eternity Splinter cooldown/stacking, credit chest conversion, account binding off), and extended the Patch Manager to correctly handle nested arrays inside inline prototype patches (fixing a regression where a loot node's own nested `Choices[]`/`Modifiers[]` silently stopped applying).

## Inherited from mtzimas92's fork

- **Gift service** — delivers configured gifts to players when they log in, driven by simple JSON files in the server's `Data` folder: `PendingItems.json` for global gifts every player receives, and `PlayerSpecificItems.json` for gifts targeted to a specific account by email. Gifts can be one-time or daily-claimable, with optional availability windows, and claims are tracked per player so nothing is delivered twice.

- **`!commendations` command** — shows the player's Demonfire commendation drop progress (Hero and Protector commendation channels, current count and drops remaining).

- **`!ultron` command** — teleports the player directly into the Ultron Raid at Cosmic difficulty.

- **`!player bring` / `!player goto`** — admin commands to teleport another player to your location, or yourself to theirs.

- **Admin-only mission reset** — the mission reset command is restricted to admin accounts.

- **Patch Manager extensions** — support for array-valued and prototype-valued entries in JSON prototype patches (originally created by Doods). This powers several of this fork's own data patches, including the stackable-boxes patch.

## Shanna and the Dinos Invade Manhattan

**What it is:** A 7-wave survival event region — dinosaurs (raptors, pterosaurs, cliffwalkers) invade a slice of Manhattan, escalating in intensity, capped off with a boss fight against a randomly-picked King Lizard or King Lizard Rider in a purpose-built arena. A threat meter rises automatically over time (scaling with player count) and falls when you kill wave mobs or grab a periodic power-up orb (the orb is the main lever — a single grab offsets far more threat than kills alone). Let threat hit its cap and the run ends early instead of continuing to the boss.

**How to access:** Talk to Shanna, an NPC placed in Avengers Tower, and confirm the travel prompt she offers. The region is hard-locked to Tier 2 Heroic difficulty regardless of each player's own difficulty preference, so a party can't accidentally split across different tier instances on entry.

**Basic rules:**
- 7 wave phases, each with an opening burst plus a ramping spawn rate, followed by a boss phase with a 6-minute timer.
- Phantom Heroes and Rogue Nemesis are both excluded from this region — the wave-spawn pacing is tuned around real in-world player count, and summoned companions or ambush portals would throw that off.
- Boss loot is a dedicated 15-slot table: guaranteed Cosmic Artifact, XP orbs, Eternity Splinters, Hero/Protector Commendation boxes, a Six-Infinity-Orb chest, Relics, and armor, plus smaller independent chances at Costume Core, Team-Up gear, and a rare Costume/Card/Pet drop. The account-limited slots (Cosmic Artifact, Commendation boxes, Costume/Card/Pet) share one combined once-per-day clock per boss variant.

**How to edit the settings:**
- `Data/Game/Patches/PatchDataMod_Event_DinosInvadeManhattan.json` — region/portal/boss-timer/threat-pacing patches.
- `Data/Game/Patches/PatchDataMod_Loot_CustomPrizeTable.json` — the full boss loot table.
- `Data/Game/LiveTuning/LiveTuningData.json` — `Entity/Characters/NPCs/ShannaA.prototype` → `eWETV_Visible` controls whether Shanna is spawned at all.
- Wave-spawn density, threat rise/fall rates, and burst sizes are tuning constants in `src/MHServerEmu.Games/MetaGames/GameModes/PvEScaleGameMode.cs` — these need a rebuild to change, unlike the JSON patches above.
- `Config.ini` → `PhantomHeroesExcludedRegions` / `RogueNemesisExcludedRegions` — add or remove `DinosInvadeManhattan` here to control the companion/ambush blackout for this region specifically.

## Avengers Tower overhaul

**What it is:** The NPE-default Avengers Tower hub had run out of usable markers for teleporters and vendors, so the raid/patrol content moved into the game's original "classic" Tower cell instead (the one reached via `!tower`), which is a genuinely different, roomier cell with its own native NPC layout.

- **Raid/Patrol teleporters relocated** — Silver Sable (Age of Ultron), Domino (Axis), Valkyrie (Muspelheim/Surtur), Misty Knight (Midtown), Cloak (ICP), and Doctor Strange (Hightown) now stand in the classic Tower instead of the NPE hub, each replacing a redundant/inactive native NPC's marker there.
- **New "Ops Floor" access point** — a dormant leftover tutorial NPC ("S.H.I.E.L.D. Agent Justin Pitta", originally the old Bodyslider-tutorial guide) now stands in the NPE hub at the teleporters' old spot and offers a one-click trip straight to the classic Tower, so players don't need to know about the `!tower` command.
- **Recipe Vendor** — a new crafting-recipe vendor replacing both the old Raid Vendor (NPE hub) and an Adam Warlock placement (classic Tower), stocked with a curated 30-recipe list.
- **Hero Ticket Vendor** — redeems Free Hero Tickets for a character token (with a curated roster, Fantastic Four members swapped out). Free Hero Tickets themselves now have a rare (2%) chance to drop from the final boss of the Surtur, Onslaught, and Age of Ultron (Phase 4) raids, gated to Tier 3 Superheroic difficulty and above.
- **Iron Fist Skill Trainer relocated** — moved off a marker it was sharing with the native BuxGiver vendor, onto its own spot.

Relevant patch files: `PatchDataMod_Content_ClassicTowerTeleporters.json`, `PatchDataMod_Content_OpsFloorTeleporter.json`, `PatchDataMod_Vendors_NewVendorsAT.json` (Recipe Vendor), `PatchDataMod_Vendors_HeroTicketVendor.json`, `PatchDataMod_Loot_Raid_HeroTicketDrop.json`, `PatchDataMod_Vendors_NewCubeShardVendor.json` (Iron Fist).
