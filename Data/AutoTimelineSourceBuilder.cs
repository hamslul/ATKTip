using System;
using System.Collections.Generic;
using System.Linq;

namespace ATKTip.Data;

internal static class AutoTimelineSourceBuilder
{
    internal const double DefaultGcdSlotSpacingSec = 2.5;
    internal const double GcdSlotEarlyWindowSec = 0.5;
    internal const double GcdSlotLateWindowSec = 0.5;
    internal const double OgcdSameActionWindowSec = 0.25;
    private const double AlignmentEpsilonSec = 0.01;

    public static List<TimelineEntry> BuildFromExactParses(
        IEnumerable<IReadOnlyList<TimelineEntry>> exactParses,
        int parseCount,
        double gcdSlotSpacingSec = DefaultGcdSlotSpacingSec)
    {
        gcdSlotSpacingSec = NormalizeGcdSlotSpacingSec(gcdSlotSpacingSec);
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
                    var slotIndex = ResolveGcdSlotIndex(entry.TimeOffsetSec, gcdSlotSpacingSec);
                    AccumulateParseUsage(
                        gcdUsage,
                        (slotIndex, entry.AbilityId),
                        entry.AbilityName,
                        entry.AbilityIcon);
                }
                else
                {
                    var slotKey = ResolveOgcdSlotKey(entry.TimeOffsetSec, gcdSlotSpacingSec);
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
                TimeOffsetSec = GetGcdSlotTimeSec(kvp.Key.slotIndex, gcdSlotSpacingSec),
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
                TimeOffsetSec = GetOgcdSlotTimeSec(kvp.Key.cycleIndex, kvp.Key.subslotIndex, gcdSlotSpacingSec),
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

    public static bool UsesFixedSlotAggregation(
        IEnumerable<TimelineEntry> entries,
        double gcdSlotSpacingSec = DefaultGcdSlotSpacingSec)
    {
        gcdSlotSpacingSec = NormalizeGcdSlotSpacingSec(gcdSlotSpacingSec);
        var any = false;
        foreach (var entry in entries)
        {
            any = true;
            if (!IsFixedSlotAligned(entry, gcdSlotSpacingSec))
                return false;
        }

        return any;
    }

    private static bool IsFixedSlotAligned(TimelineEntry entry, double gcdSlotSpacingSec)
    {
        if (entry.IsGcd)
        {
            var expectedTimeSec = GetGcdSlotTimeSec(ResolveGcdSlotIndex(entry.TimeOffsetSec, gcdSlotSpacingSec), gcdSlotSpacingSec);
            return Math.Abs(entry.TimeOffsetSec - expectedTimeSec) <= AlignmentEpsilonSec;
        }

        var (cycleIndex, subslotIndex) = ResolveOgcdSlotKey(entry.TimeOffsetSec, gcdSlotSpacingSec);
        var expectedOgcdTimeSec = GetOgcdSlotTimeSec(cycleIndex, subslotIndex, gcdSlotSpacingSec);
        return Math.Abs(entry.TimeOffsetSec - expectedOgcdTimeSec) <= AlignmentEpsilonSec;
    }

    private static int ResolveGcdSlotIndex(double timeSec, double gcdSlotSpacingSec)
    {
        return (int)Math.Round(timeSec / gcdSlotSpacingSec, MidpointRounding.AwayFromZero);
    }

    private static (int cycleIndex, int subslotIndex) ResolveOgcdSlotKey(double timeSec, double gcdSlotSpacingSec)
    {
        var cycleIndex = (int)Math.Floor(timeSec / gcdSlotSpacingSec);
        var cycleStartSec = cycleIndex * gcdSlotSpacingSec;
        var cycleOffsetSec = timeSec - cycleStartSec;
        var subslotIndex = cycleOffsetSec < gcdSlotSpacingSec / 2.0 ? 0 : 1;
        return (cycleIndex, subslotIndex);
    }

    private static double GetGcdSlotTimeSec(int slotIndex, double gcdSlotSpacingSec)
        => slotIndex * gcdSlotSpacingSec;

    private static double GetOgcdSlotTimeSec(int cycleIndex, int subslotIndex, double gcdSlotSpacingSec)
        => cycleIndex * gcdSlotSpacingSec +
           (subslotIndex <= 0 ? gcdSlotSpacingSec / 3.0 : (gcdSlotSpacingSec * 2.0) / 3.0);

    private static double NormalizeGcdSlotSpacingSec(double gcdSlotSpacingSec)
        => Math.Clamp(gcdSlotSpacingSec, 2.0, 2.5);

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
