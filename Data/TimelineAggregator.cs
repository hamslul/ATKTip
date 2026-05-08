using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace ATKTip.Data;

public sealed class TimelineAggregator
{
    private readonly IPluginLog log;
    private readonly RecastDatabase recastDb;

    public TimelineAggregator(IPluginLog log, RecastDatabase recastDb)
    {
        this.log = log;
        this.recastDb = recastDb;
    }

    public AggregatedTimeline Aggregate(
        int encounterId,
        string encounterName,
        string specName,
        List<(List<CastEvent> casts, long fightStartMs, long fightEndMs)> parseData)
    {
        if (parseData.Count == 0)
        {
            return new AggregatedTimeline
            {
                EncounterId = encounterId,
                EncounterName = encounterName,
                SpecName = specName,
            };
        }

        var parseCount = parseData.Count;
        var avgDuration = parseData.Average(p => p.fightEndMs - p.fightStartMs);
        var exactParses = new List<IReadOnlyList<TimelineEntry>>(parseCount);

        foreach (var (casts, fightStartMs, _) in parseData)
        {
            var parseEntries = new List<TimelineEntry>();

            foreach (var cast in casts.OrderBy(c => c.Timestamp))
            {
                if (cast.AbilityGameID <= 7)
                    continue;

                var timeSec = (cast.Timestamp - fightStartMs) / 1000.0;
                if (timeSec < 0)
                    continue;

                var (abilityId, abilityName, abilityIcon, isGcdAction) =
                    NormalizeActionIdentity(cast.AbilityGameID, cast.AbilityName, cast.AbilityIcon);

                parseEntries.Add(new TimelineEntry
                {
                    TimeOffsetSec = timeSec,
                    AbilityId = abilityId,
                    AbilityName = abilityName,
                    AbilityIcon = abilityIcon,
                    Frequency = 1.0,
                    AverageUses = 1.0,
                    IsGcd = isGcdAction,
                });
            }

            exactParses.Add(parseEntries);
        }

        var sourceEntries = AutoTimelineSourceBuilder.BuildFromExactParses(exactParses, parseCount);
        var condensedEntries = TimelineEntryCondenser.Condense(sourceEntries, parseCount, recastDb);
        var clonedSourceEntries = sourceEntries
            .Select(CloneTimelineEntry)
            .ToList();

        log.Debug("[Aggregator] {0}/{1}: {2} aggregated GCD + {3} oGCD entries condensed to {4} total entries from {5} parses.",
            encounterName,
            specName,
            sourceEntries.Count(static entry => entry.IsGcd),
            sourceEntries.Count(static entry => !entry.IsGcd),
            condensedEntries.Count,
            parseCount);

        return new AggregatedTimeline
        {
            EncounterId = encounterId,
            EncounterName = encounterName,
            SpecName = specName,
            AverageDurationMs = avgDuration,
            ParseCount = parseCount,
            Entries = condensedEntries,
            AutoTimelineSourceEntries = clonedSourceEntries,
        };
    }

    private (int abilityId, string abilityName, string abilityIcon, bool isGcdAction)
        NormalizeActionIdentity(int abilityId, string abilityName, string abilityIcon)
    {
        var info = recastDb.Lookup(abilityId, abilityName);
        return (
            info != null ? (int)info.AbilityId : abilityId,
            !string.IsNullOrWhiteSpace(info?.Name) ? info.Name : abilityName,
            abilityIcon,
            info?.IsGcdAction ?? false);
    }

    public List<BossTimelineEntry> AggregateBossEvents(List<RawBossCastEvent> events)
    {
        if (events.Count == 0)
            return [];

        var pending = new Dictionary<(int src, int ab), long>();
        var result = new List<BossTimelineEntry>();

        foreach (var e in events.OrderBy(x => x.Timestamp))
        {
            var key = (e.SourceId, e.AbilityGameId);

            if (e.Type == "begincast")
            {
                pending[key] = e.Timestamp;
            }
            else if (e.Type == "cast")
            {
                var startMs = pending.TryGetValue(key, out var start) ? start : e.Timestamp;
                pending.Remove(key);

                if (string.IsNullOrWhiteSpace(e.AbilityName))
                    continue;

                result.Add(new BossTimelineEntry
                {
                    CastStartSec = startMs / 1000.0,
                    CastEndSec = e.Timestamp / 1000.0,
                    AbilityId = e.AbilityGameId,
                    AbilityName = e.AbilityName,
                });
            }
        }

        result.Sort((a, b) => a.CastStartSec.CompareTo(b.CastStartSec));

        const double deduplicateWindowSec = 1.5;
        var deduped = new List<BossTimelineEntry>(result.Count);
        var lastSeen = new Dictionary<int, double>();

        foreach (var entry in result)
        {
            if (lastSeen.TryGetValue(entry.AbilityId, out var prevTime) &&
                entry.CastStartSec - prevTime < deduplicateWindowSec)
            {
                continue;
            }

            deduped.Add(entry);
            lastSeen[entry.AbilityId] = entry.CastStartSec;
        }

        log.Debug("Boss timeline: {0} cast entries ({1} after multi-hit dedup).",
            result.Count, deduped.Count);
        return deduped;
    }

    private static TimelineEntry CloneTimelineEntry(TimelineEntry entry)
        => new()
        {
            TimeOffsetSec = entry.TimeOffsetSec,
            AbilityId = entry.AbilityId,
            AbilityName = entry.AbilityName,
            AbilityIcon = entry.AbilityIcon,
            Frequency = entry.Frequency,
            AverageUses = entry.AverageUses,
            IsGcd = entry.IsGcd,
        };
}
