using System;
using System.Collections.Generic;
using System.Linq;

namespace ATKTip.Data;

public static class TimelineEntryCondenser
{
    public static List<TimelineEntry> Condense(
        IEnumerable<TimelineEntry> entries,
        int parseCount,
        RecastDatabase recastDb)
    {
        var sourceEntries = entries.ToList();
        if (sourceEntries.Count <= 1)
            return sourceEntries
                .Select(CloneEntry)
                .ToList();

        var condensedEntries = new List<TimelineEntry>(sourceEntries.Count);
        foreach (var group in sourceEntries
                     .GroupBy(GetGroupingKey, StringComparer.OrdinalIgnoreCase))
        {
            var sortedEntries = group
                .OrderBy(entry => entry.TimeOffsetSec)
                .ThenByDescending(entry => entry.Frequency)
                .ToList();
            if (sortedEntries.Count == 0)
                continue;

            var cluster = new List<TimelineEntry> { sortedEntries[0] };
            for (var index = 1; index < sortedEntries.Count; index++)
            {
                var previousEntry = sortedEntries[index - 1];
                var currentEntry = sortedEntries[index];
                var clusterGapSec = Math.Max(
                    GetCondenseGapSec(previousEntry, recastDb),
                    GetCondenseGapSec(currentEntry, recastDb));

                if (currentEntry.TimeOffsetSec - previousEntry.TimeOffsetSec <= clusterGapSec + 0.001)
                {
                    cluster.Add(currentEntry);
                    continue;
                }

                condensedEntries.Add(MergeCluster(cluster, parseCount));
                cluster.Clear();
                cluster.Add(currentEntry);
            }

            if (cluster.Count > 0)
                condensedEntries.Add(MergeCluster(cluster, parseCount));
        }

        return condensedEntries
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private static string GetGroupingKey(TimelineEntry entry)
        => entry.AbilityId > 0
            ? $"{entry.IsGcd}:{entry.AbilityId}"
            : $"{entry.IsGcd}:{entry.AbilityName.Trim()}";

    private static double GetCondenseGapSec(TimelineEntry entry, RecastDatabase recastDb)
    {
        var info = recastDb.Lookup(entry.AbilityId, entry.AbilityName);
        if (info?.IsGcdAction == true)
            return Math.Max(AutoTimelineSourceBuilder.GcdSlotEarlyWindowSec, AutoTimelineSourceBuilder.GcdSlotLateWindowSec);

        return AutoTimelineSourceBuilder.OgcdSameActionWindowSec;
    }

    private static TimelineEntry MergeCluster(
        IReadOnlyList<TimelineEntry> cluster,
        int parseCount)
    {
        if (cluster.Count == 1)
            return CloneEntry(cluster[0]);

        var representative = cluster
            .OrderByDescending(entry => entry.Frequency)
            .ThenBy(entry => entry.TimeOffsetSec)
            .First();

        var parseScale = Math.Max(1, parseCount);
        var estimatedParseUsers = cluster.Sum(entry => Math.Max(0.0, entry.Frequency) * parseScale);
        if (estimatedParseUsers <= 0.0)
            estimatedParseUsers = cluster.Count;

        var mergedParseUsers = Math.Max(1.0, Math.Min(parseScale, estimatedParseUsers));
        var totalUses = cluster.Sum(entry => Math.Max(0.0, entry.AverageUses) * Math.Max(0.0, entry.Frequency) * parseScale);
        if (totalUses <= 0.0)
            totalUses = estimatedParseUsers;

        var weightedTimeSec = cluster.Sum(entry => entry.TimeOffsetSec * Math.Max(0.0, entry.Frequency) * parseScale);
        var mergedTimeSec = weightedTimeSec > 0.0
            ? weightedTimeSec / estimatedParseUsers
            : cluster.Average(entry => entry.TimeOffsetSec);

        return new TimelineEntry
        {
            TimeOffsetSec = mergedTimeSec,
            AbilityId = representative.AbilityId,
            AbilityName = representative.AbilityName,
            AbilityIcon = cluster.Select(entry => entry.AbilityIcon).FirstOrDefault(icon => !string.IsNullOrWhiteSpace(icon)) ?? string.Empty,
            Frequency = Math.Min(1.0, mergedParseUsers / parseScale),
            AverageUses = totalUses / mergedParseUsers,
            IsGcd = representative.IsGcd,
        };
    }

    private static TimelineEntry CloneEntry(TimelineEntry entry)
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
