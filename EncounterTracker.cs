using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using ATKTip.Data;
using Lumina.Excel.Sheets;

namespace ATKTip;

/// <summary>
/// Watches for zone changes and combat start to automatically load and display
/// the relevant timeline overlay when the player enters a mapped instance.
///
/// Detection mirrors BossmodReborn's dual-system approach:
///   1. CFCID (ContentFinderConditionId from GameMain) — zone-entry trigger, same as BMR ZoneModules.
///   2. Boss actor OID (DataId scan on IObjectTable) — boss-spawn trigger, same as BMR BossModules.
///   3. Territory-type + name-matching — fallback for older/unmapped content.
/// </summary>
public sealed unsafe class EncounterTracker : IDisposable
{
    private readonly Plugin       plugin;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICondition   condition;
    private readonly IDataManager dataManager;
    private readonly IFramework   framework;
    private readonly IPluginLog   log;
    private readonly Dictionary<ushort, uint> cfcToTerritoryType = [];

    // ── Runtime lookup tables (rebuilt from stored timelines) ─────────────
    /// <summary>Maps territory type IDs to FFLogs encounter IDs (name-match fallback only).</summary>
    private Dictionary<uint, int> zoneToEncounterId = [];
    /// <summary>Maps FFLogs encounter IDs to encounter names (for custom timeline lookup).</summary>
    private Dictionary<int, string> encounterIdToName = [];
    /// <summary>Encounter IDs that have at least one custom timeline — only these trigger the overlay.</summary>
    private HashSet<int> availableEncounterIds = [];

    // ── Hardcoded encounter databases ─────────────────────────────────────

    /// <summary>
    /// ContentFinderCondition row ID → FFLogs encounter ID.
    /// CFC IDs (GroupID) sourced from BossmodReborn ModuleInfo attributes.
    /// Primary zone-entry detection; mirrors BMR ZoneModuleRegistry keyed by CFCID.
    /// </summary>
    private static readonly Dictionary<ushort, int> KnownCFCEncounters = new()
    {
        // ── Endwalker Ultimates ────────────────────────────────────────────
        { 788,  1076 },  // Dragonsong's Reprise (Ultimate)
        { 908,  1077 },  // The Omega Protocol (Ultimate)
        { 1006, 1079 },  // Futures Rewritten (Ultimate)

        // ── Endwalker Extremes ─────────────────────────────────────────────
        // Note: FFLogs IDs for 803/791/871/924/950/965 are estimated (sequential inference).
        // Endsinger (846 → 1063) is the only confirmed EW extreme FFLogs ID.
        { 803,  1064 },  // The Minstrel's Ballad: Zodiark's Fall
        { 791,  1065 },  // The Minstrel's Ballad: Hydaelyn's Call
        { 846,  1063 },  // The Minstrel's Ballad: Endsinger's Aria     ← confirmed
        { 871,  1066 },  // Storm's Crown (Extreme) — Barbariccia
        { 924,  1067 },  // Mount Ordeals (Extreme) — Rubicante
        { 950,  1068 },  // The Voidcast Dais (Extreme) — Golbez
        { 965,  1069 },  // The Abyssal Fracture (Extreme) — Zeromus

        // ── Dawntrail Extremes ─────────────────────────────────────────────
        { 833,  1071 },  // Worqor Lar Dor (Extreme) — Valigarmanda
        { 996,  1072 },  // Everkeep (Extreme) — Zoraal Ja
        { 1017, 1078 },  // The Minstrel's Ballad: Sphene's Burden — Queen Eternal
        { 1031, 1080 },  // Recollection (Extreme) — Zelenia
        { 1044, 1082 },  // The Windward Wilds (Extreme) — Guardian Arkveld
        { 1062, 1081 },  // The Minstrel's Ballad: Necron's Embrace
        { 1077, 1083 },  // Hell on Rails (Extreme) — Doomtrain

        // ── Dawntrail Savage: AAC Light-heavyweight ────────────────────────
        { 986,  93  },   // M1S — Black Cat
        { 988,  94  },   // M2S — Honey B. Lovely
        { 990,  95  },   // M3S — Brute Bomber
        { 992,  96  },   // M4S — Wicked Thunder

        // ── Dawntrail Savage: AAC Cruiserweight ───────────────────────────
        { 1020, 97  },   // M5S — Dancing Green
        { 1022, 98  },   // M6S — Sugar Riot
        { 1024, 99  },   // M7S — Brute Abombinator
        { 1026, 100 },   // M8S — Howling Blade

        // ── Dawntrail Savage: AAC Heavyweight ─────────────────────────────
        { 1069, 101 },   // M9S  — Vamp Fatale
        { 1071, 102 },   // M10S — The Xtremes
        { 1073, 103 },   // M11S — The Tyrant
        { 1075, 104 },   // M12S — Lindwurm (P1 + P2 share CFC row)
    };

    /// <summary>
    /// Boss actor NameId (BNpcName row) → FFLogs encounter ID.
    /// OIDs sourced from BossmodReborn module OID enums (Boss enum value = BNpcName row ID).
    /// Secondary detection: fires when boss entity spawns in object table.
    /// Mirrors BMR BossModuleRegistry keyed by primary actor OID.
    /// </summary>
    private static readonly Dictionary<uint, int> KnownBossOIDs = new()
    {
        // ── Endwalker Ultimates ────────────────────────────────────────────
        { 0x3139, 1076 },  // DSR — Ser Adelphel (P1 primary)
        { 0x313C, 1076 },  // DSR — King Thordan (P2 primary)
        { 0x3D5C, 1077 },  // TOP — Omega
        { 0x459B, 1079 },  // FRU — Fatebreaker P1

        // ── Endwalker Extremes ─────────────────────────────────────────────
        { 0x324D, 1064 },  // Zodiark
        { 0x34ED, 1065 },  // Hydaelyn
        { 0x38BF, 1063 },  // Endsinger  ← confirmed
        { 0x39CE, 1066 },  // Barbariccia
        { 0x3D8C, 1067 },  // Rubicante
        { 0x3F57, 1068 },  // Golbez
        { 0x40AD, 1069 },  // Zeromus

        // ── Dawntrail Extremes ─────────────────────────────────────────────
        { 0x4118, 1071 },  // Valigarmanda
        { 0x42B5, 1072 },  // Zoraal Ja
        { 0x4677, 1078 },  // Queen Eternal P1
        { 0x4678, 1078 },  // Queen Eternal P2
        { 0x47C6, 1080 },  // Zelenia
        { 0x490B, 1081 },  // Necron
        { 0x48E5, 1082 },  // Guardian Arkveld
        { 0x4A37, 1083 },  // Doomtrain

        // ── Dawntrail Savage: AAC Light-heavyweight ────────────────────────
        { 0x4329, 93  },   // M1S — Black Cat
        { 0x422D, 94  },   // M2S — Honey B. Lovely
        { 0x42C6, 95  },   // M3S — Brute Bomber
        { 0x43AA, 96  },   // M4S — Wicked Thunder P1
        { 0x43AE, 96  },   // M4S — Wicked Thunder P2

        // ── Dawntrail Savage: AAC Cruiserweight ───────────────────────────
        { 0x47B9, 97  },   // M5S — Dancing Green
        { 0x479F, 98  },   // M6S — Sugar Riot
        { 0x4783, 99  },   // M7S — Brute Abombinator
        { 0x4727, 100 },   // M8S — Howling Blade

        // ── Dawntrail Savage: AAC Heavyweight ─────────────────────────────
        { 0x4ADF, 101 },   // M9S  — Vamp Fatale
        { 0x4BDF, 102 },   // M10S — The Xtremes
        { 0x4AEC, 103 },   // M11S — The Tyrant
        { 0x4B02, 104 },   // M12S — Lindwurm (P1 + P2 share OID)
    };

    /// <summary>
    /// Territory type ID → FFLogs encounter ID (hardcoded known zones).
    /// Used only when CFCID lookup misses (CFC is 0 or unmapped).
    /// </summary>
    private static readonly Dictionary<uint, int> KnownZoneEncounters = new()
    {
        // ── Ultimates ──────────────────────────────────────────────────────
        { 733,  1073 },  // The Unending Coil of Bahamut (Ultimate)
        { 777,  1074 },  // The Weapon's Refrain (Ultimate)
        { 887,  1075 },  // The Epic of Alexander (Ultimate)
        { 968,  1076 },  // Dragonsong's Reprise (Ultimate)
        { 1122, 1077 },  // The Omega Protocol (Ultimate)
        { 1238, 1079 },  // Futures Rewritten (Ultimate)

        // ── Endwalker Extremes ─────────────────────────────────────────────
        { 1033, 1064 },  // The Minstrel's Ballad: Zodiark's Fall
        { 1036, 1065 },  // The Minstrel's Ballad: Hydaelyn's Call
        { 1049, 1063 },  // The Minstrel's Ballad: Endsinger's Aria
        { 1072, 1066 },  // Storm's Crown (Extreme) — Barbariccia
        { 1096, 1067 },  // Mount Ordeals (Extreme) — Rubicante
        { 1141, 1068 },  // The Voidcast Dais (Extreme) — Golbez
        { 1169, 1069 },  // The Abyssal Fracture (Extreme) — Zeromus

        // ── Dawntrail Extremes ─────────────────────────────────────────────
        { 1196, 1071 },  // Worqor Lar Dor (Extreme) — Valigarmanda
        { 1201, 1072 },  // Everkeep (Extreme) — Zoraal Ja
        { 1243, 1078 },  // The Minstrel's Ballad: Sphene's Burden — Queen Eternal
        { 1271, 1080 },  // Recollection (Extreme) — Zelenia
        { 1296, 1081 },  // The Minstrel's Ballad: Necron's Embrace
        { 1306, 1082 },  // The Windward Wilds (Extreme) — Guardian Arkveld
        { 1308, 1083 },  // Hell on Rails (Extreme) — Doomtrain

        // ── Dawntrail Savage: AAC Light-heavyweight ────────────────────────
        { 1226, 93  },   // M1S — Black Cat
        { 1228, 94  },   // M2S — Honey B. Lovely
        { 1230, 95  },   // M3S — Brute Bomber
        { 1232, 96  },   // M4S — Wicked Thunder

        // ── Dawntrail Savage: AAC Cruiserweight ───────────────────────────
        { 1257, 97  },   // M5S — Dancing Green
        { 1259, 98  },   // M6S — Sugar Riot
        { 1261, 99  },   // M7S — Brute Abombinator
        { 1263, 100 },   // M8S — Howling Blade

        // ── Dawntrail Savage: AAC Heavyweight ─────────────────────────────
        { 1321, 101 },   // M9S  — Vamp Fatale
        { 1323, 102 },   // M10S — The Xtremes
        { 1325, 103 },   // M11S — The Tyrant
        { 1327, 104 },   // M12S — Lindwurm
    };

    private uint lastTerritoryId;
    private ushort lastCFCId;
    private bool   wasInCombat;
    private int    loadedEncounterId;  // 0 = no encounter currently loaded

    /// <summary>
    /// Encounter ID to resolve on next frame(s) until the player object is available.
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

        BuildCfcTerritoryMap();
        framework.Update += OnUpdate;
        RebuildZoneMappings();
    }

    // ── Zone mapping ─────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds runtime encounter lookups from stored timelines.
    /// Only custom timelines are considered — fetched logs alone do not trigger
    /// the overlay. Call this after new timelines are saved.
    /// </summary>
    public void RebuildZoneMappings()
    {
        zoneToEncounterId.Clear();
        encounterIdToName.Clear();
        availableEncounterIds.Clear();

        var customs = plugin.Configuration.CustomTimelines;

        // Build encounter ID → name lookup from fetched + custom timelines.
        var fetchedEncounters = plugin.TimelineStore.GetAllTimelines()
            .Where(t => t.EncounterId > 0)
            .Select(t => (t.EncounterId, t.EncounterName));

        var customEncounters = customs.Values
            .Where(t => t.EncounterId > 0)
            .Select(t => (t.EncounterId, t.EncounterName));

        var encounters = fetchedEncounters
            .Concat(customEncounters)
            .Where(e => e.EncounterId > 0)
            .Distinct()
            .ToList();

        foreach (var (encId, encName) in encounters)
            encounterIdToName.TryAdd(encId, encName);

        // Synthetic negative IDs for EncounterId=0 custom timelines (name-only matching).
        var syntheticId = -1;
        foreach (var encNameZero in customs.Values
            .Where(t => t.EncounterId == 0 && !string.IsNullOrWhiteSpace(t.EncounterName))
            .Select(t => t.EncounterName)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (encounterIdToName.Values.Any(n => NamesMatch(n, encNameZero)))
                continue;
            encounters.Add((syntheticId, encNameZero));
            encounterIdToName[syntheticId] = encNameZero;
            syntheticId--;
        }

        if (encounters.Count == 0)
        {
            log.Debug("EncounterTracker: no encounters to map, skipping zone map build.");
            return;
        }

        // availableEncounterIds is used by OnUpdate to gate CFCID and OID detection.
        foreach (var (encId, _) in encounters)
            availableEncounterIds.Add(encId);

        // ── Name-matching fallback (territory type → encounter ID) ────────
        // Only needed for encounters not covered by KnownCFCEncounters or KnownZoneEncounters.
        var knownIds     = new HashSet<int>(KnownCFCEncounters.Values.Concat(KnownZoneEncounters.Values));
        var uncovered    = encounters.Where(e => !knownIds.Contains(e.EncounterId)).ToList();

        if (uncovered.Count > 0)
        {
            var cfcSheet = dataManager.GetExcelSheet<ContentFinderCondition>();
            if (cfcSheet == null)
            {
                log.Warning("EncounterTracker: ContentFinderCondition sheet unavailable; name-match skipped.");
            }
            else
            {
                foreach (var row in cfcSheet)
                {
                    if (row.TerritoryType.RowId == 0) continue;
                    var tid = row.TerritoryType.RowId;
                    if (zoneToEncounterId.ContainsKey(tid)) continue;

                    var cfcName = row.Name.ExtractText();
                    if (string.IsNullOrWhiteSpace(cfcName)) continue;

                    foreach (var (encId, encName) in uncovered)
                    {
                        if (!NamesMatch(cfcName, encName)) continue;
                        zoneToEncounterId[tid] = encId;
                        log.Debug("EncounterTracker: territory {0} ({1}) → encounter {2} ({3}) (name match)",
                            tid, cfcName, encId, encName);
                        break;
                    }
                }
            }
        }

        log.Info("EncounterTracker: {0} encounter(s) available, {1} name-matched zone(s).",
            availableEncounterIds.Count, zoneToEncounterId.Count);
    }

    private void BuildCfcTerritoryMap()
    {
        cfcToTerritoryType.Clear();

        var cfcSheet = dataManager.GetExcelSheet<ContentFinderCondition>();
        if (cfcSheet == null)
            return;

        foreach (var row in cfcSheet)
        {
            var cfcId = (ushort)row.RowId;
            var territoryId = row.TerritoryType.RowId;
            if (cfcId == 0 || territoryId == 0)
                continue;

            cfcToTerritoryType[cfcId] = territoryId;
        }
    }

    private bool DoesCurrentCfcMatchTerritory(ushort cfcId, uint territoryId)
        => cfcId > 0
           && territoryId > 0
           && cfcToTerritoryType.TryGetValue(cfcId, out var mappedTerritoryId)
           && mappedTerritoryId == territoryId;

    private static bool NamesMatch(string cfcName, string encounterName)
    {
        if (string.Equals(cfcName, encounterName, StringComparison.OrdinalIgnoreCase))
            return true;

        var cfc = cfcName.ToLowerInvariant();
        var enc = encounterName.ToLowerInvariant();
        return cfc.Contains(enc) || enc.Contains(cfc);
    }

    // ── Framework update (game thread polling) ───────────────────────────

    private void OnUpdate(IFramework fw)
    {
        var currentTid = clientState.TerritoryType;
        var currentCFC = (ushort)GameMain.Instance()->CurrentContentFinderConditionId;
        var inCombat   = condition[ConditionFlag.InCombat];

        // ── Zone change (territory OR CFC changed) ────────────────────────
        // Mirrors WorldStateGameSync in VBM/BMR which checks both each frame.
        if (currentTid != lastTerritoryId || currentCFC != lastCFCId)
        {
            lastTerritoryId    = currentTid;
            lastCFCId          = currentCFC;
            wasInCombat        = false;
            pendingEncounterId = null;
            loadedEncounterId  = 0;

            int encounterId;
            bool mapped = false;

            // 1. CFCID lookup (most precise — mirrors BMR ZoneModules)
            if (currentCFC > 0
                && DoesCurrentCfcMatchTerritory(currentCFC, currentTid)
                && KnownCFCEncounters.TryGetValue(currentCFC, out encounterId)
                && availableEncounterIds.Contains(encounterId))
            {
                pendingEncounterId = encounterId;
                mapped = true;
                log.Debug("EncounterTracker: CFC {0} → encounter {1} (CFCID).", currentCFC, encounterId);
            }

            // 2. Territory ID hardcoded lookup (fallback when CFC unmapped)
            if (!mapped
                && KnownZoneEncounters.TryGetValue(currentTid, out encounterId)
                && availableEncounterIds.Contains(encounterId))
            {
                pendingEncounterId = encounterId;
                mapped = true;
                log.Debug("EncounterTracker: territory {0} → encounter {1} (hardcoded).", currentTid, encounterId);
            }

            // 3. Name-match fallback (older/custom content)
            if (!mapped && zoneToEncounterId.TryGetValue(currentTid, out encounterId))
            {
                pendingEncounterId = encounterId;
                mapped = true;
                log.Debug("EncounterTracker: territory {0} → encounter {1} (name match).", currentTid, encounterId);
            }

            if (!mapped)
            {
                plugin.OverlayWindow.ClearForZoneChange();
                log.Debug("EncounterTracker: zone {0}/CFC {1} unmapped, overlay cleared.", currentTid, currentCFC);
            }
        }

        // ── Pending spec resolution ───────────────────────────────────────
        if (pendingEncounterId.HasValue)
        {
            if (OnEnterMappedZone(pendingEncounterId.Value))
                pendingEncounterId = null;
        }

        // ── Boss OID scan (BMR BossModule style) ──────────────────────────
        // Scans object table for known boss actors as secondary detection.
        // Only runs when no encounter is currently loaded (zone detection may have missed).
        if (loadedEncounterId == 0 && !pendingEncounterId.HasValue)
            ScanForBossOID();

        // ── Combat rising edge ────────────────────────────────────────────
        if (inCombat && !wasInCombat)
            OnCombatStart();
        wasInCombat = inCombat;
    }

    // ── Boss OID scan ────────────────────────────────────────────────────

    /// <summary>
    /// Scans the object table for any actor whose BNpcName ID matches a known boss OID.
    /// When found, triggers timeline load for that encounter — mirrors BMR BossModuleManager
    /// which subscribes to actor-added events and looks up modules by primary actor OID.
    /// </summary>
    private void ScanForBossOID()
    {
        foreach (var obj in objectTable)
        {
            if (obj is not IBattleNpc bnpc) continue;
            if (!KnownBossOIDs.TryGetValue(bnpc.NameId, out var encounterId)) continue;
            if (!availableEncounterIds.Contains(encounterId)) continue;

            log.Info("EncounterTracker: boss OID 0x{0:X4} (NameId {1}) → encounter {2}, triggering load.",
                bnpc.NameId, bnpc.NameId, encounterId);
            pendingEncounterId = encounterId;
            return;
        }
    }

    // ── Event handlers ───────────────────────────────────────────────────

    /// <summary>
    /// Attempts to load the custom timeline for the given encounter that matches the player's
    /// current spec. Returns <c>true</c> when conclusive (spec resolved, timeline found or
    /// confirmed absent) and <c>false</c> when the player object is not yet loaded.
    /// </summary>
    private bool OnEnterMappedZone(int encounterId)
    {
        var specName = GetCurrentSpecName();
        if (specName == null)
            return false;   // player not yet loaded — retry next frame

        var encName = encounterIdToName.GetValueOrDefault(encounterId, string.Empty);

        var timeline = plugin.Configuration.CustomTimelines.Values.FirstOrDefault(t =>
            string.Equals(t.SpecName, specName, StringComparison.OrdinalIgnoreCase) &&
            (t.EncounterId == encounterId ||
             (t.EncounterId == 0 && !string.IsNullOrEmpty(encName) && NamesMatch(encName, t.EncounterName))));

        if (timeline == null)
        {
            log.Debug("EncounterTracker: no custom timeline for encounter {0} / {1}.", encounterId, specName);
            loadedEncounterId = 0;
            return true;    // conclusive — nothing to load for this job
        }

        plugin.OverlayWindow.PrepareCombatPreview(timeline);
        loadedEncounterId = encounterId;

        log.Info("EncounterTracker: loaded [{0}/{1}] (encounter {2}), overlay ready (paused at t=0).",
            timeline.EncounterName, timeline.SpecName, encounterId);
        return true;
    }

    private void OnCombatStart()
    {
        if (plugin.OverlayWindow.HasActiveTimeline && plugin.Configuration.OverlayEnabled)
        {
            plugin.OverlayWindow.IsOpen = true;
            log.Debug("EncounterTracker: combat started, overlay shown.");
        }
    }

    // ── Manual preview (fallback for failed auto-detection) ─────────────

    /// <summary>
    /// Attempts to find and preview a custom timeline for the player's current zone and job,
    /// bypassing the availableEncounterIds gate (so it works even when FFLogs IDs are wrong).
    /// Matches by CFC/zone name against timeline.EncounterName.
    /// Returns <c>true</c> if a unique match was found and the overlay was loaded.
    /// Returns <c>false</c> if zero or multiple matches — caller should open QuickPickWindow.
    /// </summary>
    public bool TryManualPreview()
    {
        var specName = GetCurrentSpecName();
        var customs = plugin.Configuration.CustomTimelines;
        if (customs.Count == 0) return false;

        // Get current zone display name from CFC sheet (same source as name-match fallback)
        string? zoneName = null;
        if (lastCFCId > 0)
        {
            var cfcSheet = plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>();
            if (cfcSheet != null)
            {
                foreach (var row in cfcSheet)
                {
                    if (row.RowId == lastCFCId)
                    {
                        zoneName = row.Name.ExtractText();
                        break;
                    }
                }
            }
        }

        // Find custom timelines matching zone name + player's job
        var candidates = customs.Values
            .Where(t => specName == null || string.Equals(t.SpecName, specName, StringComparison.OrdinalIgnoreCase))
            .Where(t => zoneName == null || NamesMatch(zoneName, t.EncounterName))
            .ToList();

        // If zone name available but no job+zone match, try zone-only (any job)
        if (candidates.Count == 0 && zoneName != null)
        {
            candidates = customs.Values
                .Where(t => NamesMatch(zoneName, t.EncounterName))
                .ToList();
        }

        if (candidates.Count != 1) return false;

        var timeline = candidates[0];
        plugin.OverlayWindow.PrepareCombatPreview(timeline);
        plugin.OverlayWindow.IsOpen = true;
        loadedEncounterId = timeline.EncounterId;
        log.Info("EncounterTracker: manual preview → [{0}/{1}].", timeline.EncounterName, timeline.SpecName);
        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the player's current job name in title-case (e.g. "Paladin", "Dark Knight"),
    /// matching FFLogs spec name convention. Returns null if player not yet loaded.
    /// Public wrapper used by QuickPickWindow for relevance sorting.
    /// </summary>
    public string? GetCurrentSpecNamePublic() => GetCurrentSpecName();

    /// <summary>
    /// Returns the player's current job name in title-case (e.g. "Paladin", "Dark Knight"),
    /// matching FFLogs spec name convention. Returns null if player not yet loaded.
    /// </summary>
    private string? GetCurrentSpecName()
    {
        var player = objectTable.LocalPlayer;
        if (player == null) return null;

        try
        {
            var jobName = player.ClassJob.Value.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(jobName)) return null;

            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(jobName);
        }
        catch
        {
            return null;
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────

    public void Dispose()
    {
        framework.Update -= OnUpdate;
    }
}
