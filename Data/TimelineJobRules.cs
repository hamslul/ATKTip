using System;
using System.Collections.Generic;
using System.Linq;

namespace ATKTip.Data;

public static class TimelineJobRules
{
    private const string MacrocosmosAbilityName = "Macrocosmos";
    private const double BaseGcdSlotSec = 2.5;
    private const double SlotStealToleranceSec = 0.01;

    public static List<TimelineEntry> ApplyPostSelectionRules(
        string specName,
        IEnumerable<TimelineEntry> entries,
        bool promoteMacrocosmosToVisualGcd = false,
        List<string>? debugNotes = null)
    {
        var clonedEntries = entries
            .Select(CloneTimelineEntry)
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();

        if (!string.Equals(specName, "Astrologian", StringComparison.OrdinalIgnoreCase))
            return clonedEntries;

        var macroEntries = clonedEntries
            .Where(IsMacrocosmos)
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
        foreach (var macroEntry in macroEntries)
        {
            if (IsOnGcdSlot(macroEntry.TimeOffsetSec))
            {
                if (promoteMacrocosmosToVisualGcd)
                    macroEntry.IsGcd = true;
                continue;
            }

            var stolenGcdEntry = clonedEntries
                .Where(entry =>
                    !ReferenceEquals(entry, macroEntry) &&
                    entry.IsGcd &&
                    entry.TimeOffsetSec >= macroEntry.TimeOffsetSec - SlotStealToleranceSec)
                .OrderBy(entry => entry.TimeOffsetSec)
                .ThenByDescending(entry => entry.Frequency)
                .FirstOrDefault();
            if (stolenGcdEntry == null)
                continue;

            clonedEntries.Remove(stolenGcdEntry);
            var previousMacroTimeSec = macroEntry.TimeOffsetSec;
            macroEntry.TimeOffsetSec = stolenGcdEntry.TimeOffsetSec;
            if (promoteMacrocosmosToVisualGcd)
                macroEntry.IsGcd = true;

            debugNotes?.Add(
                $"  keep | {MacrocosmosAbilityName} shifted from {FormatTime(previousMacroTimeSec)} to {FormatTime(macroEntry.TimeOffsetSec)} by stealing GCD slot from {stolenGcdEntry.AbilityName} @ {FormatTime(stolenGcdEntry.TimeOffsetSec)}");
        }

        return clonedEntries
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private static bool IsMacrocosmos(TimelineEntry entry)
        => string.Equals(entry.AbilityName, MacrocosmosAbilityName, StringComparison.OrdinalIgnoreCase);

    private static bool IsOnGcdSlot(double timeOffsetSec)
    {
        var snappedSlotIndex = Math.Round(timeOffsetSec / BaseGcdSlotSec);
        var snappedSlotTimeSec = snappedSlotIndex * BaseGcdSlotSec;
        return Math.Abs(timeOffsetSec - snappedSlotTimeSec) <= SlotStealToleranceSec;
    }

    private static TimelineEntry CloneTimelineEntry(TimelineEntry entry)
    {
        return new TimelineEntry
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

    private static string FormatTime(double seconds)
    {
        var m = (int)(seconds / 60);
        var s = seconds % 60;
        return $"{m}:{s:00.0}";
    }
}
