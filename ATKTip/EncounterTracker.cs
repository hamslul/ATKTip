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

    /// <summary>
    /// When we enter a mapped zone but the player object isn't loaded yet,
    /// we store the encounter ID here and retry each frame until the spec is resolved.
    /// </summary>
    private int? pendingEncounterId;

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

        var db      = plugin.TimelineStore.Load();
        var customs = plugin.Configuration.CustomTimelines;

        // Collect distinct (encounterId, encounterName) pairs from both sources.
        // Custom timelines must have a non-zero EncounterId (set when the user picks
        // the encounter from the dropdown in the custom timeline editor).
        var encounters = db.Timelines.Values
            .Select(t => (t.EncounterId, t.EncounterName))
            .Concat(customs.Values
                .Where(t => t.EncounterId > 0)
                .Select(t => (t.EncounterId, t.EncounterName)))
            .Where(e => e.EncounterId > 0)
            .Distinct()
            .ToList();

        if (encounters.Count == 0)
        {
            log.Debug("EncounterTracker: no timelines with encounter IDs, skipping zone map build.");
            return;
        }

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

        log.Info("EncounterTracker: mapped {0} zone(s) from {1} stored + {2} custom timeline(s).",
            zoneToEncounterId.Count, db.Timelines.Count, customs.Count);
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
            lastTerritoryId  = currentTid;
            wasInCombat      = false;
            pendingEncounterId = null;          // cancel any outstanding retry

            if (zoneToEncounterId.TryGetValue(currentTid, out var encounterId))
            {
                // Player object may not be loaded on the first frame after a zone
                // transition, so attempt the lookup now but store the encounter ID
                // for retry on subsequent frames if the spec cannot be resolved yet.
                pendingEncounterId = encounterId;
            }
            else
            {
                // Leaving a mapped instance — clear overlay so it doesn't persist.
                plugin.OverlayWindow.ClearForZoneChange();
                log.Debug("EncounterTracker: zone {0} is unmapped, overlay cleared.", currentTid);
            }
        }

        // ── Pending spec resolution ───────────────────────────────────
        if (pendingEncounterId.HasValue)
        {
            if (OnEnterMappedZone(pendingEncounterId.Value))
                pendingEncounterId = null;     // resolved — stop retrying
        }

        // ── Combat rising edge ────────────────────────────────────────
        if (inCombat && !wasInCombat)
        {
            OnCombatStart();
        }
        wasInCombat = inCombat;
    }

    // ── Event handlers ──────────────────────────────────────────────────

    /// <summary>
    /// Attempts to load the timeline for the given encounter that matches the player's
    /// current spec.  Returns <c>true</c> when the attempt is conclusive (spec resolved,
    /// timeline found or confirmed absent) and <c>false</c> when the player object is not
    /// yet available and the caller should retry on the next frame.
    /// </summary>
    private bool OnEnterMappedZone(int encounterId)
    {
        var specName = GetCurrentSpecName();

        // Player not yet loaded — ask the caller to retry next frame.
        if (specName == null)
            return false;

        var customs = plugin.Configuration.CustomTimelines;

        // Look up by exact encounter + spec match only — no any-spec fallback,
        // so a Bard timeline never loads when the player is on Astrologian.
        AggregatedTimeline? timeline = plugin.TimelineStore.GetTimeline(encounterId, specName);

        if (timeline == null)
        {
            var key = TimelineDatabase.MakeKey(encounterId, specName);
            customs.TryGetValue(key, out timeline);
        }

        if (timeline == null)
        {
            log.Debug("EncounterTracker: no timeline for encounter {0} / {1}.", encounterId, specName);
            return true;    // conclusive — nothing to load for this job
        }

        // Open in paused preview mode so the player can study before the pull.
        // OnConditionChange will lock + switch to live tracking when combat starts.
        plugin.OverlayWindow.PrepareCombatPreview(timeline);

        log.Info("EncounterTracker: zone {0} loaded [{1}/{2}], overlay ready (paused at t=0).",
            lastTerritoryId, timeline.EncounterName, timeline.SpecName);
        return true;
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
