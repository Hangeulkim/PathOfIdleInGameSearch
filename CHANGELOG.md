# Changelog

## 1.1.5

- Replaced the separate primary gear and skill optimizers with one combined Auto Build plan that jointly evaluates all eight equipment slots, base/active skill combinations, and the complete mastery/point-level vector through a bounded native 60-second preview.
- Added a shared plan token and exact saved-level vector across selection, application, and verification. Saved, equipment-adjusted effective, and capped skill levels, learned skills, variants, spent points, and all eight equipment slots must match before success is reported.
- Expanded final verification to cover the exact identity of all eight equipped items, learned and equipment-granted skills, variants, saved/effective/capped levels, and spent/remaining point totals.
- Counted equipment-granted skills in performance scoring at their granted levels without consuming Shrine learned-skill slots or talent points.
- Changed Auto elemental focus to use native skill damage types instead of localized names or descriptions.
- Made unsupported runtime-dependent preview conditions fail closed rather than assigning speculative values. The score remains a bounded proxy and does not claim exact enemy or combat-AI DPS.
- Made the combined application atomic across gear and skills. A post-commit failure restores the original equipment and talent rows, refunds Blood spent by that run, restores talent progress counters and saved skill preferences, and verifies the restoration before reporting failure.
- Documented that an observed roughly 20-second optimization run is machine- and save-specific, not a promised runtime; inventory size, hero, and skill pool can change it.
- Kept advanced gear-only and current-gear-skills-only actions as explicit recovery tools; the combined action remains the primary optimizer.

## 1.1.4

- Removed all mod diagnostic logging and debug symbols from the distributed Release DLL. Fresh BepInEx installations also start with console and disk logging disabled.
- Reworked bulk box and rune opening into a one-item-per-frame verified session with cancellation, progress, save-identity checks, and immediate no-retry termination whenever the native game call does not consume the item, preventing duplicate partial rewards.
- Added quality-aware bulk opening, including multi-select tiers, `or higher`, confirmation skipping, and automatic native storage routing for newly opened equipment.
- Rebuilt Auto Skills around the selected performance objective and native same-job Shrine candidate pool, while preserving fixed and alien skills, respecting unlocked rows and weapon requirements, using the exact milestone-adjusted point budget, and failing closed when reset, transformation, or allocation verification is incomplete.
- Enforced the game's native Mythic equipment limit, including the additional slot unlocked by the game's own level rules, while evaluating all eight equipped slots and currently worn items.
- Improved Auto Gear scoring for real affixes, Legendary/Mythic/Unique effects, runes, skill variants, and active 2-piece/4-piece Set thresholds without treating official guide metadata as a performance bonus.
- Fixed overlay focus, keyboard, wheel, tooltip, and transfer-button handling so only the visible panel consumes input and opaque details no longer cover usable controls.
- Improved localized Set search/details, class display, full affix text, language refresh, and layout clipping.

## 1.1.3

- Corrected Set activation thresholds to use the game's real 2-piece and 4-piece requirements, including the full 4/4 effect, and improved theme-aware Set scoring so Fire, Ice, and Lightning builds no longer reward conflicting active Sets.
- Fixed Auto Skills allocation to check saved invested points instead of equipment-modified effective levels, ensuring transformed guide skills receive their required first point.
- Added all runtime `bodyAttr` values from normal, Legendary, Mythic, Unique, and rune affixes to equipment candidate scoring before loadout pruning, plus bounded behavior scoring for skill variants, abilities, and runeword talents.
- Removed circular scoring bias from effects already granted by equipped items or active Sets so they no longer reward themselves merely for being active in the current loadout.

## 1.1.2

- Fixed Auto Skills rejecting a successful talent reset because the mandatory level-1 base-skill point was incorrectly counted as a resettable point.
- Switched reset validation to the game's native resettable-point counter, with a version-tolerant save-data fallback.

## 1.1.1

- Made Auto Gear an exact eight-slot transaction across equipped gear, Inventory, Warehouse, and unlocked Vault storage, with per-slot verification and rollback on failure.
- Added native Vault routing normalization after a successful loadout change, plus a safe stop and restoration when a full bag has no reversible bridge slot.
- Corrected weapon constraints: job rules and the selected or recommended base skill remain requirements/preferences, while other active skills contribute synergy instead of becoming universal hard constraints.
- Reworked loadout scoring around native attributes, rune affixes, active Set breakpoints, and skill synergy. It now estimates about 60 seconds of sustained single-target output from the selected base skill's native damage, cooldown, and skill-speed calculations; it remains a proxy rather than an exact combat simulation.
- Added configurable Shrine skill transformation attempts using the normal Blood cost, with reset-cost reservation, missing-guide-skill targeting, and explicit partial-match reporting.
- Added strictly validated skill reset and concentrated allocation into the relevant build-guide masteries that are actually available.
- Kept a second confirmation click for both Auto Gear and Auto Skills.

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
