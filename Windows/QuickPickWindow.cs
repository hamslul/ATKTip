using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ATKTip.Data;

namespace ATKTip.Windows;

/// <summary>
/// Compact one-click overlay loader for manually loading a custom timeline when auto-detection fails.
/// Triggered by <c>/ATKTip preview</c>. Lists custom timelines sorted by relevance
/// (player's current job first), one click loads the live encounter overlay and closes this window.
/// </summary>
public sealed class QuickPickWindow : Window
{
    private readonly Plugin plugin;

    public QuickPickWindow(Plugin plugin)
        : base("Quick Timeline Load##ATKTip",
               ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.NoResize |
               ImGuiWindowFlags.AlwaysAutoResize |
               ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(280, 40),
            MaximumSize = new Vector2(500, 600),
        };
    }

    public override void Draw()
    {
        var customs = plugin.Configuration.CustomTimelines;
        if (customs.Count == 0)
        {
            ImGui.TextDisabled("No custom timelines saved.");
            return;
        }

        // Current job for relevance sort (null = not logged in)
        var currentSpec = plugin.EncounterTracker.GetCurrentSpecNamePublic();

        // Sort: timelines matching current job first, then alphabetical by encounter
        var sorted = customs
            .OrderBy(entry => !string.Equals(entry.Value.SpecName, currentSpec, StringComparison.OrdinalIgnoreCase))
            .ThenBy(entry => entry.Value.EncounterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Value.SpecName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool shownSeparator = false;

        ImGui.TextDisabled("Click a timeline to load it:");
        ImGui.Spacing();

        foreach (var (key, timeline) in sorted)
        {
            bool isCurrentJob = string.Equals(timeline.SpecName, currentSpec, StringComparison.OrdinalIgnoreCase);

            // Separator between current-job section and others
            if (!isCurrentJob && !shownSeparator)
            {
                // Only draw separator if there were any current-job entries above
                if (sorted.Any(entry => string.Equals(entry.Value.SpecName, currentSpec, StringComparison.OrdinalIgnoreCase)))
                {
                    ImGui.Separator();
                    ImGui.TextDisabled("Other jobs:");
                }
                shownSeparator = true;
            }

            var label = $"{timeline.EncounterName}  [{timeline.SpecName}]";
            var selectableId = $"{label}##sel_{timeline.EncounterId}_{timeline.SpecName}";

            if (!isCurrentJob)
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.55f, 0.55f, 1f));

            var consumedSelection = false;
            if (ImGui.Selectable(selectableId))
            {
                if (!isCurrentJob)
                    ImGui.PopStyleColor();
                consumedSelection = true;

                if (plugin.EncounterTracker.TryLoadEncounterByTimelineKey(key))
                {
                    IsOpen = false;
                    return;
                }
            }

            if (!isCurrentJob && !consumedSelection)
                ImGui.PopStyleColor();
        }
    }
}
