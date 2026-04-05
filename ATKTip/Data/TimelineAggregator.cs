using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace ATKTip.Data;

public sealed class TimelineAggregator
{
    /// <summary>Width of each time bucket in seconds.</summary>
    private const double BucketWidthSec = 5.0;

    private readonly IPluginLog log;

    public TimelineAggregator(IPluginLog log)
    {
        this.log = log;
    }

    /// <summary>
    /// Given raw cast events from multiple parses of the same encounter+spec,
    /// produce an aggregated timeline showing the "average" rotation.
    /// </summary>
    /// <param name="parseCasts">
    /// Each inner list is one parse's cast events.
    /// Timestamps are absolute (ms from report start); fightStartMs is subtracted to normalize.
    /// </param>
    /// <param name="fightStartsMs">Fight start timestamp (ms) for each parse, used to normalize.</param>
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

        // Key: (bucket index, ability ID) → list of use-counts per parse
        var bucketData = new Dictionary<(int bucket, int abilityId), List<int>>();
        // Accumulate actual cast timestamps so we can output the true average instead of bucket * width
        var bucketTimestamps = new Dictionary<(int bucket, int abilityId), List<double>>();
        // Ability metadata cache
        var abilityMeta = new Dictionary<int, (string name, string icon)>();

        foreach (var (casts, fightStartMs, _) in parseData)
        {
            // Count casts per bucket per ability for this one parse
            var parseBuckets = new Dictionary<(int bucket, int abilityId), int>();

            foreach (var cast in casts)
            {
                var offsetSec = (cast.Timestamp - fightStartMs) / 1000.0;
                if (offsetSec < 0)
                    continue;

                var bucket = (int)(offsetSec / BucketWidthSec);
                var key = (bucket, cast.AbilityGameID);

                parseBuckets[key] = parseBuckets.GetValueOrDefault(key) + 1;

                // Accumulate actual timestamp for averaging
                if (!bucketTimestamps.TryGetValue(key, out var tsList))
                {
                    tsList = [];
                    bucketTimestamps[key] = tsList;
                }
                tsList.Add(offsetSec);

                if (!abilityMeta.ContainsKey(cast.AbilityGameID))
                    abilityMeta[cast.AbilityGameID] = (cast.AbilityName, cast.AbilityIcon);
            }

            // Merge into aggregate
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

        // Build timeline entries
        var entries = new List<TimelineEntry>();

        foreach (var ((bucket, abilityId), useCounts) in bucketData)
        {
            var (name, icon) = abilityMeta.GetValueOrDefault(abilityId, ("Unknown", string.Empty));
            var frequency = (double)useCounts.Count / parseCount;
            var avgUses = useCounts.Average();

            // Use the actual average cast time rather than the bucket edge, so abilities
            // within the same 5-second window have distinct timestamps and sequence correctly.
            var avgTimestamp = bucketTimestamps.TryGetValue((bucket, abilityId), out var ts)
                ? ts.Average()
                : bucket * BucketWidthSec;

            entries.Add(new TimelineEntry
            {
                TimeOffsetSec = avgTimestamp,
                AbilityId = abilityId,
                AbilityName = name,
                AbilityIcon = icon,
                Frequency = frequency,
                AverageUses = avgUses,
            });
        }

        // Sort by time, then by frequency descending
        entries.Sort((a, b) =>
        {
            var cmp = a.TimeOffsetSec.CompareTo(b.TimeOffsetSec);
            return cmp != 0 ? cmp : b.Frequency.CompareTo(a.Frequency);
        });

        log.Debug("Aggregated {0} entries for {1} / {2} from {3} parses.",
            entries.Count, encounterName, specName, parseCount);

        return new AggregatedTimeline
        {
            EncounterId = encounterId,
            EncounterName = encounterName,
            SpecName = specName,
            AverageDurationMs = avgDuration,
            ParseCount = parseCount,
            Entries = entries,
        };
    }

    /// <summary>
    /// Pair begincast/cast events from a single boss parse into BossTimelineEntry list.
    /// Each entry has fight-relative start and end times in seconds.
    /// </summary>
    public List<BossTimelineEntry> AggregateBossEvents(List<RawBossCastEvent> events)
    {
        if (events.Count == 0) return [];

        // Track pending begincast by (sourceId, abilityId) → timestamp
        var pending = new Dictionary<(int src, int ab), long>();
        var result  = new List<BossTimelineEntry>();

        foreach (var e in events.OrderBy(x => x.Timestamp))
        {
            var key = (e.SourceId, e.AbilityGameId);

            if (e.Type == "begincast")
            {
                pending[key] = e.Timestamp;
            }
            else if (e.Type == "cast")
            {
                // Instant casts won't have a matching begincast
                var startMs = pending.TryGetValue(key, out var s) ? s : e.Timestamp;
                pending.Remove(key);

                // Skip auto-attacks (very short casts with no name or abilityId <= 7)
                if (string.IsNullOrWhiteSpace(e.AbilityName)) continue;

                result.Add(new BossTimelineEntry
                {
                    CastStartSec = startMs / 1000.0,
                    CastEndSec   = e.Timestamp / 1000.0,
                    AbilityId    = e.AbilityGameId,
                    AbilityName  = e.AbilityName,
                });
            }
        }

        result.Sort((a, b) => a.CastStartSec.CompareTo(b.CastStartSec));

        log.Debug("Boss timeline: {0} cast entries.", result.Count);
        return result;
    }
}
