# MHServerEmu

MHServerEmu is a server emulator for Marvel Heroes.

The following versions of the game client are supported:

- **1.52.0.1700** - Full Support

- **1.48.0.1712** (Pre-BUE) - Preliminary Support

- **1.53.0.203** (Test Center) - Preliminary Support

We post development progress reports on our [blog](https://crypto137.github.io/MHServerEmu/). You can find additional information on various topics in the [documentation](./docs/Index.md). If you would like to discuss this project and/or help with its development, feel free to join our [Discord](https://discord.gg/hjR8Bj52t3).

## What's Different in This Fork

This repository's lineage is [Crypto137/MHServerEmu](https://github.com/Crypto137/MHServerEmu) → [mtzimas92/MHServerEmu](https://github.com/mtzimas92/MHServerEmu) → this fork. It stays synced with the original upstream project and carries custom, non-upstream additions from both this fork and mtzimas92's. See **[Fork Features](./docs/Fork/Features.md)** for the full writeup of everything below, including how to configure it, and [CREDITS.md](./CREDITS.md) for attribution.

- **Gameplay systems:** Phantom Heroes, Rogue Nemesis, Incursion, Server-Side Loot Filter, Item Auto-Pickup, Stash Affinity, Throwable Options, Item Chest Auto-Open, Gift Service.
- **Content:** Shanna and the Dinos Invade Manhattan (see below), a reorganized Avengers Tower (relocated raid/patrol teleporters, Recipe Vendor, Hero Ticket Vendor), and a large set of custom loot/vendor/difficulty patches shipped in `Data/Game/Patches` and `Data/Game/LiveTuning` — building from source gets the same experience as the live server.
- **Quality of life:** stackable loot boxes & Fortune Cards, overhauled `!stash` command, in-game news page, automated Live-Tuning-driven leaderboards, weekly LiveTuning event rotation fix.
- **Admin tools:** `!commendations`, `!ultron`, `!player bring`/`goto`, admin item/power/orb dump commands, admin-only mission reset.
- **Stability:** several crash fixes and Patch Manager extensions (array/prototype-valued patch entries, including nested-array support).

### Shanna and the Dinos Invade Manhattan

A 7-wave survival event in Manhattan capped by a randomized boss fight, with a threat meter that rises over time and falls from kills or power-up grabs. Access via Shanna, an NPC in Avengers Tower (hard-locked to Tier 2 Heroic). See [Fork Features](./docs/Fork/Features.md#shanna-and-the-dinos-invade-manhattan) for the full rules and configuration file locations.

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

Preliminary support for game versions 1.48 (pre-BUE) and 1.53 (final test center version) is available if you build the source code using respective build configuration. Nightly builds will be provided at a later date. Support for these versions is still very early, and you will likely encounter game breaking bugs. For now, you should keep using 1.52 for normal play.

Some early work has also been done to support version 1.10 from mid 2013. You can find the code for it in the [MHServerEmu2013](https://github.com/Crypto137/MHServerEmu2013) repository.

**Are you going to add new content to the game (heroes, team-ups, powers, etc.)?**

The scope of this project is restoring the game to its original state. We do not have any plans to create custom content. However, all of our research on the game is completely open-source, and it can be potentially used by others in such endeavors.

**Are you going to make improvements to the game client (e.g. upgrade graphics)?**

No, we do not touch the client side of the game in any way. This project is a recreation of only the server backend needed to run the game.

**I have problems with setting the server up.**

Feel free to join our [Discord](https://discord.gg/hjR8Bj52t3) and ask for help in the `#setup-help` channel.
