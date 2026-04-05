using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ATKTip.Data;
using Lumina.Excel.Sheets;

namespace ATKTip;

/// <summary>
/// Watches for zone changes and combat start to automatically load and display
/// the relevant timeline overlay when the player enters a mapped instance.
/// </summary>
public sealed class EncounterTracker : IDisposable
{
    private readonly Plugin       plugin;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICondition   condition;
    private readonly IDataManager dataManager;
    private readonly IFramework   framework;
    private readonly IPluginLog   log;

    /// <summary>Maps territory type IDs to FFLogs encounter IDs.</summary>
    private Dictionary<ushort, int> zoneToEncounterId = [];

    private ushort lastTerritoryId;
    private bool   wasInCombat;

    public EncounterTracker(
        Plugin        plugin,
        IClientState  clientState,
        IObjectTable  objectTable,
        ICondition    condition,
        IDataManager  dataManager,
        IFramework    framework,
        IPluginLog    log)
    {
        this.plugin       = plugin;
        this.clientState  = clientState;
        this.objectTable  = objectTable;
        this.condition    = condition;
        this.dataManager  = dataManager;
        this.framework    = framework;
        this.log          = log;

        framework.Update += OnUpdate;
        RebuildZoneMappings();
    }

    // ── Zone mapping ────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the territory → encounter-ID lookup from stored timelines.
    /// Call this after new timelines are saved so the map stays current.
    /// </summary>
    public void RebuildZoneMappings()
    {
        zoneToEncounterId.Clear();

        var db = plugin.TimelineStore.Load();
        if (db.Timelines.Count == 0)
        {
            log.Debug("EncounterTracker: no timelines stored, skipping zone map build.");
            return;
        }

        // Collect distinct (encounterId, encounterName) pairs
        var encounters = db.Timelines.Values
            .Select(t => (t.EncounterId, t.EncounterName))
            .Distinct()
            .ToList();

        var cfcSheet = dataManager.GetExcelSheet<ContentFinderCondition>();
        if (cfcSheet == null)
        {
            log.Warning("EncounterTracker: ContentFinderCondition sheet not available.");
            return;
        }

        foreach (var row in cfcSheet)
        {
            if (row.TerritoryType.RowId == 0) continue;
            var territoryId = (ushort)row.TerritoryType.RowId;
            if (zoneToEncounterId.ContainsKey(territoryId)) continue;

            var cfcName = row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(cfcName)) continue;

            foreach (var (encId, encName) in encounters)
            {
                if (!NamesMatch(cfcName, encName)) continue;
                zoneToEncounterId[territoryId] = encId;
                log.Debug("EncounterTracker: territory {0} ({1}) → encounter {2} ({3})",
                    territoryId, cfcName, encId, encName);
                break;
            }
        }

        log.Info("EncounterTracker: mapped {0} zone(s) from {1} stored timelines.",
            zoneToEncounterId.Count, db.Timelines.Count);
    }

    private static bool NamesMatch(string cfcName, string encounterName)
    {
        if (string.Equals(cfcName, encounterName, StringComparison.OrdinalIgnoreCase))
            return true;

        var cfc = cfcName.ToLowerInvariant();
        var enc = encounterName.ToLowerInvariant();
        return cfc.Contains(enc) || enc.Contains(cfc);
    }

    // ── Framework update (game thread polling) ──────────────────────────

    private void OnUpdate(IFramework fw)
    {
        var currentTid = clientState.TerritoryType;
        var inCombat   = condition[ConditionFlag.InCombat];

        // ── Zone change ──────────────────────────────────────────────
        if (currentTid != lastTerritoryId)
        {
            lastTerritoryId = currentTid;
            wasInCombat     = false;

            if (zoneToEncounterId.TryGetValue(currentTid, out var encounterId))
            {
                OnEnterMappedZone(encounterId);
            }
            else
            {
                // Leaving a mapped instance — clear overlay so it doesn't persist.
                plugin.OverlayWindow.ClearForZoneChange();
                log.Debug("EncounterTracker: zone {0} is unmapped, overlay cleared.", currentTid);
            }
        }

        // ── Combat rising edge ────────────────────────────────────────
        if (inCombat && !wasInCombat)
        {
            OnCombatStart();
        }
        wasInCombat = inCombat;
    }

    // ── Event handlers ──────────────────────────────────────────────────

    private void OnEnterMappedZone(int encounterId)
    {
        var specName = GetCurrentSpecName();

        // Try to get the timeline that best matches the player's current job.
        // Fall back to any available timeline for this encounter if no spec match.
        AggregatedTimeline? timeline = null;
        if (specName != null)
            timeline = plugin.TimelineStore.GetTimeline(encounterId, specName);

        if (timeline == null)
        {
            var db = plugin.TimelineStore.Load();
            timeline = db.Timelines.Values.FirstOrDefault(t => t.EncounterId == encounterId);
        }

        if (timeline == null)
        {
            log.Debug("EncounterTracker: no stored timeline for encounter {0}.", encounterId);
            return;
        }

        // Open in paused preview mode so the player can study before the pull.
        // OnConditionChange will lock + switch to live tracking when combat starts.
        plugin.OverlayWindow.PrepareCombatPreview(timeline);

        log.Info("EncounterTracker: zone {0} loaded [{1}/{2}], overlay ready (paused at t=0).",
            lastTerritoryId, timeline.EncounterName, timeline.SpecName);
    }

    private void OnCombatStart()
    {
        // OverlayWindow.OnConditionChange already sets inCombat = true.
        // We just ensure the overlay is visible when a timeline is loaded.
        if (plugin.OverlayWindow.HasActiveTimeline && plugin.Configuration.OverlayEnabled)
        {
            plugin.OverlayWindow.IsOpen = true;
            log.Debug("EncounterTracker: combat started, overlay shown.");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the player's current job name in title-case (e.g. "Paladin", "Dark Knight"),
    /// matching the naming convention used by FFLogs spec names.
    /// Returns null if the player is not logged in or the job cannot be resolved.
    /// </summary>
    private string? GetCurrentSpecName()
    {
        var player = objectTable.LocalPlayer;
        if (player == null) return null;

        try
        {
            var jobName = player.ClassJob.Value.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(jobName)) return null;

            // ClassJob.Name is lowercase (e.g. "paladin", "dark knight").
            // FFLogs spec names use title case — convert to match.
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(jobName);
        }
        catch
        {
            return null;
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────────

    public void Dispose()
    {
        framework.Update -= OnUpdate;
    }
}
