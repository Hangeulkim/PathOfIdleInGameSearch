# Changelog

## 1.1.0

- Split Search, Bulk Open, and Auto Build into separate opaque pages.
- Added complete Legendary, Mythic, and Unique affix reconstruction and Set class display.
- Added keyboard-editable search caret navigation and multi-select search tiers.
- Blocked the game's direct `Mouse ScrollWheel` hero-switch path only while the focused overlay is under the pointer; outside input remains available.
- Added eight-slot selected-hero loadout optimization with both weapon slots, currently equipped items, hard job/skill weapon requirements, real affixes, active Set breakpoints, skill-specific effects, guide equipment, and game-calculated combat attributes.
- Fixed equipment-box and rune-box quality filters by reading the authoritative `TTool.quality` value instead of the frequently zero save-item quality.
- Added Auto, Physical, Elemental, Fire, Ice, Lightning, Minion, Bleed, Corrosion, Critical, Support, and Defense build themes.
- Added talent reset preflight, normal in-game Blood validation, guide-aware automatic skill allocation, and clear failure messages.
- Preserved action status while periodic Inventory, Bulk Open, and Auto Build data refreshes run.

## 1.0.1

- Blocked game hotkeys and Unity UI keyboard navigation while the overlay is open.
- Restored the game's previous keyboard state only after the overlay close key is released.
- Preserved mouse interaction outside the overlay while keeping clicks and wheel input blocked inside it.
- Fixed clipped quality labels in the bulk-opening controls.

## 1.0.0

- Added an opaque in-game inventory, Warehouse, and Vault search overlay.
- Added multi-select quality filters and affix-only search.
- Added full item, affix, Set membership, and Set bonus tooltips.
- Added game-native inventory, Warehouse, and Vault transfers.
- Added equipment-box and rune-box bulk opening with multi-select quality and `or higher` filtering.
- Added optional confirmation skipping, partial opening when space is limited, and automatic storage of newly opened gear.
- Added stepped and direct-input game speed control from 0.1× to 100×.
- Added localized UI for Korean, English, Simplified Chinese, and Traditional Chinese with game-language auto detection.
- Added local click and mouse-wheel blocking only inside the overlay.
- Added a one-click Windows installer/updater that preserves user settings.
