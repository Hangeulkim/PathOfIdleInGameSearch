# Path of Idle In-Game Search

This standalone BepInEx plugin displays an item search panel inside Path of Idle. It does not require the browser dashboard or local server.

For a plain-language, click-by-click explanation of what is automated, read
[the Auto Build walkthrough](README.md#auto-build-from-your-click-to-the-equipped-result).
This guide provides the more technical model and storage details.

## Installation

1. Download the ZIP from [Releases](https://github.com/Hangeulkim/PathOfIdleInGameSearch/releases/latest).
2. Extract the ZIP completely and close Path of Idle.
3. Run `install.bat`. It installs this mod and, only if missing, the pinned
   official BepInEx 6 IL2CPP runtime after checking its checksum.
4. Start the game through Steam and press `F3` or `Ctrl+F`.
5. Existing BepInEx users can instead copy the release DLL into
   `<Path of Idle>\BepInEx\plugins`. Existing mod settings are preserved.

This plugin changes game state when
you explicitly use actions such as moving or selling items, bulk-opening boxes,
or applying Auto Build. Auto Build can move equipment and spend the game's
normal skill reset/transform currency. Keep a normal game-save backup before
using automation after a game update.

## Feature overview

| Page | Main purpose | Save-changing actions |
| --- | --- | --- |
| Item Search | Search Inventory, ordinary Warehouse, and Vault; inspect matching item and effect text | Move the selected exact item through the game's native transfer routines |
| Bulk Open | Filter equipment/rune boxes, choose reward routing, and clean ordinary Warehouse gear | Open boxes, route rewards, immediately sell verified overflow, or sell all ordinary Warehouse gear |
| Auto Build | Jointly optimize the selected hero's eight gear slots, skills, masteries, transforms, and set thresholds | Equip the verified winner and, when enabled, pay normal reset/transform costs |
| Forge | Compare real Forge gains, max equipped gear, and batch-check Legendary-to-Mythic candidates | Spend Forge resources or irreversibly attempt confirmed Mythic refinements |

## Controls

- While the game is active, press `F3` or `Ctrl+F` to open and close the panel.
- Press `Esc` to close it.
- Drag the header to move the panel and drag its lower-right grip to resize it.
  Oversized pages scroll inside the current window.
- Use the speed controls in the title bar to select `0.5x`, `1x`, `2x`, `3x`, `5x`, `10x`, `20x`, `50x`, or `100x`. The `1x` button resets normal speed.
- Operation details are hidden by default. Use **Show log** on Bulk Open, Auto
  Build, or Forge when the full result is needed; active progress and safety
  warnings remain visible.
- Separate words with spaces to require every word: `critical damage`.
- Use `|` for alternatives: `legendary|unique`.
- Prefix a word with `-` to exclude it: `damage -health`.
- Quoted phrases are matched as one term: `"critical damage"`.
- The mouse wheel changes search-result pages or scrolls the panel while the
  pointer is inside it. Wheel and pointer input outside the panel remains owned
  by the game.

The Auto Build page also exposes two independent window options:

- **Start behind other windows** is off by default and takes effect on the next
  launch. After the plugin has loaded, it demotes the game without pausing Unity
  updates. Because a plugin cannot run before the game creates its first window,
  this setting cannot prevent every possible Steam/Windows focus change that
  occurs earlier in process startup.
- **Activate with background F3 / Ctrl+F** is off by default. When enabled, it
  registers both keys as system-wide shortcuts so either key brings the game
  forward and toggles the panel even while another app is active. This means
  those keys are captured from other apps for as long as the option and game are
  active. The UI reports a conflict if another program already owns either key.

Neither option applies an always-unfocusable window style. A normal taskbar or
mouse activation remains possible, including during local diagnostic runs.

## Item search

The searchable text includes the localized and English item names, rarity, equipment part, subtype, level, storage location, and affix descriptions. Inventory data is read once per second only while the panel is open. Each result can be moved between inventory and storage through the game's own transfer routines.

Search results are separated into **INVENTORY** and **WAREHOUSE** sections. Inventory results use green accents, warehouse/vault results use cyan accents, and matched words are highlighted in item names, locations, and affix descriptions.

## Bulk opening and Warehouse cleanup

The Bulk Open page can leave rewards in inventory, use the game's normal
Warehouse/Vault routing, or keep only gear that actually enters the Vault and
sell the rest. Vault replacement is handled explicitly: if a stronger reward
enters a full Vault and evicts the previous item into ordinary Warehouse (or the
inventory), the evicted item is the one sold. Every sale uses the exact native
item and source field, empties the small Market queue immediately, and verifies
both item removal and sale proceeds before another box is opened.

The same page also has **Sell all Warehouse gear (Vault excluded)**. It takes a
second confirmation click and enumerates only ordinary Warehouse equipment
fields. Vault groups, inventory items, and equipped items are never candidates;
unsellable Warehouse equipment is skipped. Processing can be cancelled between
items.

## Auto Build

**Confirming starts an applying run, not a read-only recommendation screen.**
The first button click arms the action for five seconds; the second starts
planning and allows the chosen result to be equipped without a further prompt.
Paid skill transformation is enabled by default (12-attempt default limit).
After successful application, displaced/staged gear is normalized toward Vault
and sellable gear not retained there may be sold. This cleanup is not a
keep-all-old-gear mode. Back up saves before confirming.

Auto Build jointly evaluates the hero's available skill slots and transforms,
all eight equipment slots, affixes, runes, unique and mythic effects, and active
2-piece/4-piece set thresholds. Automatic role discovery starts from the game's
native skill-tag table (including Support, Minion, Ally, Aura, and Totem); native
effect/action previews validate and score the resulting candidates. Planning is
an anytime search whose overall limit can be entered directly in the Auto Build
page: 10 seconds or more, or unlimited, with a 60-second default. The combat/DPS
preview for each retained comparison still covers 30 seconds; that simulated
preview horizon is independent of the overall planning limit.

### Themes and skill relationships

The selectable themes are Auto, Physical, Elemental, Fire, Ice, Lightning,
Minion, Bleed, Corrosion, Critical, Support, and Defense. Theme priority is a
continuous 0-100 setting: `0` favors general output, `50` is the balanced
default, and `100` optimizes as strongly as possible for the selected concept
without forcing a named item or skill.

Skill interpretation proceeds from broad native relationships to narrower
ones. It reads the game's skill tags, target selectors, effect/action rows,
summon ownership, controlled-entity type, damage type, and attributes before
using localized words as a fallback. Ally, Minion, Sentry, Totem, Aura, and
Support are therefore not treated as interchangeable labels: a controlled
entity contributes only through the native relationship and attack/stat lanes
it actually uses. Set effects are activated at their authored thresholds,
including distinct 2-piece and 4-piece effects, rather than by a generic set
count bonus.

This is data-driven rather than a list of forced builds. New skills, items, or
themes can participate automatically when they reuse native tags and effect
structures the plugin can resolve. A genuinely new native action or condition
shape is left unresolved or valued conservatively instead of being guessed; it
may require a plugin update before it can be compared fairly.

The skill planner evaluates base, active, passive, and mastery allocation as one
package. Missing final-package skills may be sought through the shrine's normal
paid transformation when that option is enabled. Application verifies the
resulting skill/mastery levels and remaining points; unused legal points are a
verification failure rather than an accepted optimization result.

### Runes, rune words, and rarity

Auto Build does not exclude Magic (`quality 7`) wearable equipment. Rarity is a
small screening prior, not proof that an item is better. The final comparison
uses the candidate's actual saved structure:

- the equipment's core and ordinary affixes;
- every affix on every rune already present in its socket list;
- the completed rune-word affix, when present;
- native attribute changes, granted abilities, skill variants, and rune-word
  talents, skills, or masteries produced by those affixes.

Numeric rune effects enter the same native attribute model as other equipment
stats. Ability and trigger effects enter the same unconditional or expected-
activation model as item and set abilities. Skill variants and rune-word extra
talents are also checked after equipment is applied; a missing expected effect
makes final verification fail.

This scope is deliberately item-based. Loose rune items in Inventory or
Warehouse are not moved, socketed, or recombined, and Auto Build does not search
hypothetical rune arrangements. It compares each equipment item with the runes
and rune word it already has.

### Combat, resource, and defense preview

Every retained package uses the same 30-second event schedule. Native cast and
action timing, hits, periodic effects, summons, HP/MP costs, available resource
budgets, and supported regeneration effects limit how often an action can
contribute. This makes the result a controlled comparison proxy, not a promise
of exact DPS against every enemy or encounter.

Defense is not evaluated against an invulnerable target dummy. One immutable
incoming-pressure profile is captured from the selected hero's current,
resource-limited base attack before candidates are changed, doubled to make
mitigation meaningful, and capped so a single modeled hit cannot exceed 45% of
the incumbent maximum health. The 30-second defense simulation then applies
damage-type defense, resistance, dodge, toughness, immunity, shield generation
and decay, health regeneration, self-healing, death prevention, remaining
health/shield, and time alive at ten steps per second.

Healing and shielding count as self-survival only when the native target path
can select the caster. For example, the native low-health-friend selector with
its include-self flag contributes to self recovery; random-friend-only effects
do not. Additional ally-only recipients remain Support value and are not
silently multiplied into the caster's own survivability.

The **Ignore forge bonuses** toggle controls the equipment basis used by Auto
Build. When it is on, candidate screening, numeric ranking, and the
native 30-second comparison all evaluate every equipped, Inventory, Warehouse,
and Vault item at its pre-Forge (`+0`) core values. Affixes, runes, unique/Mythic
effects, and set effects remain active. This is comparison-only: it does not
remove Forge levels from the real item, and the separate Forge optimizer always
uses the real current level plus its predicted next level.

Chance-based skill, affix, unique, and set effects use deterministic expected
values over that preview: observable trigger opportunities multiplied by the
authored probability. For example, 9.45 eligible casts at 30% contribute 2.835
expected activations. Cast, hit, summon, and fixed-timer triggers are supported;
conditions whose opportunity count depends on unknown enemy behavior receive a
conservative zero instead of an invented activation rate.

Before native comparison, warehouse equipment is screened by hero
compatibility, item level and quality, plus set, unique, effect, and affix
structure. The non-current shortlist is capped at 160 items (40 weapons and 20
items for each other equipment part), while currently equipped items are always
retained. This keeps expensive native work focused on plausible and
structurally distinct choices instead of simulating the entire warehouse.

Complete challenger and current loadouts are compared under the same already
selected skill/mastery plan. Precomputed item scores shortlist candidates only;
they are not added again to the final result because their stats, affixes, runes,
and native effects are already represented by the common 30-second native build
preview. Unresolved but structurally proven ability relationships and explicit
unused-effect penalties remain separate. It does not rebuild every skill package
separately for every gear candidate.

Before any winning loadout is accepted, the plugin applies it through the
game's own routines and verifies the resulting eight equipment slots, skill and
mastery levels, points, gear variants, gear abilities, and active set effects.
If a complete and fair comparison cannot finish inside the planning deadline,
the plugin leaves the current gear unchanged rather than applying a heuristic-
only result. Apply and post-apply verification may continue after planning has
finished, so total Auto Build wall time can exceed the selected planning limit.

### Practical Auto Build examples

**Fire-focused build**

1. Select the intended hero and choose **Fire**.
2. Set **Theme priority** to `100` for a strongly focused result, or `50` to
   keep general output equally important.
3. Leave the best-candidate search at its `60`-second default and enable the
   Warehouse/Vault candidate option.
4. Enable skill transformation only if spending the normal transform currency
   is acceptable, then start the joint build.

The planner can replace Fire-named skills when another native Fire/Burn package
performs better. A four-piece set is selected only when its real two- and four-
piece effects beat the alternatives in the complete loadout.

**Minion, Ally, Sentry, or Totem build**

Choose **Minion** and raise theme priority. The planner starts with native tags,
then follows ownership, summon/produce rows, target selection, action timing,
damage type, and inherited attributes. An Ally is not counted as a Minion just
because its name looks similar; it contributes when those native relationships
show controlled-entity output relevant to the package.

**Defense or Shield build**

Choose **Defense** with a high priority. The comparison uses the same incoming-
pressure profile for every loadout and includes mitigation, avoidance, shield,
self-targetable healing, regeneration, resource costs, and survival time. A
two-piece defense set can legitimately beat its four-piece version when the
four-piece effect adds less survival than the two displaced items.

**Ignore existing Forge investment**

Enable **Ignore forge bonuses** to answer, “Which item would be best if all of
them were unenhanced?” This changes comparison values only. Socketed runes, rune
words, affixes, Unique/Mythic effects, and set effects stay active, and no real
Forge level is removed. Run Auto Build first, then use the separate Forge page
if the chosen equipment should actually be upgraded.

## Workshop actions

The Forge page keeps real equipment upgrades separate from Auto Build. **Find
best target + apply one level** evaluates the selected hero's currently equipped
items at their un-forged value, current accumulated Forge value, and predicted
next `+1` value. It ranks the actual 30-second build-performance gain against the
native game cost, then applies exactly one level after a second confirmation
click. Hero, equipment, skills, available resources, and the empty Workshop
field are checked again immediately before the first save-changing action.

**Max all equipped gear** is a separate confirmed action. It works only on the
selected hero's currently equipped items, prioritizes the lowest Forge levels,
uses bounded `+5` batches where the native Workshop supports them, and stops on
an uncertain result or resource-wide block. It never selects replacement gear;
that remains Auto Build's responsibility.

**Check all Mythic candidates + batch refine** scans non-equipped equipment in
Inventory, ordinary Warehouse, and Vault from native table relationships rather
than item names. A candidate must be an unlocked, rune-free Legendary with a
native Mythic upgrade target. Duplicate base items are preferred, followed by
lower Forge investment, level, and main-attribute value. Locked, socketed, and
equipped items are never candidates.

Mythic refinement is destructive: the plugin reads the current native success
rate and price immediately before every attempt, and each failure permanently
destroys that Legendary exactly as the game does. The first click performs a
read-only preflight and arms the exact candidate count for 12 seconds. The
second click requires the same candidate set, then visits that fixed set once in
its safe ordering; newly appearing items are never admitted mid-run. Every item
is revalidated for identity, storage location, native eligibility, resources,
and Mythic Vault capacity immediately before its irreversible roll. Capacity-
blocked items are skipped, resource or Workshop-wide blocks stop the remaining
batch, and any uncertain result stops immediately. The final status reports
attempted, succeeded, failed/destroyed, and skipped counts. A successful Vault-
origin result is routed through the game's normal Vault logic. If it replaces an
older item into ordinary Warehouse or Inventory, only that exactly identified
displaced item is immediately sold; the new Mythic item is never used as the
sale target.

## Verification and limits

The published v1.1.8 regression baseline contains 17 read-only Auto Build cases
across Shield, Fire, Ice, Lightning, Pierce, Warrior Defense/Auto/Minion,
Hunter Ally-Minion/Bleed/Critical, Mercenary Minion/Physical/Sentry, Priest
Auto/Totem-Minion, and Cleric Support. Its release verification ran all 17 cases
against their stored equipment, skill, set-effect, and point-allocation
signatures. Every release-baseline case matched and verified `110/110/0`
allocated/available/remaining points. Targeted cases are also repeated after a
fresh process launch when testing deterministic ordering. Runtime previews
temporarily pause and later restore the target hero's Adventure Auto state so
background progression cannot alter the fixture during a comparison. Equal
loadouts place the stronger profile-scored weapon in the native active slot,
then use persisted item data rather than process-specific memory addresses for
the remaining deterministic tie-breaks.

Version 1.1.9 includes a cross-job-skill fix with a separate three-case Healer baseline,
repeated twice per case, plus a real eight-slot apply and injected-failure
rollback check. Owned Alien talent rows now participate in the same saved-point
plan as native job rows; equipment-granted skills remain separate. The original
17 cases also passed read-only state/selection/point-budget invariants on the
progressed live save. That live check does not claim equality with the older
save's signatures or force its old 110-point budget. See
[the reproducible checks](scripts/HEALER-REGRESSION.md) and
[verification receipt](scripts/regression-receipts/2026-09-06-healer-equip.json).

That matrix is evidence for the listed saves and game data, not proof that every
future game update or unseen effect is modeled. Apply-time identity checks,
capacity checks, rollback paths, and conservative handling of unresolved native
relations remain necessary. After a Path of Idle update, use a save backup and
run read-only previews before permitting any bulk sale, transformation, Forge,
or Mythic action.
