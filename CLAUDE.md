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
  4. Registers a **transformer** on `localeTable.Global["en"]` (a `LazyLoad<GlobalLocaleDictionary>`) that
     overwrites `conditionId → newText` on every fresh deserialize — see "Known gaps" below for why this,
     rather than writing into `LocaleService.GetLocaleDb()`'s returned dictionary directly, is required:
     `LazyLoad<T>.Value` re-deserializes from scratch on every access with no memoization, so a direct write
     is silently discarded before the next locale fetch ever sees it. `AddTransformer` is the real,
     server-sanctioned persistence path (same one `CustomQuestService.AddQuestLocales` uses).
     **Confirmed necessary to patch text at all, not assumed**: `dynamicLocale: false` on every one of these
     conditions, and each has a fully pre-written, fixed locale string keyed by its own condition ID (e.g.
     `"Eliminate Scavs at the old gas station on Customs"`) — the client does not compose this text from the
     condition's structured fields, so leaving it alone after changing the JSON would show stale/wrong text.
     English-only: other languages will show the original, now slightly stale but still perfectly
     understandable text (still says the right creature and map, just also mentions a zone that no longer
     matters) — not attempted, to avoid shipping bad machine-translated strings for a case with only one real,
     verified source language.
  - Logs a warning (not a hard failure) for any entry whose condition ID isn't found, or whose `InZone`
    sub-condition is already gone — both would mean a game/SPT update changed the underlying data and this mod
    needs re-checking, not that the mod should crash the server.
  - Never writes to disk. Removing the mod (or a future SPT update replacing the DB files) means the very next
    server start serves unmodified quests again — no cleanup needed.
- **`config/config.json`** — `{ "killCountMultiplier": 1.5 }`. Missing/unparsable file logs a warning and falls
  back to this default rather than failing mod load, same defensive pattern as SkillPointsMod's own config
  loading (reused directly — `ModHelper.GetAbsolutePathToModFolder` + `FileUtil` + `JsonUtil`).
- **`config/zone-quests.json`** — the 31-entry table: `{ quest (human label, for logging only), conditionId,
  newText, mapTargets }`. `mapTargets` is a list of BSG's internal short map keys (e.g. `"bigmap"` = Customs,
  `"RezervBase"` = Reserve, `"TarkovStreets"` = Streets of Tarkov, `"factory4_day"`/`"factory4_night"` = Factory
  — both listed for the two Factory quests, so a kill counts regardless of which day/night variant matchmaking
  rolls) — added as a fresh `Location`-type sub-condition once `InZone` is removed, which is what actually keeps
  the objective scoped to one map (see "Known gaps" — the quest's own top-level `location` field does **not**
  do this). Empty list = deliberately no map restriction (`Preemptive Strike` only). **Hand-curated, not
  templated/auto-generated** — deliberately, given the lesson from a sibling
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
    - "Preemptive Strike" is the one entry with no map in its `newText` and an empty `mapTargets` — its quest's
      own `location` field is `"any"`, and the user confirmed it should stay genuinely map-unrestricted.
    - **`mapTargets` (see below) is what actually keeps each objective scoped to one map — the quest's own
      top-level `location` field does NOT do this, despite an earlier (wrong) theory that it did. See "Known
      gaps" / the 2026-08-27 entry for the full story: it took a real cross-map completion in-game to catch.

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

- **RESOLVED, 2026-08-27 — real cross-map completion bug, root cause was a wrong theory used since this mod's
  design phase.** User completed "Provide Viewership" (meant to be Customs-only) with a kill on Ground Zero.
  Investigated properly instead of guessing:
  - Confirmed via the live `quests.json` that Provide Viewership's own `location` field genuinely **is**
    Customs (`bigmap`) - not "any", so the original theory ("a `Kills`-only condition is already correctly
    scoped via the quest's own top-level `location` field, confirmed by 'The Huntsman Path - Outcasts' having
    the same no-InZone structure") should have held if it were true. It didn't - proving the theory itself was
    wrong all along, not just misapplied to this one quest. The "Outcasts" precedent was never actually a
    working proof; it just happened not to have been noticed as broken, because nobody had tested killing on
    the wrong map for it either.
  - Found the real mechanism: BSG has a genuine `Location`-type sub-condition (`{conditionType: "Location",
    target: ["Woods"]}` etc, confirmed via a real example elsewhere in the live quest database), completely
    separate from the quest's own `location` field, which is apparently **not enforced server-side for kill
    counting at all** - it's most likely just metadata (quest-list filtering/map icon), not a gameplay
    restriction. Also found several of this mod's own affected quests have `location: "any"` despite being
    obviously single-map quests (Capturing Outposts x3, Return the Favor, Illegal Logging x4, Provide
    Viewership) - meaning those were **always** at risk of cross-map completion the moment `InZone` was
    removed, this just hadn't been noticed yet.
  - **Fix**: `zone-quests.json` gained a `mapTargets` field per entry (the correct BSG internal map key(s),
    resolved from each quest's real `location` field where that's a genuine map ID, or inferred from the
    already-verified `newText` where `location` is `"any"`/`"marathon"`). `NoMoreKillZonesHook` now adds a
    fresh `Location`-type `QuestConditionCounterCondition` in place of the removed `InZone` one, for every entry
    except `Preemptive Strike` (deliberately left map-unrestricted, unchanged). This is the actual fix for the
    "needs to remove the specific zone within a map, but does need to be the same map" requirement from this
    mod's original design - it was never really satisfied until now.
  - Not yet re-confirmed in-game after this fix (built clean, deployed) - the next real test is exactly the
    scenario that surfaced the bug: complete an affected quest's kill count on a *different* map than intended
    and confirm it no longer counts.

- **The core mechanic is fully confirmed working, in a real raid, 2026-08-27**: a Scav kill well away from the
  old gas station zone correctly incremented progress on Rite of Passage's (already-merged) objective, and the
  combined count/merge held up correctly through the raid. This was the actual point of the mod — bots not
  reliably pathing into the old zone-restricted trigger no longer matters, kills anywhere on the map count. No
  open question left on the mechanic itself.
- **RESOLVED, 2026-08-26 — root cause found and fixed.** Display text was stale for every affected quest
  (confirmed on a **brand new profile**, not just already-accepted ones — killed the last surviving "pinned for
  accepted quests" theory outright, which the user correctly challenged as mechanistically implausible before
  it was ever actually verified). The real cause has nothing to do with accept-status, client caching, UI
  pooling, or raid-load timing — all four of those were investigated and eventually ruled out (see git history
  for the paper trail) before finding the actual bug, which was **server-side and much simpler**:
  - `LocaleTable.Global` is `Dictionary<string, LazyLoad<GlobalLocaleDictionary>>`
    (`SPTarkov.Server.Core\Models\Spt\Tables\LocaleTable.cs`) — its own doc comment warns "changes will not be
    saved," and reading `LazyLoad<T>.Value`'s getter
    (`SPTarkov.Server.Core\Utils\Json\LazyLoad.cs`) confirms exactly why: it calls `deserialize()` **fresh,
    from scratch, on every single access** — there is no memoization of the result at all.
    `LocaleService.GetLocaleDb()` just returns that brand-new, throwaway dictionary. Writing into it directly
    (the original approach used here) mutated an object that was discarded the instant `OnLoadAsync` returned —
    the very next locale fetch (client login, `/client/locale/en`, anything) got a completely fresh dictionary
    with none of this mod's edits, regardless of whether the quest had ever been seen before.
  - The actual sanctioned persistence mechanism is `LazyLoad<T>.AddTransformer(Func<T?, T?>)` — registers a
    function that re-runs against every fresh deserialize, so it survives no matter how many times the
    dictionary gets rebuilt. This isn't a workaround invented here — it's the exact pattern the server's own
    `Services\Modding\Custom\CustomQuestService.AddQuestLocales` uses for the same purpose (confirmed by
    reading that source directly), just never applied to this mod's writes until now.
  - **Fix applied**: `NoMoreKillZonesHook` now injects `LocaleTable` (not `LocaleService`), builds a
    `Dictionary<string, string>` of all condition-ID → replacement-text overrides (main pass + merge pass, same
    precedence as before — merges still win), then registers **one** transformer on
    `localeTable.Global["en"]` that applies every override on each fresh deserialize. Builds clean, deployed to
    the live server; the user's earlier test methodology (full client+server restart, and a brand-new profile)
    is exactly what will confirm this actually fixes it in the next in-game check.
  - **Cross-mod confirmation this was general, not specific to this mod's merge logic**: sibling mod
    QuestKeyInfo (`C:\Dev\QuestKeyInfo`) hit the identical symptom writing to a completely different locale key
    (`"<questId> description"`, not a condition ID) via the same broken `GetLocaleDb()`-direct-write pattern —
    and got the same fix (`LocaleTable.AddTransformer`) applied at the same time.
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
