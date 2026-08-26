using System.Reflection;
using System.Text.Json.Serialization;
using IOPath = System.IO.Path;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils;

namespace NoMoreKillZones;

// Loaded from config/config.json. Missing/unparsable file falls back to this
// default rather than failing the whole mod load.
public record NoMoreKillZonesFileConfig
{
    [JsonPropertyName("killCountMultiplier")]
    public double KillCountMultiplier { get; init; } = 1.5;
}

// One row of config/zone-quests.json - a specific quest condition to derestrict,
// plus the replacement display text (no kill-count number embedded in it - the
// client renders the x/N progress badge separately from the condition's own
// numeric Value, confirmed against several vanilla map-only-restricted quests
// that already follow this exact "Eliminate {who} on {Map}" pattern with no
// count in the string).
public record ZoneQuestEntry
{
    [JsonPropertyName("quest")]
    public string? Quest { get; init; }

    [JsonPropertyName("conditionId")]
    public required string ConditionId { get; init; }

    [JsonPropertyName("newText")]
    public required string NewText { get; init; }
}

// One row of config/quest-merges.json - two zone-quests.json entries on the same
// map, for the same quest, that read better as one combined objective than two
// separate ones. Runs after the main derestrict pass: removeConditionId's
// (already-scaled) value is added onto keepConditionId's, removeConditionId is
// deleted from the quest entirely, and keepConditionId's text is overwritten
// with newText (superseding whatever the main pass set it to).
public record QuestMergeEntry
{
    [JsonPropertyName("quest")]
    public string? Quest { get; init; }

    [JsonPropertyName("keepConditionId")]
    public required string KeepConditionId { get; init; }

    [JsonPropertyName("removeConditionId")]
    public required string RemoveConditionId { get; init; }

    [JsonPropertyName("newText")]
    public required string NewText { get; init; }
}

/// <summary>
/// Derestricts SPT's zone-locked "kill in this specific sub-area" quest objectives (e.g. Rite of
/// Passage's "kill Scavs at the old gas station on Customs") to "kill anywhere on the same map" -
/// bot AI often doesn't reliably path into these small trigger zones in single-player, effectively
/// forcing the objective to be skipped. Compensates by scaling the required kill count up.
///
/// Runs once, in memory, at server startup (OnLoadOrder.PostLoad + 1 - straight after the raw
/// database load, before anything else has touched it, same as the official AfterDBLoadHook
/// example). Never writes to disk, so removing this mod reverts everything on the next restart.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public class NoMoreKillZonesHook(
    TemplateTable templateTable,
    LocaleTable localeTable,
    ModHelper modHelper,
    FileUtil fileUtil,
    JsonUtil jsonUtil,
    ISptLogger<NoMoreKillZonesHook> logger
) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modFolder = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = LoadConfig(modFolder);
        var entries = LoadZoneQuestEntries(modFolder);
        var merges = LoadQuestMerges(modFolder);

        // LocaleTable.Global[lang] is a LazyLoad<GlobalLocaleDictionary> that re-deserializes from
        // scratch on every single .Value access (confirmed by reading LazyLoad.cs - no memoization of
        // the deserialized result at all) - LocaleService.GetLocaleDb() just returns that fresh,
        // throwaway dictionary, so writing into it directly (the original approach here) was silently
        // discarded the instant this method returned; the very next locale fetch got an unmodified
        // dictionary. AddTransformer registers a function that re-runs against every fresh deserialize,
        // which is the actual persistence mechanism - same pattern the server's own
        // CustomQuestService.AddQuestLocales uses for exactly this purpose.
        var textOverrides = new Dictionary<string, string>();
        var appliedCount = 0;

        foreach (var entry in entries)
        {
            var condition = FindCondition(entry.ConditionId);
            if (condition is null)
            {
                logger.Warning(
                    $"NoMoreKillZones: condition {entry.ConditionId} ({entry.Quest}) not found in quest database - skipping. A game/SPT update may have changed it; the mod needs re-checking against the new data.");
                continue;
            }

            var removedZoneRestriction = (condition.Counter?.Conditions?.RemoveAll(c => c.ConditionType == "InZone") ?? 0) > 0;
            if (!removedZoneRestriction)
            {
                logger.Warning(
                    $"NoMoreKillZones: condition {entry.ConditionId} ({entry.Quest}) had no InZone sub-condition to remove - applying the kill-count/text change anyway, but double check this quest still makes sense.");
            }

            if (condition.Value.HasValue)
            {
                condition.Value = Math.Ceiling(condition.Value.Value * config.KillCountMultiplier);
            }

            textOverrides[entry.ConditionId] = entry.NewText;
            appliedCount++;
        }

        foreach (var merge in merges)
        {
            var keep = FindCondition(merge.KeepConditionId);
            var remove = FindCondition(merge.RemoveConditionId);
            if (keep is null || remove is null)
            {
                logger.Warning(
                    $"NoMoreKillZones: merge for {merge.Quest} skipped - keepConditionId or removeConditionId not found (a game/SPT update may have changed this quest).");
                continue;
            }

            keep.Value = (keep.Value ?? 0) + (remove.Value ?? 0);

            if (!RemoveCondition(merge.RemoveConditionId))
            {
                logger.Warning(
                    $"NoMoreKillZones: merge for {merge.Quest} - couldn't remove condition {merge.RemoveConditionId} from its quest's condition list (already gone?).");
            }

            // Overwrites whatever the main pass above set for this condition ID (keepConditionId is
            // always also a zone-quests.json entry today), matching the original precedence.
            textOverrides[merge.KeepConditionId] = merge.NewText;
        }

        if (localeTable.Global.TryGetValue("en", out var lazyLocale))
        {
            lazyLocale.AddTransformer(localeData =>
            {
                if (localeData is null)
                {
                    return null;
                }

                foreach (var (id, text) in textOverrides)
                {
                    localeData[id] = text;
                }

                return localeData;
            });
        }
        else
        {
            logger.Warning("NoMoreKillZones: no 'en' locale found in LocaleTable.Global - text changes will not apply.");
        }

        logger.Info(
            $"NoMoreKillZones: derestricted {appliedCount}/{entries.Count} zone-locked kill objectives (kill count x{config.KillCountMultiplier}), merged {merges.Count} same-map duplicate pair(s), registered {textOverrides.Count} locale text override(s).");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Quest condition IDs are unique across the whole database, but which of the five
    /// status-keyed lists (Started/AvailableForFinish/AvailableForStart/Success/Fail) a given
    /// condition lives under varies per quest - all zone-restricted Elimination conditions found
    /// live under AvailableForFinish, but this checks all five rather than assuming that stays true.
    /// </summary>
    private QuestCondition? FindCondition(string conditionId)
    {
        foreach (var quest in templateTable.Quests.Values)
        {
            List<QuestCondition>?[] lists =
            [
                quest.Conditions.Started,
                quest.Conditions.AvailableForFinish,
                quest.Conditions.AvailableForStart,
                quest.Conditions.Success,
                quest.Conditions.Fail
            ];

            foreach (var list in lists)
            {
                if (list is null)
                {
                    continue;
                }

                foreach (var condition in list)
                {
                    if (condition.Id == conditionId)
                    {
                        return condition;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Same search as FindCondition, but removes the match from whichever status list holds it.</summary>
    private bool RemoveCondition(string conditionId)
    {
        foreach (var quest in templateTable.Quests.Values)
        {
            List<QuestCondition>?[] lists =
            [
                quest.Conditions.Started,
                quest.Conditions.AvailableForFinish,
                quest.Conditions.AvailableForStart,
                quest.Conditions.Success,
                quest.Conditions.Fail
            ];

            foreach (var list in lists)
            {
                if (list is null)
                {
                    continue;
                }

                var removed = list.RemoveAll(c => c.Id == conditionId) > 0;
                if (removed)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private NoMoreKillZonesFileConfig LoadConfig(string modFolder)
    {
        var configPath = IOPath.Combine(modFolder, "config", "config.json");

        if (!fileUtil.FileExists(configPath))
        {
            logger.Warning($"NoMoreKillZones: no config.json found at {configPath}, using defaults.");
            return new NoMoreKillZonesFileConfig();
        }

        var config = jsonUtil.Deserialize<NoMoreKillZonesFileConfig>(fileUtil.ReadFile(configPath));
        if (config is null)
        {
            logger.Warning("NoMoreKillZones: config.json failed to parse, using defaults.");
            return new NoMoreKillZonesFileConfig();
        }

        return config;
    }

    private List<ZoneQuestEntry> LoadZoneQuestEntries(string modFolder)
    {
        var entriesPath = IOPath.Combine(modFolder, "config", "zone-quests.json");

        if (!fileUtil.FileExists(entriesPath))
        {
            logger.Warning($"NoMoreKillZones: no zone-quests.json found at {entriesPath} - nothing to do.");
            return [];
        }

        var entries = jsonUtil.Deserialize<List<ZoneQuestEntry>>(fileUtil.ReadFile(entriesPath));
        if (entries is null)
        {
            logger.Warning("NoMoreKillZones: zone-quests.json failed to parse - nothing to do.");
            return [];
        }

        return entries;
    }

    private List<QuestMergeEntry> LoadQuestMerges(string modFolder)
    {
        var mergesPath = IOPath.Combine(modFolder, "config", "quest-merges.json");

        if (!fileUtil.FileExists(mergesPath))
        {
            // Not a warning - most installs will have no merges at all, this file is optional.
            return [];
        }

        var merges = jsonUtil.Deserialize<List<QuestMergeEntry>>(fileUtil.ReadFile(mergesPath));
        if (merges is null)
        {
            logger.Warning("NoMoreKillZones: quest-merges.json failed to parse - skipping merges.");
            return [];
        }

        return merges;
    }
}
