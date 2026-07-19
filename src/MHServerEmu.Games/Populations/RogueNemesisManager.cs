using System;
using System.Collections.Generic;
using System.Linq;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.System.Time;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Entities.IncursionEntity;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.Regions;
using MHServerEmu.Games.RoguesGallery;

namespace MHServerEmu.Games.Populations
{
    /// <summary>
    /// Rogue Encounter / Nemesis system: a per-player, autonomous, always-running-in-the-background
    /// counterpart to Incursion. Where Incursion is one admin-triggered hunt per patrol-zone
    /// instance, this periodically ambushes opted-in players anywhere (except hubs/excluded zones)
    /// with villains drawn from their current avatar's Rogues Gallery pool. Deliberately kept
    /// fully independent of IncursionManager (own tick, own eligibility, own cooldowns) while
    /// reusing its combat engine via <see cref="IncursionManager.SpawnRogueNemesisInvader"/> - see
    /// the design plan for the "mingle without duplicating hostile-AI combat logic" rationale.
    ///
    /// Persists opt-in, cooldown, active-hunt-in-progress, and per-villain Nemesis rank onto
    /// Player.RogueNemesisData (file-per-player) so all of it survives Game-instance hops and
    /// server restarts - see RunRogueNemesisWave for the cross-Game-hop resume/drop logic and
    /// RecordNemesisWin/RecordNemesisLoss for the rank bookkeeping.
    /// </summary>
    public class RogueNemesisManager
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly Game _game;
        private readonly EventGroup _pendingEvents = new();
        private readonly EventPointer<RogueNemesisTickEvent> _tickEvent = new();

        // This server provisions a separate Game instance per region instance (see
        // WorldManager/GameHandle) - a player's session routinely hops between several Game
        // objects over time, each running its own RogueNemesisManager. Opt-in preference and the
        // post-encounter cooldown are real player state that must survive those hops (and a
        // server restart), so they live in Player.RogueNemesisData (file-per-player on disk,
        // loaded fresh whenever a Player enters ANY Game instance - see Player.EnterGame),
        // NOT in dictionaries here. What's left here is purely this Game instance's own
        // in-flight bookkeeping, which is fine to lose if a player's session moves to a
        // different Game - the encounter just ends there and, if still opted in, can pick back
        // up wherever they land next.

        // Live invader entity ids currently assigned to each player's active encounter.
        private readonly Dictionary<ulong, List<ulong>> _activeEncounters = new();

        // Players with a follow-respawn scheduled (RogueNemesisFollowDelayMs after they changed
        // to a new non-safe zone). Counts as "has an active encounter" for eligibility purposes
        // so the normal wave roll and the post-encounter cooldown don't kick in during the gap
        // between the old body vanishing and the new one catching up.
        private readonly HashSet<ulong> _pendingFollowDbGuids = new();

        public RogueNemesisManager(Game game)
        {
            _game = game;
        }

        public void Initialize()
        {
            ScheduleNextTick();
            LogInfo($"[RogueNemesis] Initialize: enabled={_game.CustomGameOptions.RogueNemesisEnable}, " +
                        $"checkIntervalMs={GetIntervalMs()}, rollChance={_game.CustomGameOptions.RogueNemesisRollChance:P0}, " +
                        $"cooldownMs={_game.CustomGameOptions.RogueNemesisCooldownMs}, maxSpawns={_game.CustomGameOptions.RogueNemesisMaxSpawns}.");
        }

        public void Shutdown()
        {
            _game.GameEventScheduler?.CancelAllEvents(_pendingEvents);
            _activeEncounters.Clear();
            _pendingFollowDbGuids.Clear();
        }

        /// <summary>
        /// True if this player has a genuinely live encounter or a follow-respawn pending. Checks
        /// liveness on demand via IncursionManager rather than trusting _activeEncounters' cached
        /// list non-emptiness - that list is only trimmed once per tick (RogueNemesisCheckIntervalMs)
        /// by PruneFinishedEncounters, so right after a kill it can still list the now-dead entity
        /// id for up to a full interval, incorrectly blocking !rogue forcespawn (and the normal
        /// wave roll) until the next scheduled prune catches up.
        /// </summary>
        public bool HasActiveEncounter(ulong dbGuid)
        {
            if (_pendingFollowDbGuids.Contains(dbGuid))
                return true;

            if (_activeEncounters.TryGetValue(dbGuid, out List<ulong> ids) == false)
                return false;

            foreach (ulong entityId in ids)
                if (_game.IncursionManager.IsIncursionEntity(entityId))
                    return true;

            return false;
        }

        private int GetIntervalMs() => Math.Max(1000, _game.CustomGameOptions.RogueNemesisCheckIntervalMs);

        private void ScheduleNextTick()
        {
            var scheduler = _game.GameEventScheduler;
            if (scheduler == null)
            {
                Logger.Warn("[RogueNemesis] ScheduleNextTick: scheduler is null.");
                return;
            }

            if (_tickEvent.IsValid)
                return;

            scheduler.ScheduleEvent(_tickEvent, TimeSpan.FromMilliseconds(GetIntervalMs()), _pendingEvents);
            _tickEvent.Get().Initialize(this);
        }

        private void OnTick()
        {
            PruneFinishedEncounters();

            if (_game.CustomGameOptions.RogueNemesisEnable)
                RunRogueNemesisWave();
            else
                LogVerbose("[RogueNemesis] Tick fired but the feature is disabled; idling.");

            // Continue ticking so re-enable does not need a restart.
            ScheduleNextTick();
        }

        /// <summary>
        /// Clears entity ids that have died/despawned from each player's active-encounter list.
        /// Once a player's list goes empty, starts their post-encounter cooldown - unless a
        /// follow-respawn is pending for them, in which case OnFollowSpawnDue owns that decision
        /// once it actually resolves (a region-change mid-hunt isn't a finished encounter).
        /// </summary>
        private void PruneFinishedEncounters()
        {
            List<ulong> emptiedPlayerIds = null;

            foreach (var kvp in _activeEncounters)
            {
                if (_pendingFollowDbGuids.Contains(kvp.Key)) continue;

                List<ulong> entityIds = kvp.Value;
                for (int i = entityIds.Count - 1; i >= 0; i--)
                {
                    if (_game.IncursionManager.IsIncursionEntity(entityIds[i]) == false)
                        entityIds.RemoveAt(i);
                }

                if (entityIds.Count == 0)
                    (emptiedPlayerIds ??= new()).Add(kvp.Key);
            }

            if (emptiedPlayerIds == null) return;

            TimeSpan cooldown = TimeSpan.FromMilliseconds(Math.Max(0, _game.CustomGameOptions.RogueNemesisCooldownMs));
            foreach (ulong dbGuid in emptiedPlayerIds)
            {
                _activeEncounters.Remove(dbGuid);
                StartCooldown(dbGuid, cooldown);
            }
        }

        /// <summary>
        /// Persists the post-encounter cooldown onto the player's own data (wall-clock based, so
        /// it means the same thing regardless of which Game instance later checks it). No-ops
        /// silently if the player isn't currently loaded in this Game instance's EntityManager -
        /// their data will simply not show a cooldown until they take another RogueNemesis action,
        /// which is an acceptable minor gap for a player who already left before this resolved.
        /// </summary>
        private void StartCooldown(ulong dbGuid, TimeSpan cooldown)
        {
            Player player = _game.EntityManager.GetEntityByDbGuid<Player>(dbGuid);
            if (player == null) return;

            // Every call site of StartCooldown marks a hunt that has genuinely concluded (killed,
            // died, reached a safe zone, or a follow-respawn giving up) - clear the persisted
            // "hunt in progress" marker here too so a later Game instance doesn't mistake a
            // long-finished hunt for one still needing to be resumed.
            player.RogueNemesisData.ActiveEnemyShorthand = null;
            player.RogueNemesisData.SetCooldown(cooldown);
            RogueNemesisPlayerDataStorage.Save(dbGuid, player.RogueNemesisData);
        }

        private void RunRogueNemesisWave()
        {
            float rollChance = Math.Clamp(_game.CustomGameOptions.RogueNemesisRollChance, 0f, 1f);

            foreach (Player player in _game.EntityManager.Players)
            {
                if (player == null || player.RogueNemesisData.Enabled == false) continue;
                if (player.IsSwitchingAvatar) continue;

                Avatar avatar = player.CurrentAvatar;
                if (avatar == null || avatar.IsAliveInWorld == false) continue;

                // Same level floor as base Incursion (IncursionManager.RunIncursionWave) - a
                // level 1-29 avatar has nowhere near the health pool this system is tuned against.
                if (avatar.CharacterLevel < 30) continue;

                Region region = avatar.Region;
                if (region == null) continue;

                ulong dbGuid = player.DatabaseUniqueId;

                // A hunt still shows a villain shorthand persisted, but THIS Game instance has no
                // record of it (fresh _activeEncounters/_pendingFollowDbGuids) - the player's
                // session crossed into a different Game instance mid-hunt (this server provisions
                // a separate Game per region instance) and the old invader has no way to detect
                // that: its Game.EntityManager can no longer resolve this player's entity at all,
                // so it just waits forever rather than noticing "target changed region" the way it
                // would for an in-Game transfer. Treat first contact here exactly like an in-Game
                // region change - follow after the same delay, or drop it if they landed
                // somewhere safe. See HandleTargetChangedRegion for the in-Game equivalent.
                if (HasActiveEncounter(dbGuid) == false && string.IsNullOrEmpty(player.RogueNemesisData.ActiveEnemyShorthand) == false)
                {
                    string pursuingShorthand = player.RogueNemesisData.ActiveEnemyShorthand;
                    bool isSafeZone = IncursionManager.IsHubRegion(region) || IsExcludedRegion(region);

                    if (isSafeZone)
                    {
                        LogInfo($"[RogueNemesis] '{pursuingShorthand}' hunt for '{player.GetName()}' dropped: landed in a safe zone " +
                                    $"('{region.PrototypeName}') on a new Game instance.");
                        StartCooldown(dbGuid, TimeSpan.FromMilliseconds(Math.Max(0, _game.CustomGameOptions.RogueNemesisCooldownMs)));
                    }
                    else
                    {
                        LogInfo($"[RogueNemesis] '{pursuingShorthand}' hunt for '{player.GetName()}' resuming after a Game-instance " +
                                    $"hop - following in {_game.CustomGameOptions.RogueNemesisFollowDelayMs}ms.");
                        ScheduleFollowSpawn(dbGuid, pursuingShorthand);
                    }

                    continue;
                }

                if (IncursionManager.IsHubRegion(region) || IsExcludedRegion(region)) continue;

                if (HasActiveEncounter(dbGuid)) continue;

                if (player.RogueNemesisData.IsOnCooldown) continue;

                if (rollChance <= 0f) continue;

                if (_game.Random.NextFloat() > rollChance) continue;

                TrySpawnEncounter(player, avatar, region);
            }
        }

        /// <summary>
        /// Comma-separated substrings matched against the region's full prototype path
        /// (RogueNemesisExcludedRegions config) - the "not in raids" half of "everywhere but
        /// hubs and raids." Hubs are always excluded regardless (see IncursionManager.IsHubRegion);
        /// this is for additional named zones (raid instances, etc.) an admin wants off-limits.
        /// </summary>
        private bool IsExcludedRegion(Region region)
        {
            if (region?.Prototype == null) return false;

            string patterns = _game.CustomGameOptions?.RogueNemesisExcludedRegions;
            if (string.IsNullOrWhiteSpace(patterns)) return false;

            string name = GameDatabase.GetPrototypeName(region.PrototypeDataRef);
            if (string.IsNullOrEmpty(name)) return false;

            foreach (string pattern in patterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private void TrySpawnEncounter(Player player, Avatar avatar, Region region)
        {
            if (IncursionManager.TryGetShorthandForAvatarPrototype(avatar.PrototypeDataRef, out string avatarShorthand) == false)
            {
                LogVerbose($"[RogueNemesis] TrySpawnEncounter: no incursion enemy type matches avatar " +
                            $"'{GameDatabase.GetPrototypeName(avatar.PrototypeDataRef)}' for player '{player.GetName()}' - skipping.");
                return;
            }

            RoguesGalleryDatabase db = RoguesGalleryDatabase.Instance;
            bool villainFlavored = db.IsVillainFlavored(avatarShorthand);
            (IReadOnlyList<string> pool, IReadOnlySet<string> curatedRogues) = villainFlavored
                ? db.GetHeroHunterPool(avatarShorthand)
                : db.GetRoguePoolForAvatar(avatarShorthand);
            if (pool.Count == 0)
            {
                LogVerbose($"[RogueNemesis] TrySpawnEncounter: resolved pool is empty for avatar '{avatarShorthand}' " +
                            $"(villainFlavored={villainFlavored}) - skipping.");
                return;
            }

            // Rank-5 defeat cooldown only ever blocks the natural ambush roll - '!rogue forcespawn'
            // never consults this pool, so admin testing is unaffected by design.
            pool = pool.Where(shorthand => player.RogueNemesisData.FindNemesisEntry(shorthand)?.IsOnTier5DefeatCooldown != true).ToList();
            if (pool.Count == 0)
            {
                LogVerbose($"[RogueNemesis] TrySpawnEncounter: every villain in the pool for avatar '{avatarShorthand}' " +
                            $"is on rank-5 defeat cooldown for '{player.GetName()}' - skipping.");
                return;
            }

            int maxSpawns = Math.Max(1, _game.CustomGameOptions.RogueNemesisMaxSpawns);
            int spawnCount = Math.Min(maxSpawns, pool.Count);

            // Distinct picks, no repeats within a single encounter - weighted so an active
            // (undefeated) Nemesis is more likely to come back than a villain with no history,
            // and so a curated rivalry (e.g. Thor -> Loki) is favored over the rest of the
            // fallback pool without excluding it entirely (see RogueNemesisCuratedRogueWeightShare).
            int levelRankCap = RogueNemesisTierDatabase.GetLevelRankCap(avatar.CharacterLevel);
            List<string> picks = PickWeightedDistinct(pool, curatedRogues, spawnCount, player.RogueNemesisData, levelRankCap);

            List<ulong> spawnedEntityIds = new();
            for (int i = 0; i < picks.Count; i++)
            {
                var (entity, error) = _game.IncursionManager.SpawnRogueNemesisInvader(avatar, picks[i], player.Id);
                if (entity == null)
                {
                    Logger.Warn($"[RogueNemesis] TrySpawnEncounter: spawn failed for '{picks[i]}' targeting '{player.GetName()}': {error}");
                    continue;
                }
                spawnedEntityIds.Add(entity.Id);
            }

            if (spawnedEntityIds.Count > 0)
            {
                _activeEncounters[player.DatabaseUniqueId] = spawnedEntityIds;

                // Persisted so a later Game instance (the player's session hopping regions) can
                // tell an unresolved hunt exists and resume it via the follow mechanism instead of
                // rolling a brand new encounter - see RunRogueNemesisWave.
                player.RogueNemesisData.ActiveEnemyShorthand = picks[0];
                RogueNemesisPlayerDataStorage.Save(player.DatabaseUniqueId, player.RogueNemesisData);

                LogInfo($"[RogueNemesis] Encounter started for '{player.GetName()}' (avatar={avatarShorthand}, " +
                            $"villainFlavored={villainFlavored}) in '{region.PrototypeName}': {string.Join(", ", picks)}.");
            }
        }

        /// <summary>
        /// Admin/testing entry point (see '!rogue forcespawn') to trigger an encounter immediately
        /// rather than waiting on the tick/roll timers. Still respects the safe-zone check and
        /// won't double-spawn on top of an existing encounter, but bypasses roll chance, the
        /// regular post-encounter cooldown, AND the rank-5 defeat cooldown (RecordNemesisWin /
        /// RogueNemesisTier5DefeatCooldown* config) - none of that is consulted here, only by the
        /// natural-roll path in TrySpawnEncounter, so admin testing is never rate-limited by it.
        /// An explicit shorthand skips pool resolution entirely and spawns that exact villain,
        /// which also lets an admin verify a specific entry without gaming the weighted pick.
        /// Returns a human-readable result for the command to relay.
        /// </summary>
        public string ForceSpawnEncounter(Player player, string villainShorthandOverride)
        {
            if (player == null) return "Player not found.";

            Avatar avatar = player.CurrentAvatar;
            if (avatar == null || avatar.IsAliveInWorld == false)
                return "Avatar is not alive in world.";

            Region region = avatar.Region;
            if (region == null) return "Avatar has no region.";

            if (IncursionManager.IsHubRegion(region) || IsExcludedRegion(region))
                return "Cannot force-spawn a Rogue Encounter in a hub/excluded region.";

            ulong dbGuid = player.DatabaseUniqueId;
            if (HasActiveEncounter(dbGuid))
                return "This player already has an active Rogue Encounter.";

            if (string.IsNullOrEmpty(villainShorthandOverride) == false)
            {
                var (entity, error) = _game.IncursionManager.SpawnRogueNemesisInvader(avatar, villainShorthandOverride, player.Id);
                if (entity == null)
                    return $"Spawn failed: {error}";

                _activeEncounters[dbGuid] = new List<ulong> { entity.Id };
                player.RogueNemesisData.ActiveEnemyShorthand = villainShorthandOverride;
                RogueNemesisPlayerDataStorage.Save(dbGuid, player.RogueNemesisData);

                LogInfo($"[RogueNemesis] Force-spawned '{villainShorthandOverride}' for '{player.GetName()}' in '{region.PrototypeName}' (admin).");
                return $"Spawned '{villainShorthandOverride}' near '{player.GetName()}'.";
            }

            TrySpawnEncounter(player, avatar, region);
            return HasActiveEncounter(dbGuid)
                ? "Spawn triggered."
                : "Spawn attempt failed - no incursion enemy matches this avatar, resolved pool is empty, or no open spawn position was found (see server log).";
        }

        /// <summary>
        /// Picks up to <paramref name="count"/> distinct villains from <paramref name="pool"/>
        /// without replacement, weighted so a villain with a standing Nemesis rank pulls extra
        /// weight proportional to that rank (RogueNemesisRankWeightMultiplier per rank) - a
        /// villain with no history (or one currently sitting at rank 0) gets no bonus.
        /// <paramref name="levelRankCap"/> (see
        /// <see cref="RogueNemesisTierDatabase.GetLevelRankCap"/>) clamps the rank used for
        /// weighting so a low-level avatar isn't disproportionately biased toward a villain whose
        /// persisted rank is higher than what that avatar's level is allowed to actually fight.
        /// </summary>
        private List<string> PickWeightedDistinct(IReadOnlyList<string> pool, IReadOnlySet<string> curatedRogues, int count, RogueNemesisPlayerData data, int levelRankCap)
        {
            float rankWeight = Math.Max(0f, _game.CustomGameOptions.RogueNemesisRankWeightMultiplier);
            float curatedBias = ComputeCuratedBias(pool, curatedRogues);

            List<string> remaining = new(pool);
            List<float> weights = new(remaining.Count);
            foreach (string shorthand in remaining)
            {
                NemesisEntry entry = data.FindNemesisEntry(shorthand);
                int rank = Math.Min(entry?.Rank ?? 0, levelRankCap);
                float weight = 1f + rank * rankWeight;
                if (curatedRogues.Contains(shorthand))
                    weight *= curatedBias;
                weights.Add(weight);
            }

            List<string> result = new();
            for (int picked = 0; picked < count && remaining.Count > 0; picked++)
            {
                float totalWeight = 0f;
                foreach (float w in weights) totalWeight += w;

                float roll = _game.Random.NextFloat() * totalWeight;
                int chosenIndex = remaining.Count - 1;
                float cumulative = 0f;
                for (int i = 0; i < remaining.Count; i++)
                {
                    cumulative += weights[i];
                    if (roll <= cumulative)
                    {
                        chosenIndex = i;
                        break;
                    }
                }

                result.Add(remaining[chosenIndex]);
                remaining.RemoveAt(chosenIndex);
                weights.RemoveAt(chosenIndex);
            }

            return result;
        }

        /// <summary>
        /// Solves the per-item weight multiplier applied to curated rogues (RoguesGallery.json's
        /// heroRogues/villainHunters) so that, before any Nemesis rank-history weighting is
        /// applied, the curated entries collectively account for RogueNemesisCuratedRogueWeightShare
        /// of total pick weight and the rest of the pool splits the remainder evenly. Returns 1
        /// (no bias) when there's no curated entry for this avatar, or when curated somehow covers
        /// the entire pool - both cases where a bias multiplier would be meaningless.
        /// </summary>
        private float ComputeCuratedBias(IReadOnlyList<string> pool, IReadOnlySet<string> curatedRogues)
        {
            int curatedCount = curatedRogues.Count;
            if (curatedCount == 0 || curatedCount >= pool.Count)
                return 1f;

            float share = Math.Clamp(_game.CustomGameOptions.RogueNemesisCuratedRogueWeightShare, 0f, 1f);
            if (share <= 0f) return 0f;
            if (share >= 1f) return 1_000_000f; // effectively "curated only", without risking a float-overflow sum below

            int fallbackOnlyCount = pool.Count - curatedCount;
            return (share * fallbackOnlyCount) / ((1f - share) * curatedCount);
        }

        /// <summary>
        /// Called when a RogueNemesis invader's assigned target has died while the hunt was
        /// still active - a loss for the player. The villain has gotten their revenge, but only
        /// knocks the vendetta down one rank rather than settling it entirely - a player stuck on
        /// a rank they can't beat yet doesn't have to re-grind every rank below it, just close the
        /// one-rank gap again. See RecordNemesisWin for the matching climb-by-one-on-win side.
        /// </summary>
        public void RecordNemesisLoss(Player player, string enemyShorthand)
        {
            if (player == null || string.IsNullOrEmpty(enemyShorthand)) return;

            NemesisEntry entry = player.RogueNemesisData.GetOrAddNemesisEntry(enemyShorthand);
            entry.Rank = Math.Max(0, entry.Rank - 1);
            RogueNemesisPlayerDataStorage.Save(player.DatabaseUniqueId, player.RogueNemesisData);

            LogInfo($"[RogueNemesis] '{enemyShorthand}' defeated '{player.GetName()}' - Nemesis rank dropped to {entry.Rank}.");
        }

        /// <summary>
        /// Called when a RogueNemesis invader actually dies in combat (see the death branch in
        /// IncursionEnemyController.Think) - a win for the player. Bumps that villain's Nemesis
        /// rank (capped at 5): a villain isn't a "nemesis" after one fight, but the more times the
        /// player beats them, the more obsessed they become with taking the player down.
        /// </summary>
        public void RecordNemesisWin(Player player, string enemyShorthand)
        {
            if (player == null || string.IsNullOrEmpty(enemyShorthand)) return;

            NemesisEntry entry = player.RogueNemesisData.GetOrAddNemesisEntry(enemyShorthand);

            // Capture BEFORE the increment - this is the rank of the villain actually just fought.
            // Rank is a spawn-time snapshot (IncursionEnemyController.ResolveNemesisRank), so the
            // kill that brings an entry from 4 to 5 fought a rank-4 villain, not a rank-5 one - the
            // cooldown must only trigger when the opponent itself was already rank 5.
            bool defeatedRank5 = entry.Rank == 5;
            entry.Rank = Math.Min(5, entry.Rank + 1);

            if (defeatedRank5 && _game.CustomGameOptions.RogueNemesisTier5DefeatCooldownEnable)
            {
                int resetHour = Math.Clamp(_game.CustomGameOptions.RogueNemesisTier5DefeatCooldownResetHour, 0, 23);
                entry.Tier5DefeatCooldownUntilUnixTimeMs = ComputeNextDailyResetUnixTimeMs(resetHour);
                LogInfo($"[RogueNemesis] '{player.GetName()}' defeated rank-5 '{enemyShorthand}' - on cooldown for this " +
                            $"player until the next {resetHour:00}:00 server-time reset.");
            }

            RogueNemesisPlayerDataStorage.Save(player.DatabaseUniqueId, player.RogueNemesisData);

            LogInfo($"[RogueNemesis] '{player.GetName()}' defeated '{enemyShorthand}' - Nemesis rank now {entry.Rank}.");
        }

        /// <summary>
        /// Next wall-clock Unix milliseconds the local server clock crosses <paramref name="resetHourLocal"/>
        /// (0-23) - "today at that hour" if it hasn't happened yet today, otherwise "tomorrow at
        /// that hour". Used for the rank-5 Nemesis defeat cooldown so it resets at a fixed time of
        /// day (e.g. 6 AM) rather than a rolling 24h window from the moment of the kill.
        /// </summary>
        private static long ComputeNextDailyResetUnixTimeMs(int resetHourLocal)
        {
            DateTime nowLocal = Clock.UtcNowPrecise.ToLocalTime();
            DateTime resetToday = new(nowLocal.Year, nowLocal.Month, nowLocal.Day, resetHourLocal, 0, 0, DateTimeKind.Local);
            DateTime nextReset = nowLocal < resetToday ? resetToday : resetToday.AddDays(1);
            return (long)Clock.DateTimeToUnixTime(nextReset.ToUniversalTime()).TotalMilliseconds;
        }

        /// <summary>
        /// Admin/testing entry point (see '!rogue setrank') to directly set a villain's Nemesis
        /// rank against a player, bypassing the normal win/loss grind. Saves immediately so the
        /// new rank is picked up by the very next spawn (forced or natural) without a relog -
        /// unlike hand-editing the player's RogueNemesisPlayers/*.json file, which would just get
        /// clobbered by the next in-memory Save() and isn't picked up until reconnect anyway.
        /// </summary>
        public string SetRank(Player player, string enemyShorthand, int rank)
        {
            if (player == null) return "Player not found.";
            if (string.IsNullOrEmpty(enemyShorthand)) return "No villain shorthand given.";
            if (rank < 0 || rank > 5) return "Rank must be 0-5.";

            if (IncursionManager.TryResolveEnemyFactoryByShorthand(enemyShorthand, out _) == false)
                return $"No incursion enemy matches shorthand '{enemyShorthand}'.";

            NemesisEntry entry = player.RogueNemesisData.GetOrAddNemesisEntry(enemyShorthand);
            entry.Rank = rank;
            RogueNemesisPlayerDataStorage.Save(player.DatabaseUniqueId, player.RogueNemesisData);

            LogInfo($"[RogueNemesis] '{player.GetName()}' set '{enemyShorthand}' Nemesis rank to {rank} (admin).");
            return $"Set '{enemyShorthand}' Nemesis rank to {rank} for '{player.GetName()}'.";
        }

        /// <summary>
        /// Called by an active invader's controller (via IncursionEnemyController.Think) when it
        /// notices its assigned target is alive but has moved to a different region. RogueNemesis
        /// invaders are a personal nuisance, not zone-bound like Incursion: this either follows
        /// the target into their new region after a delay (a fresh body of the same villain, since
        /// a live entity can't just move between separate Region instances) or ends the hunt if
        /// the new region is a hub or on the RogueNemesisExcludedRegions blacklist - the only two
        /// "safe zone" escapes, alongside killing the invader or dying to it.
        /// </summary>
        public void HandleTargetChangedRegion(IncursionEnemyController controller, Player targetPlayer, Avatar targetAvatar)
        {
            Region newRegion = targetAvatar.Region;
            bool isSafeZone = newRegion == null || IncursionManager.IsHubRegion(newRegion) || IsExcludedRegion(newRegion);

            if (isSafeZone)
            {
                LogInfo($"[RogueNemesis] '{controller.EnemyShorthand}' ended hunt: '{targetPlayer.GetName()}' reached a safe zone ('{newRegion?.PrototypeName ?? "(no region)"}').");
                _game.IncursionManager.RequestRemoval(controller, "target reached a safe zone (hub/blacklisted region)");
                return;
            }

            // The old body vanishes right away, but the replacement doesn't appear until
            // RogueNemesisFollowDelayMs later - a "catching up" window rather than an instant
            // teleport, and a bit of a head start for the player. ScheduleFollowSpawn keeps this
            // player's encounter marked active for the whole gap so the normal wave roll and the
            // post-encounter cooldown don't fire in between.
            ulong dbGuid = targetPlayer.DatabaseUniqueId;
            ScheduleFollowSpawn(dbGuid, controller.EnemyShorthand);

            LogInfo($"[RogueNemesis] '{controller.EnemyShorthand}' lost '{targetPlayer.GetName()}' to '{newRegion.PrototypeName}' - " +
                        $"following in {_game.CustomGameOptions.RogueNemesisFollowDelayMs}ms.");

            _game.IncursionManager.RequestRemoval(controller, "target changed zones - regrouping to follow");
        }

        private void ScheduleFollowSpawn(ulong dbGuid, string enemyShorthand)
        {
            var scheduler = _game.GameEventScheduler;
            if (scheduler == null)
            {
                Logger.Warn("[RogueNemesis] ScheduleFollowSpawn: scheduler is null - hunt ends here instead of following.");
                return;
            }

            _pendingFollowDbGuids.Add(dbGuid);

            int delayMs = Math.Max(0, _game.CustomGameOptions.RogueNemesisFollowDelayMs);
            var eventPointer = new EventPointer<RogueNemesisFollowSpawnEvent>();
            scheduler.ScheduleEvent(eventPointer, TimeSpan.FromMilliseconds(delayMs), _pendingEvents);
            eventPointer.Get().Initialize(this, (dbGuid, enemyShorthand));
        }

        /// <summary>
        /// Fires RogueNemesisFollowDelayMs after a hunted player changed zones. Re-validates
        /// everything from scratch (the player may have logged out, died, or moved again during
        /// the delay) rather than trusting the state captured when the follow was scheduled.
        /// </summary>
        private void OnFollowSpawnDue(ulong dbGuid, string enemyShorthand)
        {
            _pendingFollowDbGuids.Remove(dbGuid);

            Player player = _game.EntityManager.GetEntityByDbGuid<Player>(dbGuid);
            Avatar avatar = player?.CurrentAvatar;
            Region region = avatar?.Region;

            bool stillValid = player != null && avatar != null && avatar.IsAliveInWorld
                && region != null && IncursionManager.IsHubRegion(region) == false && IsExcludedRegion(region) == false;

            TimeSpan cooldown = TimeSpan.FromMilliseconds(Math.Max(0, _game.CustomGameOptions.RogueNemesisCooldownMs));

            if (stillValid == false)
            {
                LogInfo($"[RogueNemesis] OnFollowSpawnDue: '{enemyShorthand}' called off the follow for dbGuid={dbGuid:X} - " +
                            $"target is no longer a valid follow destination (offline/dead/safe zone).");
                StartCooldown(dbGuid, cooldown);
                return;
            }

            var (entity, error) = _game.IncursionManager.SpawnRogueNemesisInvader(avatar, enemyShorthand, player.Id, suppressAnnouncement: true);
            if (entity == null)
            {
                Logger.Warn($"[RogueNemesis] OnFollowSpawnDue: follow-respawn of '{enemyShorthand}' failed for " +
                            $"'{player.GetName()}' in '{region.PrototypeName}': {error}. Hunt ends here.");
                StartCooldown(dbGuid, cooldown);
                return;
            }

            if (_activeEncounters.TryGetValue(dbGuid, out List<ulong> ids) == false)
                _activeEncounters[dbGuid] = ids = new List<ulong>();
            ids.Add(entity.Id);

            _game.ChatManager?.SendChatFromCustomSystem(player, $"{enemyShorthand} has followed you!", showSender: false);

            LogInfo($"[RogueNemesis] '{enemyShorthand}' caught up to '{player.GetName()}' in '{region.PrototypeName}'.");
        }

        private bool IsLoggingEnabled => _game.CustomGameOptions?.RogueNemesisLoggingEnable ?? false;
        private bool IsVerboseLoggingEnabled => _game.CustomGameOptions?.RogueNemesisLogVerboseEnable ?? false;

        private void LogInfo(string message)
        {
            if (IsLoggingEnabled) Logger.Info(message);
        }

        private void LogVerbose(string message)
        {
            if (IsVerboseLoggingEnabled) Logger.Info(message);
        }

        private class RogueNemesisTickEvent : CallMethodEvent<RogueNemesisManager>
        {
            protected override CallbackDelegate GetCallback() => (manager) => manager.OnTick();
        }

        private class RogueNemesisFollowSpawnEvent : CallMethodEventParam1<RogueNemesisManager, (ulong DbGuid, string EnemyShorthand)>
        {
            protected override CallbackDelegate GetCallback() => (manager, param) => manager.OnFollowSpawnDue(param.DbGuid, param.EnemyShorthand);
        }
    }
}
