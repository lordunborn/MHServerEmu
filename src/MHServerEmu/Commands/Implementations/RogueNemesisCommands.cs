using System.Collections.Generic;
using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Populations;
using MHServerEmu.Games.RoguesGallery;

namespace MHServerEmu.Commands.Implementations
{
    [CommandGroup("rogue")]
    [CommandGroupDescription("Controls the Rogue Encounter / Nemesis system (per-player villain ambushes).")]
    public class RogueNemesisCommands : CommandGroup
    {
        [Command("enable")]
        [CommandDescription("Opts you in to Rogue Encounter ambushes (server-wide feature must also be enabled by an admin).")]
        [CommandUsage("rogue enable")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Enable(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;
            if (player == null) return "Player not found.";

            bool changed = player.RogueNemesisData.Enabled == false;
            player.RogueNemesisData.Enabled = true;
            RogueNemesisPlayerDataStorage.Save(player.DatabaseUniqueId, player.RogueNemesisData);

            return changed
                ? "You are now opted in to Rogue Encounter ambushes."
                : "You were already opted in.";
        }

        [Command("disable")]
        [CommandDescription("Opts you out of Rogue Encounter ambushes.")]
        [CommandUsage("rogue disable")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Disable(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;
            if (player == null) return "Player not found.";

            bool changed = player.RogueNemesisData.Enabled;
            player.RogueNemesisData.Enabled = false;
            RogueNemesisPlayerDataStorage.Save(player.DatabaseUniqueId, player.RogueNemesisData);

            return changed
                ? "You are now opted out of Rogue Encounter ambushes."
                : "You were already opted out.";
        }

        [Command("status")]
        [CommandDescription("Shows your current Rogue Encounter opt-in, active-encounter state, and Nemesis history.")]
        [CommandUsage("rogue status")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string Status(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;
            if (player == null) return "Player not found.";

            Game game = playerConnection.Game;
            bool activeEncounter = game?.RogueNemesisManager != null && game.RogueNemesisManager.HasActiveEncounter(player.DatabaseUniqueId);
            bool onCooldown = player.RogueNemesisData.IsOnCooldown;

            int avatarLevel = player.CurrentAvatar?.CharacterLevel ?? 0;
            int levelRankCap = RogueNemesisTierDatabase.GetLevelRankCap(avatarLevel);
            float levelBaselineScale = RogueNemesisTierDatabase.GetLevelBaselineScale(avatarLevel);

            string summary = $"Rogue Encounter: optedIn={player.RogueNemesisData.Enabled}, activeEncounter={activeEncounter}, " +
                              $"onCooldown={onCooldown}, avatarLevel={avatarLevel}, levelRankCap={levelRankCap}, " +
                              $"levelBaselineScale={levelBaselineScale:0.###}.";

            List<NemesisEntry> entries = player.RogueNemesisData.NemesisEntries;
            if (entries == null || entries.Count == 0)
                return summary + " No Nemesis history yet.";

            List<string> lines = new();
            foreach (NemesisEntry entry in entries)
            {
                int effectiveRank = System.Math.Min(entry.Rank, levelRankCap);
                string rankPart = effectiveRank == entry.Rank
                    ? $"Rank {entry.Rank}"
                    : $"Rank {entry.Rank}, capped to {effectiveRank} at your level";
                string cooldownPart = entry.IsOnTier5DefeatCooldown
                    ? $", on cooldown until {System.DateTimeOffset.FromUnixTimeMilliseconds(entry.Tier5DefeatCooldownUntilUnixTimeMs).LocalDateTime:yyyy-MM-dd HH:mm} server time"
                    : "";
                lines.Add($"{entry.EnemyShorthand} ({rankPart}{cooldownPart})");
            }

            return summary + " Nemesis history: " + string.Join(", ", lines) + ".";
        }

        [Command("forcespawn")]
        [CommandDescription("Admin/testing: triggers a Rogue Encounter for yourself immediately, bypassing the roll chance and cooldown.")]
        [CommandUsage("rogue forcespawn [villainShorthand]")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string ForceSpawn(string[] @params, NetClient client)
        {
            if (HasAdminAccess(client, out string accessError) == false) return accessError;

            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;
            if (player == null) return "Player not found.";

            Game game = playerConnection.Game;
            if (game?.RogueNemesisManager == null) return "RogueNemesisManager is not available.";

            string villainShorthandOverride = @params.Length > 0 ? @params[0] : null;
            return game.RogueNemesisManager.ForceSpawnEncounter(player, villainShorthandOverride);
        }

        [Command("setrank")]
        [CommandDescription("Admin/testing: directly sets a villain's Nemesis rank against you, bypassing the normal win/loss grind.")]
        [CommandUsage("rogue setrank <villainShorthand> <rank 0-5>")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string SetRank(string[] @params, NetClient client)
        {
            if (HasAdminAccess(client, out string accessError) == false) return accessError;

            if (@params.Length < 2)
                return "Usage: rogue setrank <villainShorthand> <rank 0-5>";

            if (int.TryParse(@params[1], out int rank) == false)
                return "Rank must be a number 0-5.";

            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;
            if (player == null) return "Player not found.";

            Game game = playerConnection.Game;
            if (game?.RogueNemesisManager == null) return "RogueNemesisManager is not available.";

            return game.RogueNemesisManager.SetRank(player, @params[0], rank);
        }

        /// <summary>
        /// Returns true if the invoker may use admin/diagnostic '!rogue' subcommands. The
        /// player-facing enable/disable/status commands above deliberately don't call this - they
        /// should always work regardless of this setting. Mirrors IncursionCommands.HasAccess.
        /// </summary>
        private static bool HasAdminAccess(NetClient client, out string error)
        {
            error = null;

            if (client == null)
                return true;

            var options = ConfigManager.Instance.GetConfig<CustomGameOptionsConfig>();
            if (options.RogueNemesisCommandsRequireAdmin == false)
                return true;

            DBAccount account = CommandHelper.GetClientAccount(client);
            if (account != null && account.UserLevel >= AccountUserLevel.Admin)
                return true;

            error = "You do not have enough privileges to use this command (RogueNemesisCommandsRequireAdmin is enabled).";
            return false;
        }
    }
}
