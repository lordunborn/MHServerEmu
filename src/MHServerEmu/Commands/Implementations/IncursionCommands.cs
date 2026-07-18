using System.Linq;
using System.Text;
using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Entities.IncursionEntity;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Calligraphy;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Populations;
using MHServerEmu.Games.RoguesGallery;

namespace MHServerEmu.Commands.Implementations
{
    [CommandGroup("incursion")]
    [CommandGroupDescription("Controls the incursion system.")]
    public class IncursionCommands : CommandGroup
    {
        [Command("now")]
        [CommandDescription("Spawns a hostile invader near your avatar. In-game only.")]
        [CommandUsage("incursion now")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Now(string[] @params, NetClient client)
        {
            if (HasAccess(client, out string accessError) == false) return accessError;

            PlayerConnection playerConnection = (PlayerConnection)client;
            Game game = playerConnection.Game;
            if (game?.IncursionManager == null) return "Incursion manager not available.";

            Avatar avatar = playerConnection.Player?.CurrentAvatar;
            if (avatar == null || avatar.IsAliveInWorld == false) return "Avatar not found or not alive in world.";

            var (entity, reason) = game.IncursionManager.ForceIncursionForAvatar(avatar);
            if (entity == null) return $"Incursion failed: {reason}";

            return $"Invader spawned: {entity.PrototypeName} (id {entity.Id}).";
        }

        [Command("spawn")]
        [CommandDescription("Spawns a specific incursion invader by name pattern near your avatar. In-game only.")]
        [CommandUsage("incursion spawn <pattern>")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)]
        public string Spawn(string[] @params, NetClient client)
        {
            if (HasAccess(client, out string accessError) == false) return accessError;

            PlayerConnection playerConnection = (PlayerConnection)client;
            Game game = playerConnection.Game;
            if (game?.IncursionManager == null) return "Incursion manager not available.";

            Avatar avatar = playerConnection.Player?.CurrentAvatar;
            if (avatar == null || avatar.IsAliveInWorld == false) return "Avatar not found or not alive in world.";

            var (entity, reason) = game.IncursionManager.ForceSpawnByPattern(avatar, @params[0]);
            if (entity == null) return $"Spawn failed: {reason}";

            return $"Invader spawned: {entity.PrototypeName} (id {entity.Id}).";
        }

        [Command("start")]
        [CommandDescription("Enables incursion spawning process-wide.")]
        [CommandUsage("incursion start")]
        [CommandInvokerType(CommandInvokerType.Any)]
        public string Start(string[] @params, NetClient client)
        {
            if (HasAccess(client, out string accessError) == false) return accessError;

            bool changed = IncursionManager.EnableSpawning();
            return changed ? "Incursion spawning enabled." : "Incursion spawning was already enabled.";
        }

        [Command("stop")]
        [CommandDescription("Disables incursion spawning process-wide.")]
        [CommandUsage("incursion stop")]
        [CommandInvokerType(CommandInvokerType.Any)]
        public string Stop(string[] @params, NetClient client)
        {
            if (HasAccess(client, out string accessError) == false) return accessError;

            bool changed = IncursionManager.DisableSpawning();
            return changed ? "Incursion spawning disabled." : "Incursion spawning was already disabled.";
        }

        [Command("status")]
        [CommandDescription("Shows the current incursion system state and configuration.")]
        [CommandUsage("incursion status")]
        [CommandInvokerType(CommandInvokerType.Any)]
        public string Status(string[] @params, NetClient client)
        {
            if (HasAccess(client, out string accessError) == false) return accessError;

            return IncursionManager.GetStatusString();
        }

        [Command("roguepool")]
        [CommandDescription("Dumps the Rogues Gallery spawn pool resolved for your current avatar (or a given shorthand).")]
        [CommandUsage("incursion roguepool [avatarShorthand]")]
        [CommandInvokerType(CommandInvokerType.Any)]
        public string RoguePool(string[] @params, NetClient client)
        {
            if (HasAccess(client, out string accessError) == false) return accessError;

            string shorthand;
            if (@params != null && @params.Length > 0)
            {
                shorthand = @params[0];
            }
            else
            {
                if (client is not PlayerConnection playerConnection)
                    return "Usage: incursion roguepool <avatarShorthand> (required from the server console).";

                Avatar avatar = playerConnection.Player?.CurrentAvatar;
                if (avatar == null) return "Avatar not found.";

                if (IncursionManager.TryGetShorthandForAvatarPrototype(avatar.PrototypeDataRef, out shorthand) == false)
                    return $"No incursion enemy type matches your current avatar ({GameDatabase.GetPrototypeName(avatar.PrototypeDataRef)}).";
            }

            var db = RoguesGalleryDatabase.Instance;
            bool villainFlavored = db.IsVillainFlavored(shorthand);
            IReadOnlyList<string> pool = villainFlavored ? db.GetHeroHunterPool(shorthand) : db.GetRoguePoolForAvatar(shorthand);

            return $"'{shorthand}' is {(villainFlavored ? "villain-flavored (hero hunter pool)" : "hero-flavored (rogue pool)")}: " +
                   $"{(pool.Count > 0 ? string.Join(", ", pool) : "(empty)")}";
        }

        [Command("roguegallery")]
        [CommandDescription("Reloads the Rogues Gallery data files (Data/Game/RoguesGallery/*.json) without a rebuild.")]
        [CommandUsage("incursion roguegallery reload")]
        [CommandInvokerType(CommandInvokerType.Any)]
        [CommandParamCount(1)]
        public string RogueGallery(string[] @params, NetClient client)
        {
            if (HasAccess(client, out string accessError) == false) return accessError;

            if (string.Equals(@params[0], "reload", StringComparison.OrdinalIgnoreCase) == false)
                return "Usage: incursion roguegallery reload";

            bool galleryOk = RoguesGalleryDatabase.Instance.Initialize();
            bool tiersOk = RogueNemesisTierDatabase.Instance.Initialize();
            bool overridesOk = IncursionPowerOverrideDatabase.Instance.Initialize();

            if (galleryOk && tiersOk && overridesOk) return "Rogues Gallery data, Nemesis tier data, and power overrides reloaded.";
            return $"Reload finished with issues (see log) - gallery={(galleryOk ? "ok" : "FAILED")}, tiers={(tiersOk ? "ok" : "FAILED")}, overrides={(overridesOk ? "ok" : "FAILED")}.";
        }

        [Command("roguetiers")]
        [CommandDescription("Dumps the resolved RogueNemesis tier scaling (HP/damage mult, loot pool override) for a rank, or all ranks 0-5 if none given.")]
        [CommandUsage("incursion roguetiers [rank]")]
        [CommandInvokerType(CommandInvokerType.Any)]
        public string RogueTiers(string[] @params, NetClient client)
        {
            if (HasAccess(client, out string accessError) == false) return accessError;

            var db = RogueNemesisTierDatabase.Instance;

            if (@params != null && @params.Length > 0)
            {
                if (int.TryParse(@params[0], out int rank) == false || rank < 0 || rank > 5)
                    return "Usage: incursion roguetiers [rank] - rank must be 0-5.";

                return DescribeTier(db, rank);
            }

            var lines = new List<string>();
            for (int rank = 0; rank <= 5; rank++)
                lines.Add(DescribeTier(db, rank));

            return string.Join("\n", lines);
        }

        private static string DescribeTier(RogueNemesisTierDatabase db, int rank)
        {
            float healthMult = db.GetHealthMultiplier(rank);
            float damageMult = db.GetDamageMultiplier(rank);
            IReadOnlyList<string> lootPools = db.GetLootPoolNames(rank);

            string lootDesc = lootPools == null || lootPools.Count == 0 ? "(default pool)" : string.Join(", ", lootPools);
            return $"Rank {rank}: healthMult=x{healthMult:0.###}, damageMult=x{damageMult:0.###}, lootPools=[{lootDesc}]";
        }

        [Command("debug")]
        [CommandDescription("Toggles verbose incursion enemy diagnostics.")]
        [CommandUsage("incursion debug [on|off]")]
        [CommandInvokerType(CommandInvokerType.Any)]
        public string Debug(string[] @params, NetClient client)
        {
            if (HasAccess(client, out string accessError) == false) return accessError;

            bool enabled;
            if (@params != null && @params.Length > 0)
            {
                string arg = @params[0].ToLowerInvariant();
                if (arg is "on" or "true" or "1")
                    enabled = true;
                else if (arg is "off" or "false" or "0")
                    enabled = false;
                else
                    return "Usage: incursion debug [on|off]";
            }
            else
            {
                enabled = IncursionEnemyController.VerboseLogging == false;
            }

            IncursionEnemyController.VerboseLogging = enabled;
            return $"Incursion enemy verbose logging {(enabled ? "enabled" : "disabled")}.";
        }

        [Command("enemy")]
        [CommandDescription("Sets the invader prototype by name pattern (searches agent prototypes). Works in-game and from the server console.")]
        [CommandUsage("incursion enemy [pattern]")]
        [CommandInvokerType(CommandInvokerType.Any)]
        [CommandParamCount(1)]
        public string Enemy(string[] @params, NetClient client)
        {
            if (HasAccess(client, out string accessError) == false) return accessError;

            var (enemyRef, message) = ResolveEnemy(@params[0]);
            if (enemyRef == PrototypeId.Invalid) return message;

            return IncursionManager.SetEnemyStatic(enemyRef);
        }

        [Command("trial")]
        [CommandDescription("Starts or stops an incursion trial: a 1v1 gauntlet against every incursion enemy type.")]
        [CommandUsage("incursion trial [stop]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Trial(string[] @params, NetClient client)
        {
            if (HasAccess(client, out string accessError) == false) return accessError;

            PlayerConnection playerConnection = (PlayerConnection)client;
            Game game = playerConnection.Game;
            if (game?.IncursionManager == null) return "Incursion manager not available.";

            if (@params != null && @params.Length > 0 && @params[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                game.IncursionManager.EndTrial("Stopped by player.");
                return "Incursion trial stopped.";
            }

            Player player = playerConnection.Player;
            return game.IncursionManager.StartTrial(player);
        }

        [Command("findskins")]
        [CommandDescription("Diagnostic: searches WorldEntity prototypes by name pattern and reports whether " +
            "each is avatar-typed plus its UnrealClass mesh asset. Used to find existing non-avatar NPC " +
            "prototypes that already render with a specific hero's model (gamepad target-lock excludes " +
            "avatar-typed entities, so a non-avatar match would be lockable).")]
        [CommandUsage("incursion findskins <pattern>")]
        [CommandInvokerType(CommandInvokerType.Any)]
        [CommandParamCount(1)]
        public string FindSkins(string[] @params, NetClient client)
        {
            if (HasAccess(client, out string accessError) == false) return accessError;

            const int MaxMatches = 25;
            string pattern = @params[0];

            var matches = GameDatabase.SearchPrototypes(pattern,
                DataFileSearchFlags.SortMatchesByName | DataFileSearchFlags.IgnoreCase,
                parentPrototypeClassType: typeof(WorldEntityPrototype))
                .Where(protoRef => GameDatabase.GetPrototypeName(protoRef).Contains("Entity/Items/", StringComparison.OrdinalIgnoreCase) == false)
                .ToList();

            if (matches.Count == 0)
                return $"No WorldEntity prototypes match '{pattern}'.";

            var lines = matches.Take(MaxMatches).Select(protoRef =>
            {
                var proto = protoRef.As<WorldEntityPrototype>();
                string name = GameDatabase.GetPrototypeName(protoRef);
                bool isAvatar = proto is AvatarPrototype;
                string unrealClass = proto != null && proto.UnrealClass != AssetId.Invalid
                    ? GameDatabase.GetAssetName(proto.UnrealClass)
                    : "(none)";
                return $"isAvatar={isAvatar,-5} unrealClass={unrealClass,-40} {name}";
            });

            string header = matches.Count <= MaxMatches
                ? $"Found {matches.Count} matches for '{pattern}':"
                : $"Found {matches.Count} matches for '{pattern}', first {MaxMatches}:";
            return header + "\r\n" + string.Join("\r\n", lines);
        }

        [Command("inspect")]
        [CommandDescription("Diagnostic: dumps detailed fields (type, mesh, rank, alliance, keywords, " +
            "post-killed state, properties) for a single WorldEntity prototype matched by an exact-ish " +
            "name pattern. Use 'findskins' first to narrow down the exact prototype path.")]
        [CommandUsage("incursion inspect <pattern>")]
        [CommandInvokerType(CommandInvokerType.Any)]
        [CommandParamCount(1)]
        public string Inspect(string[] @params, NetClient client)
        {
            if (HasAccess(client, out string accessError) == false) return accessError;

            const int MaxMatches = 15;
            string pattern = @params[0];

            var matches = GameDatabase.SearchPrototypes(pattern,
                DataFileSearchFlags.SortMatchesByName | DataFileSearchFlags.IgnoreCase,
                parentPrototypeClassType: typeof(WorldEntityPrototype))
                .Where(protoRef => GameDatabase.GetPrototypeName(protoRef).Contains("Entity/Items/", StringComparison.OrdinalIgnoreCase) == false)
                .ToList();

            if (matches.Count == 0)
                return $"No WorldEntity prototypes match '{pattern}'.";

            if (matches.Count > 1)
            {
                var names = matches.Take(MaxMatches).Select(GameDatabase.GetPrototypeName);
                string ambiguousHeader = matches.Count <= MaxMatches
                    ? $"Found {matches.Count} matches for '{pattern}', be more specific:"
                    : $"Found {matches.Count} matches for '{pattern}', first {MaxMatches}, be more specific:";
                return ambiguousHeader + "\r\n" + string.Join("\r\n", names);
            }

            PrototypeId matchedRef = matches[0];
            var proto = matchedRef.As<WorldEntityPrototype>();
            if (proto == null)
                return $"'{GameDatabase.GetPrototypeName(matchedRef)}' failed to resolve as a WorldEntityPrototype.";

            static string RefName(PrototypeId r) => r != PrototypeId.Invalid ? GameDatabase.GetPrototypeName(r) : "(none)";

            var sb = new StringBuilder();
            sb.AppendLine(GameDatabase.GetPrototypeName(matchedRef));
            sb.AppendLine($"type={proto.GetType().Name}");
            sb.AppendLine($"unrealClass={(proto.UnrealClass != AssetId.Invalid ? GameDatabase.GetAssetName(proto.UnrealClass) : "(none)")}");
            sb.AppendLine($"rank={RefName(proto.Rank)}");
            sb.AppendLine($"alliance={RefName(proto.Alliance)}");
            sb.AppendLine($"preInteractPower={RefName(proto.PreInteractPower)}");

            if (proto.Keywords != null && proto.Keywords.Length > 0)
                sb.AppendLine($"keywords={string.Join(",", proto.Keywords.Select(RefName))}");

            if (proto.PostKilledState != null)
            {
                sb.AppendLine($"postKilledState.type={proto.PostKilledState.GetType().Name}");
                if (proto.PostKilledState is StateSetPrototype stateSet)
                    sb.AppendLine($"postKilledState.state={RefName(stateSet.State)}");
                else if (proto.PostKilledState is StateTogglePrototype stateToggle)
                    sb.AppendLine($"postKilledState.stateA={RefName(stateToggle.StateA)} stateB={RefName(stateToggle.StateB)}");
            }

            if (proto.Properties != null)
                sb.AppendLine($"properties={proto.Properties}");

            return sb.ToString();
        }

        [Command("skrullbodies")]
        [CommandDescription("Diagnostic: dedupes the base game's SecretInvasion Skrull-boss prototypes by " +
            "their shared UnrealClass disguise chassis, listing which named heroes use each one plus " +
            "rough bounds size, so we can pick candidate non-avatar bodies for our own invaders.")]
        [CommandUsage("incursion skrullbodies")]
        [CommandInvokerType(CommandInvokerType.Any)]
        public string SkrullBodies(string[] @params, NetClient client)
        {
            if (HasAccess(client, out string accessError) == false) return accessError;

            var matches = GameDatabase.SearchPrototypes("skrull",
                DataFileSearchFlags.SortMatchesByName | DataFileSearchFlags.IgnoreCase,
                parentPrototypeClassType: typeof(WorldEntityPrototype))
                .Where(protoRef => GameDatabase.GetPrototypeName(protoRef).Contains("SecretInvasion", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
                return "No SecretInvasion Skrull-boss prototypes found.";

            var groups = matches
                .Select(protoRef => (protoRef, proto: protoRef.As<WorldEntityPrototype>()))
                .Where(t => t.proto != null)
                .GroupBy(t => t.proto.UnrealClass)
                .OrderByDescending(g => g.Count());

            var sb = new StringBuilder();
            foreach (var group in groups)
            {
                string chassisName = group.Key != AssetId.Invalid ? GameDatabase.GetAssetName(group.Key) : "(none)";
                var (firstRef, firstProto) = group.First();
                float radius = firstProto.Bounds?.GetSphereRadius() ?? 0f;
                float halfHeight = firstProto.Bounds?.GetBoundHalfHeight() ?? 0f;

                sb.AppendLine($"chassis={chassisName}  count={group.Count()}  radius={radius:F0} halfHeight={halfHeight:F0} (from {GameDatabase.GetPrototypeName(firstRef)})");
                foreach (var (protoRef, proto) in group)
                    sb.AppendLine($"    {GameDatabase.GetPrototypeName(protoRef)}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Resolves an agent prototype from a name pattern.
        /// </summary>
        private static (PrototypeId, string) ResolveEnemy(string pattern)
        {
            const int MaxMatches = 10;

            var matches = GameDatabase.SearchPrototypes(pattern,
                DataFileSearchFlags.SortMatchesByName | DataFileSearchFlags.IgnoreCase,
                HardcodedBlueprints.Agent).ToList();

            if (matches.Count == 0)
                return (PrototypeId.Invalid, $"No agent prototypes match '{pattern}'.");

            if (matches.Count > 1)
            {
                var names = matches.Take(MaxMatches).Select(GameDatabase.GetPrototypeName);
                string header = matches.Count <= MaxMatches
                    ? $"Found {matches.Count} matches for '{pattern}':"
                    : $"Found {matches.Count} matches for '{pattern}', first {MaxMatches}:";
                return (PrototypeId.Invalid, header + "\r\n" + string.Join("\r\n", names));
            }

            return (matches[0], null);
        }

        /// <summary>
        /// Returns true if the invoker may use incursion commands. Server console invocations
        /// (client == null) are always allowed. In-game invocations require admin only when
        /// the IncursionCommandsRequireAdmin config option is enabled.
        /// </summary>
        private static bool HasAccess(NetClient client, out string error)
        {
            error = null;

            if (client == null)
                return true;

            var options = ConfigManager.Instance.GetConfig<CustomGameOptionsConfig>();
            if (options.IncursionCommandsRequireAdmin == false)
                return true;

            DBAccount account = CommandHelper.GetClientAccount(client);
            if (account != null && account.UserLevel >= AccountUserLevel.Admin)
                return true;

            error = "You do not have enough privileges to use incursion commands (IncursionCommandsRequireAdmin is enabled).";
            return false;
        }
    }
}
