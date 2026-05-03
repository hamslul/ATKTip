using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace ATKTip.Data;

public sealed class TimelineAggregator
{
    private const double DowntimeGapSec = 5.0;
    private const double BucketWidthSec = 5.0;

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

        var abilityMeta = new Dictionary<int, (string name, string icon)>();

        var allGcdSequences = new List<List<GcdCastEntry>>(parseCount);
        var allOgcdCasts = new List<List<(int id, string name, string icon, double timeSec)>>(parseCount);

        foreach (var (casts, fightStartMs, _) in parseData)
        {
            var gcdSeq = new List<GcdCastEntry>();
            var ogcdList = new List<(int id, string name, string icon, double timeSec)>();

            double lastGcdTimeSec = double.MinValue;
            int phase = 0;
            int posInPhase = 0;

            foreach (var cast in casts.OrderBy(c => c.Timestamp))
            {
                if (cast.AbilityGameID <= 7)
                    continue;

                var timeSec = (cast.Timestamp - fightStartMs) / 1000.0;
                if (timeSec < 0)
                    continue;

                var (abilityId, abilityName, abilityIcon, isGcdAction) =
                    NormalizeActionIdentity(cast.AbilityGameID, cast.AbilityName, cast.AbilityIcon);

                if (abilityMeta.TryGetValue(abilityId, out var existingMeta))
                {
                    if (string.IsNullOrWhiteSpace(existingMeta.icon) && !string.IsNullOrWhiteSpace(abilityIcon))
                        abilityMeta[abilityId] = (abilityName, abilityIcon);
                }
                else
                {
                    abilityMeta[abilityId] = (abilityName, abilityIcon);
                }

                if (isGcdAction)
                {
                    if (lastGcdTimeSec != double.MinValue && timeSec - lastGcdTimeSec >= DowntimeGapSec)
                    {
                        phase++;
                        posInPhase = 0;
                    }

                    gcdSeq.Add(new GcdCastEntry(
                        phase,
                        posInPhase,
                        abilityId,
                        abilityName,
                        abilityIcon,
                        timeSec));

                    posInPhase++;
                    lastGcdTimeSec = timeSec;
                }
                else
                {
                    ogcdList.Add((abilityId, abilityName, abilityIcon, timeSec));
                }
            }

            allGcdSequences.Add(gcdSeq);
            allOgcdCasts.Add(ogcdList);
        }

        var posAbilityData =
            new Dictionary<(int phase, int pos),
                Dictionary<int, (int count, double totalTime, string name, string icon)>>();

        foreach (var seq in allGcdSequences)
        {
            foreach (var e in seq)
            {
                var key = (e.Phase, e.Pos);
                if (!posAbilityData.TryGetValue(key, out var abilities))
                {
                    abilities = [];
                    posAbilityData[key] = abilities;
                }

                if (abilities.TryGetValue(e.AbilityId, out var existing))
                {
                    abilities[e.AbilityId] = (
                        existing.count + 1,
                        existing.totalTime + e.TimeSec,
                        existing.name,
                        existing.icon);
                }
                else
                {
                    abilities[e.AbilityId] = (1, e.TimeSec, e.Name, e.Icon);
                }
            }
        }

        var gcdCandidates = new List<GcdAggEntry>();

        foreach (var ((phase, pos), abilities) in posAbilityData)
        {
            foreach (var (abilityId, data) in abilities)
            {
                gcdCandidates.Add(new GcdAggEntry(
                    phase,
                    pos,
                    abilityId,
                    data.name,
                    data.icon,
                    data.count,
                    data.totalTime / data.count));
            }
        }

        gcdCandidates.Sort((a, b) =>
        {
            var cmp = a.Phase.CompareTo(b.Phase);
            if (cmp != 0) return cmp;
            cmp = a.Pos.CompareTo(b.Pos);
            if (cmp != 0) return cmp;
            cmp = a.MeanTime.CompareTo(b.MeanTime);
            if (cmp != 0) return cmp;
            return b.Count.CompareTo(a.Count);
        });

        var bucketData = new Dictionary<(int bucket, int id), List<int>>();
        var bucketTimestamps = new Dictionary<(int bucket, int id), List<double>>();

        foreach (var ogcdList in allOgcdCasts)
        {
            var parseBuckets = new Dictionary<(int bucket, int id), int>();

            foreach (var (id, _, _, timeSec) in ogcdList)
            {
                var bucket = (int)(timeSec / BucketWidthSec);
                var key = (bucket, id);

                parseBuckets[key] = parseBuckets.GetValueOrDefault(key) + 1;

                if (!bucketTimestamps.TryGetValue(key, out var tsList))
                {
                    tsList = [];
                    bucketTimestamps[key] = tsList;
                }

                tsList.Add(timeSec);
            }

            foreach (var (key, count) in parseBuckets)
            {
                if (!bucketData.TryGetValue(key, out var list))
                {
                    list = [];
                    bucketData[key] = list;
                }

                list.Add(count);
            }
        }

        var ogcdEntries = new List<OgcdEntry>(bucketData.Count);

        foreach (var ((bucket, id), useCounts) in bucketData)
        {
            var (name, icon) = abilityMeta.GetValueOrDefault(id, ("Unknown", string.Empty));
            var avgTime = bucketTimestamps.TryGetValue((bucket, id), out var ts)
                ? ts.Average()
                : bucket * BucketWidthSec;

            ogcdEntries.Add(new OgcdEntry(
                id,
                name,
                icon,
                avgTime,
                (double)useCounts.Count / parseCount,
                useCounts.Average()));
        }

        var gcdEntries = gcdCandidates
            .Select(cand => new TimelineEntry
            {
                TimeOffsetSec = cand.MeanTime,
                AbilityId = cand.AbilityId,
                AbilityName = cand.Name,
                AbilityIcon = cand.Icon,
                Frequency = (double)cand.Count / parseCount,
                AverageUses = 1.0,
                IsGcd = true,
            })
            .ToList();

        var finalOgcdEntries = ogcdEntries
            .Select(e => new TimelineEntry
            {
                TimeOffsetSec = e.TimeSec,
                AbilityId = e.AbilityId,
                AbilityName = e.Name,
                AbilityIcon = e.Icon,
                Frequency = e.Frequency,
                AverageUses = e.AvgUses,
                IsGcd = false,
            })
            .ToList();

        var rawEntries = new List<TimelineEntry>(gcdEntries.Count + finalOgcdEntries.Count);
        rawEntries.AddRange(gcdEntries);
        rawEntries.AddRange(finalOgcdEntries);
        rawEntries.Sort((a, b) =>
        {
            var cmp = a.TimeOffsetSec.CompareTo(b.TimeOffsetSec);
            return cmp != 0 ? cmp : b.Frequency.CompareTo(a.Frequency);
        });

        var condensedEntries = TimelineEntryCondenser.Condense(rawEntries, parseCount, recastDb);
        var sourceEntries = rawEntries
            .Select(CloneTimelineEntry)
            .ToList();

        log.Debug("[Aggregator] {0}/{1}: {2} aggregated GCD + {3} oGCD entries condensed to {4} total entries from {5} parses.",
            encounterName, specName, gcdEntries.Count, finalOgcdEntries.Count, condensedEntries.Count, parseCount);

        return new AggregatedTimeline
        {
            EncounterId = encounterId,
            EncounterName = encounterName,
            SpecName = specName,
            AverageDurationMs = avgDuration,
            ParseCount = parseCount,
            Entries = condensedEntries,
            AutoTimelineSourceEntries = sourceEntries,
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

    private record GcdCastEntry(
        int Phase,
        int Pos,
        int AbilityId,
        string Name,
        string Icon,
        double TimeSec);

    private record GcdAggEntry(
        int Phase,
        int Pos,
        int AbilityId,
        string Name,
        string Icon,
        int Count,
        double MeanTime);

    private record OgcdEntry(
        int AbilityId,
        string Name,
        string Icon,
        double TimeSec,
        double Frequency,
        double AvgUses);

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
