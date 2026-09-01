# Path of Idle In-Game Search

**English** · [한국어](README.ko.md) · [简体中文](README.zh-CN.md) · [繁體中文](README.zh-TW.md)

An unofficial Windows mod for searching and managing Inventory, Warehouse, and Vault items without leaving Path of Idle. It provides search, detailed tooltips, game-native item transfers, filtered bulk opening, and 0.1×–100× speed controls in a single opaque overlay.

## Features

- Open or close the overlay with `F3` or `Ctrl+F`.
- Search by name, quality, slot, level, affix, Set, and storage location.
- Use spaces for `AND`, `|` for OR, `-word` to exclude, and `"quoted text"` for phrases.
- Show only matching items and highlight matching text.
- Edit the search caret with Left, Right, Home, End, Backspace, and Delete.
- Browse Inventory and Warehouse/Vault results separately.
- Multi-select Rare, Legendary, Mythic, Set, Unique, and Other quality filters; click a selected quality again to remove it.
- Restrict searches to affixes and Set bonuses.
- Hover over an item to view its full description, complete Legendary/Mythic/Unique affixes, Set class restriction, Set pieces, and staged Set bonuses.
- Move items between Inventory and storage from the result list.
- Use the game's own storage-routing function so unlocked quality rules determine whether gear enters the Warehouse or Vault.
- Bulk-open equipment boxes and rune boxes with multi-select quality filters and an optional `or higher` mode.
- Optionally skip the second confirmation click, stop when inventory space runs out, and automatically store newly opened gear.
- Keep Search, Bulk Open, and Auto Build on separate pages with opaque detail panels.
- Use the primary Auto Build action to jointly evaluate all eight equipment slots, base/active skill combinations, and the complete mastery/point-level vector under one bounded native 60-second preview.
- Apply and verify the exact chosen joint plan under the same plan token. All eight item identities, learned and equipment-granted skills, variants, saved/effective/capped levels, and spent/remaining point totals must match; incomplete application is never reported as success.
- Keep the combined application atomic. If any gear move, transformation, reset, allocation, or final verification fails, the mod restores the original gear, talents, Blood spent by that run, talent progress counters, and saved skill preferences; if a full bag has no reversible bridge slot, it stops safely before committing.
- Follow the game's native Warehouse/Vault rules during equipping and normalize committed items into their correct unlocked Vault groups.
- Enforce job weapon rules and the selected or recommended base skill's weapon requirement/preference. Other active-skill matches contribute to synergy scoring instead of becoming false hard constraints.
- Score native attributes, rune affixes, active Set breakpoints, skill variants, and skill synergy as an estimated 60-second sustained-output proxy. Unsupported runtime-dependent conditions fail closed instead of receiving a guessed score; this remains a bounded preview, not exact enemy/AI combat DPS.
- Count skills granted by equipment in build performance without consuming a Shrine learned-skill slot or talent points.
- Choose Auto, Physical, Elemental, Fire, Ice, Lightning, Minion, Bleed, Corrosion, Critical, Support, or Defense themes. Auto derives elemental focus from native skill damage types instead of localized name or description keywords.
- Optionally use the Shrine's normal paid transformation, for a configurable number of attempts, to seek the performance-selected skills. The optimizer reserves the reset cost, preserves fixed and alien rows, and stops without claiming success when transformation, reset, or allocation verification is incomplete.
- Keep advanced `Gear only` and `Skills for current gear only` actions as recovery tools when only one side should be reapplied. They do not replace the primary joint optimization.
- Select preset speeds or enter any value from `0.1×` to `100×`.
- Block game keyboard controls while the overlay is focused and block character-switching wheel input only while the pointer is over the focused overlay. Input outside the panel remains available.
- Preserve window position, query, filters, speed, language, and bulk-opening preferences.
- Follow the game's language automatically or manually select Korean, English, Simplified Chinese, or Traditional Chinese.

Item names and descriptions use the localization currently supplied by the game. English item names are also indexed as search aliases.

## Screenshots

### Search, quality filters, and bulk opening

![Search, quality filters, and bulk opening](docs/images/search-and-bulk-open.png)

### Full Set item details and bonuses

![Full Set item details and bonuses](docs/images/set-tooltip.png)

### Opaque in-game overlay

![Opaque in-game overlay](docs/images/opaque-overlay.png)

## Installation and Updates

1. Download the latest `PathOfIdleInGameSearch-*.zip` from [Releases](../../releases).
2. Extract the ZIP completely.
3. Close Path of Idle.
4. Double-click `install.bat`.
5. Start the game and press `F3`.

The installer finds the Steam installation automatically. If BepInEx is missing, it downloads the pinned official `6.0.0-be.760` IL2CPP x64 build and verifies its SHA-256 hash before installation. Existing BepInEx installations are reused, and only this mod DLL is installed or updated.

The distributed Release DLL contains no mod diagnostic logging or debug symbols. A fresh BepInEx installation also starts with console and disk logging disabled; an existing BepInEx installation keeps its own logging preference.

Reinstalling over an older version does not overwrite `BepInEx/config/local.pathofidle.ingame-search.cfg`, so user settings are preserved.

## Controls

| Action | Control |
| --- | --- |
| Open or close the overlay | `F3` or `Ctrl+F` |
| Close the overlay | `Esc` or the top-right `×` button |
| Change the UI language | The `AUTO·…`/language button in the title bar |
| Step the game speed | `−` and `+` |
| Enter a custom speed | Click the speed field, enter `0.1`–`100`, then press Enter or `Apply` |
| Restore normal speed | `1×` |
| View full item details | Hover over a result row |
| Change result page | Footer buttons or the mouse wheel over the result area |
| Choose and run an auto build | Open `AUTO BUILD`, select a theme, then run the primary combined Auto Build action |

The `WAREHOUSE` tab includes all normal Warehouse pages and unlocked Vault quality groups. When depositing equipment, the mod calls the game's native `QuickMoveItemFromBagToStore` routine instead of guessing the destination from quality names.

Search and bulk-open quality filters support multiple selections. Clicking an active quality removes it; clearing every selection returns to `All`. With `Or higher` enabled, bulk opening starts at the lowest selected ranked quality and includes every higher ranked quality. `Other` remains a separate, unranked selection.

Bulk opening requires two clicks by default. Enabling `Skip confirm` makes it run immediately, so check the selected quality, available space, and `Auto storage` setting first. Items are opened one at a time across frames and each native consumption is verified. If the game does not consume an item, the session stops without retrying to prevent duplicate partial rewards. The filter applies to the boxes you own; it does not alter the random quality of generated rewards.

Auto Build operates on the hero currently selected in the game. Its primary action jointly searches both weapon slots plus the other six equipment slots, the base/active skill combination, and the full saved mastery/point-level vector. Currently equipped items remain candidates. Job and skill weapon rules, unlocked Shrine rows, fixed and alien skills, the milestone-adjusted point budget, native Mythic limits, and storage routing remain constraints. Skills granted by equipment are included in the performance preview at their granted level but consume neither a Shrine learned-skill slot nor talent points.

Each joint candidate is evaluated with a bounded native preview of roughly 60 seconds using native attributes, runtime affixes, runes, granted skills, skill variants, active 2-piece/4-piece Set effects, damage types, cooldowns, and skill speed. Auto focus reads native skill damage types rather than guessing from localized words. A runtime-dependent trigger or condition that cannot be represented safely causes that preview path to fail closed instead of receiving an invented value. The score assumes sufficient resources and uptime and is not exact enemy/AI combat DPS: it does not reproduce every defense, movement decision, target pattern, complex trigger schedule, projectile interaction, DoT stack, summon behavior, or combat-AI choice.

One observed test run completing in about 20 seconds is only a machine- and save-specific measurement, not a promised optimization time. Larger inventories or different heroes and skill pools may take longer.

The selected result receives one plan token. That same token and its exact saved-level vector are used when the mod applies the eight-slot loadout, performs any configured normal Blood-paid Shrine transformations and reset, allocates points, and verifies the result. The exact verifier checks all eight item identities, learned skills, equipment-granted skills re-derived from the final loadout, variants, saved/effective/capped levels, and spent/remaining point totals; unsupported or incomplete work stops without claiming success. The whole joint application is atomic: a failure after gear is moved restores the original equipment and talent rows, refunds Blood spent by that run, restores talent progress counters and saved base-skill/skill preferences, and verifies that restoration before reporting the failure. The action requires a second confirmation click.

Advanced `Gear only` and `Skills for current gear only` actions are available as recovery tools when one side must be reapplied. Because they intentionally hold the other side fixed, their result is not equivalent to the primary joint optimum.

Very high speeds such as `100×` may skip short animations or timers. Return to `1×` after completing the desired action.

## Uninstallation

Close the game and run `uninstall.bat`. It removes only this mod DLL while preserving BepInEx, other mods, and the user configuration file.

## Building from Source

.NET 6 SDK or newer is required. Install BepInEx, run the game once to generate interop assemblies, close the game, and run `scripts\build.ps1`. The build references assemblies from the locally installed game; game files are not included in this repository.

## Compatibility

- Windows x64
- Steam version of Path of Idle
- Unity IL2CPP with BepInEx 6

Game updates that change internal data structures may require a mod update.

This is an unofficial fan project and is not affiliated with the developers of Path of Idle or BepInEx. Back up important save data before using unofficial mods.
