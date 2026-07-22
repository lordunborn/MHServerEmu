using System;
using System.Collections.Generic;
using MHServerEmu.Core.Collisions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.Serialization;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Items;
using MHServerEmu.Games.Entities.Locomotion;
using MHServerEmu.Games.Entities.PowerCollections;
using MHServerEmu.Games.Events;
using MHServerEmu.Games.Events.Templates;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Loot;
using MHServerEmu.Games.Navi;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Powers;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Entities.Avatars
{
    /// <summary>
    /// Phantom-hero spawning: creates a Player entity in this Game (no
    /// PlayerConnection, no DB persistence) that owns a real AvatarPrototype
    /// entity from the actual playable roster. Bypasses the login pipeline —
    /// the Player exists only inside this Game's EntityManager.
    ///
    /// Why: the engine's Avatar.ApplyInitialReplicationState hard-requires the
    /// EntitySettings.InventoryLocation.ContainerId to resolve to a Player
    /// entity (Verify.IsNotNull at Avatar.cs:150). Without a Player owner the
    /// Avatar refuses to spawn. Hero-shaped Agent variants (CivilWar bosses,
    /// Skrull-hero variants, etc.) skip that check because they're Agents, not
    /// Avatars — which is why the old bot pool used them and why the names
    /// came out as "Skrull Luke Cage" etc. This gives us the real 64 heroes.
    /// </summary>
    public partial class Avatar
    {
        private static readonly Logger PhantomLogger = LogManager.CreateLogger();

        // No hardcoded roster — the pool is built at first spawn by iterating the
        // client's actual AvatarPrototype hierarchy (NoAbstractApprovedOnly, i.e.
        // concrete + shipping-approved entries). This makes the mod version-
        // agnostic: whatever heroes the currently-loaded client data ships with
        // become spawn candidates automatically. See EnsureResolvedPool().

        private static readonly object s_phantomDeckLock = new();
        private static readonly List<int> s_phantomDeck = new();
        private static int s_phantomDeckIdx;
        private static ulong s_phantomDbIdSeed = 0xB07_FADED_0000_0001UL;

        // Ownership lists moved to Player (see Player.PhantomHero.cs). The
        // Avatar shell no longer owns anything — every operation delegates to
        // GetOwnerOfType<Player>() so avatar swaps and region hops don't
        // orphan phantoms. Left in place for source-compat: PhantomHeroCount
        // + the Ids reader now pull straight from the Player's list.
        private Player PhantomHost => GetOwnerOfType<Player>();
        public int PhantomHeroCount => PhantomHost?.PhantomHeroCount ?? 0;
        private IReadOnlyList<ulong> PhantomIds => PhantomHost?.PhantomAvatarIds ?? (IReadOnlyList<ulong>)System.Array.Empty<ulong>();

        // Comic-book flavored random usernames. Kept short so nameplates fit.
        private static readonly string[] s_phantomAdjectives =
            { "Crimson", "Cosmic", "Silent", "Void", "Neon", "Feral", "Prime", "Shadow", "Solar", "Astral", "Rogue", "Onyx", "Phoenix", "Nova", "Iron", "Storm", "Cyber", "Ghost", "Wraith", "Phantom" };
        private static readonly string[] s_phantomNouns =
            { "Falcon", "Warden", "Reaper", "Nomad", "Sentinel", "Vector", "Specter", "Vanguard", "Sable", "Pulse", "Envoy", "Titan", "Arbiter", "Herald", "Blade", "Fury", "Strike", "Guard", "Reign", "Shade" };
        private static int s_phantomNameCounter;

        private static string NewPhantomUsername(MHServerEmu.Core.System.Random.GRandom rng)
        {
            string a = s_phantomAdjectives[rng.Next(0, s_phantomAdjectives.Length)];
            string n = s_phantomNouns[rng.Next(0, s_phantomNouns.Length)];
            int suffix = System.Threading.Interlocked.Increment(ref s_phantomNameCounter) % 1000;
            return $"{a}{n}{suffix:D3}";
        }

        // Follow-tick constants + scheduler state. Phantoms follow the caller via
        // Locomotor.FollowEntity when idle (see PhantomIdleFollowStopDist below)
        // and chase/attack hostiles when one's in range. The distance leash here
        // is a safety net for pathing failures (stuck/wall-clipped), not the
        // primary follow mechanism: every ~1s, if a phantom is still more than
        // 1500u from the caller despite trying to walk back, we teleport it with
        // a random offset so multiple phantoms spread out.
        // Tighter than the old 2500u — phantoms should read as "with you"
        // not "vaguely nearby." 1500u ≈ two-thirds of a screen at default
        // zoom; if they wander beyond that the leash snaps them back.
        private const float PhantomFollowMaxDistSq = 1500f * 1500f;
        // Hunting is bounded to inside the leash radius (with a safety
        // margin), not just PhantomSearchRange from the phantom's own
        // position. Without this, a phantom sitting near the caller could
        // spot a hostile up to PhantomSearchRange (3500u) away, start
        // chasing it, immediately blow past the 1500u leash, get teleported
        // back by OnPhantomTick's leash check, then re-detect the same
        // still-distant hostile and run out again — the "runs off screen
        // and snaps back" loop. Tighter than the leash itself so there's
        // room to actually fight (kite/reposition) without tripping it
        // mid-combat.
        private const float PhantomHuntMaxCallerDist = 1300f;
        private const float PhantomHuntMaxCallerDistSq = PhantomHuntMaxCallerDist * PhantomHuntMaxCallerDist;
        // Idle companion-follow distance: when a phantom has no hostile to hunt
        // and no downed ally to revive, it walks toward the caller instead of
        // just standing still, stopping once within this range — reads as a
        // continuously-following companion instead of "teleport when far,
        // stand still otherwise." The 1500u leash above stays as a safety net
        // for pathing failures (stuck/wall-clipped), not the primary follow
        // mechanism. Also doubles as the ring radius for ComputePhantomFormationSlot.
        private const float PhantomIdleFollowStopDist = 200f;
        // How close a phantom needs to be to its assigned formation slot
        // before it stops walking. Smaller than PhantomIdleFollowStopDist —
        // this is a "close enough, stop fidgeting" tolerance around the slot
        // itself, not the distance from the caller. Widened from 60u so
        // phantoms settle and hold instead of micro-correcting every tick.
        private const float PhantomFormationArriveDist = 120f;
        // Stuck detection: if the phantom's position barely changes across
        // this many ticks (500ms each) they're either wall-clipped or
        // pathed into an out-of-bounds corner — force a teleport back to
        // caller.
        private const int PhantomStuckTickThreshold = 4;      // 2 seconds
        private const float PhantomStuckMoveEpsilonSq = 40f * 40f;
        private static readonly Dictionary<ulong, (Vector3 lastPos, int stuckTicks)> s_phantomStuckTrack = new();

        // Power-stuck recovery: a power whose activation phase never returns
        // to Inactive on its own (TwoStageTargeted waiting on a second confirm,
        // a channel waiting on a release — anything filtered out of
        // TryPhantomAttack's candidate pool by design, but a phantom can still
        // end up here via a proc, an AI-granted power, or an engine edge case)
        // rejects every future ActivatePower call, so the phantom looks
        // permanently "skill locked" mid-animation. Track how many
        // consecutive ticks the SAME power has stayed active; after a few
        // seconds with no progress, force-end it so the phantom can act again.
        // 10 ticks (5s) matches the upstream fork's own watchdog, tuned from
        // live-session log analysis — bumped from our original 3s guess.
        private const int PhantomPowerStuckTickThreshold = 10; // 5 seconds
        private static readonly Dictionary<ulong, (PrototypeId powerRef, int stuckTicks)> s_phantomPowerStuckTrack = new();

        // Tracks phantoms currently sitting in the downed grace period (see
        // HandlePhantomDeath). Presence in this dict marks "was down as of
        // the last tick I noticed"; OnPhantomTick's alive-path uses removing
        // an entry (IsDead flipped back to false) to detect a fresh revive
        // and run the stuck-power-clear/pose-refresh one-time cleanup below.
        private static readonly Dictionary<ulong, long> s_phantomDownedSinceMs = new();

        // Per-HUMAN-PLAYER cooldown gate for phantom-driven revives of that
        // human specifically (PhantomHeroesReviveCooldownMs) - deliberately
        // keyed by the human's Player id, NOT per-phantom, so a squad of
        // several phantoms can't just round-robin revives with no effective
        // downtime (each phantom's own resurrect-power cooldown is
        // independent, so bypassing THAT alone isn't a real guardrail).
        // Phantom-to-phantom revives never touch this.
        private static readonly Dictionary<ulong, long> s_phantomHumanReviveCooldown = new();

        // Revive-claim exclusivity: keyed by the DOWNED target's entity id,
        // so only one phantom at a time commits to reviving a given corpse.
        // Without this, every idle phantom independently finds the same
        // downed target and all abandon their fights to rush it —
        // TruSkillzzRuns' "whole squad piling onto one revive." A hard
        // timeout (not refreshed while the claim holds) lets the claim
        // release to someone else if the claimant dies, gets stuck, or
        // otherwise never finishes — same shape as their own fix.
        private const long PhantomReviveClaimTimeoutMs = 20000;
        private static readonly Dictionary<ulong, (ulong phantomId, long claimedAtMs)> s_phantomReviveClaim = new();

        private static bool TryClaimRevive(ulong targetId, ulong phantomId, long nowMs)
        {
            if (s_phantomReviveClaim.TryGetValue(targetId, out var claim))
            {
                if (claim.phantomId == phantomId) return true;
                if (nowMs - claim.claimedAtMs < PhantomReviveClaimTimeoutMs) return false;
                // Claim expired without the original claimant finishing — free for anyone.
            }
            s_phantomReviveClaim[targetId] = (phantomId, nowMs);
            return true;
        }

        private static void ReleaseReviveClaim(ulong targetId, ulong phantomId)
        {
            if (s_phantomReviveClaim.TryGetValue(targetId, out var claim) && claim.phantomId == phantomId)
                s_phantomReviveClaim.Remove(targetId);
        }

        // Drops every claim where phantomId is either the CLAIMANT (it
        // despawned mid-approach, freeing whoever it was reviving) or the
        // TARGET (it was itself the downed one and is now gone for good —
        // destroyed outright rather than revived, e.g. squad cleared).
        private static void PruneReviveClaimsFor(ulong phantomId)
        {
            List<ulong> toRemove = null;
            foreach (var kvp in s_phantomReviveClaim)
                if (kvp.Key == phantomId || kvp.Value.phantomId == phantomId) (toRemove ??= new()).Add(kvp.Key);
            if (toRemove != null)
                foreach (ulong key in toRemove) s_phantomReviveClaim.Remove(key);
        }

        private const float PhantomAttackRange = 1200f;
        private const float PhantomAttackRangeSq = PhantomAttackRange * PhantomAttackRange;
        // Wider search — phantom will walk to any hostile in this radius.
        private const float PhantomSearchRange = 3500f;
        private const float PhantomSearchRangeSq = PhantomSearchRange * PhantomSearchRange;
        // Tick twice as often so movement + attack feel snappy.
        private static readonly TimeSpan PhantomTickInterval = TimeSpan.FromMilliseconds(500);

        private readonly EventGroup _phantomPendingEvents = new();
        private readonly EventPointer<PhantomTickEvent> _phantomTick = new();

        private void SchedulePhantomTick()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_phantomTick.IsValid) return;
            scheduler.ScheduleEvent(_phantomTick, PhantomTickInterval, _phantomPendingEvents);
            _phantomTick.Get().Initialize(this);
        }

        private void OnPhantomTick()
        {
            Player host = PhantomHost;
            if (host == null || host.PhantomHeroCount == 0 || IsInWorld == false) return;

            Vector3 callerPos = RegionLocation.Position;
            var rng = Game.Random;
            List<ulong> stale = null;
            var ids = host.PhantomAvatarIds; // snapshot count for stable iteration

            int callerLevel = CharacterLevel;
            for (int i = 0; i < ids.Count; i++)
            {
                ulong id = ids[i];
                Avatar phantom = Game.EntityManager.GetEntity<Avatar>(id);
                if (phantom == null || phantom.IsDestroyed || phantom.IsInWorld == false)
                {
                    (stale ??= new List<ulong>()).Add(id);
                    continue;
                }

                // Power-stuck recovery — see s_phantomPowerStuckTrack above. Runs
                // before anything else so a freed phantom can act again this
                // same tick instead of waiting for the next one.
                if (phantom.IsExecutingPower)
                {
                    PrototypeId activePowerRef = phantom.ActivePowerRef;
                    if (s_phantomPowerStuckTrack.TryGetValue(phantom.Id, out var powerStuckState) && powerStuckState.powerRef == activePowerRef)
                    {
                        int newPowerStuckTicks = powerStuckState.stuckTicks + 1;
                        if (newPowerStuckTicks >= PhantomPowerStuckTickThreshold)
                        {
                            try { phantom.ActivePower?.EndPower(EndPowerFlags.ExplicitCancel | EndPowerFlags.Force); }
                            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] force-end stuck power {activePowerRef.GetName()} on 0x{phantom.Id:X} failed: {ex.Message}"); }
                            s_phantomPowerStuckTrack.Remove(phantom.Id);
                        }
                        else
                        {
                            s_phantomPowerStuckTrack[phantom.Id] = (activePowerRef, newPowerStuckTicks);
                        }
                    }
                    else
                    {
                        s_phantomPowerStuckTrack[phantom.Id] = (activePowerRef, 0);
                    }
                }
                else if (s_phantomPowerStuckTrack.ContainsKey(phantom.Id))
                {
                    s_phantomPowerStuckTrack.Remove(phantom.Id);
                }

                // Downed handling. A killable phantom that hit 0 HP stays
                // IsInWorld true but IsDead - same "downed" state a real
                // player enters - for PhantomHeroesDownedGracePeriodMs (see
                // HandlePhantomDeath), so the human or a teammate phantom can
                // revive via ResurrectOtherAvatar (UpdatePhantomHunt's
                // Priority-1 triage). While downed, skip movement/hunt
                // entirely - a corpse doesn't walk or fight, and the revive
                // priority on OTHER phantoms is what finds and raises it.
                long nowMsDowned = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
                if (phantom.IsDead)
                {
                    if (s_phantomDownedSinceMs.ContainsKey(phantom.Id) == false)
                        s_phantomDownedSinceMs[phantom.Id] = nowMsDowned;

                    // A corpse doesn't walk, but it also can't be left behind
                    // forever - the leash below (for ALIVE phantoms) never
                    // runs for a downed one since we continue past it, so
                    // nothing normally stops the gap between "where this
                    // phantom died" and "where the squad currently is" from
                    // growing without bound as the human moves on (chasing
                    // enemies, a region reconnect landing at a different
                    // spot, etc.) - confirmed live: a downed phantom left
                    // behind 6500+ units from the caller, with another
                    // phantom stuck trying to walk the whole way to revive
                    // it. Reuse the same leash threshold/position picker the
                    // alive path uses so a downed corpse gets teleported back
                    // within reach instead of drifting arbitrarily far.
                    float callerDistSqDowned = Vector3.DistanceSquared2D(phantom.RegionLocation.Position, callerPos);
                    if (callerDistSqDowned > PhantomFollowMaxDistSq)
                    {
                        Region downedRegion = phantom.Region;
                        if (downedRegion != null)
                        {
                            try
                            {
                                Vector3 downedLeashPos = ChoosePhantomLeashPos(downedRegion, callerPos, rng, phantom.Bounds.Radius);
                                phantom.ChangeRegionPosition(downedLeashPos, null);
                                PhantomLogger.Info($"[PhantomHero:Down] {phantom} downed corpse was {MathF.Sqrt(callerDistSqDowned):F0} from caller, leashed to {downedLeashPos.ToStringNames()}");
                            }
                            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Down] corpse leash failed: {ex.Message}"); }
                        }
                    }

                    continue;
                }

                // Alive path: if we were tracking this phantom as downed,
                // they just got revived (by the human or a teammate phantom
                // via ResurrectOtherAvatar). Force-clear any power that was
                // still active the moment they went down - OnKilled/
                // OnRemoveFromWorld never end active powers for Avatar-type
                // entities (that's what keeps a downed phantom revivable in
                // place instead of being destroyed), so without this,
                // IsExecutingPower stays true after revival and every check
                // above silently no-ops forever - the phantom just stands
                // there instead of resuming hunt/revive logic. Also forces a
                // locomotor/position refresh so the client stops rendering
                // the downed pose (same primitives the leash below uses).
                if (s_phantomDownedSinceMs.Remove(phantom.Id))
                {
                    // Revived via any path (a teammate phantom, or the human's
                    // own manual revive click) — the claim on this target no
                    // longer means anything, whoever holds it. Clear it
                    // outright instead of relying only on the phantom-driven
                    // revive path's own release (that path already double-
                    // releases the same entry harmlessly).
                    s_phantomReviveClaim.Remove(phantom.Id);
                    try
                    {
                        if (phantom.ActivePowerRef != PrototypeId.Invalid)
                        {
                            Power stuckPower = phantom.PowerCollection?.GetPower(phantom.ActivePowerRef);
                            stuckPower?.EndPower(EndPowerFlags.ExplicitCancel | EndPowerFlags.Force);
                        }
                        s_phantomPowerStuckTrack.Remove(phantom.Id);

                        Vector3 herePos = phantom.RegionLocation.Position;
                        phantom.Locomotor?.Stop();
                        phantom.ChangeRegionPosition(herePos, null);
                        PhantomLogger.Info($"[PhantomHero:Down] {phantom} revived - pose refreshed");
                    }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Down] revive refresh failed: {ex.Message}"); }
                }

                // Level sync: phantoms always match the caller's CURRENT
                // level exactly, both up and down — closes a real exploit
                // where a low-level player could spawn (or restore a saved
                // squad of) permanently over-levelled phantoms. Runs every
                // 500ms; InitializeLevel is a no-op internally when the new
                // level already matches, so this is cheap on stable ticks.
                // IsPhantomLevelLocked is now permanently neutered (always
                // false) rather than removed outright, so this check stays
                // for documentation/future-proofing but never actually
                // exempts anything.
                if (callerLevel > 0 && phantom.CharacterLevel != callerLevel && host.IsPhantomLevelLocked(phantom.Id) == false)
                {
                    try
                    {
                        phantom.InitializeLevel(callerLevel);
                        phantom.CombatLevel = callerLevel;
                        // Rescale damage buffs so a phantom that just
                        // synced to a new level stops hitting like its old
                        // one (or overshoots — the anchor curve tracks
                        // level, not spawn-time snapshot).
                        ApplyPhantomDamageScaling(phantom, callerLevel);
                        // Re-roll gear for the new level too — otherwise a
                        // phantom that syncs UP into 60 never receives BiS
                        // gear without a manual respawn, and one that syncs
                        // DOWN from 60 keeps wearing gear it can no longer
                        // support. ApplyPhantomGear only ever fills EMPTY
                        // slots (ChangeInventoryLocation fails silently into
                        // an occupied single-slot equip inventory), so an
                        // already-geared slot must be stripped first — same
                        // as RerollPhantomGear does — or the old item just
                        // sits there forever and only genuinely-blank slots
                        // (e.g. Legendary/UruForged on first reaching 60)
                        // ever pick anything up.
                        Player phantomPlayerObj = phantom.GetOwnerOfType<Player>();
                        if (phantomPlayerObj != null)
                        {
                            AvatarPrototype phantomAvatarProto = phantom.AvatarPrototype;
                            if (phantomAvatarProto?.EquipmentInventories != null)
                            {
                                foreach (var assignment in phantomAvatarProto.EquipmentInventories)
                                {
                                    var invProto = assignment.Inventory.As<InventoryPrototype>();
                                    if (invProto == null || invProto.ConvenienceLabel == InventoryConvenienceLabel.Costume) continue;
                                    phantom.GetInventoryByRef(assignment.Inventory)?.DestroyContained();
                                }
                            }
                            List<ulong> resynced = ApplyPhantomGear(phantomPlayerObj, phantom, callerLevel, null);
                            host.UpdatePhantomGear(phantom.Id, resynced);
                        }
                        // Refresh the stored descriptor so cross-region
                        // migration re-spawns at the new level, not the
                        // stale spawn-time value.
                        host.UpdatePhantomLevel(phantom.Id, callerLevel);
                    }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] level sync {phantom.Id:X} → {callerLevel} failed: {ex.Message}"); }
                }

                // Stuck detection: if the phantom's position barely moved
                // this tick despite the Locomotor being set to move, count
                // it. After N consecutive stuck ticks assume they're
                // wall-clipped or pathed out of bounds and force-leash.
                Vector3 curPos = phantom.RegionLocation.Position;
                bool forceLeash = false;
                if (s_phantomStuckTrack.TryGetValue(phantom.Id, out var stuckState))
                {
                    float movedSq = Vector3.DistanceSquared2D(curPos, stuckState.lastPos);
                    bool wantsToMove = phantom.Locomotor != null && phantom.Locomotor.IsMoving;
                    int newStuck = (wantsToMove && movedSq < PhantomStuckMoveEpsilonSq) ? stuckState.stuckTicks + 1 : 0;
                    if (newStuck >= PhantomStuckTickThreshold) forceLeash = true;
                    s_phantomStuckTrack[phantom.Id] = (curPos, forceLeash ? 0 : newStuck);
                }
                else s_phantomStuckTrack[phantom.Id] = (curPos, 0);

                // Leash: teleport back if stranded far or wall-stuck.
                float distSq = Vector3.DistanceSquared2D(curPos, callerPos);
                if (distSq > PhantomFollowMaxDistSq || forceLeash)
                {
                    Region r = phantom.Region;
                    Vector3 leashPos = ChoosePhantomLeashPos(r, callerPos, rng, phantom.Bounds.Radius);
                    try
                    {
                        phantom.Locomotor?.Stop();
                        phantom.ChangeRegionPosition(leashPos, null);
                        s_phantomStuckTrack[phantom.Id] = (leashPos, 0);
                    }
                    catch { /* keep ticking */ }
                }

                // Hunt: locomotor-walk toward the nearest hostile in a wider sweep,
                // then attack once in range. Locomotor.FollowEntity refreshes each
                // tick (250ms repath delay) so the phantom will keep advancing.
                try { UpdatePhantomHunt(phantom, rng); } catch { /* keep ticking */ }
            }

            if (stale != null)
                foreach (ulong id in stale) host.UnregisterPhantom(id);

            if (host.PhantomHeroCount > 0)
                SchedulePhantomTick();
        }

        // One-time-per-phantom diagnostic set. Removed once attack is verified.
        private static readonly HashSet<ulong> s_phantomAttackLogged = new();
        // One-time-per-(phantom,target) diagnostic set for the Attack log so a
        // new boss gets its state dumped even after this phantom has already
        // logged an attack on a mob.
        private static readonly HashSet<ulong> s_phantomAttackTargetLogged = new();

        // Revive-priority range — search a bit wider than combat range so
        // phantoms notice downed players from across a room.
        private const float PhantomReviveSearchRange = 4000f;
        private const float PhantomReviveSearchRangeSq = PhantomReviveSearchRange * PhantomReviveSearchRange;

        // Fallback melee reach for the revive cast-range check, when the
        // resurrect power reports no positive range at all.
        private const float PhantomReviveMeleeFallbackRangeSq = 400f * 400f;

        /// <summary>
        /// Real usable range (squared) for a phantom's resurrect-other power.
        /// A hardcoded "cast range" guess let phantoms stop and attempt the
        /// cast well outside the power's actual range, so ActivatePower
        /// rejected almost every attempt with OutOfPosition - ask the power
        /// itself instead of guessing. Falls back to melee reach if the
        /// power reports no positive range at all.
        /// </summary>
        private static float GetReviveCastRangeSq(Avatar phantom, PrototypeId resurrectPowerRef)
        {
            if (resurrectPowerRef != PrototypeId.Invalid)
            {
                Power resurrectPower = phantom.GetPower(resurrectPowerRef);
                float r = resurrectPower?.GetRange() ?? 0f;
                if (r > 0f)
                {
                    float withMargin = r + 50f;
                    return withMargin * withMargin;
                }
            }
            return PhantomReviveMeleeFallbackRangeSq;
        }

        private void UpdatePhantomHunt(Avatar phantom, MHServerEmu.Core.System.Random.GRandom rng)
        {
            Region region = phantom.Region;
            if (region == null || phantom.PowerCollection == null) return;

            // Mid-cast: stand still, like a real player. Without this the tick
            // kept re-issuing FollowEntity every 500ms while a power was
            // executing, so phantoms slid across the ground through their
            // cast animations. Any new attack would return PowerInProgress
            // anyway, and the stuck-power watchdog (in OnPhantomTick, which
            // runs before this) still force-ends channels that never finish —
            // so skipping the whole hunt for the duration of a cast is safe.
            if (phantom.IsExecutingPower)
            {
                var castLoco = phantom.Locomotor;
                if (castLoco != null && castLoco.IsMoving)
                    castLoco.Stop();
                return;
            }

            // Self-heal at low HP, like a real player reaching for their
            // medkit, takes priority over both revive-triage and combat —
            // a phantom about to die is more useful topping off first.
            if (TryPhantomSelfHeal(phantom, rng)) return;

            Vector3 phantomPos = phantom.RegionLocation.Position;

            // Priority 1: revive any downed real player OR friendly teammate
            // phantom within revive range. Both are still IsInWorld while
            // downed (dead-but-revivable); nearest wins, with the human's own
            // revive-cooldown (see below) skipping them as a candidate while
            // still on cooldown so the search falls through to a downed
            // teammate phantom instead, if any.
            Avatar downed = null;
            float downedDistSq = float.MaxValue;
            var reviveSphere = new Sphere(phantomPos, PhantomReviveSearchRange);
            var reviveCtx = new MHServerEmu.Games.Entities.EntityRegionSPContext(MHServerEmu.Games.Entities.EntityRegionSPContextFlags.PrimaryPartition);

            long nowMsRevive = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            bool humanReviveOnCooldown = false;
            Player humanPlayer = this.GetOwnerOfType<Player>();
            if (humanPlayer != null && s_phantomHumanReviveCooldown.TryGetValue(humanPlayer.Id, out long lastHumanReviveMs))
            {
                int reviveCooldownMs = Math.Max(0, Game?.CustomGameOptions?.PhantomHeroesReviveCooldownMs ?? 60000);
                if (nowMsRevive - lastHumanReviveMs < reviveCooldownMs)
                    humanReviveOnCooldown = true;
            }

            // Direct check on the caller first — this covers the case where the
            // human died far from the phantom (out of the 4000u sphere) or
            // during a scripted death animation where the AOI doesn't return
            // them from IterateEntitiesInVolume. The caller is the phantom's
            // owner, so we always know exactly who to look for.
            if (humanReviveOnCooldown == false && this.IsDead && this.IsInWorld && this.Region == region)
            {
                downed = this;
                downedDistSq = Vector3.DistanceSquared(this.RegionLocation.Position, phantomPos);
            }
            else
            {
                // Direct roster scan — this phantom's own squad, checked by
                // ID with NO distance ceiling. A squad spread out fighting
                // separate targets can end up farther apart than the
                // PhantomReviveSearchRange sweep, which would otherwise make
                // the last downed teammate invisible to everyone else.
                Player rosterHost = this.PhantomHost;
                if (rosterHost != null)
                {
                    var rosterIds = rosterHost.PhantomAvatarIds;
                    for (int ri = 0; ri < rosterIds.Count; ri++)
                    {
                        ulong avId = rosterIds[ri];
                        if (avId == phantom.Id) continue;
                        Avatar candidate = Game.EntityManager.GetEntity<Avatar>(avId);
                        if (candidate == null || candidate.IsDead == false || candidate.IsInWorld == false || candidate.Region != region) continue;

                        float d = Vector3.DistanceSquared(candidate.RegionLocation.Position, phantomPos);
                        if (d < downedDistSq) { downedDistSq = d; downed = candidate; }
                    }
                }

                // Spatial sweep for OTHER real players (not this phantom's own
                // roster, e.g. a party member) — kept bounded to
                // PhantomReviveSearchRange since these are strangers to the
                // squad, not something tracked directly by ID.
                foreach (WorldEntity we in region.IterateEntitiesInVolume(reviveSphere, reviveCtx))
                {
                    if (we is not Avatar candidate) continue;
                    if (candidate.Id == phantom.Id) continue;
                    if (candidate.IsDead == false) continue;

                    Player candOwner = candidate.GetOwnerOfType<Player>();
                    if (candOwner == null || candOwner.PlayerConnection == null) continue;
                    if (candOwner == humanPlayer && humanReviveOnCooldown) continue;

                    float d = Vector3.DistanceSquared(candidate.RegionLocation.Position, phantomPos);
                    if (d > PhantomReviveSearchRangeSq) continue;
                    if (d < downedDistSq) { downedDistSq = d; downed = candidate; }
                }
            }
            // Claim exclusivity: without this every idle phantom independently
            // finds the same downed target and all pile on it, abandoning
            // whatever they were fighting — "whole squad piling onto one
            // revive." Only the phantom already holding (or newly winning)
            // the claim commits; everyone else falls through to normal
            // combat instead. A hard timeout lets the claim release to
            // someone else if the claimant gets stuck/dies mid-approach.
            if (downed != null && TryClaimRevive(downed.Id, phantom.Id, nowMsRevive) == false)
                downed = null;

            if (downed != null)
            {
                PrototypeId reviveCastPowerRef = phantom.AvatarPrototype?.ResurrectOtherEntityPower ?? PrototypeId.Invalid;
                float castRangeSq = GetReviveCastRangeSq(phantom, reviveCastPowerRef);
                Player downedOwnerDiag = downed.GetOwnerOfType<Player>();
                bool revivingHumanDiag = downedOwnerDiag != null && downedOwnerDiag.PlayerConnection != null;
                string downedKindDiag = revivingHumanDiag ? "human" : "phantom";

                // Walk to them if we're not in cast range yet.
                if (downedDistSq > castRangeSq)
                {
                    var reviveLoco = phantom.Locomotor;
                    bool followOk = false;
                    if (reviveLoco != null)
                    {
                        var reviveOpts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(250) };
                        followOk = reviveLoco.FollowEntity(downed.Id, 50f, 50f, ref reviveOpts, false);

                        // If the navmesh genuinely can't path there (not just
                        // "still walking"), force-leash next to the downed
                        // target instead of silently retrying forever every
                        // tick - same rescue the caller-leash logic already
                        // uses for a stuck phantom.
                        if (followOk == false
                            && (reviveLoco.LastGeneratedPathResult == MHServerEmu.Games.Navi.NaviPathResult.Failed
                             || reviveLoco.LastGeneratedPathResult == MHServerEmu.Games.Navi.NaviPathResult.FailedNaviMesh
                             || reviveLoco.LastGeneratedPathResult == MHServerEmu.Games.Navi.NaviPathResult.FailedNoPathFound))
                        {
                            Vector3 targetPos = downed.RegionLocation.Position;
                            Vector3 rescuePos = ChoosePhantomLeashPos(region, targetPos, rng, phantom.Bounds.Radius);
                            try
                            {
                                reviveLoco.Stop();
                                phantom.ChangeRegionPosition(rescuePos, null);
                                s_phantomStuckTrack[phantom.Id] = (rescuePos, 0);
                                PhantomLogger.Info($"[PhantomHero:Revive] {phantom} path to downed {downedKindDiag} {downed} failed (pathResult={reviveLoco.LastGeneratedPathResult}), force-leashed to {rescuePos.ToStringNames()}");
                            }
                            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Revive] rescue leash threw: {ex.Message}"); }
                        }
                    }
                    PhantomLogger.Info($"[PhantomHero:Revive] {phantom} approaching downed {downedKindDiag} {downed} dist={MathF.Sqrt(downedDistSq):F0} castRange={MathF.Sqrt(castRangeSq):F0} followOk={followOk}");
                }
                else
                {
                    // In cast range — fire the built-in resurrect-other power.
                    // bypassCooldown: true so this phantom can keep chain-
                    // reviving the rest of a wiped squad instead of sitting on
                    // its own resurrect power's cooldown after the first cast.
                    // The human-specific cooldown enforced above (skipping
                    // them as a candidate) is the real guardrail against
                    // reviving the HUMAN too often - this bypass only affects
                    // how fast a phantom can help OTHER downed teammates.
                    try
                    {
                        var reviveResult = phantom.ResurrectOtherAvatar(downed, bypassCooldown: true);
                        PhantomLogger.Info($"[PhantomHero:Revive] {phantom} -> {downedKindDiag} {downed} dist={MathF.Sqrt(downedDistSq):F0} castRange={MathF.Sqrt(castRangeSq):F0} result={reviveResult}");
                        if (revivingHumanDiag && reviveResult == PowerUseResult.Success && downedOwnerDiag != null)
                            s_phantomHumanReviveCooldown[downedOwnerDiag.Id] = nowMsRevive;
                        if (reviveResult == PowerUseResult.Success)
                            ReleaseReviveClaim(downed.Id, phantom.Id);
                    }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero:Revive] {phantom.Id:X} -> {downed.Id:X} failed: {ex.Message}"); }
                }
                return; // don't hunt while triaging a downed teammate
            }

            // Support phantoms use their buffs: any phantom whose kit
            // happens to include a friendly-targeting power (heal/buff, not
            // just attacks) uses it on whichever ally needs it most, instead
            // of that power just sitting unused because the AI only ever
            // considered hostile targets. Runs after revive-triage (a downed
            // teammate is more urgent than a buff) but before the combat
            // sweep — a support hero should top someone off between attacks,
            // not only when idle.
            if (TryPhantomSupportBuff(phantom, rng)) return;

            // Widest sweep so we start advancing on enemies before they're in
            // attack range. IterateEntitiesInVolume walks the region spatial
            // partition, cheap.
            var sweepSphere = new Sphere(phantomPos, PhantomSearchRange);
            var ctx = new MHServerEmu.Games.Entities.EntityRegionSPContext(MHServerEmu.Games.Entities.EntityRegionSPContextFlags.PrimaryPartition);

            // Build a full sorted candidate list of hostile Agents instead of just
            // "the nearest one." Some encounters (dramatic-entrance bosses, mission
            // untargetable phases, out-of-line-of-sight bosses on elevated
            // platforms) leave the nearest hostile in a state where
            // Power.IsValidTarget silently rejects — and if that's the only entity
            // we track, the phantom locks onto it, ActivatePower burns the
            // cooldown returning BadTarget, and the phantom stands still for the
            // whole fight. With a list we fall through to the next-nearest until
            // one accepts the attack.
            var candidates = new List<(WorldEntity we, float distSq, float score)>();
            List<(WorldEntity we, float distSq, string reason)> diagRejected = null;
            bool diagWant = ShouldEmitPhantomDiag(phantom.Id);
            long nowMsSweep = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            ulong callerId = Id;
            IReadOnlyList<ulong> squadIds = phantom.PhantomHost?.PhantomAvatarIds;
            foreach (WorldEntity we in region.IterateEntitiesInVolume(sweepSphere, ctx))
            {
                if (we == null || we.Id == phantom.Id || we.Id == Id) continue;
                if (we.IsDead || we.IsInWorld == false) continue;
                // Only Agents — filters out props/destructibles/spawner markers.
                if (we is not Agent) continue;
                if (phantom.IsHostileTo(we) == false) continue;
                // Don't chase anything that would drag the phantom past the
                // caller leash — see PhantomHuntMaxCallerDist above.
                if (Vector3.DistanceSquared2D(we.RegionLocation.Position, RegionLocation.Position) > PhantomHuntMaxCallerDistSq)
                {
                    if (diagWant) (diagRejected ??= new()).Add((we, Vector3.DistanceSquared2D(we.RegionLocation.Position, phantomPos), "outside-leash"));
                    continue;
                }
                float d = Vector3.DistanceSquared2D(we.RegionLocation.Position, phantomPos);
                // Skip anything the engine won't accept as a valid target yet.
                // Dramatic-entrance bosses (Doom, Loki, terminal bosses...) spawn
                // with IsDormant=true until their intro cutscene wakes them
                // (Agent.cs:97 + WakeEndCallback line 3119). While dormant,
                // IsAffectedByPowersInternal returns false so
                // Power.IsValidTarget rejects the attack (Power.Validation.cs
                // line 313).
                if (we.IsDormant || we.IsUntargetable || we.IsUnaffectable)
                {
                    if (diagWant) (diagRejected ??= new()).Add((we, d,
                        we.IsDormant ? "dormant" : we.IsUntargetable ? "untargetable" : "unaffectable"));
                    continue;
                }
                // Per-phantom blacklist: if we tried this target recently and
                // ActivatePower returned non-Success, skip for the blacklist window.
                // Lets phantoms rotate through other hostiles while a cutscene
                // boss finishes waking up, and lets the boss get picked up again
                // on the next tick after the blacklist expires.
                if (IsTargetBlacklisted(phantom.Id, we.Id, nowMsSweep))
                {
                    if (diagWant) (diagRejected ??= new()).Add((we, d, "blacklist"));
                    continue;
                }
                candidates.Add((we, d, ScoreHostileCandidate(phantom, we, d, callerId, squadIds)));
            }
            // Rank by tactical priority instead of pure nearest-first: focus
            // whoever's attacking us, finish off near-death enemies first so
            // damage isn't spread thin across a pack that all survive, and
            // converge with a squadmate that's already committed to a
            // target. Distance still breaks ties, and PhantomHuntMaxCallerDist
            // above is unaffected — this only reorders which of the already-
            // in-range candidates gets tried first.
            candidates.Sort(static (a, b) =>
            {
                int cmp = b.score.CompareTo(a.score);
                return cmp != 0 ? cmp : a.distSq.CompareTo(b.distSq);
            });

            if (diagWant && (candidates.Count == 0 || diagRejected != null))
                DumpPhantomHuntDiag(phantom, phantomPos,
                    candidates.Count > 0 ? candidates[0].we : null,
                    candidates.Count > 0 ? candidates[0].distSq : 0f,
                    diagRejected, region, sweepSphere, ctx);

            if (candidates.Count == 0)
            {
                // Nothing to fight and nobody to revive — walk back toward the
                // caller instead of standing still. Each phantom paths to its
                // own evenly-spaced slot in a ring around the caller (instead
                // of every phantom converging on the same FollowEntity range),
                // so an idle group spreads into a small formation instead of
                // piling into a single blob. Re-evaluated every tick so it
                // reads as continuous companion-style following.
                var idleLoco = phantom.Locomotor;
                if (idleLoco != null)
                {
                    Vector3 slotPos = ComputePhantomFormationSlot(phantom, region);
                    float idleDistSq = Vector3.DistanceSquared2D(phantomPos, slotPos);
                    if (idleDistSq > PhantomFormationArriveDist * PhantomFormationArriveDist)
                    {
                        var idleOpts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(250) };
                        idleLoco.PathTo(slotPos, ref idleOpts);
                    }
                    else
                    {
                        idleLoco.Stop();
                    }
                }
                return;
            }

            // Advance toward the closest survivor for movement, but for the
            // attack try each in order — the closest might be a Living Laser
            // waiting on his cutscene entry that rejects power activation for a
            // few seconds, while the actual boss is right behind him and
            // attackable now. Without the fallback the phantom stood on the
            // first target and never fired.
            WorldEntity nearest = candidates[0].we;
            float nearestDistSq = candidates[0].distSq;

            // Always keep the Locomotor advancing toward the target — even when
            // we're inside attack range. Stopping while attacking was the reason
            // phantoms visually stood still: my previous tick called Stop() every
            // time nearestDistSq was in range, so they only ever ticked "stop,
            // cast, stop, cast" with no walking between. Now we walk in, stop only
            // if Locomotor reaches the target's radius, and fire the power
            // regardless — the engine cancels movement automatically while a
            // cast animation runs (Locomotor.Locomote respects ActivePower flags).
            var loco = phantom.Locomotor;
            if (loco != null)
            {
                var opts = new LocomotionOptions { RepathDelay = TimeSpan.FromMilliseconds(250) };
                // Follow only as close as the phantom's widest usable power's
                // range. Ranged heroes (Storm, Iron Man, Rocket) stop at
                // projectile range and start casting; melee heroes (Thing,
                // Colossus) keep walking in to 50u. Without this every
                // phantom sprinted into point-blank on every target — visually
                // wrong for ranged kits, and left the phantom stuck at 50u
                // firing projectiles the client had to render at melee.
                float followStopDist = ComputePhantomFollowStopDist(phantom, nearest);

                // Ranged phantoms kite: a hero whose best usable power has
                // real reach (followStopDist meaningfully past melee) should
                // back off when something closes to point-blank instead of
                // just standing there taking hits like a melee hero would.
                // Edge-to-edge, matching the melee-reach fix above.
                bool isRangedPhantom = followStopDist > PhantomKiteRangedThreshold;
                float edgeDistToNearest = phantom.GetDistanceTo(nearest, true);
                if (isRangedPhantom && edgeDistToNearest < PhantomKiteTooCloseDist)
                {
                    Vector3 awayDir = Vector3.SafeNormalize2D(phantomPos - nearest.RegionLocation.Position);
                    Vector3 kitePos = phantomPos + awayDir * PhantomKiteStepDist;
                    kitePos = RegionLocation.ProjectToFloor(region, kitePos);
                    loco.PathTo(kitePos, ref opts);
                }
                else
                {
                    bool ok = loco.FollowEntity(nearest.Id, followStopDist, followStopDist, ref opts, false);
                    if (s_phantomLocoLogged.Add(phantom.Id))
                    {
                        PhantomLogger.Info($"[PhantomHero:Loco] {phantom} authoritative={phantom.IsMovementAuthoritative} simulated={phantom.IsSimulated} inWorld={phantom.IsInWorld} target={nearest.Id:X} dist={MathF.Sqrt(nearestDistSq):F0} FollowEntity returned={ok} locoEnabled={loco.IsEnabled} isMoving={loco.IsMoving} method={loco.Method} baseSpeed={loco.DefaultRunSpeed} hasPath={loco.HasPath} pathResult={loco.LastGeneratedPathResult} canMove={phantom.CanMove()}");
                    }
                }
            }

            // Only fire an attack when the phantom is settled — either the
            // Locomotor has arrived (or is close enough that the last step
            // is trivial), or the target is inside melee range. Firing while
            // FollowEntity is mid-path produces the "skating" look: the
            // cast animation cancels walking mid-stride but position keeps
            // advancing, so the character glides without a walk cycle.
            //
            // Melee reach is measured edge-to-edge (GetDistanceTo(calcRadius:
            // true), the same helper WorldEntity.cs itself uses for its own
            // range math) rather than raw center-to-center distance — against
            // a big-bounds target (a raid boss) center distance stays large
            // long after the phantom is physically pressed against the
            // model, so a center-only check left melee phantoms standing at
            // the boss for several seconds before this ever read "in melee."
            const float PhantomMeleeRange = 400f;
            bool arrived = loco == null || loco.IsMoving == false;
            bool inMelee = phantom.GetDistanceTo(nearest, true) <= PhantomMeleeRange;
            if ((arrived || inMelee) && nearestDistSq <= PhantomAttackRangeSq)
            {
                // Per-phantom attack cooldown — prevents the 2 Hz tick from
                // burst-firing 2 attacks per second. Real players average
                // closer to 1 attack per 800-1200 ms after animation locks.
                long now = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
                if (s_phantomNextAttackMs.TryGetValue(phantom.Id, out long nextAt) == false || now >= nextAt)
                {
                    // Try candidates in distance order. First one that
                    // ActivatePower accepts wins. Others get blacklisted only
                    // when they actually get an activate attempt — we don't
                    // pre-check IsValidTarget because that would double the
                    // per-tick work for the common case where the nearest is
                    // fine.
                    bool fired = false;
                    int maxTries = Math.Min(5, candidates.Count);
                    for (int i = 0; i < maxTries; i++)
                    {
                        WorldEntity tryTarget = candidates[i].we;
                        float tryDistSq = candidates[i].distSq;
                        if (tryDistSq > PhantomAttackRangeSq) break; // rest are out of range
                        PowerUseResult r = TryPhantomAttack(phantom, tryTarget, tryDistSq, rng);
                        if (r == PowerUseResult.Success)
                        {
                            fired = true;
                            // Successful hit — make sure this target isn't
                            // blacklisted from a stale prior tick.
                            ClearTargetBlacklist(phantom.Id, tryTarget.Id);
                            // Remember this as our committed target so
                            // squadmates' ScoreHostileCandidate can converge
                            // on it too (focus-fire) instead of everyone
                            // chipping a different target.
                            s_phantomLastAttackTarget[phantom.Id] = tryTarget.Id;
                            break;
                        }
                        // BadTarget / InsufficientEndurance / TargetIsMissing /
                        // OutOfPosition / FullscreenMovie — blacklist this
                        // target for 3 seconds so the sweep skips it while
                        // whatever transient state clears.
                        BlacklistTarget(phantom.Id, tryTarget.Id, nowMsSweep);
                    }
                    if (!fired && diagWant)
                        PhantomLogger.Info($"[PhantomHero:Attack] {phantom} all {maxTries} candidates rejected the attack — sweep found {candidates.Count} hostile(s), first={candidates[0].we} dist={MathF.Sqrt(candidates[0].distSq):F0}");
                    // 800 ms floor + 400 ms jitter so 3 phantoms don't fire in
                    // lockstep.
                    s_phantomNextAttackMs[phantom.Id] = now + 800 + (long)(rng.NextDouble() * 400);
                }
            }
        }

        // Per-phantom next-attack timestamp (ms). Enforces at least ~800ms
        // between casts so the tick doesn't spam-fire.
        private static readonly Dictionary<ulong, long> s_phantomNextAttackMs = new();

        // Per-phantom "what did I last successfully hit" — read by
        // ScoreHostileCandidate so a squad converges on one target (focus
        // fire) instead of every phantom independently picking whoever's
        // nearest to ITSELF.
        private static readonly Dictionary<ulong, ulong> s_phantomLastAttackTarget = new();

        // Tactical weights for re-ranking hunt candidates beyond pure
        // nearest-first — see the Sort call above. Distance still
        // contributes (closer is slightly favored) but the strategic
        // bonuses dominate so the squad reads as coordinated rather than
        // "whoever's standing closest to ME specifically."
        private const float PhantomScoreNearDeathCritical = 3.0f;   // <=15% HP
        private const float PhantomScoreNearDeathLow = 1.5f;        // <=35% HP
        private const float PhantomScoreAttackingUs = 4.0f;         // AI's own target is the caller or a squadmate
        private const float PhantomScoreSquadFocus = 2.0f;          // a squadmate already committed to this target

        /// <summary>
        /// Tactical priority score for a hostile hunt candidate — see the
        /// re-rank Sort call in UpdatePhantomHunt for how this replaces pure
        /// nearest-first targeting. Higher wins.
        /// </summary>
        private static float ScoreHostileCandidate(Avatar phantom, WorldEntity we, float distSq, ulong callerId, IReadOnlyList<ulong> squadIds)
        {
            float dist = MathF.Sqrt(distSq);
            // Distance stays a soft, secondary signal (0..1) — the
            // strategic bonuses below are what actually differentiate.
            float score = MathF.Max(0f, PhantomSearchRange - dist) / PhantomSearchRange;

            // Near-death priority: finish off enemies close to dying instead
            // of spreading damage thin across a pack that all survive.
            long hpMax = we.Properties[PropertyEnum.HealthMax];
            if (hpMax > 0)
            {
                float hpFrac = (float)((long)we.Properties[PropertyEnum.Health]) / hpMax;
                if (hpFrac <= 0.15f) score += PhantomScoreNearDeathCritical;
                else if (hpFrac <= 0.35f) score += PhantomScoreNearDeathLow;
            }

            // Focus whoever's actively attacking the human or a squadmate —
            // AIController.TargetEntity is the same "who is this NPC
            // currently going after" the engine's own AI already tracks, no
            // new threat system needed.
            if (we is Agent agent)
            {
                ulong aiTargetId = agent.AIController?.TargetEntity?.Id ?? 0;
                if (aiTargetId != 0 && (aiTargetId == callerId || ContainsId(squadIds, aiTargetId)))
                    score += PhantomScoreAttackingUs;
            }

            // Squad focus-fire: converge on whatever another phantom already
            // committed to instead of everyone chipping a different target.
            if (squadIds != null)
            {
                for (int i = 0; i < squadIds.Count; i++)
                {
                    ulong mateId = squadIds[i];
                    if (mateId == phantom.Id) continue;
                    if (s_phantomLastAttackTarget.TryGetValue(mateId, out ulong mateTarget) && mateTarget == we.Id)
                    {
                        score += PhantomScoreSquadFocus;
                        break;
                    }
                }
            }

            return score;
        }

        private static bool ContainsId(IReadOnlyList<ulong> list, ulong id)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (list[i] == id) return true;
            return false;
        }

        /// <summary>
        /// How many hostile, living Agents sit within radius of pos — used
        /// to decide whether an AoE power is actually worth its cooldown
        /// weight (see the AoE-awareness block in TryPhantomAttack).
        /// </summary>
        private static int CountHostilesNear(Avatar phantom, Vector3 pos, float radius)
        {
            if (radius <= 0f) radius = 1f;
            Region region = phantom.Region;
            if (region == null) return 1;
            var sphere = new Sphere(pos, radius);
            var ctx = new MHServerEmu.Games.Entities.EntityRegionSPContext(MHServerEmu.Games.Entities.EntityRegionSPContextFlags.PrimaryPartition);
            int count = 0;
            foreach (WorldEntity we in region.IterateEntitiesInVolume(sphere, ctx))
            {
                if (we == null || we.IsDead || we.IsInWorld == false) continue;
                if (we is not Agent) continue;
                if (phantom.IsHostileTo(we) == false) continue;
                count++;
            }
            return count;
        }

        // Per-phantom next-ultimate timestamp (ms). Ultimates fire on any
        // target once available, then rest for 20 minutes regardless of
        // what the power data's own cooldown says.
        private const long PhantomUltimateCooldownMs = 20 * 60 * 1000;
        private static readonly Dictionary<ulong, long> s_phantomNextUltimateMs = new();

        // Per-(phantom, power) blacklist. Some powers fail for reasons that
        // won't clear on their own — RestrictiveCondition (unmet condition
        // requirement, e.g. transform-state powers, OR a short-lived state
        // like being stunned/held/immobilized), WeaponMissing (needs an
        // equipped item the phantom doesn't have). Without this, a broken
        // power with a big cooldown weight gets picked every tick against
        // every target (per-TARGET blacklist doesn't help) and the phantom
        // never lands a hit.
        //
        // Two different expiries, not one flat ban for everything —
        // RestrictiveCondition is frequently caused by a transient state
        // (a stun clearing in a couple seconds), and a flat 10-minute ban
        // for that shape is exactly the bug TruSkillzzRuns' own audit pass
        // found in their equivalent system ("enemies getting passive over
        // long fights": more and more powers got benched for the long
        // timer as transient conditions kept re-triggering it, until the
        // AI ran out of things to do). WeaponMissing is genuinely
        // structural — a missing weapon slot doesn't fix itself — so it
        // keeps the long timer.
        private const long PhantomPowerBlacklistTransientMs = 15 * 1000;
        private const long PhantomPowerBlacklistStructuralMs = 10 * 60 * 1000;
        private static readonly Dictionary<(ulong phantomId, PrototypeId powerRef), long> s_phantomPowerBlacklist = new();
        private static bool IsPhantomPowerBlacklisted(ulong phantomId, PrototypeId powerRef, long nowMs)
            => s_phantomPowerBlacklist.TryGetValue((phantomId, powerRef), out long expiresAt) && nowMs < expiresAt;
        private static void PrunePowerBlacklistFor(ulong phantomId)
        {
            List<(ulong, PrototypeId)> toRemove = null;
            foreach (var key in s_phantomPowerBlacklist.Keys)
                if (key.phantomId == phantomId) (toRemove ??= new()).Add(key);
            if (toRemove != null)
                foreach (var k in toRemove) s_phantomPowerBlacklist.Remove(k);
        }

        // Per-(phantom,target) blacklist expiry. Populated when ActivatePower
        // returns non-Success, so the sweep skips that target for
        // PhantomBlacklistDurationMs. Lets phantoms rotate to hittable targets
        // during scripted encounters (cutscene bosses, mid-transition mission
        // NPCs, temporary Invulnerable phases) instead of glueing to the
        // first-picked hostile forever.
        private const long PhantomBlacklistDurationMs = 3000;
        private static readonly Dictionary<(ulong phantomId, ulong targetId), long> s_phantomTargetBlacklist = new();
        private static bool IsTargetBlacklisted(ulong phantomId, ulong targetId, long nowMs)
            => s_phantomTargetBlacklist.TryGetValue((phantomId, targetId), out long expiresAt) && nowMs < expiresAt;
        private static void BlacklistTarget(ulong phantomId, ulong targetId, long nowMs)
            => s_phantomTargetBlacklist[(phantomId, targetId)] = nowMs + PhantomBlacklistDurationMs;
        private static void ClearTargetBlacklist(ulong phantomId, ulong targetId)
            => s_phantomTargetBlacklist.Remove((phantomId, targetId));
        private static void PruneBlacklistFor(ulong phantomId)
        {
            List<(ulong, ulong)> toRemove = null;
            foreach (var key in s_phantomTargetBlacklist.Keys)
                if (key.phantomId == phantomId) (toRemove ??= new()).Add(key);
            if (toRemove != null)
                foreach (var k in toRemove) s_phantomTargetBlacklist.Remove(k);
        }

        /// <summary>
        /// Pick a leash-teleport position near the caller that lands on the
        /// walkable navi mesh. Retries up to 6 times with fresh random
        /// angles/radii; falls back to caller position if nothing validates.
        /// Fixes the "phantom leashes into a wall/out-of-bounds corner and
        /// stays there" case that only server-restart used to unstick.
        /// </summary>
        private static Vector3 ChoosePhantomLeashPos(Region region, Vector3 callerPos, MHServerEmu.Core.System.Random.GRandom rng, float avatarRadius)
        {
            if (region == null) return callerPos;
            var walkCheck = new DefaultContainsPathFlagsCheck(PathFlags.Walk);
            for (int attempt = 0; attempt < 6; attempt++)
            {
                float angle = (float)(rng.NextDouble() * Math.PI * 2.0);
                // Tighter than the old 200-800 range so leashed phantoms
                // land right next to the caller instead of "somewhere on
                // this screen."
                float radius = 150f + (float)(rng.NextDouble() * 250f);
                Vector3 candidate = callerPos + new Vector3((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0f);
                candidate = RegionLocation.ProjectToFloor(region, candidate);
                if (region.NaviMesh.Contains(candidate, MathF.Max(20f, avatarRadius), walkCheck))
                    return candidate;
            }
            // Fallback: caller's exact position. Guaranteed walkable since
            // the caller is standing on it.
            return callerPos;
        }

        /// <summary>
        /// Per-phantom idle position around the caller, so a group of idle
        /// phantoms spreads into a small formation instead of every phantom
        /// converging on the same point. The slot is derived from a hash of
        /// this phantom's own runtime id, not its index/order in the phantom
        /// list, so it's stable across ticks and doesn't reshuffle the whole
        /// formation whenever another phantom spawns or despawns.
        /// </summary>
        private Vector3 ComputePhantomFormationSlot(Avatar phantom, Region region)
        {
            Vector3 callerPos = RegionLocation.Position;

            // Personal (angle, distance) derived from a hash of the phantom's
            // runtime id, instead of an evenly-divided ring position — an
            // even ring reads as a drill formation marching in lockstep.
            // Because the angle is unique per phantom they never converge on
            // the same slot (no stacking), and distance varies too so a
            // squad spreads at natural depths rather than a perfect circle.
            ulong id = phantom.Id;
            ulong h = id * 2654435761UL ^ (id >> 16);
            float angle = ((h & 0xFFFF) / 65535f) * MathF.PI * 2f;
            float dist = PhantomIdleFollowStopDist + (((h >> 16) & 0xFF) / 255f - 0.5f) * 140f; // 130 .. 270

            Vector3 slot = callerPos + new Vector3(MathF.Cos(angle) * dist, MathF.Sin(angle) * dist, 0f);
            return region != null ? RegionLocation.ProjectToFloor(region, slot) : slot;
        }

        private static readonly HashSet<ulong> s_phantomLocoLogged = new();

        // ================================================================
        //  Phantom damage-scaling curve
        //
        //  Real avatars pick up damage the same way from levels 1 -> 60:
        //  a level curve on the base power damage (already baked into
        //  each PowerPrototype) PLUS gear-scaling from DamageRating.
        //  Phantoms have no gear, so we synthesise the "gear" side by
        //  interpolating three properties along the level track:
        //
        //    DamageMult      1.5   ->  3.0   (final-damage multiplier)
        //    DamagePctBonus  0.2   ->  1.5   (percent bonus)
        //    DamageRating    0     ->  5000  (feeds combat-globals curve;
        //                                     ~100 rating ≈ 10% damage,
        //                                     5000 ≈ a fully-BiS endgame
        //                                     avatar)
        //
        //  Tuning runs on t = ((level - 1) / 59)^2 — QUADRATIC, not linear.
        //  Playtesting showed linear scaling made phantoms hit too hard
        //  through the story levels (1-30): at level 30 linear-t was 0.49,
        //  handing out half the endgame bonus while mobs still have
        //  story-tier health pools. Squaring t keeps the ramp shallow
        //  early (t=0.24 at level 30, t=0.06 at level 15) and steep into
        //  endgame, where mob health scales up to meet it. Level 1 and
        //  level 60 anchors are unaffected.
        //
        //  Clamped to [0,1] so a level-lock override (e.g. `!phantom
        //  spawn 4 45`) still gets the level-45 damage anchors and
        //  doesn't stay at spawn-time values while the human levels past
        //  it.
        //
        //  If you want phantoms to hit harder / softer, adjust the six
        //  anchor constants — the interpolation and call sites don't
        //  need to change.
        // ================================================================
        // Rebalanced after gear landed: rolled equipment now provides real
        // affix stats, so the synthetic curve only needs to cover the gap
        // between "AI that never dodges or optimizes" and a live player —
        // not simulate an entire BiS loadout. The old anchors (up to 3.0x /
        // +150% / 5000 rating) double-dipped with gear affixes and made
        // phantoms shred everything from level 1 to 60.
        private const float PhantomDmgMultLvl1  = 1.0f;
        private const float PhantomDmgMultLvl60 = 1.6f;
        private const float PhantomDmgPctBonusLvl1  = 0.0f;
        private const float PhantomDmgPctBonusLvl60 = 0.4f;
        private const float PhantomDmgRatingLvl1  = 0f;
        private const float PhantomDmgRatingLvl60 = 1200f;

        // Relics stack for a passive bonus rather than being "equipped"
        // outright, so a single roll at level 1 is nearly worthless. Scale
        // the rolled stack size with the phantom's level instead.
        private const int PhantomRelicStackPerLevel = 5;
        // Level-60 BiS phantoms get a flat stack instead of the per-level
        // formula (5 * 60 = 300) — a real level-60 player's relic stack
        // isn't level-derived, it's just "as many as you've farmed", and
        // 300 reads as undertuned compared to the rest of the BiS loadout.
        private const int PhantomBiSRelicStackFlat = 1000;

        // Follow-stop bounds. 50u = "on top of the target" (old behaviour),
        // 1000u = a comfortable ranged-cast distance well inside the widest
        // player-attack ranges (~1400u for artillery-tier abilities). If a
        // phantom's collection has no usable ranged option we fall back to
        // PhantomFollowStopMin — melee heroes get closed distance the same
        // way real players do.
        private const float PhantomFollowStopMin = 50f;
        private const float PhantomFollowStopMax = 1000f;
        // Margin subtracted from the picked power's range so the phantom
        // stops just inside effective range rather than exactly at the edge
        // (where the target moving away one tick would kick the shot out).
        private const float PhantomFollowRangeMargin = 100f;

        // Ranged-kite tuning. A phantom whose follow-stop distance clears
        // this threshold has a genuine ranged option (not just melee with a
        // 100u margin), so it's classified as "ranged" for kiting purposes.
        // When something closes inside PhantomKiteTooCloseDist, step away by
        // PhantomKiteStepDist instead of holding position or walking closer.
        private const float PhantomKiteRangedThreshold = 300f;
        private const float PhantomKiteTooCloseDist = 150f;
        private const float PhantomKiteStepDist = 200f;

        private static float ComputePhantomFollowStopDist(Avatar phantom, WorldEntity target)
        {
            var pc = phantom.PowerCollection;
            if (pc == null) return PhantomFollowStopMin;

            float bestRange = 0f;
            foreach (var kvp in pc)
            {
                Power power = kvp.Value?.Power;
                if (power == null) continue;
                PowerPrototype pp = power.Prototype;
                if (pp == null) continue;
                if (pp is MovementPowerPrototype) continue;
                if (pp.PowerCategory != PowerCategoryType.NormalPower) continue;
                if (pp.Activation == PowerActivationType.Passive) continue;
                if (pp.IsToggled) continue;
                if (pp.IsTravelPower) continue;
                // Same exclusion as TryPhantomAttack — never used to actually
                // fire, so don't let it bias the follow-stop distance either.
                if (pp.Activation == PowerActivationType.TwoStageTargeted) continue;
                if (power.IsCancelledOnRelease() || power.IsSecondActivateOnRelease()) continue;
                if (power.IsOnCooldown()) continue;

                float r = power.GetRange();
                if (r > bestRange) bestRange = r;
            }

            // FollowEntity's stop distance is measured center-to-center (see
            // Locomotor.IsPathComplete), so the standoff we actually want
            // from the target's SURFACE needs the target's own bounds radius
            // added back on top — otherwise a phantom's "stop 50u from a
            // melee target" reading meant 50u from a boss's CENTER, which for
            // a big-bounds target (e.g. King Lizard, Magneto) is still deep
            // inside the model and unreachable, so it just walked into the
            // collision mesh and sat there short of its own stop distance.
            float targetRadius = target?.Bounds.Radius ?? 0f;

            if (bestRange <= 0f) return PhantomFollowStopMin + targetRadius;

            float dist = Math.Clamp(bestRange - PhantomFollowRangeMargin,
                PhantomFollowStopMin, PhantomFollowStopMax);
            return dist + targetRadius;
        }

        // ================================================================
        //  Phantom gear
        //
        //  Rolls one level-appropriate item per equip slot using the same
        //  data the loot system uses for real drops:
        //  AvatarPrototype.EquipmentInventories declares the slots, and
        //  LootUtilities.BuildInventoryLootPicker resolves every concrete
        //  item prototype that fits a given (avatar, slot) pair from the
        //  loaded client data. Nothing item-specific lives in source.
        //
        //  Besides stats, this un-breaks weapon-gated powers: powers that
        //  returned WeaponMissing (e.g. shield-throw style kits) work once
        //  the hero-specific weapon slot is filled.
        // ================================================================

        // ----------------------------------------------------------------
        //  Gear rarity bands (see GetPhantomGearAllowedRarities for the
        //  data-reality notes on the top tiers):
        //    levels  1-10  → tier 1                       (white)
        //    levels 11-19  → tiers 2-3                    (green/blue)
        //    levels 20-30  → tier 4                       (purple)
        //    levels 31-50  → tier 4 + RarityCosmic         (purple/yellow)
        //    levels 51-60  → RarityCosmic + RarityUnique   (yellow/orange)
        // ----------------------------------------------------------------
        private static readonly object s_rarityTierLock = new();
        private static Dictionary<int, PrototypeId> s_rarityByTier;

        private static void EnsureRarityTiers()
        {
            lock (s_rarityTierLock)
            {
                if (s_rarityByTier != null) return;
                var map = new Dictionary<int, PrototypeId>();
                foreach (PrototypeId rarityRef in DataDirectory.Instance
                    .IteratePrototypesInHierarchy<RarityPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
                {
                    RarityPrototype rarityProto = rarityRef.As<RarityPrototype>();
                    if (rarityProto == null) continue;
                    // First proto wins per tier; the core ladder is a single
                    // DowngradeTo chain so collisions shouldn't happen.
                    map.TryAdd(rarityProto.Tier, rarityRef);
                }
                s_rarityByTier = map;
                PhantomLogger.Info($"[PhantomHero:Gear] rarity tier map built: {map.Count} tiers");
            }
        }

        /// <summary>
        /// The rarities a phantom's CORE ARMOR (Gear01-05) is allowed to end
        /// up at for a given level. This is both the roll pool and the
        /// acceptance filter: some item prototypes carry their own rarity
        /// restriction (red "Ultimate" items, Runeword items), and the spec
        /// builder silently overrides whatever rarity we request to satisfy
        /// it — so forcing the rarity up front is not enough, the FINAL
        /// spec rarity must be validated against this list and off-band
        /// items re-picked (see ApplyPhantomGear's TryBuildInBandSpec).
        ///
        /// Data reality: the DowngradeTo tier chain covers Common(1) →
        /// Uncommon(2) → Rare(3) → Epic(4), but yellow Cosmic and orange
        /// Unique are not on that chain — they're anchored directly by the
        /// engine's LootGlobalsPrototype refs. The chain above Epic holds
        /// the red special rarities (e.g. Ultimate) that must never roll.
        /// </summary>
        private static List<PrototypeId> GetPhantomGearAllowedRarities(int level)
        {
            EnsureRarityTiers();
            var lootGlobals = GameDatabase.LootGlobalsPrototype;
            var allowed = new List<PrototypeId>(2);

            void AddTier(int tier)
            {
                if (s_rarityByTier.TryGetValue(tier, out PrototypeId r) && r != PrototypeId.Invalid)
                    allowed.Add(r);
            }

            if (level <= 10) AddTier(1);
            else if (level <= 19) { AddTier(2); AddTier(3); }
            else if (level <= 30) AddTier(4);
            else if (level <= 50)
            {
                AddTier(4);
                if (lootGlobals.RarityCosmic != PrototypeId.Invalid) allowed.Add(lootGlobals.RarityCosmic);
            }
            else
            {
                if (lootGlobals.RarityCosmic != PrototypeId.Invalid) allowed.Add(lootGlobals.RarityCosmic);
                if (lootGlobals.RarityUnique != PrototypeId.Invalid) allowed.Add(lootGlobals.RarityUnique);
            }

            return allowed;
        }

        /// <summary>
        /// Equip the phantom. If <paramref name="gearOverride"/> is
        /// non-empty, those exact item protos are recreated at the
        /// phantom's level (squad/migration restore); otherwise one random
        /// valid item is rolled per unlocked equip slot. Rarity follows the
        /// level band table above. Returns the applied item proto refs for
        /// descriptor storage.
        /// </summary>
        internal static List<ulong> ApplyPhantomGear(Player phantomPlayer, Avatar phantomAvatar, int level, List<ulong> gearOverride)
        {
            var applied = new List<ulong>();
            AvatarPrototype avatarProto = phantomAvatar.AvatarPrototype;
            if (avatarProto?.EquipmentInventories == null) return applied;

            Game game = phantomAvatar.Game;
            var lootManager = game.LootManager;
            var rng = game.Random;

            bool useOverride = gearOverride != null && gearOverride.Count > 0;
            int overrideIdx = 0;

            List<PrototypeId> allowedRarities = GetPhantomGearAllowedRarities(level);

            // Red "Ultimate" tier — banned from every slot, core or special.
            EnsureRarityTiers();
            s_rarityByTier.TryGetValue(5, out PrototypeId bannedUltimateRef);

            // Level-60 curated Best-in-Slot loadout (see PhantomBiSData for
            // attribution and why heroes/slots missing from the curated
            // file fall through to the normal restricted random-roll path
            // below instead of an unfiltered generator). Looked up once per
            // pass, not per slot.
            Dictionary<EquipmentInvUISlot, PrototypeId> bisLoadout = null;
            if (level >= 60)
                PhantomBiSData.TryGetLoadout(avatarProto.DataRef, out bisLoadout);

            foreach (AvatarEquipInventoryAssignmentPrototype assignment in avatarProto.EquipmentInventories)
            {
                if (assignment.UnlocksAtCharacterLevel > level) continue;

                InventoryPrototype invProto = assignment.Inventory.As<InventoryPrototype>();
                if (invProto == null) continue;
                // The costume slot is driven by the phantom costume system —
                // equipping a rolled costume item here would clobber it.
                if (invProto.ConvenienceLabel == InventoryConvenienceLabel.Costume) continue;
                // Pet and interactive-visual ("flourish") slots have no
                // combat value for an AI companion — they're pure cosmetics
                // that would otherwise burn a roll and clutter the phantom's
                // inventory for nothing. Always skipped, at any level.
                if (assignment.UISlot is EquipmentInvUISlot.Pet or EquipmentInvUISlot.InteractiveVisual) continue;

                // Legendary and UruForged (excluded per server-op direction,
                // 2026-07-13 — a randomly-rolled Legendary silently stayed
                // Unranked/statless) only ever fill via a curated BiS pick
                // now that the Legendary rank-up fix below makes that
                // meaningful. No BiS entry for this exact slot = still
                // skipped, same as before.
                bool hasBiSForThisSlot = bisLoadout != null && bisLoadout.ContainsKey(assignment.UISlot);
                if (assignment.UISlot is EquipmentInvUISlot.Legendary or EquipmentInvUISlot.UruForged
                    && hasBiSForThisSlot == false) continue;

                Inventory equipInventory = phantomAvatar.GetInventoryByRef(assignment.Inventory);
                if (equipInventory == null) continue;

                EquipmentInvUISlot uiSlot = assignment.UISlot;
                bool isCoreGear = IsPhantomGearArmorSlot(uiSlot);

                // Stored ref on restore (consumed even if it fails, to keep
                // slot alignment); random picks otherwise.
                PrototypeId overrideItemRef = PrototypeId.Invalid;
                if (useOverride && overrideIdx < gearOverride.Count)
                    overrideItemRef = (PrototypeId)gearOverride[overrideIdx++];

                PrototypeId bisItemRef = PrototypeId.Invalid;
                bisLoadout?.TryGetValue(uiSlot, out bisItemRef);

                var picker = BuildFilteredPhantomGearPicker(rng, avatarProto.DataRef, uiSlot, game.CustomGameOptions.PhantomGearItemBlacklist);

                try
                {
                    // Two independent things can reject a candidate: (1) its
                    // FINAL built rarity is out of band — an item prototype
                    // can carry its own rarity restriction (red "Ultimate" /
                    // Runeword items live in the same slot pools as normal
                    // gear) that silently overrides whatever we request; (2)
                    // it fails Agent.CanEquip's Requirement property check
                    // (Entity.cs ChangeInventoryLocation → InvalidProperty-
                    // Restriction) — items can carry a baked-in level/stat
                    // requirement independent of the rarity/level we rolled
                    // them at, especially from the unrestricted general Armor
                    // pool below, which has no per-level filtering at all.
                    // Both cases must fall through to the NEXT candidate, not
                    // just abandon the slot — so item creation and the actual
                    // equip attempt live inside the same drained retry loop
                    // as the rarity check. The pool is DRAINED via PickRemove
                    // rather than sampled: every rejected item is removed and
                    // never retried, so if any item in the pool is both
                    // in-band AND actually equippable, it will be found.
                    Item acceptedItem = null;
                    PrototypeId acceptedItemRef = PrototypeId.Invalid;

                    bool TryEquipCandidate(PrototypeId itemProtoRef)
                    {
                        if (itemProtoRef == PrototypeId.Invalid) return false;

                        ItemSpec spec = null;
                        if (isCoreGear)
                        {
                            // Core armor: try every banded rarity, random
                            // start for variety. A Unique-class item may
                            // only build at Unique while a normal piece
                            // only reaches Cosmic — one random rarity per
                            // item would wrongly discard valid items.
                            int rarityCount = Math.Max(1, allowedRarities.Count);
                            int start = rng.Next(0, rarityCount);
                            for (int i = 0; i < rarityCount; i++)
                            {
                                PrototypeId rarityRef = allowedRarities.Count > 0
                                    ? allowedRarities[(start + i) % allowedRarities.Count]
                                    : PrototypeId.Invalid;

                                ItemSpec candidate = lootManager.CreateItemSpec(itemProtoRef, LootContext.Drop, phantomPlayer, level, rarityRef);
                                if (candidate == null) continue;
                                if (allowedRarities.Count > 0 && allowedRarities.Contains(candidate.RarityProtoRef) == false) continue;

                                spec = candidate;
                                break;
                            }
                            if (spec == null) return false;
                        }
                        else
                        {
                            // Special slots (artifacts, medal, relic,
                            // insignia, ring): these item families own their
                            // own rarity ranges — forcing the core armor
                            // band on them excludes their entire pool. Roll
                            // the natural (level-based) rarity instead; only
                            // red Ultimate stays banned.
                            spec = lootManager.CreateItemSpec(itemProtoRef, LootContext.Drop, phantomPlayer, level);
                            if (spec == null) return false;
                            if (bannedUltimateRef != PrototypeId.Invalid && spec.RarityProtoRef == bannedUltimateRef) return false;
                        }

                        // Relics are a stack-size passive, not a single
                        // equippable item — roll the stack scaled to level
                        // so it's actually meaningful (5x level, clamped to
                        // the relic's own max stack size). Level-60 BiS
                        // phantoms get a flat stack instead of the formula's
                        // 300 (60*5) — a real level-60 loadout isn't
                        // level-derived, it's "as many as you've farmed".
                        if (uiSlot == EquipmentInvUISlot.Relic)
                            spec.StackCount = level >= 60 ? PhantomBiSRelicStackFlat : Math.Max(1, level * PhantomRelicStackPerLevel);

                        Item item;
                        using (var itemSettings = ObjectPoolManager.Instance.Get<EntitySettings>())
                        {
                            itemSettings.EntityRef = itemProtoRef;
                            itemSettings.ItemSpec = spec;
                            item = game.EntityManager.CreateEntity(itemSettings) as Item;
                        }
                        if (item == null) return false;

                        if (uiSlot == EquipmentInvUISlot.Relic)
                        {
                            int desiredStack = spec.StackCount;
                            int maxStack = item.Properties[PropertyEnum.InventoryStackSizeMax];
                            if (maxStack > 0) desiredStack = Math.Min(desiredStack, maxStack);
                            item.Properties[PropertyEnum.InventoryStackCount] = desiredStack;
                        }

                        // A rejected candidate here is the EXPECTED common
                        // case, not an error (e.g. an artifact whose real
                        // level requirement is higher than the phantom's
                        // level — a large fraction of Tier1Artifacts falls
                        // into this bucket) — check silently via
                        // CanChangeInventoryLocation first so a routine
                        // rejection doesn't spam a WARN through Verify.
                        // ChangeInventoryLocation itself only runs once
                        // we already know it will succeed.
                        if (item.CanChangeInventoryLocation(equipInventory) != InventoryResult.Success)
                        {
                            item.Destroy();
                            return false;
                        }

                        if (item.ChangeInventoryLocation(equipInventory) != InventoryResult.Success)
                        {
                            item.Destroy();
                            return false;
                        }

                        acceptedItem = item;
                        acceptedItemRef = itemProtoRef;
                        return true;
                    }

                    // Curated BiS pick wins first when available (level 60
                    // only) — a known-good real item beats both a saved
                    // override and a random roll. Falls through to override
                    // then random if this specific BiS item somehow fails
                    // to equip, so a slot never ends up empty just because
                    // one curated pick didn't validate.
                    if (bisItemRef != PrototypeId.Invalid)
                        TryEquipCandidate(bisItemRef);

                    // Stored override item next (squad/migration restore)...
                    if (acceptedItem == null && overrideItemRef != PrototypeId.Invalid)
                        TryEquipCandidate(overrideItemRef);

                    // ...then drain the slot pool until something equips.
                    while (acceptedItem == null && picker.Empty() == false)
                    {
                        if (picker.PickRemove(out Prototype pickedProto) == false || pickedProto == null) break;
                        TryEquipCandidate(pickedProto.DataRef);
                    }

                    // Legendary rank-up: the affix-rank grind is real XP
                    // (confirmed live: rank 3->4 alone needed 240,000,000)
                    // gated behind Item.AwardAffixXP, which internally casts
                    // its long parameter to int before applying — one huge
                    // grant silently overflows into nothing. Loop bounded
                    // calls instead of assuming one call reaches the cap;
                    // each call self-caps via AwardAffixXP's own >= check.
                    // This is why Legendary was skipped entirely before —
                    // an equipped-but-unranked Legendary has no stats at
                    // all. Only reachable via the BiS path above (Legendary
                    // is otherwise skipped for this slot, see above).
                    if (acceptedItem != null && acceptedItem.Prototype is LegendaryPrototype)
                    {
                        int affixLevelCap = acceptedItem.GetAffixLevelCap();
                        for (int guard = 0; guard < 10 && acceptedItem.Properties[PropertyEnum.ItemAffixLevel] < affixLevelCap; guard++)
                            acceptedItem.AwardAffixXP(2_000_000_000L);
                    }

                    // The UniqueAvatarArmor pool is fixed-rarity (RarityUnique
                    // only, confirmed 2026-07-12 via a level-50 phantom coming
                    // up with all 5 armor slots empty while level-51+ filled
                    // fine) — below the level where Unique unlocks, that pool
                    // has nothing that can ever validate. Fall back to the
                    // general Armor pool (still blacklist-filtered) rather
                    // than leave the slot empty for the entire 1-50 range.
                    if (acceptedItem == null && isCoreGear)
                    {
                        var fallbackPicker = BuildFilteredPhantomGearPicker(
                            rng, avatarProto.DataRef, uiSlot, game.CustomGameOptions.PhantomGearItemBlacklist,
                            restrictArmorToUnique: false);
                        while (acceptedItem == null && fallbackPicker.Empty() == false)
                        {
                            if (fallbackPicker.PickRemove(out Prototype pickedProto) == false || pickedProto == null) break;
                            TryEquipCandidate(pickedProto.DataRef);
                        }
                        if (acceptedItem != null)
                            PhantomLogger.Info($"[PhantomHero:Gear] slot {uiSlot} on {avatarProto.DataRef.GetName()} — UniqueAvatarArmor pool had nothing usable at level {level}, filled from general Armor pool instead");
                    }

                    if (acceptedItem == null)
                    {
                        PhantomLogger.Warn($"[PhantomHero:Gear] slot pool for {uiSlot} on {avatarProto.DataRef.GetName()} has NO item usable/equippable at level {level} — slot left empty");
                        continue;
                    }

                    applied.Add((ulong)acceptedItemRef);
                }
                catch (Exception ex)
                {
                    PhantomLogger.Warn($"[PhantomHero:Gear] equip roll for slot {uiSlot} on {phantomAvatar.Id:X} failed: {ex.Message}");
                }
            }

            return applied;
        }

        /// <summary>
        /// What's actually sitting in each gear slot on a live phantom right
        /// now — the direct answer to "what did slot X roll" instead of
        /// making an op comb through a candidate list of a couple hundred
        /// generically-named artifacts to find the one that needs blacklisting.
        /// </summary>
        internal static List<(EquipmentInvUISlot Slot, string ShortName, int StackCount)> GetPhantomEquippedGear(Avatar phantomAvatar)
        {
            var results = new List<(EquipmentInvUISlot, string, int)>();
            AvatarPrototype avatarProto = phantomAvatar.AvatarPrototype;
            if (avatarProto?.EquipmentInventories == null) return results;

            foreach (AvatarEquipInventoryAssignmentPrototype assignment in avatarProto.EquipmentInventories)
            {
                InventoryPrototype invProto = assignment.Inventory.As<InventoryPrototype>();
                if (invProto == null || invProto.ConvenienceLabel == InventoryConvenienceLabel.Costume) continue;

                Inventory equipInventory = phantomAvatar.GetInventoryByRef(assignment.Inventory);
                if (equipInventory == null || equipInventory.Count == 0) continue;

                ulong itemId = equipInventory.GetEntityInSlot(0);
                if (itemId == Entity.InvalidId) continue;

                Item item = phantomAvatar.Game.EntityManager.GetEntity<Item>(itemId);
                if (item == null) continue;

                results.Add((assignment.UISlot, ExtractPrototypeShortName(item.PrototypeDataRef.GetName()), item.CurrentStackSize));
            }

            return results;
        }

        /// <summary>
        /// Builds the actual candidate pool a phantom gear roll draws from
        /// for one slot — raw picker + every phantom-specific restriction
        /// (armor/artifact pool restriction, blacklist). Shared by the real
        /// roll in <see cref="ApplyPhantomGear"/> and the diagnostic
        /// "!phantom gear candidates" command so the two can never diverge.
        /// <paramref name="rng"/> may be null if the caller only enumerates
        /// (e.g. the candidates listing) and never calls Pick().
        /// </summary>
        private static MHServerEmu.Core.Collections.Picker<Prototype> BuildFilteredPhantomGearPicker(
            MHServerEmu.Core.System.Random.GRandom rng, PrototypeId avatarProtoRef, EquipmentInvUISlot slot, string blacklistConfig,
            bool restrictArmorToUnique = true)
        {
            var picker = new MHServerEmu.Core.Collections.Picker<Prototype>(rng);
            LootUtilities.BuildInventoryLootPicker(picker, avatarProtoRef, slot);

            // The 5 general gear slots (Gear01-05) pull from the full Armor
            // item pool by default, which includes generic Armor/Prototypes/
            // items — many of these are stat-less (deprecated/leftover data
            // no longer meant to be equippable). Restrict to
            // Armor/UniquePrototypes/Avatars/, the real itemized hero-armor
            // pool, instead. That pool turned out to be fixed-rarity
            // (RarityUnique only, confirmed 2026-07-12 — a level-50 phantom
            // came up with all 5 armor slots empty while level-51+ filled
            // fine), so callers that need a rarity band below Unique pass
            // restrictArmorToUnique=false to fall back to the general pool
            // once the restricted one has proven to have nothing usable.
            if (restrictArmorToUnique && IsPhantomGearArmorSlot(slot))
                FilterPhantomGearPickerToUniqueAvatarArmor(picker);

            // Artifact slots pull from the full artifact pool by default,
            // which includes non-itemized/special/holiday-visual families.
            // Restrict to Tier1Artifacts — the blacklist below still applies
            // on top of this to cut the handful of TEST-only entries that
            // also live under Tier1Artifacts.
            if (IsPhantomGearArtifactSlot(slot))
                FilterPhantomGearPickerToTier1Artifacts(picker);

            // Server-op-curated exclusion list (artifacts pulled from the
            // game, purely cosmetic rewards, items with known broken icons,
            // etc.) — see PhantomGearItemBlacklist in Config.ini. Applies to
            // every rolled slot, not just armor/artifacts.
            FilterPhantomGearPickerExcludingBlacklist(picker, blacklistConfig);

            return picker;
        }

        private static bool IsPhantomGearArmorSlot(EquipmentInvUISlot slot)
        {
            return slot is EquipmentInvUISlot.Gear01 or EquipmentInvUISlot.Gear02 or EquipmentInvUISlot.Gear03
                or EquipmentInvUISlot.Gear04 or EquipmentInvUISlot.Gear05;
        }

        private static bool IsPhantomGearArtifactSlot(EquipmentInvUISlot slot)
        {
            return slot is EquipmentInvUISlot.Artifact01 or EquipmentInvUISlot.Artifact02
                or EquipmentInvUISlot.Artifact03 or EquipmentInvUISlot.Artifact04;
        }

        /// <summary>
        /// Removes every candidate whose prototype path isn't under
        /// Entity/Items/Artifacts/Prototypes/Tier1Artifacts/ — the only
        /// artifact pool phantoms should draw from per server-op direction.
        /// The handful of TEST-only entries that also live in that folder
        /// are cut separately via PhantomGearItemBlacklist, not here.
        /// </summary>
        private static void FilterPhantomGearPickerToTier1Artifacts(MHServerEmu.Core.Collections.Picker<Prototype> picker)
        {
            for (int i = picker.GetNumElements() - 1; i >= 0; i--)
            {
                if (picker.GetElementAt(i, out Prototype proto) == false || proto == null)
                {
                    picker.RemoveIndex(i);
                    continue;
                }

                string path = proto.DataRef.GetName();
                if (string.IsNullOrEmpty(path) || path.Contains("/Artifacts/Prototypes/Tier1Artifacts/", StringComparison.OrdinalIgnoreCase) == false)
                    picker.RemoveIndex(i);
            }
        }

        /// <summary>
        /// Removes every candidate whose prototype path isn't under
        /// Armor/UniquePrototypes/Avatars/ — the real itemized hero-armor
        /// pool. Leaves the picker empty if the avatar has none for this
        /// slot rather than falling back to the generic (often stat-less)
        /// Armor/Prototypes/ pool.
        /// </summary>
        private static void FilterPhantomGearPickerToUniqueAvatarArmor(MHServerEmu.Core.Collections.Picker<Prototype> picker)
        {
            for (int i = picker.GetNumElements() - 1; i >= 0; i--)
            {
                if (picker.GetElementAt(i, out Prototype proto) == false || proto == null)
                {
                    picker.RemoveIndex(i);
                    continue;
                }

                string path = proto.DataRef.GetName();
                if (string.IsNullOrEmpty(path) || path.Contains("/UniquePrototypes/Avatars/", StringComparison.OrdinalIgnoreCase) == false)
                    picker.RemoveIndex(i);
            }
        }

        /// <summary>
        /// Lists the raw candidate pool (short names) for a single equip
        /// slot on the given avatar, unfiltered by rarity/blacklist — used
        /// by "!phantom gear candidates" so an op can identify item names
        /// to add to PhantomGearItemBlacklist (pulled artifacts, cosmetic-
        /// only rewards, broken icons, etc.) without guessing.
        /// </summary>
        internal static List<string> ListPhantomGearCandidates(PrototypeId avatarProtoRef, EquipmentInvUISlot slot, string blacklistConfig, out int rawCount)
        {
            var rawPicker = new MHServerEmu.Core.Collections.Picker<Prototype>();
            LootUtilities.BuildInventoryLootPicker(rawPicker, avatarProtoRef, slot);
            rawCount = rawPicker.GetNumElements();

            var names = new List<string>();
            var picker = BuildFilteredPhantomGearPicker(null, avatarProtoRef, slot, blacklistConfig);
            for (int i = 0; i < picker.GetNumElements(); i++)
            {
                if (picker.GetElementAt(i, out Prototype proto) && proto != null)
                    names.Add(ExtractPrototypeShortName(proto.DataRef.GetName()));
            }

            return names;
        }

        /// <summary>
        /// Removes every candidate whose prototype path contains any
        /// substring from the (comma-separated) blacklist config value.
        /// Read fresh each roll so ops can tune the list in Config.ini
        /// without a rebuild — just a server restart.
        /// </summary>
        private static void FilterPhantomGearPickerExcludingBlacklist(MHServerEmu.Core.Collections.Picker<Prototype> picker, string blacklistConfig)
        {
            if (string.IsNullOrWhiteSpace(blacklistConfig)) return;

            string[] terms = blacklistConfig.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (terms.Length == 0) return;

            for (int i = picker.GetNumElements() - 1; i >= 0; i--)
            {
                if (picker.GetElementAt(i, out Prototype proto) == false || proto == null)
                {
                    picker.RemoveIndex(i);
                    continue;
                }

                string path = proto.DataRef.GetName();
                if (string.IsNullOrEmpty(path)) continue;

                foreach (string term in terms)
                {
                    if (term.Length == 0) continue;
                    if (path.Contains(term, StringComparison.OrdinalIgnoreCase))
                    {
                        picker.RemoveIndex(i);
                        break;
                    }
                }
            }
        }

        private static void ApplyPhantomDamageScaling(Avatar phantom, int level)
        {
            float t = Math.Clamp((level - 1) / 59f, 0f, 1f);
            t *= t; // quadratic — shallow through story levels, steep into endgame
            float dmgMult   = PhantomDmgMultLvl1     + t * (PhantomDmgMultLvl60     - PhantomDmgMultLvl1);
            float pctBonus  = PhantomDmgPctBonusLvl1 + t * (PhantomDmgPctBonusLvl60 - PhantomDmgPctBonusLvl1);
            float dmgRating = PhantomDmgRatingLvl1   + t * (PhantomDmgRatingLvl60   - PhantomDmgRatingLvl1);

            phantom.Properties[PropertyEnum.DamageMult]     = dmgMult;
            phantom.Properties[PropertyEnum.DamagePctBonus] = pctBonus;
            phantom.Properties[PropertyEnum.DamageRating]   = dmgRating;
        }

        // Rate-limit the "why isn't my phantom attacking" dump to at most one
        // per phantom every 5 seconds so a 500ms tick doesn't spam the log.
        private static readonly Dictionary<ulong, long> s_phantomNextDiagMs = new();
        private const long PhantomDiagIntervalMs = 5000;

        private bool ShouldEmitPhantomDiag(ulong phantomId)
        {
            long now = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            if (s_phantomNextDiagMs.TryGetValue(phantomId, out long nextAt) && now < nextAt) return false;
            s_phantomNextDiagMs[phantomId] = now + PhantomDiagIntervalMs;
            return true;
        }

        private static void DumpPhantomHuntDiag(Avatar phantom, Vector3 phantomPos, WorldEntity picked,
            float pickedDistSq, List<(WorldEntity we, float distSq, string reason)> rejected,
            Region region, Sphere sweepSphere, MHServerEmu.Games.Entities.EntityRegionSPContext ctx)
        {
            // Full state dump of every hostile Agent in the sweep sphere so we
            // can identify exactly which flag is stopping the attack on a
            // cutscene boss. Runs at most once per 5s per phantom.
            var sb = new System.Text.StringBuilder();
            sb.Append($"[PhantomHero:Diag] {phantom} pos={phantomPos.ToStringNames()} ");
            if (picked != null)
                sb.Append($"picked={picked} dist={MathF.Sqrt(pickedDistSq):F0}");
            else
                sb.Append("picked=<none>");

            int n = 0;
            foreach (WorldEntity we in region.IterateEntitiesInVolume(sweepSphere, ctx))
            {
                if (we == null || we.Id == phantom.Id) continue;
                if (we is not Agent) continue;
                if (we.IsDead) continue;
                if (phantom.IsHostileTo(we) == false) continue;
                if (n++ >= 8) { sb.Append(" ...(more truncated)"); break; }
                float d = Vector3.Distance2D(we.RegionLocation.Position, phantomPos);
                string allianceRef = we.Alliance != null ? we.Alliance.DataRef.GetName() : "<null>";
                sb.Append($" | {we} dist={d:F0} dormant={we.IsDormant} untargetable={we.IsUntargetable} unaffectable={we.IsUnaffectable} affectedByPowers={we.IsAffectedByPowers()} sim={we.IsSimulated} inWorld={we.IsInWorld} alliance={allianceRef}");
            }
            if (rejected != null)
            {
                sb.Append(" | rejected=[");
                for (int i = 0; i < rejected.Count && i < 5; i++)
                    sb.Append($"{rejected[i].we}({rejected[i].reason},{MathF.Sqrt(rejected[i].distSq):F0}) ");
                sb.Append(']');
            }
            PhantomLogger.Info(sb.ToString());
        }

        // Below this fraction of max HP, a phantom reaches for its own
        // medkit instead of only ever waiting to be revived. Own cooldown
        // (not the heal power's own cooldown data) because the heal only
        // restores a percentage, not to full — without a floor a phantom
        // sitting under the threshold would spend nearly every 500ms tick
        // healing instead of fighting.
        private const float PhantomSelfHealHpFraction = 0.35f;
        private const long PhantomSelfHealCooldownMs = 8000;
        private static readonly Dictionary<ulong, long> s_phantomNextSelfHealMs = new();

        /// <summary>
        /// Casts the same innate medkit power a real player uses on
        /// themselves (GlobalsPrototype.AvatarHealPower, Self-shaped /
        /// WillTargetCaster) when the phantom is its own caster AND target —
        /// the exact mechanic a real player already exercises on themselves,
        /// so no special hook is needed the way phantom-as-TARGET-of-the-
        /// HUMAN's-medkit needed one (see WorldEntity.ApplyHealthPowerResults,
        /// which fully heals a caller's phantoms when the HUMAN uses their
        /// medkit — a different, separate mechanic from this one).
        /// </summary>
        private static bool TryPhantomSelfHeal(Avatar phantom, MHServerEmu.Core.System.Random.GRandom rng)
        {
            long healthMax = phantom.Properties[PropertyEnum.HealthMax];
            if (healthMax <= 0) return false;
            long health = phantom.Properties[PropertyEnum.Health];
            if (health > (long)(healthMax * PhantomSelfHealHpFraction)) return false;

            long nowMs = phantom.Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            if (s_phantomNextSelfHealMs.TryGetValue(phantom.Id, out long nextAt) && nowMs < nextAt) return false;

            PrototypeId healPowerRef = GameDatabase.GlobalsPrototype?.AvatarHealPower ?? PrototypeId.Invalid;
            if (healPowerRef == PrototypeId.Invalid) return false;
            Power healPower = phantom.GetPower(healPowerRef);
            if (healPower == null || healPower.IsOnCooldown()) return false;

            Vector3 selfPos = phantom.RegionLocation.Position;
            int fxSeed = rng.Next(1, 10000);
            var settings = new PowerActivationSettings(phantom.Id, selfPos, selfPos)
            {
                Flags = PowerActivationSettingsFlags.NotifyOwner,
                FXRandomSeed = fxSeed,
                PowerRandomSeed = fxSeed,
            };
            PowerUseResult result = phantom.ActivatePower(healPowerRef, ref settings);
            // Set the cooldown even on a non-Success result — a rejection
            // (e.g. HealingBlocked) won't clear on its own within the same
            // tick either, and retrying every 500ms against a standing
            // rejection is the same "spam a broken action" shape the power
            // blacklist above exists to prevent.
            s_phantomNextSelfHealMs[phantom.Id] = nowMs + PhantomSelfHealCooldownMs;
            if (result == PowerUseResult.Success)
                PhantomLogger.Info($"[PhantomHero:SelfHeal] {phantom} used medkit at {health}/{healthMax} HP");
            return result == PowerUseResult.Success;
        }

        // Only bother looking for a support power at all if someone's below
        // this fraction of max HP — otherwise a support-capable phantom
        // would burn its buff on a squad that's already fine.
        private const float PhantomSupportBuffNeedFraction = 0.7f;
        private const float PhantomSupportBuffAllyRange = 2500f;
        private const float PhantomSupportBuffAllyRangeSq = PhantomSupportBuffAllyRange * PhantomSupportBuffAllyRange;
        // Own cooldown floor, independent of the chosen power's actual
        // cooldown data — a rejected activation (out of range, LOS, etc.)
        // shouldn't be retried every single 500ms tick.
        private const long PhantomSupportBuffCooldownMs = 5000;
        private static readonly Dictionary<ulong, long> s_phantomNextSupportBuffMs = new();

        /// <summary>
        /// Any phantom whose kit happens to include a friendly-targeting
        /// power (TargetingReachPrototype.TargetsFriendly) — a heal or buff,
        /// not an attack — uses it on whichever ally (the human, or a
        /// squadmate) is hurt worst, instead of that power just never being
        /// considered because the rest of the AI only ever looks at hostile
        /// targets. Self-centered AOE heals target the phantom itself (the
        /// AOE naturally reaches nearby allies); single-target reach powers
        /// target the neediest ally directly.
        /// </summary>
        private static bool TryPhantomSupportBuff(Avatar phantom, MHServerEmu.Core.System.Random.GRandom rng)
        {
            long nowMs = phantom.Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
            if (s_phantomNextSupportBuffMs.TryGetValue(phantom.Id, out long nextAt) && nowMs < nextAt) return false;

            var pc = phantom.PowerCollection;
            if (pc == null) return false;

            Player host = phantom.PhantomHost;
            Region region = phantom.Region;
            if (region == null) return false;
            Vector3 phantomPos = phantom.RegionLocation.Position;

            WorldEntity neediest = null;
            float neediestFrac = PhantomSupportBuffNeedFraction;

            void Consider(WorldEntity candidate)
            {
                if (candidate == null || candidate.IsDead || candidate.IsInWorld == false) return;
                if (candidate.Region != region) return;
                long hpMax = candidate.Properties[PropertyEnum.HealthMax];
                if (hpMax <= 0) return;
                float frac = (float)((long)candidate.Properties[PropertyEnum.Health]) / hpMax;
                if (frac >= neediestFrac) return;
                if (Vector3.DistanceSquared2D(candidate.RegionLocation.Position, phantomPos) > PhantomSupportBuffAllyRangeSq) return;
                neediestFrac = frac;
                neediest = candidate;
            }

            Consider(host?.CurrentAvatar);
            if (host != null)
            {
                var squadIds = host.PhantomAvatarIds;
                for (int i = 0; i < squadIds.Count; i++)
                {
                    if (squadIds[i] == phantom.Id) continue;
                    Consider(phantom.Game.EntityManager.GetEntity<Avatar>(squadIds[i]));
                }
            }
            Consider(phantom);

            if (neediest == null) return false; // nobody needs help right now

            foreach (var kvp in pc)
            {
                Power power = kvp.Value?.Power;
                if (power == null) continue;
                PowerPrototype pp = power.Prototype;
                if (pp == null) continue;
                if (pp is MovementPowerPrototype) continue;
                if (pp.PowerCategory != PowerCategoryType.NormalPower) continue;
                if (pp.Activation == PowerActivationType.Passive) continue;
                if (pp.IsToggled) continue;
                if (pp.IsTravelPower) continue;
                if (pp.Activation == PowerActivationType.TwoStageTargeted) continue;
                if (power.IsCancelledOnRelease()) continue;
                if (power.IsOnCooldown()) continue;

                var reach = pp.GetTargetingReach();
                // Excludes powers that ALSO target enemies (ambiguous —
                // could be an attack with an incidental friendly clause);
                // only powers that are friendly-only count as "support."
                if (reach == null || reach.TargetsFriendly == false || reach.TargetsEnemy) continue;

                var style = pp.GetTargetingStyle();
                bool selfCentered = style != null && (style.AOESelfCentered || style.TargetingShape == TargetingShapeType.Self);
                WorldEntity effectiveTarget = selfCentered ? phantom : neediest;

                float range = power.GetRange();
                if (selfCentered == false && range > 0f && phantom.GetDistanceTo(effectiveTarget, true) > range) continue;

                Vector3 tgtPos = effectiveTarget.RegionLocation.Position;
                int fxSeed = rng.Next(1, 10000);
                var settings = new PowerActivationSettings(effectiveTarget.Id, tgtPos, phantomPos)
                {
                    Flags = PowerActivationSettingsFlags.NotifyOwner,
                    FXRandomSeed = fxSeed,
                    PowerRandomSeed = fxSeed,
                };
                PowerUseResult result = phantom.ActivatePower(power.PrototypeDataRef, ref settings);
                s_phantomNextSupportBuffMs[phantom.Id] = nowMs + PhantomSupportBuffCooldownMs;
                if (result == PowerUseResult.Success)
                    PhantomLogger.Info($"[PhantomHero:Support] {phantom} used {pp.DataRef.GetName()} on {effectiveTarget}");
                return result == PowerUseResult.Success;
            }
            return false;
        }

        private PowerUseResult TryPhantomAttack(Avatar phantom, WorldEntity target, float targetDistSq, MHServerEmu.Core.System.Random.GRandom rng)
        {
            if (target == null || phantom.PowerCollection == null) return PowerUseResult.GenericError;
            Vector3 phantomPos = phantom.RegionLocation.Position;

            // Refill Endurance so InsufficientEndurance doesn't gate every non-basic
            // power. Phantom has no resource regen wiring; we just keep the pool at
            // ceiling. Loop across every ManaType the avatar declares so multi-pool
            // heroes (Iron Man / Nova / Storm) all get topped up.
            foreach (PrimaryResourceManaBehaviorPrototype manaBehavior in phantom.GetPrimaryResourceManaBehaviors())
            {
                var manaType = manaBehavior.ManaType;
                float max = phantom.Properties[PropertyEnum.EnduranceMax, manaType];
                if (max > 0) phantom.Properties[PropertyEnum.Endurance, manaType] = max;
            }

            // Edge-to-edge, not center-to-center — same helper WorldEntity.cs
            // itself uses for its own range math (GetDistanceTo(calcRadius:
            // true)). Against a big-bounds target (a raid boss) center
            // distance stays large long after the phantom is standing at the
            // model's surface, so a raw-center range gate below rejected
            // every power whose real reach was actually sufficient — the
            // same bug TruSkillzzRuns independently found and fixed ("range
            // check measuring distance to center instead of edge").
            float targetEdgeDist = phantom.GetDistanceTo(target, true);

            // Build the candidate list with real prioritization instead of pure
            // reservoir sampling.
            //
            //   Filters (hard rejects):
            //     - is a Movement / Travel / Passive / Toggled power
            //     - is not NormalPower category
            //     - power.GetRange() < target distance (would return OutOfPosition)
            //     - power is currently on cooldown
            //     - ultimates: additionally gated behind a 20-minute
            //       per-phantom timer (see s_phantomNextUltimateMs)
            //
            //   Score = cooldown duration in ms (used as a proxy for hit weight —
            //   powers with longer cooldowns are baked bigger, and it's the only
            //   universal numeric signal we can get without a per-hero damage table).
            //
            //   Pick strategy: a ready ultimate wins outright — 20 minutes
            //   apart it should never lose a coin flip to a basic attack.
            //   Otherwise sort survivors by score desc, weighted-random among
            //   the top 5. Favors real cooldown-worthy hits while still varying,
            //   and always fires the basic (0 cd) when nothing bigger is available.
            var candidates = ListPool<(PrototypeId, long)>.Instance.Get();
            try
            {
                long nowMs = Game.CurrentTime.Ticks / TimeSpan.TicksPerMillisecond;
                PrototypeId readyUltimate = PrototypeId.Invalid;

                foreach (var kvp in phantom.PowerCollection)
                {
                    PowerCollectionRecord rec = kvp.Value;
                    Power power = rec?.Power;
                    if (power == null) continue;
                    PowerPrototype pp = power.Prototype;
                    if (pp == null) continue;
                    if (pp is MovementPowerPrototype) continue;
                    if (pp.PowerCategory != PowerCategoryType.NormalPower) continue;
                    if (pp.Activation == PowerActivationType.Passive) continue;
                    if (pp.IsToggled) continue;
                    if (pp.IsTravelPower) continue;

                    // Skip powers whose lifecycle depends on a follow-up client
                    // signal a phantom can never send (second-click confirm on a
                    // targeted reticle, etc). Activating one leaves the power's
                    // activation phase stuck non-Inactive forever, which then
                    // rejects EVERY future activation attempt on ANY power — the
                    // "skill locked mid-animation" bug. Charge-and-release
                    // powers (IsSecondActivateOnRelease) are handled below
                    // instead of excluded — see the ReleaseVariableActivation
                    // call after a successful activation.
                    if (pp.Activation == PowerActivationType.TwoStageTargeted) continue;
                    if (power.IsCancelledOnRelease()) continue;

                    float pRange = power.GetRange();
                    if (pRange > 0f && pRange + 50f < targetEdgeDist) continue;

                    if (power.IsOnCooldown()) continue;

                    // Skip powers that recently failed with power-specific
                    // errors (RestrictiveCondition / WeaponMissing) — they
                    // won't start working by themselves, and their big
                    // cooldown weights would otherwise get them picked every
                    // single tick.
                    if (IsPhantomPowerBlacklisted(phantom.Id, rec.PowerPrototypeRef, nowMs)) continue;

                    // Ultimates fire on any target, but at most once per 20
                    // minutes per phantom (on top of whatever cooldown the
                    // power data itself carries).
                    string pName = pp.DataRef.GetName() ?? string.Empty;
                    if (pName.EndsWith("Ultimate.prototype", StringComparison.Ordinal))
                    {
                        if (s_phantomNextUltimateMs.TryGetValue(phantom.Id, out long ultReadyAt) && nowMs < ultReadyAt)
                            continue;
                        readyUltimate = rec.PowerPrototypeRef;
                        continue; // not part of the weighted pool — it wins outright below
                    }

                    long cdMs = (long)power.GetCooldownDuration().TotalMilliseconds;

                    // AoE awareness: weigh area powers by how many hostiles
                    // they'd actually hit around the target, instead of
                    // scoring them purely by cooldown like every other
                    // power. A lone target gets a heavy penalty (don't waste
                    // a cone attack on one guy) and a real pack gets a
                    // scaling bonus (favor the AoE over poking the group one
                    // at a time), capped so a huge pack doesn't completely
                    // drown out every single-target option.
                    if (power.TargetsAOE())
                    {
                        int hitCount = CountHostilesNear(phantom, target.RegionLocation.Position, power.GetAOERadius());
                        cdMs = hitCount >= 2
                            ? (long)(cdMs * (1.0 + 0.5 * Math.Min(hitCount, 6)))
                            : (long)(cdMs * 0.4);
                    }

                    candidates.Add((rec.PowerPrototypeRef, cdMs));
                }

                if (candidates.Count == 0 && readyUltimate == PrototypeId.Invalid)
                    return PowerUseResult.OutOfPosition;

                PrototypeId chosenPower;
                bool chosenIsUltimate = readyUltimate != PrototypeId.Invalid;
                if (chosenIsUltimate)
                {
                    chosenPower = readyUltimate;
                }
                else
                {
                    // Sort by cooldown desc — biggest hitter first.
                    candidates.Sort(static (a, b) => b.Item2.CompareTo(a.Item2));

                    // Take top 5 (or fewer). Weighted-random pick — weight = 1 + cooldownMs/1000
                    // so a 5s power is ~6x more likely than a basic (0s) attack.
                    //
                    // NOTE: this used to read `(long)rng.NextDouble() * totalWeight * 1000L`,
                    // which casts NextDouble()'s [0,1) result to long BEFORE multiplying —
                    // that truncates to 0 every time, so `roll` was always 0 and candidates[0]
                    // (the single highest-cooldown power) won unconditionally. Phantoms never
                    // actually varied their attacks; cast the product, not the double.
                    int take = Math.Min(5, candidates.Count);
                    long totalWeight = 0;
                    for (int i = 0; i < take; i++) totalWeight += 1 + (candidates[i].Item2 / 1000);
                    long roll = (long)(rng.NextDouble() * totalWeight);
                    if (roll >= totalWeight) roll = totalWeight - 1;

                    chosenPower = candidates[0].Item1;
                    long acc = 0;
                    for (int i = 0; i < take; i++)
                    {
                        long w = 1 + (candidates[i].Item2 / 1000);
                        acc += w;
                        if (roll < acc) { chosenPower = candidates[i].Item1; break; }
                    }
                }
                candidates.Clear();

                // Pre-generate FXRandomSeed BEFORE the call so it's stored
                // in settings.FXRandomSeed. Without this the seed is 0,
                // ArchiveMessageBuilder auto-generates a random one for
                // the outbound NetMessageActivatePower (line 357), but
                // that generated seed never gets back into the
                // PowerApplication / PowerPayload / PowerResult. Client
                // sees ActivatePower(fxSeed=N) then PowerResult(fxSeed=0)
                // — mismatched. Body-emitter particle systems that vary
                // by seed treat 0 as "no effect" or drop it because the
                // cast doesn't correlate to the hit. Missile / projectile
                // FX works even with seed=0 because those spawn from a
                // separate Missile entity.
                //
                // ServerCombo forces the broadcast path (Power.cs line
                // 3680) to include the owner client and skip the combo-
                // effect early-out.
                int fxSeed = rng.Next(1, 10000);
                var settings = new PowerActivationSettings(target.Id, target.RegionLocation.Position, phantomPos)
                {
                    Flags = PowerActivationSettingsFlags.NotifyOwner | PowerActivationSettingsFlags.ServerCombo,
                    FXRandomSeed = fxSeed,
                    PowerRandomSeed = fxSeed,
                };
                var result = phantom.ActivatePower(chosenPower, ref settings);

                // Charge-and-release powers: the first activation only
                // STARTS the charge and waits for a button-release message
                // that will never come (no client). Release immediately —
                // ReleaseVariableActivation schedules the actual firing at
                // the power's MinReleaseTimeMS on the game scheduler, so
                // the shot still charges the minimum time and then goes
                // off on its own. Without this, phantoms wound up holding
                // the charge until the stuck-power watchdog cancelled it
                // 5 seconds later, and the power never fired at all.
                if (result == PowerUseResult.Success)
                {
                    Power chosenPowerInstance = phantom.PowerCollection?.GetPower(chosenPower);
                    if (chosenPowerInstance?.Prototype?.ExtraActivation is SecondaryActivateOnReleasePrototype)
                    {
                        try { chosenPowerInstance.ReleaseVariableActivation(ref settings); }
                        catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] ReleaseVariableActivation({chosenPower.GetName()}) failed on {phantom.Id:X}: {ex.Message}"); }
                    }
                }

                // Start the 20-minute ultimate timer only on a successful
                // cast — a rejected attempt (target died mid-windup etc.)
                // shouldn't burn the ult for the next 20 minutes.
                if (chosenIsUltimate && result == PowerUseResult.Success)
                    s_phantomNextUltimateMs[phantom.Id] = nowMs + PhantomUltimateCooldownMs;

                // Power-specific failures won't clear by retrying with a
                // different target — park the power so the picker falls
                // back to ones that actually work (see s_phantomPowerBlacklist).
                if (result == PowerUseResult.RestrictiveCondition)
                    s_phantomPowerBlacklist[(phantom.Id, chosenPower)] = nowMs + PhantomPowerBlacklistTransientMs;
                else if (result == PowerUseResult.WeaponMissing)
                    s_phantomPowerBlacklist[(phantom.Id, chosenPower)] = nowMs + PhantomPowerBlacklistStructuralMs;

                // Log every failed activation so we can see WHY a cutscene boss
                // rejects the phantom's power (Dormant/Unaffectable/etc). Log
                // successes only once per (phantom, target) pair to avoid spam.
                bool logThis = result != PowerUseResult.Success;
                ulong key = phantom.Id ^ (target.Id * 0x9E3779B97F4A7C15UL);
                if (!logThis && s_phantomAttackTargetLogged.Add(key)) logThis = true;
                if (logThis)
                {
                    Power probePower = phantom.PowerCollection?.GetPower(chosenPower);
                    bool isValid = probePower != null && probePower.IsValidTarget(target);
                    string allianceRef = target.Alliance != null ? target.Alliance.DataRef.GetName() : "<null>";
                    PhantomLogger.Info($"[PhantomHero:Attack] {phantom} → target={target} power={chosenPower.GetName()} result={result} isValidTarget={isValid} tgtDormant={target.IsDormant} tgtUntargetable={target.IsUntargetable} tgtUnaffectable={target.IsUnaffectable} tgtAffectedByPowers={target.IsAffectedByPowers()} tgtSim={target.IsSimulated} tgtInWorld={target.IsInWorld} tgtAlliance={allianceRef} phantomAlliance={(phantom.Alliance?.DataRef.GetName() ?? "<null>")}");
                }
                return result;
            }
            finally { ListPool<(PrototypeId, long)>.Instance.Return(candidates); }
        }

        private class PhantomTickEvent : CallMethodEvent<Avatar>
        {
            protected override CallbackDelegate GetCallback() => (avatar) => avatar.OnPhantomTick();
        }

        // Cached list of resolvable pool entries. Filled lazily on first call
        // so we can log which paths fail once and pull them out of the rotation.
        private static readonly object s_phantomResolvedLock = new();
        private static List<PrototypeId> s_phantomResolved;

        private static void EnsureResolvedPool()
        {
            lock (s_phantomResolvedLock)
            {
                if (s_phantomResolved != null) return;
                var resolved = new List<PrototypeId>(64);
                // Same iteration the login pipeline (PlayerConnection), the
                // equipment tables, and PowerCommands use to get "every real
                // playable hero for this client." Guarantees the pool tracks
                // the loaded client version exactly.
                foreach (PrototypeId avatarRef in DataDirectory.Instance
                    .IteratePrototypesInHierarchy<AvatarPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
                {
                    if (avatarRef == PrototypeId.Invalid) continue;
                    if (avatarRef.As<AvatarPrototype>() == null) continue;
                    resolved.Add(avatarRef);
                }
                s_phantomResolved = resolved;
                PhantomLogger.Info($"[PhantomHero] pool built from client data: {resolved.Count} playable avatars");
            }
        }

        /// <summary>
        /// Match a user-typed hero name against the playable-avatar pool
        /// resolved from the loaded client data. Matching is entirely
        /// runtime — hero names come from the user's own data files and
        /// their chat input, never from this source tree. Exact short-name
        /// match (case-insensitive) wins immediately; otherwise all
        /// substring matches are returned so the caller can ask the user
        /// to be more specific.
        /// </summary>
        public static List<(PrototypeId AvatarRef, string ShortName)> FindPhantomHeroRefs(string query)
        {
            var results = new List<(PrototypeId, string)>();
            if (string.IsNullOrWhiteSpace(query)) return results;
            EnsureResolvedPool();

            lock (s_phantomResolvedLock)
            {
                foreach (PrototypeId avatarRef in s_phantomResolved)
                {
                    string shortName = ExtractPrototypeShortName(avatarRef.GetName());
                    if (shortName.Equals(query, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Clear();
                        results.Add((avatarRef, shortName));
                        return results;
                    }
                    if (shortName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        results.Add((avatarRef, shortName));
                }
            }

            return results;
        }

        /// <summary>
        /// All approved costumes usable by the given avatar, resolved from
        /// the loaded client data (CostumePrototype.UsableBy). Like the
        /// hero pool, this is entirely runtime — no costume names live in
        /// server source.
        /// </summary>
        public static List<(PrototypeId CostumeRef, string ShortName)> GetCostumesForAvatar(PrototypeId avatarRef)
        {
            var results = new List<(PrototypeId, string)>();
            if (avatarRef == PrototypeId.Invalid) return results;

            foreach (PrototypeId costumeRef in DataDirectory.Instance
                .IteratePrototypesInHierarchy<CostumePrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                CostumePrototype costumeProto = costumeRef.As<CostumePrototype>();
                if (costumeProto == null) continue;
                if (costumeProto.UsableBy != avatarRef) continue;
                if (costumeProto.CostumeUnrealClass == AssetId.Invalid) continue;
                results.Add((costumeRef, ExtractPrototypeShortName(costumeRef.GetName())));
            }

            return results;
        }

        /// <summary>
        /// Match a user-typed costume name against the avatar's costume
        /// pool. Exact short-name match wins; otherwise all substring
        /// matches are returned.
        /// </summary>
        public static List<(PrototypeId CostumeRef, string ShortName)> FindCostumeRefs(PrototypeId avatarRef, string query)
        {
            var all = GetCostumesForAvatar(avatarRef);
            var results = new List<(PrototypeId, string)>();
            if (string.IsNullOrWhiteSpace(query)) return results;

            foreach (var entry in all)
            {
                if (entry.ShortName.Equals(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Clear();
                    results.Add(entry);
                    return results;
                }
                if (entry.ShortName.Contains(query, StringComparison.OrdinalIgnoreCase))
                    results.Add(entry);
            }

            return results;
        }

        /// <summary>Pick a random costume for the avatar, or Invalid if it has none.</summary>
        public static PrototypeId PickRandomCostume(PrototypeId avatarRef, MHServerEmu.Core.System.Random.GRandom rng)
        {
            var pool = GetCostumesForAvatar(avatarRef);
            if (pool.Count == 0) return PrototypeId.Invalid;
            return pool[rng.Next(0, pool.Count)].CostumeRef;
        }

        /// <summary>
        /// "Entity/Characters/Avatars/Shipping/SomeHero.prototype" → "SomeHero".
        /// </summary>
        private static string ExtractPrototypeShortName(string prototypePath)
        {
            if (string.IsNullOrEmpty(prototypePath)) return string.Empty;
            int slash = prototypePath.LastIndexOf('/');
            string fileName = slash >= 0 ? prototypePath[(slash + 1)..] : prototypePath;
            const string suffix = ".prototype";
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                fileName = fileName[..^suffix.Length];
            return fileName;
        }

        private PrototypeId NextPhantomHeroRef()
        {
            EnsureResolvedPool();
            lock (s_phantomDeckLock)
            {
                if (s_phantomResolved.Count == 0) return PrototypeId.Invalid;
                if (s_phantomDeckIdx >= s_phantomDeck.Count)
                {
                    s_phantomDeck.Clear();
                    for (int i = 0; i < s_phantomResolved.Count; i++) s_phantomDeck.Add(i);
                    for (int i = s_phantomDeck.Count - 1; i > 0; i--)
                    {
                        int j = Game.Random.Next(0, i + 1);
                        (s_phantomDeck[i], s_phantomDeck[j]) = (s_phantomDeck[j], s_phantomDeck[i]);
                    }
                    s_phantomDeckIdx = 0;
                }
                int idx = s_phantomDeck[s_phantomDeckIdx++];
                return s_phantomResolved[idx];
            }
        }

        /// <summary>
        /// Spawns a phantom hero (real AvatarPrototype) at a random offset from
        /// this avatar. Returns the Avatar entity id, or 0 with a reason in
        /// <paramref name="error"/>.
        /// </summary>
        public ulong SpawnPhantomHero(int levelOverride, string username, out string error)
            // A non-zero levelOverride from the chat command means the user
            // explicitly asked for a specific level (e.g. `!phantom spawn 4 45`).
            // Lock that level in — the tick loop will not auto-level these
            // phantoms as the caller gains XP. Costume 0 = roll random,
            // gear null = roll random per slot.
            => SpawnPhantomHeroCore(PrototypeId.Invalid, levelOverride, username, levelOverride > 0, 0, null, out error);

        /// <summary>
        /// Respawns a phantom from a MigrationData intent — same avatarRef +
        /// level + username + LockLevel + costume as the pre-transfer state.
        /// Used by Player.RestorePhantomsFromMigration after cross-region
        /// travel and by saved-squad spawns.
        /// </summary>
        public ulong SpawnPhantomHeroFromIntent(PrototypeId avatarRefOverride, int level, string username, bool lockLevel, ulong costumeRef, out string error, List<ulong> gearRefs = null)
            => SpawnPhantomHeroCore(avatarRefOverride, level, username, lockLevel, costumeRef, gearRefs, out error);

        /// <summary>
        /// Comma-separated substrings matched against the region's full prototype path
        /// (PhantomHeroesExcludedRegions config) - same pattern as RogueNemesisManager.IsExcludedRegion.
        /// Used to keep phantoms out of content that scales around a fixed player count
        /// (e.g. wave-battle events), where N players each bringing up to 9 phantoms would
        /// blow past the intended scaling.
        /// </summary>
        private bool IsPhantomExcludedRegion(Region region)
        {
            if (region?.Prototype == null) return false;

            string patterns = Game.CustomGameOptions?.PhantomHeroesExcludedRegions;
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

        private ulong SpawnPhantomHeroCore(PrototypeId avatarRefOverride, int levelOverride, string username, bool lockLevel, ulong costumeRef, List<ulong> gearRefs, out string error)
        {
            error = null;
            if (IsInWorld == false) { error = "avatar not in world"; return 0; }

            Region region = Region;
            if (region == null) { error = "no region"; return 0; }

            if (IsPhantomExcludedRegion(region)) { error = "phantoms are disabled in this region"; return 0; }

            int maxActive = Game.CustomGameOptions.PhantomHeroesMaxActive;
            if (PhantomHeroCount >= maxActive) { error = $"phantom cap reached ({PhantomHeroCount}/{maxActive})"; return 0; }

            PrototypeId avatarRef = avatarRefOverride != PrototypeId.Invalid ? avatarRefOverride : NextPhantomHeroRef();
            if (avatarRef == PrototypeId.Invalid) { error = "hero ref resolve failed"; return 0; }

            AvatarPrototype avatarProto = avatarRef.As<AvatarPrototype>();
            if (avatarProto == null) { error = "not an AvatarPrototype"; return 0; }

            // Step 1: phantom Player entity, no PlayerConnection. The Player
            // is created inside this Game's EntityManager so InventoryLocation
            // ContainerId lookups (used by Avatar.ApplyInitialReplicationState)
            // resolve locally without touching the login/DB path.
            ulong phantomDbId = System.Threading.Interlocked.Increment(ref s_phantomDbIdSeed);
            // If caller didn't supply a name, mint a comic-book-flavored one so
            // nameplates read "CrimsonFalcon042" instead of "Bot001".
            if (string.IsNullOrEmpty(username))
                username = NewPhantomUsername(Game.Random);
            Player phantomPlayer;
            using (var playerSettings = ObjectPoolManager.Instance.Get<EntitySettings>())
            {
                playerSettings.DbGuid = phantomDbId;
                playerSettings.EntityRef = GameDatabase.GlobalsPrototype.DefaultPlayer;
                playerSettings.OptionFlags = EntitySettingsOptionFlags.PopulateInventories;
                playerSettings.PlayerConnection = null; // Player.SendMessage is null-conditional; OK.
                playerSettings.PlayerName = username;
                playerSettings.ArchiveSerializeType = ArchiveSerializeType.Database;
                playerSettings.ArchiveData = null; // fresh account, triggers new-account init

                phantomPlayer = Game.EntityManager.CreateEntity(playerSettings) as Player;
            }
            if (phantomPlayer == null) { error = "phantom Player entity create failed"; return 0; }

            // Stamp the human's Player entity id on the phantom so kill /
            // damage-tag paths can substitute the phantom out and credit the
            // real player. Without this, mission counters and loot rolls skip
            // phantom kills because Player.IsMissionPlayer on the synthetic
            // phantom Player always returns false.
            Player humanHost = PhantomHost;
            if (humanHost != null) phantomPlayer.PhantomCreatorId = humanHost.Id;

            // Step 2: create the Avatar as a child of the phantom Player. Uses
            // the same Player.CreateAvatar helper the real login path calls
            // (PlayerConnection.LoadFromDBAccount:222).
            Avatar phantomAvatar = phantomPlayer.CreateAvatar(avatarRef);
            if (phantomAvatar == null) { error = $"CreateAvatar failed for {avatarRef.GetName()}"; DestroyPhantomPlayer(phantomPlayer); return 0; }

            // Step 3: move avatar from AvatarLibrary to AvatarInPlay so it can
            // enter the world. Same handshake the real client's SwitchAvatar
            // NetMessage triggers (PlayerConnection.LoadFromDBAccount:246-249).
            Inventory avatarInPlay = phantomPlayer.GetInventory(InventoryConvenienceLabel.AvatarInPlay);
            if (avatarInPlay == null) { error = "AvatarInPlay inventory missing"; DestroyPhantomPlayer(phantomPlayer); return 0; }
            InventoryResult moveResult = phantomAvatar.ChangeInventoryLocation(avatarInPlay);
            if (moveResult != InventoryResult.Success) { error = $"ChangeInventoryLocation failed: {moveResult}"; DestroyPhantomPlayer(phantomPlayer); return 0; }

            // Step 4: level + resources so the avatar has real stats.
            int effectiveLevel = levelOverride > 0 ? levelOverride : CharacterLevel;
            phantomAvatar.InitializeLevel(effectiveLevel);
            phantomAvatar.CombatLevel = effectiveLevel;
            phantomAvatar.ResetResources(false);

            // Step 4b: costume. Explicit ref (squad restore / migration /
            // command) wins; otherwise roll a random one from the avatar's
            // costume pool so phantom crowds don't all wear the default.
            // Applied before EnterWorld so the initial replication already
            // carries the final look. The ACTUAL applied ref is stored in
            // the descriptor below, so saves/transfers reproduce this
            // costume instead of re-rolling.
            PrototypeId appliedCostumeRef = costumeRef != 0 ? (PrototypeId)costumeRef : PickRandomCostume(avatarRef, Game.Random);
            if (appliedCostumeRef != PrototypeId.Invalid)
            {
                if (phantomAvatar.ChangeCostume(appliedCostumeRef) == false)
                {
                    PhantomLogger.Warn($"[PhantomHero] ChangeCostume({appliedCostumeRef.GetName()}) failed for {avatarRef.GetName()}");
                    appliedCostumeRef = PrototypeId.Invalid;
                }
            }

            // Step 4c: gear — one level-appropriate item per unlocked equip
            // slot (or the stored set on squad/migration restore). Fills
            // hero-specific weapon slots too, which un-breaks WeaponMissing
            // powers. The applied refs go on the descriptor below.
            List<ulong> appliedGearRefs = ApplyPhantomGear(phantomPlayer, phantomAvatar, effectiveLevel, gearRefs);

            // Step 5: pick a spawn point close to the caller and enter the
            // world. Two goals:
            //   * Close — old range (300-1100u) put phantoms half a screen
            //     away; tightened to 200-400u so they land within visible
            //     radius and read as "with you" instead of "over there".
            //   * No stacking — reject candidates within PhantomMinSpacing of
            //     any already-alive phantom. Up to 8 tries; last try
            //     accepted regardless so we never fail-to-spawn on a crowd.
            var rng = Game.Random;
            Vector3 origin = RegionLocation.Position;
            Vector3 candidate = origin;
            const float minRadius = 150f;
            const float maxRadius = 320f;
            const float PhantomMinSpacing = 130f;              // ≈ 1.4 avatar widths
            const float PhantomMinSpacingSq = PhantomMinSpacing * PhantomMinSpacing;
            Player spacingHost = PhantomHost;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                float ang = (float)(rng.NextDouble() * Math.PI * 2.0);
                float radius = minRadius + (float)(rng.NextDouble() * (maxRadius - minRadius));
                candidate = origin + new Vector3((float)Math.Cos(ang) * radius, (float)Math.Sin(ang) * radius, 0f);
                if (spacingHost == null || spacingHost.PhantomHeroCount == 0) break;

                bool tooClose = false;
                for (int i = 0; i < spacingHost.PhantomAvatarIds.Count; i++)
                {
                    Avatar existing = Game.EntityManager.GetEntity<Avatar>(spacingHost.PhantomAvatarIds[i]);
                    if (existing == null || existing.IsInWorld == false) continue;
                    if (Vector3.DistanceSquared2D(existing.RegionLocation.Position, candidate) < PhantomMinSpacingSq) { tooClose = true; break; }
                }
                if (tooClose == false) break;
                // On the last attempt, accept whatever we've got — better a
                // slight overlap than no spawn.
            }
            Vector3 spawnPos = RegionLocation.ProjectToFloor(region, candidate);
            Orientation spawnOri = RegionLocation.Orientation;

            // Mark phantom Player + Avatar as IsInGame so real clients' AOI
            // GetNewInterestPolicies passes the (entity.IsInGame == false) gate
            // and actually broadcasts them. Player.EnterGame cascades into
            // contained entities (avatar + inventories) via Entity.EnterGame.
            // Any downstream init that assumes a real client is trapped; the
            // essential IsInGame flag lands on entity-level first.
            try { phantomPlayer.EnterGame(); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] phantomPlayer.EnterGame() partial: {ex.Message}"); }

            // Clear the loading-screen state that Player.Initialize (line 233 —
            // QueueLoadingScreen(Invalid)) sets unconditionally on every Player.
            // Real clients ack it and it clears; the phantom has no client to ack.
            // Left set, it makes IsFullscreenObscured=true → every power activation
            // returns PowerUseResult.FullscreenMovie via Agent.CanTriggerPower line 500.
            try { phantomPlayer.OnLoadingScreenFinished(); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] OnLoadingScreenFinished failed: {ex.Message}"); }

            if (phantomAvatar.EnterWorld(region, spawnPos, spawnOri) == false)
            {
                error = "EnterWorld returned false";
                DestroyPhantomPlayer(phantomPlayer);
                return 0;
            }

            // Force AOI broadcast to every real client so they see the phantom.
            // Without this the phantom exists server-side but no NetMessage tells
            // clients about it — invisible bot.
            try
            {
                // Broadcast phantom Player first so client can resolve avatar owner.
                phantomPlayer.UpdateInterestPolicies(true, null);
                phantomAvatar.UpdateInterestPolicies(true, null);

                // Re-broadcast the phantom's PowerCollection to every real
                // player that now has it in AOI. PowerCollection.AssignPower
                // only ships NetMessagePowerCollectionAssignPower to
                // clients when _owner.IsInGame is true (PowerCollection.cs
                // line 339) — but phantom powers get assigned during
                // CreateAvatar / InitializeLevel BEFORE phantomPlayer.
                // EnterGame() runs. That means the initial assign batch
                // never reaches anyone, and the client's PowerCollection
                // for this phantom stays empty. Empty collection ->
                // NetMessageActivatePower arrives referencing a power the
                // client doesn't know the phantom has -> the cast
                // animation never plays -> no VFX. Sending the whole
                // collection here fixes both: cast animations play and
                // VFX renders for every phantom power.
                if (phantomAvatar.PowerCollection != null)
                {
                    int collectionSize = 0;
                    foreach (var _ in phantomAvatar.PowerCollection) collectionSize++;
                    foreach (Player realPlayer in new PlayerIterator(Game))
                    {
                        if (realPlayer.PlayerConnection == null) continue;
                        var aoi = realPlayer.AOI;
                        if (aoi == null) continue;
                        bool interested = aoi.InterestedInEntity(phantomAvatar.Id, AOINetworkPolicyValues.AOIChannelProximity);
                        if (!interested)
                        {
                            PhantomLogger.Info($"[PhantomHero:PowerSync] SKIP {realPlayer.GetName()} — not interested in phantom {phantomAvatar.Id:X} (proximity=false). collectionSize={collectionSize}");
                            continue;
                        }
                        bool sent = phantomAvatar.PowerCollection.SendEntireCollection(realPlayer);
                        PhantomLogger.Info($"[PhantomHero:PowerSync] {realPlayer.GetName()} ← phantom {phantomAvatar.Id:X} collection ({collectionSize} powers) sent={sent}");
                    }
                }
                else
                {
                    PhantomLogger.Warn($"[PhantomHero:PowerSync] phantom {phantomAvatar.Id:X} has no PowerCollection at spawn — client can't render any VFX");
                }

                // Diagnostic: log what each real player's AOI decided for the phantom.
                foreach (Player realPlayer in new PlayerIterator(Game))
                {
                    if (realPlayer.PlayerConnection == null) continue;
                    var aoi = realPlayer.AOI;
                    if (aoi == null) { PhantomLogger.Info($"[PhantomHero:AOI] real={realPlayer} AOI=null"); continue; }
                    bool avatarInterested = aoi.InterestedInEntity(phantomAvatar.Id);
                    bool playerInterested = aoi.InterestedInEntity(phantomPlayer.Id);
                    Vector3 phantomPos = phantomAvatar.RegionLocation.Position;
                    Vector3 realPos = realPlayer.CurrentAvatar?.RegionLocation.Position ?? Vector3.Zero;
                    float dist = Vector3.Distance2D(phantomPos, realPos);
                    PhantomLogger.Info($"[PhantomHero:AOI] real={realPlayer.GetName()} sameRegion={realPlayer.GetRegion() == region} avatarInterested={avatarInterested} playerInterested={playerInterested} dist={dist:F0} phantomPos={phantomPos.ToStringNames()} realPos={realPos.ToStringNames()} inWorld={phantomAvatar.IsInWorld} cell={phantomAvatar.Cell?.Id.ToString() ?? "null"}");
                }
            }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] AOI broadcast failed: {ex.Message}"); }

            // By default phantoms take real damage and are despawned instead of
            // going down when they'd otherwise die (see Avatar.OnKilled's
            // IsPhantomHero branch / HandlePhantomDeath below) — client-
            // controlled Avatars have a revive flow a phantom can't drive, so a
            // downed phantom would otherwise be dead weight until manually
            // cleared. PhantomHeroesDespawnOnDeath=false reverts to the old
            // fully-invulnerable behavior instead.
            if (Game.CustomGameOptions.PhantomHeroesDespawnOnDeath == false)
                phantomAvatar.Properties[PropertyEnum.Invulnerable] = true;

            // Damage scaling — see ApplyPhantomDamageScaling for the level
            // curve. Anchored at "helpful but not obliterating" for level 1
            // and "full-BiS-omega teammate" for level 60, linear between.
            ApplyPhantomDamageScaling(phantomAvatar, effectiveLevel);

            // Server-authoritative movement — real avatars have IsMovementAuthoritative=false
            // because the client drives them. Phantoms have no client, so we must
            // flip it, or Locomotor.FollowEntity produces no visible walking on
            // the real client's screen.
            phantomAvatar.IsPhantomHero = true;

            // Force simulation on. WorldEntity.SetSimulated adds the entity to
            // EntityCollection.Locomotion which is what actually steps
            // Locomotor path progress per tick AND broadcasts LocomotionState
            // changes to interested clients. Without this, the tick still
            // updates position but the client receives only raw position
            // snaps — no walk animation, hence the "sliding" look. Real
            // players get flipped simulated=true when a peer's AOI notices
            // them; phantoms may not go through that path reliably.
            try { phantomAvatar.SetSimulated(true); }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] SetSimulated(true) failed: {ex.Message}"); }

            // Book-keeping goes on the human Player (source of truth) — not on
            // this Avatar shell — so `!phantom clear` and tick reattachment
            // still find these entries after hero swaps or region hops.
            Player host = PhantomHost;
            if (host == null)
            {
                error = "no Player host to register phantom against";
                try { if (phantomAvatar.IsInWorld) phantomAvatar.ExitWorld(); phantomAvatar.Destroy(); } catch { }
                DestroyPhantomPlayer(phantomPlayer);
                return 0;
            }
            var descriptor = new MHServerEmu.DatabaseAccess.Models.PhantomIntent
            {
                AvatarRef = (ulong)avatarRef,
                Level = effectiveLevel,
                Username = username,
                LockLevel = lockLevel,
                CostumeRef = (ulong)appliedCostumeRef,
                GearRefs = appliedGearRefs,
            };
            host.RegisterPhantom(phantomAvatar.Id, phantomPlayer.Id, descriptor);
            SchedulePhantomTick();

            PhantomLogger.Info($"[PhantomHero] {this} spawned '{avatarRef.GetName()}' (avatarId 0x{phantomAvatar.Id:X}, phantomPlayerId 0x{phantomPlayer.Id:X}) at {spawnPos.ToStringNames()} level {effectiveLevel}");
            return phantomAvatar.Id;
        }

        // ================================================================
        //  Off-thread entry point used by the WebFrontend HTTP handler.
        //  SpawnPhantomHero touches Game.Current (a thread-static) via
        //  Player.EnterGame → CheckMapDiscoveryDataExpiration, so calling it
        //  from a ThreadPool thread NREs on the first line. Schedule a
        //  zero-delay event on the game's own scheduler so the actual spawn
        //  runs inside the game tick.
        // ================================================================

        private sealed class WebSpawnEvent : CallMethodEventParam2<Avatar, int, int>
        {
            protected override CallbackDelegate GetCallback() => static (avatar, count, level) =>
            {
                for (int i = 0; i < count; i++)
                    avatar.SpawnPhantomHero(level, null, out _);
            };
        }

        private sealed class WebClearEvent : CallMethodEvent<Avatar>
        {
            protected override CallbackDelegate GetCallback() => static (avatar) => avatar.DespawnAllPhantomHeroes();
        }

        private readonly EventPointer<WebSpawnEvent> _webSpawnEvent = new();
        private readonly EventPointer<WebClearEvent> _webClearEvent = new();
        private readonly EventGroup _webEvents = new();

        /// <summary>
        /// Thread-safe web-facing spawn. Enqueues one game-thread event that
        /// spawns <paramref name="count"/> phantoms at <paramref name="level"/>.
        /// Returns immediately; poll <see cref="PhantomHeroCount"/> to observe.
        /// </summary>
        public void SpawnPhantomHeroesFromWeb(int count, int level)
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_webSpawnEvent.IsValid) scheduler.CancelEvent(_webSpawnEvent);
            scheduler.ScheduleEvent(_webSpawnEvent, TimeSpan.Zero, _webEvents);
            _webSpawnEvent.Get().Initialize(this, count, level);
        }

        /// <summary>Thread-safe clear.</summary>
        public void DespawnAllPhantomHeroesFromWeb()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_webClearEvent.IsValid) scheduler.CancelEvent(_webClearEvent);
            scheduler.ScheduleEvent(_webClearEvent, TimeSpan.Zero, _webEvents);
            _webClearEvent.Get().Initialize(this);
        }

        /// <summary>Destroys every phantom hero this caller has spawned.</summary>
        public int DespawnAllPhantomHeroes()
        {
            Player host = PhantomHost;
            if (host == null) return 0;
            // Snapshot ids for the diagnostic-cache scrub — Player.PurgePhantoms
            // clears its own list, so we need the ids before it runs.
            var ids = new List<ulong>(host.PhantomAvatarIds);
            int removed = host.PurgePhantoms();
            foreach (ulong id in ids) { s_phantomAttackLogged.Remove(id); s_phantomLocoLogged.Remove(id); s_phantomNextAttackMs.Remove(id); s_phantomStuckTrack.Remove(id); s_phantomPowerStuckTrack.Remove(id); s_phantomNextDiagMs.Remove(id); s_phantomNextUltimateMs.Remove(id); s_phantomDownedSinceMs.Remove(id); s_phantomNextSelfHealMs.Remove(id); s_phantomNextSupportBuffMs.Remove(id); s_phantomLastAttackTarget.Remove(id); PruneBlacklistFor(id); PrunePowerBlacklistFor(id); PruneReviveClaimsFor(id); }
            return removed;
        }

        /// <summary>
        /// Called from Avatar.OnEnteredWorld. Prunes phantoms that don't
        /// belong to this Avatar's region (they were left behind by an old
        /// Avatar in another region and can't be seen anyway) and restarts
        /// the tick on survivors so their AI resumes. This is the core of
        /// Option B: same-region avatar swaps keep phantoms alive; cross-
        /// region hops clean them up automatically.
        /// </summary>
        internal void ReattachPhantomTick()
        {
            Player host = PhantomHost;
            if (host == null || host.PhantomHeroCount == 0) return;
            Region myRegion = Region;
            if (myRegion == null) return;

            var mgr = Game?.EntityManager;
            if (mgr == null) return;

            var stale = new List<ulong>();
            int alive = 0;
            for (int i = 0; i < host.PhantomAvatarIds.Count; i++)
            {
                ulong id = host.PhantomAvatarIds[i];
                Avatar phantom = mgr.GetEntity<Avatar>(id);
                if (phantom == null || phantom.IsDestroyed) { stale.Add(id); continue; }
                // Different region OR not in world = can't be driven from
                // here; destroy so the count is honest and !phantom clear
                // stays accurate.
                if (phantom.IsInWorld == false || phantom.Region != myRegion) { stale.Add(id); continue; }
                alive++;
            }

            if (stale.Count > 0)
            {
                foreach (ulong id in stale)
                {
                    int idx = -1;
                    for (int j = 0; j < host.PhantomAvatarIds.Count; j++)
                        if (host.PhantomAvatarIds[j] == id) { idx = j; break; }
                    ulong playerId = idx >= 0 && idx < host.PhantomPlayerIds.Count ? host.PhantomPlayerIds[idx] : 0;
                    try
                    {
                        Avatar av = mgr.GetEntity<Avatar>(id);
                        if (av != null)
                        {
                            if (av.IsInWorld) av.ExitWorld();
                            av.Destroy();
                        }
                    }
                    catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] stale-region cleanup avatar 0x{id:X} failed: {ex.Message}"); }
                    if (playerId != 0)
                    {
                        try
                        {
                            Player p = mgr.GetEntity<Player>(playerId);
                            if (p != null) { if (p.IsInGame) p.ExitGame(); p.Destroy(); }
                        }
                        catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] stale-region cleanup phantom-player 0x{playerId:X} failed: {ex.Message}"); }
                    }
                    host.UnregisterPhantom(id);
                    s_phantomAttackLogged.Remove(id);
                    s_phantomLocoLogged.Remove(id);
                    s_phantomNextAttackMs.Remove(id); s_phantomStuckTrack.Remove(id);
                    s_phantomPowerStuckTrack.Remove(id);
                    s_phantomNextDiagMs.Remove(id);
                    s_phantomNextUltimateMs.Remove(id);
                    s_phantomDownedSinceMs.Remove(id);
                    s_phantomNextSelfHealMs.Remove(id);
                    s_phantomNextSupportBuffMs.Remove(id);
                    s_phantomLastAttackTarget.Remove(id);
                    PruneBlacklistFor(id);
                    PrunePowerBlacklistFor(id);
                    PruneReviveClaimsFor(id);
                }
                PhantomLogger.Info($"[PhantomHero] {this} reattach: pruned {stale.Count} stale, {alive} alive");
            }

            if (alive > 0)
                SchedulePhantomTick();
        }

        private void DestroyPhantomPlayer(Player p)
        {
            if (p == null) return;
            try { p.Destroy(); } catch { /* best effort cleanup on partial init */ }
        }

        // ================================================================
        //  Death handling (PhantomHeroesDespawnOnDeath)
        //
        //  Called from Avatar.OnKilled when IsPhantomHero is true and this
        //  phantom's health reached 0. The base OnKilled call above already
        //  ran normally (loot/mission/kill-message), so from here the
        //  phantom sits in a genuine downed state (IsDead=true, IsInWorld
        //  still true) for PhantomHeroesDownedGracePeriodMs - exactly like a
        //  real player's own downed state - so the human or a teammate
        //  phantom has a real window to revive them via ResurrectOtherAvatar
        //  (see UpdatePhantomHunt's Priority-1 triage). If nobody revives
        //  them before the grace period elapses, they're despawned.
        // ================================================================

        private readonly EventPointer<PhantomDeathEvent> _phantomDeathEvent = new();

        /// <summary>
        /// Schedules despawn of this phantom after the downed grace period,
        /// giving a revive a real chance to land first. Deferred to a
        /// scheduled event rather than destroying synchronously here —
        /// OnKilled runs mid-way through the engine's own Kill()/damage-
        /// application call chain (potentially inside an entity enumeration
        /// for AOE damage), and destroying `this` out from under that chain
        /// risks corrupting whatever collection is still being iterated up
        /// the stack.
        /// </summary>
        internal void HandlePhantomDeath()
        {
            var scheduler = Game?.GameEventScheduler;
            if (scheduler == null) return;
            if (_phantomDeathEvent.IsValid) return;
            int graceMs = Math.Max(0, Game?.CustomGameOptions?.PhantomHeroesDownedGracePeriodMs ?? 30000);
            scheduler.ScheduleEvent(_phantomDeathEvent, TimeSpan.FromMilliseconds(graceMs), _phantomPendingEvents);
            _phantomDeathEvent.Get().Initialize(this);
        }

        private void OnPhantomDeathDespawn()
        {
            // Revived during the grace period - nothing to despawn. The
            // downed-tracking cleanup (s_phantomDownedSinceMs etc.) already
            // happened in OnPhantomTick's revive-detection branch.
            if (IsDead == false) return;

            Player phantomPlayerEntity = GetOwnerOfType<Player>();
            ulong myId = Id;

            // Proactively unregister from the human's bookkeeping so the party
            // HUD updates immediately instead of waiting for the next
            // OnPhantomTick stale-check to notice this avatar is gone.
            Player humanHost = phantomPlayerEntity != null
                ? Game?.EntityManager?.GetEntity<Player>(phantomPlayerEntity.PhantomCreatorId)
                : null;
            humanHost?.UnregisterPhantom(myId);

            s_phantomAttackLogged.Remove(myId);
            s_phantomLocoLogged.Remove(myId);
            s_phantomNextAttackMs.Remove(myId);
            s_phantomStuckTrack.Remove(myId);
            s_phantomPowerStuckTrack.Remove(myId);
            s_phantomNextDiagMs.Remove(myId);
            s_phantomNextUltimateMs.Remove(myId);
            s_phantomDownedSinceMs.Remove(myId);
            s_phantomNextSelfHealMs.Remove(myId);
            s_phantomNextSupportBuffMs.Remove(myId);
            s_phantomLastAttackTarget.Remove(myId);
            PruneBlacklistFor(myId);
            PrunePowerBlacklistFor(myId);
            PruneReviveClaimsFor(myId);

            PhantomLogger.Info($"[PhantomHero] {this} was defeated and has been despawned.");

            try
            {
                if (IsInWorld) ExitWorld();
                Destroy();
            }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] death-despawn avatar cleanup failed: {ex.Message}"); }

            try
            {
                if (phantomPlayerEntity != null)
                {
                    if (phantomPlayerEntity.IsInGame) phantomPlayerEntity.ExitGame();
                    phantomPlayerEntity.Destroy();
                }
            }
            catch (Exception ex) { PhantomLogger.Warn($"[PhantomHero] death-despawn phantom-player cleanup failed: {ex.Message}"); }
        }

        private class PhantomDeathEvent : CallMethodEvent<Avatar>
        {
            protected override CallbackDelegate GetCallback() => (avatar) => avatar.OnPhantomDeathDespawn();
        }
    }
}
