using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Network;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Commands.Implementations
{
    /// <summary>
    /// Phantom Heroes — chat commands.
    ///   !phantom spawn [count] [level]   — spawn N phantom-hero NPCs near you
    ///   !phantom squad save/spawn/list/delete [name] — manage saved squads
    ///   !phantom costume ...             — manage phantom costumes
    ///   !phantom clear                   — despawn every phantom you've spawned
    /// </summary>
    [CommandGroup("phantom")]
    [CommandGroupDescription("Spawn / clear phantom-hero server-side NPC bots.")]
    public class PhantomHeroCommands : CommandGroup
    {
        /// <summary>
        /// Blocks phantom commands while the CALLER is in combat. Checks only the
        /// caller's own Avatar.IsInCombat flag - a phantom hero still holding aggro
        /// from something the player has since walked away from does not count.
        /// </summary>
        private static bool IsCallerInCombat(Avatar avatar)
        {
            return avatar.Properties[PropertyEnum.IsInCombat];
        }

        [Command("spawn")]
        [CommandDescription("Spawn phantom-hero NPCs near you. Args: [count=4] [level=your level], or [heroname] [level] to spawn a specific hero. Phantoms auto-level with you unless an explicit level is given.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Spawn(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom spawn.");
            var avatar = pc.Player?.CurrentAvatar;
            if (avatar == null) return "No avatar in world.";

            if (pc.Player.Game.CustomGameOptions.PhantomHeroesEnable == false)
                return "Phantom Heroes is disabled on this server.";

            if (IsCallerInCombat(avatar))
                return "Cannot use !phantom commands while in combat.";

            // 0 = "match caller's CharacterLevel" (handled inside
            // SpawnPhantomHeroCore). The tick loop then keeps them in sync
            // if the human levels up — see OnPhantomTick's level-sync block.
            int level = 0;
            if (@params.Length >= 2 && int.TryParse(@params[1], out int l)) level = System.Math.Clamp(l, 1, 60);

            // Non-numeric first arg = spawn a specific hero by name. The
            // name is matched at runtime against the playable-avatar pool
            // from the loaded client data — no hero names live in server
            // source.
            int count = 0;
            if (@params.Length >= 1 && int.TryParse(@params[0], out count) == false)
            {
                var matches = Avatar.FindPhantomHeroRefs(@params[0]);
                if (matches.Count == 0)
                    return $"No hero matching '{@params[0]}'.";
                if (matches.Count > 1)
                {
                    var names = new System.Text.StringBuilder();
                    for (int i = 0; i < matches.Count && i < 8; i++)
                    {
                        if (i > 0) names.Append(", ");
                        names.Append(matches[i].ShortName);
                    }
                    if (matches.Count > 8) names.Append(", ...");
                    return $"Multiple matches: {names}. Be more specific.";
                }

                ulong heroId = avatar.SpawnPhantomHeroFromIntent(matches[0].AvatarRef, level, null, level > 0, 0, out string heroError);
                return heroId != 0
                    ? $"Spawned {matches[0].ShortName}."
                    : $"Failed to spawn {matches[0].ShortName}: {heroError}";
            }

            // Numeric path: spawn N random heroes. Default 4 — a party is 5
            // total INCLUDING the human caller, not 5 phantoms plus the caller.
            count = @params.Length >= 1 ? System.Math.Clamp(count, 1, 50) : 4;

            int spawned = 0, failed = 0;
            var firstError = string.Empty;
            for (int i = 0; i < count; i++)
            {
                ulong id = avatar.SpawnPhantomHero(level, null, out string error);
                if (id != 0) spawned++;
                else
                {
                    failed++;
                    if (firstError.Length == 0 && error != null) firstError = error;
                    // Once the active-phantom cap is hit, every further
                    // attempt this loop will fail identically — stop
                    // instead of spamming pointless failed spawns.
                    if (error != null && error.StartsWith("phantom cap reached", System.StringComparison.Ordinal))
                        break;
                }
            }
            return firstError.Length > 0
                ? $"Phantoms: spawned={spawned} failed={failed}. First err: {firstError}"
                : $"Phantoms: spawned={spawned} failed={failed}.";
        }

        [Command("squad")]
        [CommandDescription("Manage saved phantom squads. Usage: squad save [name] | squad spawn [name] | squad list | squad delete [name]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Squad(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom squad.");
            var player = pc.Player;
            var avatar = player?.CurrentAvatar;
            if (player == null || avatar == null) return "No avatar in world.";

            if (player.Game.CustomGameOptions.PhantomHeroesEnable == false)
                return "Phantom Heroes is disabled on this server.";

            if (IsCallerInCombat(avatar))
                return "Cannot use !phantom commands while in combat.";

            if (@params.Length == 0)
                return "Usage: phantom squad save [name] | spawn [name] | list | delete [name]";

            string op = @params[0].ToLowerInvariant();
            string name = @params.Length >= 2 ? @params[1] : null;

            switch (op)
            {
                case "save":
                    if (name == null) return "Usage: phantom squad save [name]";
                    return player.SavePhantomSquad(name);

                case "spawn":
                case "load":
                    if (name == null) return "Usage: phantom squad spawn [name]";
                    return player.SpawnPhantomSquad(name, avatar);

                case "list":
                    return player.ListPhantomSquads();

                case "delete":
                    if (name == null) return "Usage: phantom squad delete [name]";
                    return player.DeletePhantomSquad(name);

                default:
                    return $"Unknown squad operation '{op}'. Use save, spawn, list or delete.";
            }
        }

        [Command("costume")]
        [CommandDescription("Phantom costumes. Usage: costume random | costume [hero] random | costume [hero] [costumename] | costume list [hero]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Costume(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom costume.");
            var player = pc.Player;
            if (player == null || player.CurrentAvatar == null) return "No avatar in world.";

            if (player.Game.CustomGameOptions.PhantomHeroesEnable == false)
                return "Phantom Heroes is disabled on this server.";

            if (IsCallerInCombat(player.CurrentAvatar))
                return "Cannot use !phantom commands while in combat.";

            if (@params.Length == 0)
                return "Usage: phantom costume random | costume [hero] random | costume [hero] [costumename] | costume list [hero]";

            // costume random — every active phantom re-rolls.
            if (@params.Length == 1 && @params[0].Equals("random", System.StringComparison.OrdinalIgnoreCase))
                return player.RandomizePhantomCostumes();

            // costume list <hero>
            if (@params[0].Equals("list", System.StringComparison.OrdinalIgnoreCase))
            {
                if (@params.Length < 2) return "Usage: phantom costume list [hero]";
                return player.ListPhantomCostumes(@params[1]);
            }

            // costume <hero> <random|costumename>
            if (@params.Length < 2)
                return "Usage: phantom costume [hero] random | costume [hero] [costumename]";
            return player.SetPhantomCostume(@params[0], @params[1]);
        }

        [Command("gear")]
        [CommandDescription("Re-roll phantom gear, or inspect gear. Usage: gear (all phantoms) | gear [hero] (one phantom) | gear equipped [hero] (what's actually equipped right now, per slot) | gear candidates [hero] [slot] (full possible pool for a slot, e.g. Artifact01, also written to the server log — use to find item names for PhantomGearItemBlacklist)")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Gear(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom gear.");
            var player = pc.Player;
            if (player == null || player.CurrentAvatar == null) return "No avatar in world.";

            if (player.Game.CustomGameOptions.PhantomHeroesEnable == false)
                return "Phantom Heroes is disabled on this server.";

            if (IsCallerInCombat(player.CurrentAvatar))
                return "Cannot use !phantom commands while in combat.";

            if (@params.Length >= 1 && @params[0].Equals("candidates", System.StringComparison.OrdinalIgnoreCase))
            {
                if (@params.Length < 3) return "Usage: phantom gear candidates [hero] [slot]";
                return player.ListPhantomGearCandidates(@params[1], @params[2]);
            }

            if (@params.Length >= 1 && @params[0].Equals("equipped", System.StringComparison.OrdinalIgnoreCase))
            {
                if (@params.Length < 2) return "Usage: phantom gear equipped [hero]";
                return player.ListPhantomEquippedGear(@params[1]);
            }

            return player.RerollPhantomGear(@params.Length >= 1 ? @params[0] : null);
        }

        [Command("clear")]
        [CommandDescription("Despawn every phantom you've spawned.")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Clear(string[] @params, NetClient client)
        {
            var pc = (client as PlayerConnection) ?? throw new System.InvalidOperationException("Only clients can run !phantom clear.");
            var avatar = pc.Player?.CurrentAvatar;
            if (avatar == null) return "No avatar in world.";

            if (IsCallerInCombat(avatar))
                return "Cannot use !phantom commands while in combat.";

            int removed = avatar.DespawnAllPhantomHeroes();
            return $"Phantoms despawned: {removed}.";
        }
    }
}
