using System.Text;
using MHServerEmu.Commands.Attributes;
using MHServerEmu.Core.Network;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.Network;

namespace MHServerEmu.Commands.Implementations
{
    [CommandGroup("autopickup")]
    [CommandGroupDescription("Manage your personal item auto-pickup preferences (Currency, Crafting Ingredients, Glyphs, Relics). The server decides which categories are available; these commands only let you opt out of a category or choose stash vs. inventory for your own pickups.")]
    public class AutoPickupCommands : CommandGroup
    {
        private static readonly string[] ValidCategories = { "currency", "crafting", "glyph", "relic" };

        private static bool? ParseBoolean(string token)
        {
            return token.ToLowerInvariant() switch
            {
                "on" or "true" or "yes" => true,
                "off" or "false" or "no" => false,
                _ => null,
            };
        }

        private static string FormatOverride(bool? value, string trueLabel = "ON", string falseLabel = "OFF")
        {
            return value == null ? "(default)" : (value.Value ? trueLabel : falseLabel);
        }

        private static void AppendCategory(StringBuilder sb, string name, bool serverEnabled, bool? playerEnabled, bool? serverToStash, bool? playerToStash)
        {
            bool effectiveEnabled = serverEnabled && (playerEnabled ?? true);
            sb.Append($"  {name}: server={(serverEnabled ? "ON" : "OFF")}, you={FormatOverride(playerEnabled)}, effective={(effectiveEnabled ? "ON" : "OFF")}");

            if (effectiveEnabled && serverToStash != null)
            {
                bool effectiveToStash = playerToStash ?? serverToStash.Value;
                sb.Append($", destination={(effectiveToStash ? "stash" : "inventory")} (you: {FormatOverride(playerToStash, "stash", "inventory")})");
            }

            sb.Append('\n');
        }

        [Command("list")]
        [CommandDescription("Shows your effective auto-pickup settings for each category.")]
        [CommandUsage("autopickup list")]
        [CommandInvokerType(CommandInvokerType.Client)]
        public string List(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;

            var customOptions = player.Game.CustomGameOptions;
            var settings = player.AutoPickupSettings;

            var sb = new StringBuilder();
            sb.Append("Auto-pickup settings:\n");
            AppendCategory(sb, "currency", customOptions.EnableItemAutoPickup, settings.CurrencyEnabled, null, null);
            AppendCategory(sb, "crafting", customOptions.EnableCraftingIngredientAutoPickup, settings.CraftingEnabled, customOptions.CraftingIngredientAutoPickupToStash, settings.CraftingToStash);
            AppendCategory(sb, "glyph", customOptions.EnableGlyphAutoPickup, settings.GlyphEnabled, customOptions.GlyphAutoPickupToStash, settings.GlyphToStash);
            AppendCategory(sb, "relic", customOptions.EnableRelicAutoPickup, settings.RelicEnabled, customOptions.RelicAutoPickupToStash, settings.RelicToStash);

            return sb.ToString().TrimEnd();
        }

        [Command("set")]
        [CommandDescription("Sets your auto-pickup preference for a category. Value: on/off, or stash/inventory for destination.")]
        [CommandUsage("autopickup set <currency|crafting|glyph|relic> <on|off|stash|inventory>")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(2)]
        public string Set(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;

            string category = @params[0].ToLowerInvariant();
            string valueToken = @params[1].ToLowerInvariant();

            if (Array.IndexOf(ValidCategories, category) < 0)
                return $"Unknown category '{category}'. Valid: {string.Join(", ", ValidCategories)}.";

            PlayerAutoPickupSettings settings = player.AutoPickupSettings;

            if (valueToken == "stash" || valueToken == "inventory")
            {
                if (category == "currency")
                    return "Currency pickups don't have a stash/inventory destination — they're converted directly to currency, not stored as items.";

                bool toStash = valueToken == "stash";
                switch (category)
                {
                    case "crafting": settings.CraftingToStash = toStash; break;
                    case "glyph": settings.GlyphToStash = toStash; break;
                    case "relic": settings.RelicToStash = toStash; break;
                }

                PlayerAutoPickupSettingsStorage.Save(player.DatabaseUniqueId, settings);
                return $"Auto-pickup destination for {category} set to {valueToken}.";
            }

            bool? boolValue = ParseBoolean(valueToken);
            if (boolValue == null)
                return $"Invalid value '{valueToken}'. Use on/off, or stash/inventory for destination.";

            switch (category)
            {
                case "currency": settings.CurrencyEnabled = boolValue; break;
                case "crafting": settings.CraftingEnabled = boolValue; break;
                case "glyph": settings.GlyphEnabled = boolValue; break;
                case "relic": settings.RelicEnabled = boolValue; break;
            }

            PlayerAutoPickupSettingsStorage.Save(player.DatabaseUniqueId, settings);
            return $"Auto-pickup for {category} set to {(boolValue.Value ? "ON" : "OFF")}. Note: the server must also have this category enabled for it to take effect.";
        }

        [Command("clear")]
        [CommandDescription("Clears your override for a category, reverting to the server default.")]
        [CommandUsage("autopickup clear <currency|crafting|glyph|relic>")]
        [CommandInvokerType(CommandInvokerType.Client)]
        [CommandParamCount(1)]
        public string Clear(string[] @params, NetClient client)
        {
            PlayerConnection playerConnection = (PlayerConnection)client;
            Player player = playerConnection.Player;

            string category = @params[0].ToLowerInvariant();
            if (Array.IndexOf(ValidCategories, category) < 0)
                return $"Unknown category '{category}'. Valid: {string.Join(", ", ValidCategories)}.";

            PlayerAutoPickupSettings settings = player.AutoPickupSettings;
            switch (category)
            {
                case "currency":
                    settings.CurrencyEnabled = null;
                    break;
                case "crafting":
                    settings.CraftingEnabled = null;
                    settings.CraftingToStash = null;
                    break;
                case "glyph":
                    settings.GlyphEnabled = null;
                    settings.GlyphToStash = null;
                    break;
                case "relic":
                    settings.RelicEnabled = null;
                    settings.RelicToStash = null;
                    break;
            }

            PlayerAutoPickupSettingsStorage.Save(player.DatabaseUniqueId, settings);
            return $"Cleared your auto-pickup override for {category}. Now using server defaults.";
        }
    }
}
