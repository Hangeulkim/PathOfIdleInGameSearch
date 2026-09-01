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
- Auto-equip all eight slots of the selected hero from equipped, Inventory, Warehouse, and unlocked Vault gear. The optimizer enforces the hero and active skills' weapon requirements, evaluates complete Set breakpoints and skill-specific effects, and scores expected final damage, survival, and support from a temporary copy of the game's calculated attributes.
- Choose Auto, Physical, Elemental, Fire, Ice, Lightning, Minion, Bleed, Corrosion, Critical, Support, or Defense themes. Auto analyzes the selected hero's job and active skills.
- Reset and auto-allocate the selected hero's skills using the matching in-game job build guide; the action stops before changing anything when the required Blood is insufficient.
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
| Choose an auto-build theme | Open `AUTO BUILD`, then select a theme before running gear or skill optimization |

The `WAREHOUSE` tab includes all normal Warehouse pages and unlocked Vault quality groups. When depositing equipment, the mod calls the game's native `QuickMoveItemFromBagToStore` routine instead of guessing the destination from quality names.

Search and bulk-open quality filters support multiple selections. Clicking an active quality removes it; clearing every selection returns to `All`. With `Or higher` enabled, bulk opening starts at the lowest selected ranked quality and includes every higher ranked quality. `Other` remains a separate, unranked selection.

Bulk opening requires two clicks by default. Enabling `Skip confirm` makes it run immediately, so check the selected quality, available space, and `Auto storage` setting first. If space runs out, the mod opens only the amount that fits and leaves the remainder untouched. The filter applies to the boxes you own; it does not alter the random quality of generated rewards.

Auto Build operates on the hero currently selected in the game. Gear optimization keeps currently equipped items in the candidate pool, searches both weapon slots plus the other six slots as one loadout, rejects weapons incompatible with the job or active skills, and evaluates affixes, granted skills, active Set thresholds, build-guide equipment, and calculated combat attributes together. Manually selected themes bias the result without overriding hard weapon requirements. Skill optimization always resets the current allocation first, uses the game's normal reset cost and validation, and then distributes available points according to the closest matching job guide and talent descriptions. Each action requires a second confirmation click.

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
