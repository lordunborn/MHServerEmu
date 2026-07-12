# MHServerEmu

MHServerEmu is a server emulator for Marvel Heroes.

The only currently supported version of the game client is **1.52.0.1700** (also known as **2.16a**) released on September 7th, 2017.

We post development progress reports on our [blog](https://crypto137.github.io/MHServerEmu/). You can find additional information on various topics in the [documentation](./docs/Index.md). If you would like to discuss this project and/or help with its development, feel free to join our [Discord](https://discord.gg/hjR8Bj52t3).

## What's Different in This Fork

This is a fork of [Crypto137/MHServerEmu](https://github.com/Crypto137/MHServerEmu) with the following custom, non-upstream additions:

- **Phantom Heroes** — spawn server-side AI-controlled hero companions (`!phantom spawn`) that fight alongside a solo player as a synthetic party, up to 9 active phantoms per player (configurable). Includes idle-follow formation locomotion, level-scaled damage and gear (armor/artifact pool restrictions, a configurable bad-item blacklist, level-scaled relic stacks), ultimates and charge-release power support, death/despawn handling, and full party-HUD integration (health bars, leave/kick/disband/convert-to-raid). Based on and adapted from [TruSkillzzRuns's fork](https://github.com/TruSkillzzRuns/MHServerEmu).

- **Server-Side Player Loot Filter** — players can configure filters for loot they don't want, and the server simply won't spawn those items for them. Based on [CorvaeOboro's fork](https://corvaeoboro.github.io/MHServerEmu/).

- **Item Auto-Pickup** — configurable server-side auto-pickup for currency, crafting ingredients, Runeword Glyphs, and Relics within a radius, with per-category stash-vs-inventory routing. Players can opt out of individual categories or override routing for themselves via `!autopickup`, independent of the server-wide defaults. Based on CorvaeOboro's fork.

- **Stash Affinity** — manually moving an item into a stash redirects it to the best-matching tab automatically (a character-specific stash for bound items, or a tab whose name matches the item's type) if one exists with space. Based on CorvaeOboro's fork.

- **Throwable Options** — server toggles to disable interactive throwable pickups, auto-cancel a held throwable when another power is used, and auto-throw a held object before a movement power activates. Based on CorvaeOboro's fork.

- **Item Chest Auto-Open** — automatically opens chest-type items in inventory on a cooldown, with a configurable name whitelist. Based on CorvaeOboro's fork.

- **`!stash` command overhaul** — collapsed from a command group with subcommands into a single, more flexible command with a sensible default action. Based on [mtzimas92's fork](https://github.com/mtzimas92/MHServerEmu).

- **Automated Live-Tuning-driven leaderboards** — leaderboards tied to a scheduled Live Tuning event now automatically activate and deactivate in sync with that event's active window, instead of requiring manual leaderboard-schedule edits. Based on work done by @Omega in the MHServerEmu Discord.

- **Changed `DayOfWeekRotation`** — changed LiveTuning event rotation to actually cycle weekly on the configured weekday, instead of firing all rotation events simultaneously on the month's 5th occurrence. Based on [mtzimas92's fork](https://github.com/mtzimas92/MHServerEmu).

See [CREDITS.md](./CREDITS.md) for full attribution.

## Download

We provide two kinds of builds: stable and nightly.

|                      | Stable         | Nightly               |
| -------------------- | -------------- | --------------------- |
| **Update Frequency** | Quarterly      | Daily                 |
| **Features**         | Fewer          | More                  |
| **Stability**        | High           | Medium                |
| **Platforms**        | Windows        | Windows / Linux       |
| **Configuration**    | Pre-Configured | Just the Server Files |

If you are setting the server up for the first time and/or unsure which one to use, we recommend you to start with a stable build. See [Initial Setup](./docs/Setup/InitialSetup.md) for information on how to set the server up.

You can always upgrade from stable to nightly simply by downloading the latest nightly build and overwriting your stable files.

### Stable

[![Stable Release](https://img.shields.io/github/v/release/Crypto137/MHServerEmu?include_prereleases)](https://github.com/Crypto137/MHServerEmu/releases)

### Nightly

[![Nightly Release (Windows x64)](https://github.com/Crypto137/MHServerEmu/actions/workflows/nightly-release-windows-x64.yml/badge.svg)](https://nightly.link/Crypto137/MHServerEmu/workflows/nightly-release-windows-x64/master?preview) [![Nightly Release (Linux x64)](https://github.com/Crypto137/MHServerEmu/actions/workflows/nightly-release-linux-x64.yml/badge.svg)](https://nightly.link/Crypto137/MHServerEmu/workflows/nightly-release-linux-x64/master?preview)

## FAQ

**Is the game fully playable?**

All systems and content that were in the game when it was shut down in 2017 have been restored.

**Where can I download the game client?**

We do not provide download links for the game client for legal reasons. If you have played the game through Steam when it was live, you should be able to download it in your Steam library.

**How to update the server?**

Download the latest stable or nightly build and overwrite your existing files. Nightly builds can be potentially unstable, so it is recommended to back up your account database file located in `MHServerEmu\Data\Account.db` before updating.

**Are you going to support other versions of the game, like the ones from before the Biggest Update Ever (BUE) came out?**

Yes, we do plan to implement support for other versions, including the final pre-BUE version (1.48) from late 2016. Currently there are no timeframes for when this is going to happen. The current work-in-progress 1.48 code is available on the [v48](https://github.com/Crypto137/MHServerEmu/tree/v48) branch.

Some early work has also been done to support version 1.10 from mid 2013. You can find the code for it in the [MHServerEmu2013](https://github.com/Crypto137/MHServerEmu2013) repository.

**Are you going to add new content to the game (heroes, team-ups, powers, etc.)?**

The scope of this project is restoring the game to its original state. We do not have any plans to create custom content. However, all of our research on the game is completely open-source, and it can be potentially used by others in such endeavors.

**Are you going to make improvements to the game client (e.g. upgrade graphics)?**

No, we do not touch the client side of the game in any way. This project is a recreation of only the server backend needed to run the game.

**I have problems with setting the server up.**

Feel free to join our [Discord](https://discord.gg/hjR8Bj52t3) and ask for help in the `#setup-help` channel.
