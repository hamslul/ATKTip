using System;
using System.Collections.Generic;
using System.Linq;

namespace ATKTip.Data;

internal static class AutoTimelineSourceBuilder
{
    internal const double GcdSlotSpacingSec = 2.5;
    internal const double GcdSlotEarlyWindowSec = 0.5;
    internal const double GcdSlotLateWindowSec = 0.5;
    internal const double OgcdSlotBoundarySec = GcdSlotSpacingSec / 2.0;
    internal const double OgcdFirstSlotOffsetSec = GcdSlotSpacingSec / 3.0;
    internal const double OgcdSecondSlotOffsetSec = (GcdSlotSpacingSec * 2.0) / 3.0;
    internal const double OgcdSameActionWindowSec = 0.25;
    private const double AlignmentEpsilonSec = 0.01;

    public static List<TimelineEntry> BuildFromExactParses(
        IEnumerable<IReadOnlyList<TimelineEntry>> exactParses,
        int parseCount)
    {
        var materializedParses = exactParses
            .Select(parse => parse
                .Where(entry => entry.AbilityId > 7)
                .OrderBy(entry => entry.TimeOffsetSec)
                .ThenByDescending(entry => entry.Frequency)
                .ToList())
            .ToList();
        if (materializedParses.Count == 0 || parseCount <= 0)
            return [];

        var gcdCounts = new Dictionary<(int slotIndex, int abilityId), AggregateState>();
        var ogcdCounts = new Dictionary<(int cycleIndex, int subslotIndex, int abilityId), AggregateState>();

        foreach (var parse in materializedParses)
        {
            var gcdUsage = new Dictionary<(int slotIndex, int abilityId), ParseUsageState>();
            var ogcdUsage = new Dictionary<(int cycleIndex, int subslotIndex, int abilityId), ParseUsageState>();

            foreach (var entry in parse)
            {
                if (entry.IsGcd)
                {
                    var slotIndex = ResolveGcdSlotIndex(entry.TimeOffsetSec);
                    AccumulateParseUsage(
                        gcdUsage,
                        (slotIndex, entry.AbilityId),
                        entry.AbilityName,
                        entry.AbilityIcon);
                }
                else
                {
                    var slotKey = ResolveOgcdSlotKey(entry.TimeOffsetSec);
                    AccumulateParseUsage(
                        ogcdUsage,
                        (slotKey.cycleIndex, slotKey.subslotIndex, entry.AbilityId),
                        entry.AbilityName,
                        entry.AbilityIcon);
                }
            }

            MergeParseUsage(gcdCounts, gcdUsage);
            MergeParseUsage(ogcdCounts, ogcdUsage);
        }

        var gcdEntries = gcdCounts
            .Select(kvp => new TimelineEntry
            {
                TimeOffsetSec = GetGcdSlotTimeSec(kvp.Key.slotIndex),
                AbilityId = kvp.Key.abilityId,
                AbilityName = kvp.Value.AbilityName,
                AbilityIcon = kvp.Value.AbilityIcon,
                Frequency = (double)kvp.Value.ParseUsers / parseCount,
                AverageUses = (double)kvp.Value.TotalUses / kvp.Value.ParseUsers,
                IsGcd = true,
            })
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();

        var ogcdEntries = ogcdCounts
            .Select(kvp => new TimelineEntry
            {
                TimeOffsetSec = GetOgcdSlotTimeSec(kvp.Key.cycleIndex, kvp.Key.subslotIndex),
                AbilityId = kvp.Key.abilityId,
                AbilityName = kvp.Value.AbilityName,
                AbilityIcon = kvp.Value.AbilityIcon,
                Frequency = (double)kvp.Value.ParseUsers / parseCount,
                AverageUses = (double)kvp.Value.TotalUses / kvp.Value.ParseUsers,
                IsGcd = false,
            })
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();

        return gcdEntries
            .Concat(ogcdEntries)
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    public static bool UsesFixedSlotAggregation(IEnumerable<TimelineEntry> entries)
    {
        var any = false;
        foreach (var entry in entries)
        {
            any = true;
            if (!IsFixedSlotAligned(entry))
                return false;
        }

        return any;
    }

    private static bool IsFixedSlotAligned(TimelineEntry entry)
    {
        if (entry.IsGcd)
        {
            var expectedTimeSec = GetGcdSlotTimeSec(ResolveGcdSlotIndex(entry.TimeOffsetSec));
            return Math.Abs(entry.TimeOffsetSec - expectedTimeSec) <= AlignmentEpsilonSec;
        }

        var (cycleIndex, subslotIndex) = ResolveOgcdSlotKey(entry.TimeOffsetSec);
        var expectedOgcdTimeSec = GetOgcdSlotTimeSec(cycleIndex, subslotIndex);
        return Math.Abs(entry.TimeOffsetSec - expectedOgcdTimeSec) <= AlignmentEpsilonSec;
    }

    private static int ResolveGcdSlotIndex(double timeSec)
    {
        var clampedTimeSec = Math.Max(0.0, timeSec);
        return Math.Max(0, (int)Math.Round(clampedTimeSec / GcdSlotSpacingSec, MidpointRounding.AwayFromZero));
    }

    private static (int cycleIndex, int subslotIndex) ResolveOgcdSlotKey(double timeSec)
    {
        var clampedTimeSec = Math.Max(0.0, timeSec);
        var cycleIndex = (int)Math.Floor(clampedTimeSec / GcdSlotSpacingSec);
        var cycleStartSec = cycleIndex * GcdSlotSpacingSec;
        var cycleOffsetSec = clampedTimeSec - cycleStartSec;
        var subslotIndex = cycleOffsetSec < OgcdSlotBoundarySec ? 0 : 1;
        return (cycleIndex, subslotIndex);
    }

    private static double GetGcdSlotTimeSec(int slotIndex)
        => slotIndex * GcdSlotSpacingSec;

    private static double GetOgcdSlotTimeSec(int cycleIndex, int subslotIndex)
        => cycleIndex * GcdSlotSpacingSec +
           (subslotIndex <= 0 ? OgcdFirstSlotOffsetSec : OgcdSecondSlotOffsetSec);

    private static void AccumulateParseUsage<TKey>(
        IDictionary<TKey, ParseUsageState> target,
        TKey key,
        string abilityName,
        string abilityIcon)
        where TKey : notnull
    {
        if (target.TryGetValue(key, out var existing))
        {
            target[key] = existing with
            {
                Uses = existing.Uses + 1,
                AbilityIcon = string.IsNullOrWhiteSpace(existing.AbilityIcon) ? abilityIcon : existing.AbilityIcon,
            };
            return;
        }

        target[key] = new ParseUsageState(1, abilityName, abilityIcon);
    }

    private static void MergeParseUsage<TKey>(
        IDictionary<TKey, AggregateState> target,
        IReadOnlyDictionary<TKey, ParseUsageState> parseUsage)
        where TKey : notnull
    {
        foreach (var (key, usage) in parseUsage)
        {
            if (target.TryGetValue(key, out var existing))
            {
                target[key] = existing with
                {
                    ParseUsers = existing.ParseUsers + 1,
                    TotalUses = existing.TotalUses + usage.Uses,
                    AbilityIcon = string.IsNullOrWhiteSpace(existing.AbilityIcon) ? usage.AbilityIcon : existing.AbilityIcon,
                };
                continue;
            }

            target[key] = new AggregateState(1, usage.Uses, usage.AbilityName, usage.AbilityIcon);
        }
    }

    private readonly record struct AggregateState(
        int ParseUsers,
        int TotalUses,
        string AbilityName,
        string AbilityIcon);

    private readonly record struct ParseUsageState(
        int Uses,
        string AbilityName,
        string AbilityIcon);
}
