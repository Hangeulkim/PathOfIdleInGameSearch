# Path of Idle In-Game Search

**English** · [한국어](README.ko.md) · [简体中文](README.zh-CN.md) · [繁體中文](README.zh-TW.md)

An unofficial Windows mod for item search, equipment and skill optimization, box opening, storage management and upgrades directly inside Path of Idle. No browser dashboard or separate server is needed.

**[Download the latest release](https://github.com/Hangeulkim/PathOfIdleInGameSearch/releases/latest)** · [Detailed guide and examples](GUIDE.md) · [Changelog](CHANGELOG.md)

This is the standalone **Hangeulkim/PathOfIdleInGameSearch** project. **1.1.9** includes the Healer/cross-job-skill auto-equip fix. Earlier links to `path-of-idle-stats` were a publishing mistake; use this repository for source and downloads.

## Install or update

**1.1.10 fixes the Windows installer launch error** (`all.ps1` / `is not recognized`). If an older installer fails, download the new ZIP and extract it into a fresh folder. This update does not change the build algorithm; the v1.1.9 screenshots below still show the same interface.

1. Download `PathOfIdleInGameSearch-v1.1.10.zip` from Releases and extract it completely.
2. Close Path of Idle, then run `install.bat`.
3. Start the game and press **F3** or **Ctrl+F**.

The installer finds the Steam game directory, verifies the mod DLL, and preserves existing mod settings. If BepInEx is missing, it verifies and installs the unmodified pinned official BepInEx 6 IL2CPP x64 archive included in the player ZIP; no separate installer download is needed. The matching BepInEx source and license are included too. Game binaries are not bundled. Existing BepInEx users can instead copy the separate release DLL into `BepInEx/plugins` while the game is closed.

Back up your save before automation, particularly after game updates. Resetting skills, transformations and upgrades use normal game resources. **Sales and failed Mythic refinements are irreversible.**

## Main features

- **Search:** Find Inventory, Warehouse and Vault items by name, quality, slot, level, affix or set. Inspect full descriptions and move items using the game's storage rules.
- **Auto Build:** Compare all eight equipment slots and skill/point allocation together, then equip and verify the result. Optional skill transformation uses normal in-game currency. Owned Alien skills remain learned skills; equipment-granted skills do not consume learned slots or points.
- **Themes:** Auto, Physical, Elemental, Fire, Ice, Lightning, Minion, Bleed, Corrosion, Critical, Support and Defense. Priority ranges from 0 (general performance) to 100 (selected concept), without forcing named items or sets.
- **Box opening:** Open filtered equipment/rune boxes in batches. “Keep Vault gear, sell rest” keeps rewards accepted by Vault and immediately sells verified overflow. If a better reward replaces an older Vault item, the displaced item is the sale target.
- **Warehouse cleanup:** A separate confirmed action sells ordinary Warehouse gear, excluding Vault, Inventory and equipped items.
- **Forge and Mythic:** Compare the next Forge upgrade, max equipped gear, or check eligible unequipped Legendary items for batch Mythic refinement. Mythic checks do not need a selected hero, recheck capacity/resources, and report successes, failures and skips.
- **Interface:** Drag/resize the panel, choose one of four languages, enter 0.1×–100× speed, and show detailed messages only when needed. Background startup and system-wide activation shortcuts are optional and off by default.

## Item Search: read the screen

Type a term in **Item Search**, leave **Include warehouse and Vault** enabled
to search storage too, then choose the Inventory or Warehouse/Vault result tab.
Quality buttons narrow the list. Searching does not move, sell or equip anything.

![v1.1.9 Item Search with a minion text query and Warehouse/Vault results](docs/images/v1.1.9-search.jpg)

*The search field is at the top; the two wide tabs above the results switch
locations. This example searches the text `minion`, not the Auto Build theme.
Each result shows its location and a **Move to inventory** button on the right;
that button moves that item only. Hover over the item for its full effects.*

These are real v1.1.9 game captures. Hero names, item counts, speed and saved
settings are examples from this save, not recommended defaults. Click a picture
to read it at full size.

## Examples

- Search `critical damage -health` to require both “critical” and “damage” but exclude “health”. Use `|` for alternatives and quotes for a phrase.
- For Fire, choose **Fire**, increase **Theme priority**, include Warehouse/Vault items and start Auto Build. Sets are compared by their effects, not their names.
- For summons, choose **Minion**. The comparison follows ownership, tags, attack type and inherited stats for Ally, Sentry and Totem skills; these labels are not automatically interchangeable.
- For Shield/survival, choose **Defense**. It compares mitigation, avoidance, shields, recovery, resource costs and time alive under the same incoming attacks.
- Enable **Ignore forge bonuses** to compare gear at its pre-Forge core values. Real upgrades are unchanged; affixes, existing runes and set effects still count.

## What does “automatic” mean?

The mod runs these operations inside the game after you start and confirm them.
You do not have to drag each chosen item onto the hero or spend each skill point
yourself. It is not a cloud AI agent watching the screen: the build calculations
run locally, read the game's item/skill data, and call its normal equipment,
storage and skill functions. No AI subscription or API key is needed.

Auto Build is one run for the selected hero, not a command to manage every hero
or keep changing builds forever. Search, box opening, Warehouse sale and upgrades
are separate actions; using Search alone does not start Auto Build. A separate,
off-by-default startup option can press Continue and enable the game's own Auto
for eligible active parties. That is not the build optimizer.

## Auto Build: from your click to the equipped result

### 1. Choose what you want

Select the hero in the game, open **AUTO BUILD**, and check the displayed hero.
Choose a theme and priority, choose whether to include Warehouse/Vault gear,
and enter a search time. Inventory and the hero's current gear remain sources;
turning storage inclusion off prevents searching Warehouse/Vault candidates.

![v1.1.9 Auto Build selected hero, theme and priority controls](docs/images/v1.1.9-auto-build-theme.jpg)

*Check the hero in the blue box, then use the theme buttons below it. Here
SHIELD has **Defense** selected. **Theme priority** and **Best-candidate search
time** are separate inputs. The pictured 100% / unlimited are saved example
settings; **Default 60** restores the normal 60-second search limit. If a newly
selected hero is not shown yet, use **Refresh** at the upper right.*

Check **Ignore forge bonuses** and the paid skill-transformation option before
starting. Transformation is enabled by default, with up to 12 attempts in the
default configuration; turn it off if you do not want those paid rolls. The mod
does not unlock unavailable skills or make the Shrine's random result certain.
Skill reset/allocation can still be needed even when transformation is off.

Click **Auto-build Gear + Skills**, then click its **Confirm** button within
five seconds. The first click only arms the action; it does not calculate a
final recommendation. The second click starts a run that may actually equip,
reset and allocate. There is no separate approval screen after the winner is
calculated, so make your settings and save backup first.

![v1.1.9 Auto Build options and combined execution button](docs/images/v1.1.9-auto-build.jpg)

*Scroll down inside Auto Build to see the storage, **Ignore forge bonuses** and
paid transformation choices immediately above **Auto-build Gear + Skills**.
That wide button starts the combined workflow after confirmation. The two
**Advanced** buttons below deliberately change only gear or only skills; they
are not the main combined action. No build was applied to take these pictures.*

### 2. Read what the hero can actually use

The mod reads the hero's job/weapon restrictions, unlocked skill rows, available
points, owned cross-job skills and Mythic equipment limit. It reads actual
owned item instances, including their affixes, current Forge levels, socketed
runes and rune words. It does not invent hypothetical perfect items or take
another hero's equipped loadout as an owned spare item.

The current loadout stays in the comparison, including empty slots. A
currently equipped item is not assumed to be best, but it is not discarded
merely because a newer item has a higher level or rarity.

### 3. Narrow the candidates, then compare complete builds

Trying every possible item/skill combination would take too long. The first
pass removes incompatible items and uses level, attributes, affixes and effect
structure to select promising alternatives. It also retains representatives of
different sets and special effects so an unusual combination can be considered.
That quick screening is not the final damage calculation.

The remaining work compares eight-slot loadouts with a skill/point plan. Skills
are classified using the game's tags and actual effects: what they target,
whether they attack/heal/shield/summon, their damage type, and which attributes
they use. A buff can be useful without directly dealing damage. A trap-only
effect is not credited as an active trap benefit when no matching trap exists;
the same item may still offer other useful ordinary stats.

Two-piece and four-piece bonuses are evaluated at their real thresholds. Four
pieces are not automatically better: the extra bonus has to outweigh what is
lost by replacing the other two items. Skill-granted and equipment-granted
effects are included without spending learned-skill points on the latter.

### 4. Apply the selected plan and check it

After a valid result is selected, the mod moves the exact items using the
game's own functions, performs allowed paid skill changes, allocates the
planned points and checks the actual result. It checks all eight item
identities, skills and levels, equipment-granted effects, and remaining legal
points. It does not declare success just because some buttons were clicked.

If application or verification fails, it attempts to restore the original gear,
skills, saved preferences and this run's Blood spending, then checks that
restoration. Other parties' Blood earnings are not part of the refund.
Extracted Vault items may be preserved in ordinary Warehouse during recovery;
“restored equipment” does not promise identical storage placement.

**Old-gear warning:** successful Auto Build also cleans up displaced/staged
equipment. It tries to keep it in Vault and may sell eligible gear that is not
kept there. This is not a keep-every-old-item mode. These committed sales are
not undone by a later run; a save backup is the protection against unwanted
sales.

You can cancel during planning while **Cancel** is enabled. Once item/skill
application starts, cancellation is disabled so it can finish or restore the
transaction. Closing the panel is not an undo button. Use **Show log** to see
why a run stopped, what changed, or whether restoration was incomplete.

## What Auto Build calculates

**Search time** is how long the mod may look for candidates: 10 seconds to unlimited, default 60. **Combat time** is the same simulated 30 seconds used to compare retained candidates. Applying and verifying the result can take additional time.

The comparison includes supported skill timing, mana/health costs, regeneration, summons, real 2-/4-piece set effects, Unique/Mythic effects, and runes/rune words already on each item. Chance effects use expected activations. Loose runes are not rearranged or socketed automatically.

For example, a 30% effect with ten eligible trigger opportunities contributes
three expected activations in the calculation. It is not forced to trigger
three times in your next real battle. If a trigger depends on enemy behavior
the model cannot establish, it is not given made-up uptime.

For damage themes, the model estimates how much supported output the build can
produce over those 30 seconds, accounting for resource and timing limits. For
Defense, every candidate receives the same modeled incoming attacks; health
can reach zero, and staying alive, mitigation, shields and usable recovery
matter. Only healing that can target the caster counts toward the caster's own
survival. Healing other allies contributes to Support rather than secretly
making the wearer invulnerable.

The displayed result is a comparison score, not the game's combat-log DPS. It
can contain theme-specific utility or survival value. “Bounded” in older
descriptions simply meant that only a limited set of candidates is examined
within a time limit, not that “bounded build” is a special kind of build.

Theme priority is a preference, not “100% Fire damage” or “100% certainty”. At
50, general and theme-focused performance are balanced; at 100, the selected
concept is emphasized. Useful buffs/passives and other support can still be
selected. Auto derives a focus from the hero's usable skill/effect data instead
of blindly requiring the current skills.

It is a build comparison estimate, **not exact damage against every enemy and not a guarantee of the best possible build**. New game mechanics can require an update. The mod verifies final gear, skills and points; an incomplete application is reported as a failure and restoration is attempted and checked.

A shorter search may examine fewer alternatives. Unlimited removes the search
deadline; it does not mean every possible combination or every unknown effect
is solved. A large stash, many skill variants and slow native game calls can
still take time. If no complete, safely applicable comparison is available,
the mod stops rather than treating a rough item-screening score as a verified
winner. A run that leaves your gear unchanged can therefore mean either that
the current gear remained preferred or that the run was unable to apply a
result; read the status to distinguish them.

The two **Advanced** actions deliberately optimize only one side: **apply gear
only** keeps the skill side fixed, and **skills for current gear** keeps gear
fixed. Use the main combined action for a joint build. Auto Build does not
perform real Forge upgrades or Mythic rolls; those are separate Workshop
actions. It also does not rearrange loose runes.

## Box opening and selling: exactly what is automated

Open **BULK OPEN**, choose equipment or rune boxes and their quality filters,
and select the equipment reward destination. The quality filter chooses which
boxes to open, not what random reward quality the game must generate.

![v1.1.9 Bulk Open destination, sale, box and quality controls](docs/images/v1.1.9-bulk-open.jpg)

*First choose **Inventory**, **Warehouse** or **Vault + Sell** in the destination
row. The two large opening buttons below show equipment/rune box counts; the
quality filters are underneath. The red **Sell all Warehouse gear (Vault
excluded)** button is a separate cleanup action, not the box-open button.
The pictured **Vault + Sell** means keep Vault-accepted gear and sell the rest.*

- **Inventory:** leave opened equipment in the bag.
- **Warehouse/Vault:** use the game's native routing rules; selecting storage
  does not guarantee every item goes to Vault.
- **Keep Vault gear, sell rest:** open one box, check where its reward actually
  went, retain Vault-accepted gear and sell the verified overflow immediately.
  For example, if new item B replaces old Vault item A, B is retained and the
  exactly identified A is the sale target. The small Market queue is cleared
  as each sale is verified, before continuing.

The default requires a second confirmation click. **Skip confirm** removes
that extra click only for non-selling box opening; selling still requires two
clicks. Leave it off until the selected filters and destination are what you
want. Batch work checks each item and can stop
between items; it does not blindly keep retrying an uncertain result. Sales
already completed are not reversed by cancellation.

**Sell all Warehouse gear (Vault excluded)** is a different, double-confirmed
action. It scans ordinary Warehouse fields only, excluding Vault, Inventory
and equipped gear; unsellable/locked items are skipped. It is not a command to
empty every storage system.

## Forge and Mythic: separate from choosing a build

![v1.1.9 Forge one-level, max-equipped and Mythic check controls](docs/images/v1.1.9-forge.jpg)

*On **Forge**, the upper two large buttons are **Find best target + apply one
level** and **Max all equipped gear (+5 first)**. Scroll down for the purple
**Check all Mythic candidates (no consumption)** button. Its first click only
checks candidates; a subsequent confirmed batch can consume items and resources.
It is separate from the two equipped-gear upgrades above.*

- **Find best target + apply one level:** compare the selected hero's equipped
  items at their current Forge level and predicted next level, then spend the
  normal cost on one confirmed upgrade.
- **Max all equipped gear:** repeatedly upgrade the current equipped items
  toward the native maximum while legal and affordable. It does not replace
  them with a new loadout, and it stops on resource or verification problems.
- **Check all Mythic candidates + batch refine:** the first click checks
  eligible unequipped Legendary items in Inventory, Warehouse and Vault. Locked,
  socketed or equipped items are not eligible. Confirm the same candidate set
  within 12 seconds to attempt the batch. No selected hero is required.

Before each Mythic attempt the mod rechecks the game's current success chance,
cost, item identity, resources and Mythic Vault capacity. It attempts the game's
normal roll, not a guaranteed upgrade. Success creates the native Mythic result;
failure destroys the Legendary. Capacity-blocked items are skipped, and a
resource-wide or uncertain failure stops the remaining batch. The result shows
attempted, succeeded, failed/destroyed and skipped counts. Newly appearing items
are not added halfway through a confirmed batch.

The startup/background settings, build choices and bulk-open choices are saved
locally. Reopening the panel keeps those settings; it does not by itself
authorize another sale, Mythic batch or Auto Build run.

## Verification, source and removal

The [guide](GUIDE.md#verification-and-limits) and [cross-job regression notes](scripts/HEALER-REGRESSION.md) distinguish current-save checks from historical exact-selection baselines. The 17-case current-save checks and six repeated Healer previews passed; actual Healer equipment application and failure restoration were also tested. These results are not a promise that every unseen save works.

To build, install .NET 6 SDK or newer and BepInEx, launch the game once to generate its interop files, then run `scripts/build.ps1`. Production builds exclude runtime test hooks and debug symbols. See the [release checklist](RELEASING.md).

To uninstall, close the game and run `uninstall.bat`. Settings, BepInEx and other mods are preserved.

Windows x64 / Steam / BepInEx 6 IL2CPP. An unofficial fan project, not affiliated with the game developers or BepInEx. See [third-party runtime details](THIRD-PARTY.md).
