# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

An SPT (Single Player Tarkov) **server-only** mod: derestricts "kill in this one specific sub-area of the map"
quest objectives to "kill anywhere on the same map," compensating by scaling the required kill count up
(configurable, default 1.5x).

**Why:** several vanilla Elimination quests (e.g. Rite of Passage's "kill 10 Scavs at the old gas station on
Customs") require the kill to happen inside a small trigger zone, not just on the right map. In single-player,
bot AI often doesn't reliably path into these specific zones, effectively forcing the player to skip the
objective. The fix removes only the zone restriction — the quest still requires killing on the correct map — and
raises the kill count so it isn't trivially easier than before.

Like ItemCountTooltip, this needs no client half — the whole thing is a one-time, in-memory mutation of the
quest database at server startup. Unlike ItemCountTooltip, it's the reverse case: **server-only, no client
project** (the client never needs to know these quests changed shape — it just renders whatever conditions the
server serves it).

## SPT version

Targets SPT 4.1.3, same as the user's other mods (SkillPointsMod, ItemCountTooltip, TaskItemIndicator fork —
see their own CLAUDE.md files under `C:\Dev\`). Reference source for verifying real server APIs:
`C:\Dev\spt-reference\server-csharp` and `\server-mod-examples`. The real, live quest/locale data actually used
to build this mod's `config\zone-quests.json` came from the user's own server install at
`C:\Games\SPT 4.1\SPT_Runtime\SPT_Data\database\` (`templates\quests.json`, `locales\global\en.json`,
`locations\<map>\base.json`) — read-only, per the same "never modify the live install" rule the other mods'
CLAUDE.md files establish, just for data lookups.

## Build

```
dotnet build
```

Single project, `net10.0`, `Microsoft.NET.Sdk.Web` (matching every official `server-mod-examples` template and
SkillPointsMod's own server half), output at `bin\Debug\NoMoreKillZones\NoMoreKillZones.dll`. No test or lint
scripts.

## Architecture

One file (`NoMoreKillZonesHook.cs`) plus two shipped data files:

- **`NoMoreKillZonesHook.cs`** — `[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)] : IOnLoad`, the exact
  pattern from `server-mod-examples/14AfterDBLoadHook` (runs once, straight after the raw database load, before
  anything else — including SPT's own logic — has touched it). `OnLoadAsync` reads `config/config.json`
  (`killCountMultiplier`, default 1.5) and `config/zone-quests.json` (the 31-entry table, see below), then for
  each entry:
  1. `FindCondition` — quest condition IDs are unique across the whole database, but which of
     `Quest.Conditions`'s five status-keyed lists (`Started`/`AvailableForFinish`/`AvailableForStart`/
     `Success`/`Fail`) a condition lives under varies per quest (all 31 found here live under
     `AvailableForFinish`, i.e. what you need to complete to finish, but the search checks all five rather than
     assuming that stays true across game updates).
  2. `condition.Counter.Conditions.RemoveAll(c => c.ConditionType == "InZone")` — strips **only** the zone
     restriction. Confirmed via decompiling the real condition JSON that the zone restriction is a **separate
     sibling sub-condition** from the `Kills` sub-condition (not a field merged into it), so this is a precise,
     surgical removal that leaves every other constraint on the objective untouched — weapon-type restrictions
     (e.g. Gendarmerie's "while using Pistols"), time-of-day windows (Illegal Logging's "22:00-10:00"), "In one
     raid" flags (Forester's Duty), etc. all still apply exactly as before.
  3. `condition.Value = Math.Ceiling(condition.Value * multiplier)` — scales the required kill count.
     `Math.Ceiling`, not rounding or truncating, so the multiplier never has *less* effect than configured for
     odd source values (e.g. 5 × 1.5 = 7.5 → 8, not 7).
  4. `localeService.GetLocaleDb("en")[conditionId] = newText` — overwrites the condition's own display string.
     **Confirmed necessary, not assumed**: `dynamicLocale: false` on every one of these conditions, and each has
     a fully pre-written, fixed locale string keyed by its own condition ID (e.g. `"Eliminate Scavs at the old
     gas station on Customs"`) — the client does not compose this text from the condition's structured fields,
     so leaving it alone after changing the JSON would show stale/wrong text. **`LocaleService.GetLocaleDb`
     is used deliberately instead of touching `LocaleTable.Global` directly** — that property's own doc comment
     warns it's lazy-loaded and changes made directly to it won't persist; `GetLocaleDb` triggers the lazy load
     and returns the real backing dictionary. English-only: other languages will show the original, now
     slightly stale but still perfectly understandable text (still says the right creature and map, just also
     mentions a zone that no longer matters) — not attempted, to avoid shipping bad machine-translated strings
     for a case with only one real, verified source language.
  - Logs a warning (not a hard failure) for any entry whose condition ID isn't found, or whose `InZone`
    sub-condition is already gone — both would mean a game/SPT update changed the underlying data and this mod
    needs re-checking, not that the mod should crash the server.
  - Never writes to disk. Removing the mod (or a future SPT update replacing the DB files) means the very next
    server start serves unmodified quests again — no cleanup needed.
- **`config/config.json`** — `{ "killCountMultiplier": 1.5 }`. Missing/unparsable file logs a warning and falls
  back to this default rather than failing mod load, same defensive pattern as SkillPointsMod's own config
  loading (reused directly — `ModHelper.GetAbsolutePathToModFolder` + `FileUtil` + `JsonUtil`).
- **`config/zone-quests.json`** — the 31-entry table: `{ quest (human label, for logging only), conditionId,
  newText }`. **Hand-curated, not templated/auto-generated** — deliberately, given the lesson from a sibling
  project's key-name generation bug earlier: with only 31 entries and real varied English sentence structure to
  preserve (weapon/time/other qualifiers that must survive the zone removal), a generic "strip everything after
  the last preposition" transform would have been both fragile and lower-quality than just doing it by hand
  once, checked against the real original text for every entry.
    - Found by scanning the live `quests.json` for every `CounterCreator` condition whose `counter.conditions`
      contains **both** a `Kills`-type and an `InZone`-type sub-condition (31 across 20 quests — not guessed,
      queried directly against the user's actual game data). A looser first-pass filter (`InZone` present, not
      requiring `Kills` too) produced a false positive — "Back Door" (an extraction-related zone condition, not
      an elimination one) — corrected before finalizing the list.
    - `newText` never embeds the kill-count number. Confirmed by example that the client renders the x/N
      progress badge separately from the condition's own `Value` field, not from the string — verified against
      real vanilla text like `"Eliminate Rogues on Lighthouse"` (no "10" in it, despite requiring 10 kills).
    - `newText` reuses BSG's own established phrasing pattern for this exact scenario — `"Eliminate {who} on
      {Map}"` — rather than inventing new wording, found via a real vanilla precedent quest ("The Huntsman Path
      - Outcasts") that already has a plain map-only elimination objective with no zone restriction at all.
      That same quest is also what confirmed map-scoping survives removing only the `InZone` sub-condition: it
      proves a `Kills`-only condition (no `InZone`, no separate `Location` sub-condition) is already correctly
      scoped to one map purely via the *quest's own* top-level `location` field — so deleting `InZone` here
      doesn't risk accidentally making the objective count on any map, only removes the finer zone restriction,
      exactly matching the user's explicit clarification ("needs to remove the specific zone within a map, but
      does need to be the same map").
    - "Preemptive Strike" is the one exception without a map in its `newText` — its quest's own `location` field
      is `"any"` (not a specific map), so post-fix the objective is genuinely "kill Scavs anywhere," matching
      what removing a zone restriction under an already-any-map quest naturally means.

## Design decisions (confirmed with the user, not guessed)

- **Apply to all 20 found quests**, not a curated subset — explicit user choice over hand-picking which zones
  are "actually broken enough to fix." Simpler, and per-quest exclusions could be added to `zone-quests.json`
  later (just delete the entry) if one ever turns out to need the zone restriction kept.
- **Kill-count multiplier is configurable** (`config.json`), not hardcoded, matching the pattern already used
  across this user's other mods (SkillPointsMod's `skillPointsPerLevel` etc.) — default 1.5x, the user's own
  example figure ("Kill 10... would become kill 15").
- **In-memory mutation at server startup, never touching disk** — the user asked directly whether to take the
  same approach as a mod they'd seen before (which worked this way, reverting cleanly if removed) or "something
  more efficient." Confirmed this **is** the efficient/standard approach for this class of SPT mod, not just a
  similar one — same mechanism `server-mod-examples/14AfterDBLoadHook` demonstrates officially.
- **Display text patching was treated as a hard requirement, not an afterthought** — the user raised it
  unprompted, correctly anticipating that changing only the JSON condition would leave a mismatched description
  on screen. Confirmed necessary by decompiling the actual condition data (`dynamicLocale: false`, real
  hardcoded per-condition locale strings) before writing any code, not assumed either way.

## Known gaps

- **Confirmed working: kill count, value merge, and structural condition merge (two objectives -> one).**
  **Still genuinely stale: the display text**, specifically and only for Rite of Passage, which was already
  *accepted* on this profile before the mod was ever installed. The user's first "looks merged and updated"
  read turned out to be a quick glance that only registered the two objectives becoming one — a closer look
  afterward showed the surviving objective still reads "Eliminate Scavs at the new gas station on Customs," the
  exact original text, not the patched "Eliminate Scavs on Customs." This is **not** a key-mismatch bug — the
  diagnostic log's own "before" value for this exact condition ID
  (`675c1f17cf59d5433be7ae77`) is character-for-character the same string the user sees, confirming we're
  reading/writing the one real key correctly; the server has proven correct twice now.

  **Ruled out**: the player profile save (`user\profiles\<id>.json`) — checked directly. It stores only
  `{id, type, value, sourceId}` per `TaskConditionCounters` entry and `{qid, status, statusTimers,
  completedConditions}` per quest — zero text of any kind. Not the source of the staleness.

  **Found, not yet confirmed as the actual cause**: the client (`Assembly-CSharp`, `DataPrepareOperation.cs`)
  has two separate locale-fetch paths that both merge into the same underlying dictionary
  (`LocalizationManager._locales`, via the same `UpdateLocale` method — so this isn't two separate stores, and
  server data always wins on overwrite either way), but with different refresh timing:
  - `LoadMainMenuLocale` (called around login) fetches via `GetMainMenuLocalization`, guarded by
    `!ContainsMainMenuCulture(locale)` — **explicitly skipped on every call after the first, for the rest of
    that client process's lifetime**, regardless of how many times the quest screen is viewed afterward.
  - `ReloadLocale` → `DataPrepareOperation.Run`, called specifically when **loading into a raid** (bundled with
    weather/time/level-settings) — calls `session.GetLocalization(locale)` **unconditionally, no cache guard**,
    every single raid load.
  So checking the quest screen from the main menu, before entering a raid that session, may show whatever was
  cached at initial login; entering a raid forces a full unconditional re-fetch that can't be stale the same
  way. **Still not certain this is the actual mechanism at play here** — it's a real, confirmed asymmetry in the
  client's own fetch logic, not a guess, but hasn't been directly tied to this specific symptom by observation
  yet. Next check: whether the quest text reads correctly once viewed after a raid load this session (the user
  was already heading into one to separately test in-raid kill counting, so this may resolve itself as a
  byproduct of that test). If it's still stale even after a raid load, this theory is wrong and the "pinned for
  already-accepted quests" theory (a **not-yet-accepted** quest among the other 30 conditions showing correct
  text immediately would confirm that instead) is the next one to chase.
  Structural note this test *did* settle: removing a `QuestCondition` outright from an already-accepted quest's
  list caused no observed desync — the merge itself (going from two objectives to one, combined count) rendered
  and behaved correctly, it's specifically the string display that's stuck.
- **Still not verified**: whether an actual in-raid kill on one of these now-derestricted objectives (e.g. a
  Scav killed anywhere on Customs, not at the old/new gas station specifically) correctly increments progress.
  Data-level correctness (values, text, structure) is now fully confirmed; the live counting behavior during a
  raid is the one remaining thing that needs an actual raid to test.
- **Whether `OnLoadOrder.PostLoad + 1` runs early enough relative to whatever else touches quest data** — the
  official example uses the same priority for exactly this kind of raw-DB edit, a strong precedent, and every
  observed result so far (correct values, correct text, correct merge) is consistent with it running at the
  right time, but this still hasn't been separately/directly isolated as a variable.
- **Only the English locale is patched.** Any other configured server/game language will keep showing the
  original zone-specific text (harmless — still names the right target and map, just also mentions a
  now-irrelevant zone) until someone verifies and adds correct translations for the other 15+ languages this
  server ships (`locales\global\*.json`) — not attempted here, deliberately, to avoid guessing at translations.
- **If a future SPT/game update changes any of these 31 conditions** (new zone IDs, restructured quest, removed
  quest), the affected entries will just log a "not found"/"no InZone to remove" warning at startup rather than
  silently doing the wrong thing — but `config/zone-quests.json` itself would need regenerating the same way it
  was built the first time (query the live `quests.json`/`en.json`, don't hand-patch individual IDs), and the
  affected `newText` values re-checked by hand again.
