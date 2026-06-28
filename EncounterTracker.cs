using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Game.DutyState;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
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
    public sealed class TrackerDebugEncounterOption
    {
        public required int EncounterId { get; init; }
        public required string EncounterName { get; init; }
        public required string SpecName { get; init; }
        public int PhaseCount { get; init; }
        public bool HasFullTimeline { get; init; }
    }

    public sealed class TrackerDebugState
    {
        public bool HasActiveEncounter { get; init; }
        public int EncounterId { get; init; }
        public string EncounterName { get; init; } = string.Empty;
        public string SpecName { get; init; } = string.Empty;
        public int ActivePhaseOrdinal { get; init; }
        public int AvailablePhaseCount { get; init; }
        public int PendingPreviewPhaseOrdinal { get; init; }
        public bool HasNextPhase { get; init; }
    }

    private sealed class ManualPreviewCandidate
    {
        public required string Key { get; init; }
        public required AggregatedTimeline Timeline { get; init; }
        public int BaseEncounterId { get; init; }
        public required string BaseEncounterName { get; init; }
    }

    private readonly Plugin       plugin;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ICondition   condition;
    private readonly IDutyState   dutyState;
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
    private string loadedSpecName = string.Empty;
    private DateTime encounterCombatStartTimeUtc;
    private PhaseTimelineSet? activePhaseTimelineSet;
    private int activePhaseOrdinal;
    private uint activePhasePrimaryBossDataId;
    private uint pendingPhasePrimaryBossDataId;
    private DateTime pendingPhasePrimaryBossSeenUtc;
    private uint pendingExpectedPhaseBossDataId;
    private DateTime pendingExpectedPhaseBossSeenUtc;
    private int pendingPreviewPhaseOrdinal;
    private bool phaseTransitionClearedTimeline;
    private const double LivePhaseBossChangeStableSec = 1.5;
    private const double LivePhaseFallbackDelaySec = 30.0;
    private const double LivePhaseNamedBossStableSec = 0.75;

    /// <summary>
    /// Encounter ID to resolve on next frame(s) until the player object is available.
    /// </summary>
    private int? pendingEncounterId;

    private sealed class PhaseTimelineCandidate
    {
        public required string Key { get; init; }
        public required AggregatedTimeline Timeline { get; init; }
        public int PhaseOrdinal { get; init; }
        public List<string> ExpectedBossNames { get; init; } = [];
        public List<uint> ExpectedBossActorIds { get; init; } = [];
    }

    private sealed class EncounterPhaseActorRule
    {
        public int PhaseOrdinal { get; init; }
        public List<uint> ActorIds { get; init; } = [];
    }

    private static readonly Dictionary<int, List<EncounterPhaseActorRule>> KnownEncounterPhaseActorRules = new()
    {
        [1074] =
        [
            new() { PhaseOrdinal = 1, ActorIds = [0x2212] },
            new() { PhaseOrdinal = 2, ActorIds = [0x221A] },
            new() { PhaseOrdinal = 3, ActorIds = [0x2217] },
            new() { PhaseOrdinal = 4, ActorIds = [0x221E, 0x221C] },
            new() { PhaseOrdinal = 5, ActorIds = [0x221E] },
        ],
    };

    private const int SyntheticPhaseEncounterIdBase = 1_000_000;

    private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> LegacyGeneratedEncounterPhaseIds =
        new Dictionary<int, IReadOnlyDictionary<int, int>>
        {
            [104] = new Dictionary<int, int>
            {
                [2] = 105,
            },
        };

    private sealed class EncounterPhaseMarker
    {
        public int Ordinal { get; init; }
        public double StartSec { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    private sealed class PhaseTimelineSet
    {
        public required int EncounterId { get; init; }
        public required string SpecName { get; init; }
        public required Dictionary<int, PhaseTimelineCandidate> PhaseTimelines { get; init; }
        public required List<EncounterPhaseMarker> PhaseMarkers { get; init; }
        public PhaseTimelineCandidate? FullTimeline { get; init; }
    }

    public EncounterTracker(
        Plugin        plugin,
        IClientState  clientState,
        IObjectTable  objectTable,
        ICondition    condition,
        IDutyState    dutyState,
        IDataManager  dataManager,
        IFramework    framework,
        IPluginLog    log)
    {
        this.plugin       = plugin;
        this.clientState  = clientState;
        this.objectTable  = objectTable;
        this.condition    = condition;
        this.dutyState    = dutyState;
        this.dataManager  = dataManager;
        this.framework    = framework;
        this.log          = log;

        BuildCfcTerritoryMap();
        framework.Update += OnUpdate;
        dutyState.DutyWiped += OnDutyReset;
        dutyState.DutyCompleted += OnDutyReset;
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
        var encounterNames = new Dictionary<int, string>();

        void RegisterEncounter(AggregatedTimeline timeline)
        {
            if (!TryGetBaseEncounterIdFromPhaseEncounterId(timeline.EncounterId, out var baseEncounterId) ||
                baseEncounterId <= 0)
            {
                return;
            }

            var candidateName = GetTimelineBaseEncounterName(timeline);
            if (string.IsNullOrWhiteSpace(candidateName))
                candidateName = timeline.EncounterName;
            if (string.IsNullOrWhiteSpace(candidateName))
                return;

            if (encounterNames.TryGetValue(baseEncounterId, out var existingName))
                encounterNames[baseEncounterId] = ChooseCanonicalEncounterName(existingName, candidateName);
            else
                encounterNames[baseEncounterId] = candidateName;
        }

        foreach (var timeline in plugin.TimelineStore.GetAllTimelines())
            RegisterEncounter(timeline);

        foreach (var timeline in customs.Values)
            RegisterEncounter(timeline);

        var encounters = encounterNames
            .Select(entry => (EncounterId: entry.Key, EncounterName: entry.Value))
            .ToList();

        foreach (var (encId, encName) in encounters)
            encounterIdToName[encId] = encName;

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
            loadedSpecName     = string.Empty;
            activePhaseTimelineSet = null;
            activePhaseOrdinal = 0;
            activePhasePrimaryBossDataId = 0;
            pendingPhasePrimaryBossDataId = 0;
            pendingPhasePrimaryBossSeenUtc = default;
            pendingExpectedPhaseBossDataId = 0;
            pendingExpectedPhaseBossSeenUtc = default;
            pendingPreviewPhaseOrdinal = 0;
            phaseTransitionClearedTimeline = false;

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
        else if (!inCombat && wasInCombat)
            OnCombatEnd();

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

        var phaseTimelineSet = BuildPhaseTimelineSet(encounterId, specName, encName);
        var timelineCandidate = ResolveInitialTimelineCandidate(phaseTimelineSet, encounterId, specName, encName);

        if (timelineCandidate == null)
        {
            log.Debug("EncounterTracker: no custom timeline for encounter {0} / {1}.", encounterId, specName);
            loadedEncounterId = 0;
            loadedSpecName = string.Empty;
            activePhaseTimelineSet = null;
            activePhaseOrdinal = 0;
            activePhasePrimaryBossDataId = 0;
            pendingPhasePrimaryBossDataId = 0;
            pendingPhasePrimaryBossSeenUtc = default;
            pendingExpectedPhaseBossDataId = 0;
            pendingExpectedPhaseBossSeenUtc = default;
            pendingPreviewPhaseOrdinal = 0;
            phaseTransitionClearedTimeline = false;
            return true;    // conclusive — nothing to load for this job
        }

        plugin.OverlayWindow.PrepareCombatPreview(timelineCandidate.Timeline, timelineCandidate.Key);
        loadedEncounterId = encounterId;
        loadedSpecName = specName;
        activePhaseTimelineSet = phaseTimelineSet;
        activePhaseOrdinal = timelineCandidate.PhaseOrdinal;
        activePhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossSeenUtc = default;
        pendingExpectedPhaseBossDataId = 0;
        pendingExpectedPhaseBossSeenUtc = default;
        pendingPreviewPhaseOrdinal = 0;
        phaseTransitionClearedTimeline = false;

        log.Info("EncounterTracker: loaded [{0}/{1}] (encounter {2}), overlay ready (paused at t=0).",
            timelineCandidate.Timeline.EncounterName, timelineCandidate.Timeline.SpecName, encounterId);
        return true;
    }

    private void OnCombatStart()
    {
        encounterCombatStartTimeUtc = DateTime.UtcNow;
        activePhasePrimaryBossDataId = GetPrimaryBossDataId();
        pendingPhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossSeenUtc = default;
        pendingExpectedPhaseBossDataId = 0;
        pendingExpectedPhaseBossSeenUtc = default;
        pendingPreviewPhaseOrdinal = 0;
        phaseTransitionClearedTimeline = false;

        if (activePhaseTimelineSet != null)
        {
            var initialCandidate = ResolveInitialPhaseTimelineCandidate(activePhaseTimelineSet);
            if (initialCandidate != null && activePhaseOrdinal <= 0)
            {
                plugin.OverlayWindow.PrepareCombatPreview(initialCandidate.Timeline, initialCandidate.Key);
                activePhaseOrdinal = initialCandidate.PhaseOrdinal;
            }
        }

        if (plugin.OverlayWindow.HasActiveTimeline && plugin.Configuration.OverlayEnabled)
        {
            plugin.OverlayWindow.IsOpen = true;
            log.Debug("EncounterTracker: combat started, overlay shown.");
        }
    }

    private void OnCombatEnd()
    {
        activePhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossSeenUtc = default;
        pendingExpectedPhaseBossDataId = 0;
        pendingExpectedPhaseBossSeenUtc = default;
        pendingPreviewPhaseOrdinal = 0;
        phaseTransitionClearedTimeline = false;
    }

    private void TryAdvanceCombatPhase()
    {
        if (activePhaseTimelineSet == null || activePhaseTimelineSet.PhaseTimelines.Count == 0)
            return;

        var elapsedSec = (DateTime.UtcNow - encounterCombatStartTimeUtc).TotalSeconds;
        var nextPhaseOrdinal = activePhaseOrdinal > 0 ? activePhaseOrdinal + 1 : 1;
        var nextMarker = activePhaseTimelineSet.PhaseMarkers
            .FirstOrDefault(marker => marker.Ordinal == nextPhaseOrdinal);
        if (!activePhaseTimelineSet.PhaseTimelines.TryGetValue(nextPhaseOrdinal, out var timelineCandidate))
            return;

        var currentTimelineCandidate = activePhaseTimelineSet.PhaseTimelines.GetValueOrDefault(activePhaseOrdinal);
        var currentExpectedBossActorIds = currentTimelineCandidate?.ExpectedBossActorIds ?? [];
        var currentExpectedBossNames = currentTimelineCandidate?.ExpectedBossNames ?? [];
        var usesDistinctActorBossSpawn = timelineCandidate.ExpectedBossActorIds.Count > 0 &&
                                         !HaveSamePhaseActorIds(currentExpectedBossActorIds, timelineCandidate.ExpectedBossActorIds);
        var usesDistinctNamedBossSpawn = !usesDistinctActorBossSpawn &&
                                         timelineCandidate.ExpectedBossNames.Count > 0 &&
                                         !HaveSameNormalizedBossNames(currentExpectedBossNames, timelineCandidate.ExpectedBossNames);
        var hasReachedNextPhaseMarker = nextMarker == null || elapsedSec >= nextMarker.StartSec;

        if (!hasReachedNextPhaseMarker && (usesDistinctActorBossSpawn || usesDistinctNamedBossSpawn))
        {
            pendingExpectedPhaseBossDataId = 0;
            pendingExpectedPhaseBossSeenUtc = default;
            pendingPreviewPhaseOrdinal = 0;
            phaseTransitionClearedTimeline = false;
            return;
        }

        if (usesDistinctActorBossSpawn)
        {
            if (TryGetTargetablePhaseBossActorId(timelineCandidate.ExpectedBossActorIds, out var expectedPhaseBossActorId))
            {
                if (pendingExpectedPhaseBossDataId != expectedPhaseBossActorId)
                {
                    pendingExpectedPhaseBossDataId = expectedPhaseBossActorId;
                    pendingExpectedPhaseBossSeenUtc = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - pendingExpectedPhaseBossSeenUtc).TotalSeconds >= LivePhaseNamedBossStableSec)
                {
                    plugin.OverlayWindow.SwitchCombatTimeline(timelineCandidate.Timeline, timelineCandidate.Key);
                    activePhaseOrdinal = nextPhaseOrdinal;
                    activePhasePrimaryBossDataId = expectedPhaseBossActorId;
                    pendingPhasePrimaryBossDataId = 0;
                    pendingPhasePrimaryBossSeenUtc = default;
                    pendingExpectedPhaseBossDataId = 0;
                    pendingExpectedPhaseBossSeenUtc = default;
                    pendingPreviewPhaseOrdinal = 0;
                    phaseTransitionClearedTimeline = false;
                    log.Info("EncounterTracker: advanced to phase {0} for encounter {1} / {2} via targetable phase actor 0x{3:X}.",
                        nextPhaseOrdinal,
                        activePhaseTimelineSet.EncounterId,
                        activePhaseTimelineSet.SpecName,
                        expectedPhaseBossActorId);
                    return;
                }
            }
            else
            {
                pendingExpectedPhaseBossDataId = 0;
                pendingExpectedPhaseBossSeenUtc = default;
            }
        }
        else if (usesDistinctNamedBossSpawn)
        {
            if (TryGetTargetablePhaseBossDataId(timelineCandidate.ExpectedBossNames, out var expectedPhaseBossDataId))
            {
                if (pendingExpectedPhaseBossDataId != expectedPhaseBossDataId)
                {
                    pendingExpectedPhaseBossDataId = expectedPhaseBossDataId;
                    pendingExpectedPhaseBossSeenUtc = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - pendingExpectedPhaseBossSeenUtc).TotalSeconds >= LivePhaseNamedBossStableSec)
                {
                    plugin.OverlayWindow.SwitchCombatTimeline(timelineCandidate.Timeline, timelineCandidate.Key);
                    activePhaseOrdinal = nextPhaseOrdinal;
                    activePhasePrimaryBossDataId = expectedPhaseBossDataId;
                    pendingPhasePrimaryBossDataId = 0;
                    pendingPhasePrimaryBossSeenUtc = default;
                    pendingExpectedPhaseBossDataId = 0;
                    pendingExpectedPhaseBossSeenUtc = default;
                    pendingPreviewPhaseOrdinal = 0;
                    phaseTransitionClearedTimeline = false;
                    log.Info("EncounterTracker: advanced to phase {0} for encounter {1} / {2} via targetable phase boss {3} (0x{4:X}).",
                        nextPhaseOrdinal,
                        activePhaseTimelineSet.EncounterId,
                        activePhaseTimelineSet.SpecName,
                        string.Join(", ", timelineCandidate.ExpectedBossNames),
                        expectedPhaseBossDataId);
                    return;
                }
            }
            else
            {
                pendingExpectedPhaseBossDataId = 0;
                pendingExpectedPhaseBossSeenUtc = default;
            }
        }

        if (nextMarker == null || elapsedSec < nextMarker.StartSec)
            return;

        var currentBossDataId = GetPrimaryBossDataId();
        var canAdvanceByBossChange = false;
        if (currentBossDataId != 0 &&
            activePhasePrimaryBossDataId != 0 &&
            currentBossDataId != activePhasePrimaryBossDataId)
        {
            if (pendingPhasePrimaryBossDataId != currentBossDataId)
            {
                pendingPhasePrimaryBossDataId = currentBossDataId;
                pendingPhasePrimaryBossSeenUtc = DateTime.UtcNow;
            }
            else if ((DateTime.UtcNow - pendingPhasePrimaryBossSeenUtc).TotalSeconds >= LivePhaseBossChangeStableSec)
            {
                canAdvanceByBossChange = true;
            }
        }
        else
        {
            pendingPhasePrimaryBossDataId = 0;
            pendingPhasePrimaryBossSeenUtc = default;
        }

        var canAdvanceByFallbackTime = elapsedSec >= nextMarker.StartSec + LivePhaseFallbackDelaySec;
        if (!canAdvanceByBossChange && !canAdvanceByFallbackTime)
            return;

        plugin.OverlayWindow.SwitchCombatTimeline(timelineCandidate.Timeline, timelineCandidate.Key);
        activePhaseOrdinal = nextPhaseOrdinal;
        activePhasePrimaryBossDataId = currentBossDataId != 0 ? currentBossDataId : activePhasePrimaryBossDataId;
        pendingPhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossSeenUtc = default;
        log.Info("EncounterTracker: advanced to phase {0} for encounter {1} / {2} via {3}.",
            nextPhaseOrdinal,
            activePhaseTimelineSet.EncounterId,
            activePhaseTimelineSet.SpecName,
            canAdvanceByBossChange ? $"live boss change to 0x{currentBossDataId:X}" : "timing fallback");
    }

    private bool TryGetTargetablePhaseBossDataId(IReadOnlyList<string> expectedBossNames, out uint matchedDataId)
    {
        matchedDataId = 0;
        if (expectedBossNames.Count == 0)
            return false;

        var normalizedExpectedNames = expectedBossNames
            .Select(NormalizePhaseBossName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedExpectedNames.Count == 0)
            return false;

        ulong bestMaxHp = 0;
        ulong bestCurrentHp = 0;
        foreach (var obj in objectTable)
        {
            if (obj == null ||
                obj.ObjectKind != ObjectKind.BattleNpc ||
                !obj.IsTargetable ||
                obj.IsDead ||
                obj.BaseId == 0)
            {
                continue;
            }

            var objectName = NormalizePhaseBossName(obj.Name.TextValue);
            if (string.IsNullOrWhiteSpace(objectName) ||
                !normalizedExpectedNames.Any(expectedName => DoesPhaseBossNameMatch(expectedName, objectName)))
            {
                continue;
            }

            var maxHp = obj is ICharacter character ? character.MaxHp : 0UL;
            var currentHp = obj is ICharacter hpCharacter ? hpCharacter.CurrentHp : 0UL;

            if (maxHp < bestMaxHp)
                continue;

            if (maxHp == bestMaxHp && currentHp <= bestCurrentHp)
                continue;

            matchedDataId = obj.BaseId;
            bestMaxHp = maxHp;
            bestCurrentHp = currentHp;
        }

        return matchedDataId != 0;
    }

    private bool TryGetTargetablePhaseBossActorId(IReadOnlyList<uint> expectedBossActorIds, out uint matchedActorId)
    {
        matchedActorId = 0;
        if (expectedBossActorIds.Count == 0)
            return false;

        var actorIdSet = expectedBossActorIds.ToHashSet();
        ulong bestMaxHp = 0;
        ulong bestCurrentHp = 0;
        foreach (var obj in objectTable)
        {
            if (obj is not IBattleNpc battleNpc ||
                obj.ObjectKind != ObjectKind.BattleNpc ||
                !obj.IsTargetable ||
                obj.IsDead ||
                !actorIdSet.Contains(battleNpc.NameId))
            {
                continue;
            }

            var maxHp = obj is ICharacter character ? character.MaxHp : 0UL;
            var currentHp = obj is ICharacter hpCharacter ? hpCharacter.CurrentHp : 0UL;

            if (maxHp < bestMaxHp)
                continue;

            if (maxHp == bestMaxHp && currentHp <= bestCurrentHp)
                continue;

            matchedActorId = battleNpc.NameId;
            bestMaxHp = maxHp;
            bestCurrentHp = currentHp;
        }

        return matchedActorId != 0;
    }

    private bool HasPresentPhaseBoss(IReadOnlyList<string> expectedBossNames)
    {
        if (expectedBossNames.Count == 0)
            return false;

        var normalizedExpectedNames = expectedBossNames
            .Select(NormalizePhaseBossName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedExpectedNames.Count == 0)
            return false;

        foreach (var obj in objectTable)
        {
            if (obj == null ||
                obj.ObjectKind != ObjectKind.BattleNpc ||
                obj.IsDead ||
                obj.BaseId == 0)
            {
                continue;
            }

            var objectName = NormalizePhaseBossName(obj.Name.TextValue);
            if (string.IsNullOrWhiteSpace(objectName))
                continue;

            if (normalizedExpectedNames.Any(expectedName => DoesPhaseBossNameMatch(expectedName, objectName)))
                return true;
        }

        return false;
    }

    private bool HasPresentPhaseBossActor(IReadOnlyList<uint> expectedBossActorIds)
    {
        if (expectedBossActorIds.Count == 0)
            return false;

        var actorIdSet = expectedBossActorIds.ToHashSet();
        foreach (var obj in objectTable)
        {
            if (obj is not IBattleNpc battleNpc ||
                obj.ObjectKind != ObjectKind.BattleNpc ||
                obj.IsDead ||
                !actorIdSet.Contains(battleNpc.NameId))
            {
                continue;
            }

            if (obj is ICharacter character &&
                character.CurrentHp <= 1 &&
                !obj.IsTargetable)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private uint GetPrimaryBossDataId()
    {
        uint bestDataId = 0;
        ulong bestMaxHp = 0;
        ulong bestCurrentHp = 0;

        foreach (var obj in objectTable)
        {
            if (obj == null ||
                obj.ObjectKind != ObjectKind.BattleNpc ||
                !obj.IsTargetable ||
                obj.IsDead ||
                obj is not IBattleNpc battleNpc ||
                obj is not ICharacter character ||
                character.CurrentHp == 0 ||
                character.MaxHp == 0)
            {
                continue;
            }

            var maxHp = character.MaxHp;
            var currentHp = character.CurrentHp;
            if (maxHp < bestMaxHp)
                continue;

            if (maxHp == bestMaxHp && currentHp <= bestCurrentHp)
                continue;

            bestDataId = battleNpc.NameId;
            bestMaxHp = maxHp;
            bestCurrentHp = currentHp;
        }

        return bestDataId;
    }

    private static bool DoesPhaseBossNameMatch(string expectedName, string objectName)
        => string.Equals(expectedName, objectName, StringComparison.OrdinalIgnoreCase) ||
           objectName.Contains(expectedName, StringComparison.OrdinalIgnoreCase) ||
           expectedName.Contains(objectName, StringComparison.OrdinalIgnoreCase);

    private static string NormalizePhaseBossName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var normalized = name.Trim().ToLowerInvariant();
        if (normalized.StartsWith("the ", StringComparison.Ordinal))
            normalized = normalized[4..];

        if (normalized.EndsWith("s", StringComparison.Ordinal) && normalized.Length > 1)
            normalized = normalized[..^1];

        return normalized;
    }

    private static bool HaveSameNormalizedBossNames(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var leftNames = left
            .Select(NormalizePhaseBossName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var rightNames = right
            .Select(NormalizePhaseBossName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return leftNames.SequenceEqual(rightNames, StringComparer.OrdinalIgnoreCase);
    }

    private PhaseTimelineSet? BuildPhaseTimelineSet(int encounterId, string specName, string encounterName)
    {
        var baseKey = TimelineDatabase.MakeKey(encounterId, specName);
        var matchingCustoms = plugin.Configuration.CustomTimelines
            .Where(entry => string.Equals(entry.Value.SpecName, specName, StringComparison.OrdinalIgnoreCase))
            .Where(entry => TimelineMatchesEncounter(entry.Key, entry.Value, encounterId, encounterName))
            .ToList();
        if (matchingCustoms.Count == 0)
            return null;

        var phaseTimelines = new Dictionary<int, PhaseTimelineCandidate>();
        PhaseTimelineCandidate? fullTimeline = null;
        var linkedPhaseOrdinals = BuildLinkedPhaseOrdinals(matchingCustoms);

        PhaseTimelineCandidate CreatePhaseTimelineCandidate(string key, AggregatedTimeline timeline, int phaseOrdinal)
            => new()
            {
                Key = key,
                Timeline = timeline,
                PhaseOrdinal = phaseOrdinal,
                ExpectedBossNames = GetExpectedPhaseBossNames(timeline.PhaseInfo ?? plugin.TimelineStore.GetTimeline(encounterId, specName)?.PhaseInfo, phaseOrdinal),
                ExpectedBossActorIds = GetExpectedPhaseBossActorIds(encounterId, phaseOrdinal),
            };

        foreach (var (key, timeline) in matchingCustoms)
        {
            if (linkedPhaseOrdinals.TryGetValue(key, out var linkedPhaseOrdinal))
            {
                phaseTimelines[linkedPhaseOrdinal] = CreatePhaseTimelineCandidate(key, timeline, linkedPhaseOrdinal);
                continue;
            }

            if (TryResolvePhaseOrdinal(key, timeline, encounterId, baseKey, out var phaseOrdinal))
            {
                phaseTimelines[phaseOrdinal] = CreatePhaseTimelineCandidate(key, timeline, phaseOrdinal);
                continue;
            }

            if (IsFullTimelineCandidate(key, timeline, encounterId, baseKey) &&
                (fullTimeline == null || string.Equals(key, baseKey, StringComparison.OrdinalIgnoreCase)))
            {
                fullTimeline = new PhaseTimelineCandidate
                {
                    Key = key,
                    Timeline = timeline,
                    PhaseOrdinal = 0,
                    ExpectedBossNames = [],
                    ExpectedBossActorIds = [],
                };
            }
        }

        var phaseInfo = plugin.TimelineStore.GetTimeline(encounterId, specName)?.PhaseInfo
                        ?? fullTimeline?.Timeline.PhaseInfo
                        ?? phaseTimelines.OrderBy(entry => entry.Key).Select(entry => entry.Value.Timeline.PhaseInfo).FirstOrDefault(info => info != null);
        var phaseMarkers = BuildEncounterPhaseMarkers(phaseInfo);
        if (phaseTimelines.Count == 0)
            return null;

        return new PhaseTimelineSet
        {
            EncounterId = encounterId,
            SpecName = specName,
            PhaseTimelines = phaseTimelines,
            PhaseMarkers = phaseMarkers,
            FullTimeline = fullTimeline,
        };
    }

    private Dictionary<string, int> BuildLinkedPhaseOrdinals(IReadOnlyList<KeyValuePair<string, AggregatedTimeline>> matchingCustoms)
    {
        var timelineKeys = matchingCustoms
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        var nextLinks = plugin.Configuration.TimelineNextLinks
            .Where(entry => timelineKeys.Contains(entry.Key) &&
                            timelineKeys.Contains(entry.Value) &&
                            !string.Equals(entry.Key, entry.Value, StringComparison.Ordinal))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        if (nextLinks.Count == 0)
            return [];

        var inboundCounts = timelineKeys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
        foreach (var targetKey in nextLinks.Values)
            inboundCounts[targetKey] = inboundCounts.GetValueOrDefault(targetKey) + 1;

        var rootKeys = nextLinks.Keys
            .Where(sourceKey => inboundCounts.GetValueOrDefault(sourceKey) == 0)
            .OrderBy(sourceKey => sourceKey, StringComparer.Ordinal)
            .ToList();
        if (rootKeys.Count == 0)
            return [];

        var linkedOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextPhaseOrdinal = 1;

        foreach (var rootKey in rootKeys)
        {
            var currentKey = rootKey;
            var visitedKeys = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrWhiteSpace(currentKey) &&
                   timelineKeys.Contains(currentKey) &&
                   visitedKeys.Add(currentKey) &&
                   !linkedOrdinals.ContainsKey(currentKey))
            {
                linkedOrdinals[currentKey] = nextPhaseOrdinal++;
                if (!nextLinks.TryGetValue(currentKey, out currentKey!))
                    break;
            }
        }

        return linkedOrdinals;
    }

    private static PhaseTimelineCandidate? ResolveInitialPhaseTimelineCandidate(PhaseTimelineSet phaseTimelineSet)
    {
        if (phaseTimelineSet.PhaseTimelines.TryGetValue(1, out var phaseOneTimeline))
            return phaseOneTimeline;

        return phaseTimelineSet.PhaseTimelines
            .OrderBy(entry => entry.Key)
            .Select(entry => entry.Value)
            .FirstOrDefault();
    }

    private PhaseTimelineCandidate? ResolveInitialTimelineCandidate(
        PhaseTimelineSet? phaseTimelineSet,
        int encounterId,
        string specName,
        string encounterName)
    {
        var initialPhaseTimeline = phaseTimelineSet != null
            ? ResolveInitialPhaseTimelineCandidate(phaseTimelineSet)
            : null;
        if (initialPhaseTimeline != null)
            return initialPhaseTimeline;

        var fullTimeline = plugin.Configuration.CustomTimelines
            .FirstOrDefault(entry =>
                string.Equals(entry.Value.SpecName, specName, StringComparison.OrdinalIgnoreCase) &&
                TimelineMatchesEncounter(entry.Key, entry.Value, encounterId, encounterName) &&
                IsFullTimelineCandidate(entry.Key, entry.Value, encounterId, TimelineDatabase.MakeKey(encounterId, specName)));
        if (string.IsNullOrEmpty(fullTimeline.Key))
            return null;

        return new PhaseTimelineCandidate
        {
            Key = fullTimeline.Key,
            Timeline = fullTimeline.Value,
            PhaseOrdinal = 0,
            ExpectedBossNames = [],
            ExpectedBossActorIds = [],
        };
    }

    private static bool TryGetPhaseOrdinalFromCustomKey(string key, string baseKey, out int phaseOrdinal)
    {
        phaseOrdinal = 0;
        if (!key.StartsWith(baseKey, StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = key[baseKey.Length..];
        if (!suffix.StartsWith("_p", StringComparison.OrdinalIgnoreCase))
            return false;

        return int.TryParse(suffix[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out phaseOrdinal) &&
               phaseOrdinal > 0;
    }

    private static bool TryResolvePhaseOrdinal(string key, AggregatedTimeline timeline, int encounterId, string baseKey, out int phaseOrdinal)
    {
        if (TryGetPhaseOrdinalFromEncounterId(timeline.EncounterId, encounterId, out phaseOrdinal))
            return true;

        return TryGetPhaseOrdinalFromCustomKey(key, baseKey, out phaseOrdinal);
    }

    private bool TryGetDebugNextPhaseCandidate(out PhaseTimelineCandidate timelineCandidate)
    {
        timelineCandidate = null!;
        if (activePhaseTimelineSet == null || activePhaseTimelineSet.PhaseTimelines.Count == 0)
            return false;

        var nextPhaseOrdinal = pendingPreviewPhaseOrdinal > 0
            ? pendingPreviewPhaseOrdinal
            : activePhaseOrdinal > 0 ? activePhaseOrdinal + 1 : 1;
        return activePhaseTimelineSet.PhaseTimelines.TryGetValue(nextPhaseOrdinal, out timelineCandidate!);
    }

    private static bool TimelineMatchesEncounter(string key, AggregatedTimeline timeline, int encounterId, string encounterName)
    {
        if (timeline.EncounterId > 0 &&
            TryGetBaseEncounterIdFromPhaseEncounterId(timeline.EncounterId, out var baseEncounterId) &&
            baseEncounterId == encounterId)
        {
            return true;
        }

        return timeline.EncounterId == 0 &&
               !string.IsNullOrWhiteSpace(encounterName) &&
               NamesMatch(encounterName, GetTimelineBaseEncounterName(timeline));
    }

    private static bool IsFullTimelineCandidate(string key, AggregatedTimeline timeline, int encounterId, string baseKey)
    {
        if (TryGetPhaseOrdinalFromEncounterId(timeline.EncounterId, encounterId, out _))
            return false;

        if (TryGetPhaseOrdinalFromCustomKey(key, baseKey, out _))
            return false;

        return timeline.EncounterId == 0 || timeline.EncounterId == encounterId;
    }

    private static string GetTimelineBaseEncounterName(AggregatedTimeline timeline)
    {
        if (!string.IsNullOrWhiteSpace(timeline.PhaseInfo?.EncounterName))
            return timeline.PhaseInfo.EncounterName.Trim();

        return timeline.EncounterName;
    }

    private static string ChooseCanonicalEncounterName(string existingName, string candidateName)
    {
        if (string.IsNullOrWhiteSpace(existingName))
            return candidateName;

        if (string.IsNullOrWhiteSpace(candidateName) ||
            string.Equals(existingName, candidateName, StringComparison.OrdinalIgnoreCase))
        {
            return existingName;
        }

        if (existingName.Contains(candidateName, StringComparison.OrdinalIgnoreCase))
            return candidateName;

        if (candidateName.Contains(existingName, StringComparison.OrdinalIgnoreCase))
            return existingName;

        return existingName.Length <= candidateName.Length ? existingName : candidateName;
    }

    private static bool TryGetBaseEncounterIdFromPhaseEncounterId(int encounterId, out int baseEncounterId)
    {
        foreach (var (legacyBaseEncounterId, legacyPhaseIds) in LegacyGeneratedEncounterPhaseIds)
        {
            if (legacyPhaseIds.Values.Contains(encounterId))
            {
                baseEncounterId = legacyBaseEncounterId;
                return true;
            }
        }

        if (encounterId >= SyntheticPhaseEncounterIdBase)
        {
            baseEncounterId = (encounterId - SyntheticPhaseEncounterIdBase) / 100;
            return baseEncounterId > 0;
        }

        baseEncounterId = encounterId;
        return encounterId > 0;
    }

    private static bool TryGetPhaseOrdinalFromEncounterId(int encounterId, int baseEncounterId, out int phaseOrdinal)
    {
        phaseOrdinal = 0;

        if (baseEncounterId <= 0 || encounterId <= 0)
            return false;

        if (LegacyGeneratedEncounterPhaseIds.TryGetValue(baseEncounterId, out var legacyPhaseIds))
        {
            foreach (var (ordinal, legacyEncounterId) in legacyPhaseIds)
            {
                if (legacyEncounterId == encounterId)
                {
                    phaseOrdinal = ordinal;
                    return true;
                }
            }
        }

        if (encounterId < SyntheticPhaseEncounterIdBase)
            return false;

        var rawValue = encounterId - SyntheticPhaseEncounterIdBase;
        var candidateBaseEncounterId = rawValue / 100;
        var candidatePhaseOrdinal = rawValue % 100;
        if (candidateBaseEncounterId != baseEncounterId || candidatePhaseOrdinal <= 1)
            return false;

        phaseOrdinal = candidatePhaseOrdinal;
        return true;
    }

    private static List<EncounterPhaseMarker> BuildEncounterPhaseMarkers(FightPhaseInfo? phaseInfo)
    {
        if (phaseInfo == null || phaseInfo.PhaseTransitions.Count == 0)
            return [];

        var metadataById = phaseInfo.PhaseMetadata.ToDictionary(phase => phase.Id);
        var markers = new List<EncounterPhaseMarker>();

        foreach (var transition in phaseInfo.PhaseTransitions.OrderBy(transition => transition.StartTime))
        {
            if (metadataById.TryGetValue(transition.Id, out var metadata) && metadata.IsIntermission)
                continue;

            markers.Add(new EncounterPhaseMarker
            {
                Ordinal = markers.Count + 1,
                StartSec = Math.Max(0.0, (transition.StartTime - phaseInfo.FightStartTime) / 1000.0),
                Name = metadata?.Name ?? string.Empty,
            });
        }

        return markers;
    }

    private static List<string> GetExpectedPhaseBossNames(FightPhaseInfo? phaseInfo, int phaseOrdinal)
    {
        var marker = BuildEncounterPhaseMarkers(phaseInfo)
            .FirstOrDefault(candidate => candidate.Ordinal == phaseOrdinal);
        if (marker == null || string.IsNullOrWhiteSpace(marker.Name))
            return [];

        var normalized = marker.Name.Trim();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            normalized,
        };

        if (normalized.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            names.Add(normalized[..^1]);

        if (normalized.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            names.Add(normalized[4..]);

        return names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
    }

    private static List<uint> GetExpectedPhaseBossActorIds(int encounterId, int phaseOrdinal)
    {
        if (!KnownEncounterPhaseActorRules.TryGetValue(encounterId, out var rules))
            return [];

        return rules
            .Where(rule => rule.PhaseOrdinal == phaseOrdinal)
            .SelectMany(rule => rule.ActorIds)
            .Distinct()
            .ToList();
    }

    private static bool HaveSamePhaseActorIds(IReadOnlyList<uint> left, IReadOnlyList<uint> right)
    {
        if (left.Count == 0 && right.Count == 0)
            return true;

        var leftIds = left.Distinct().OrderBy(id => id).ToArray();
        var rightIds = right.Distinct().OrderBy(id => id).ToArray();
        return leftIds.SequenceEqual(rightIds);
    }

    private void OnDutyReset(Dalamud.Game.DutyState.IDutyStateEventArgs _)
    {
        if (activePhaseTimelineSet == null)
            return;

        var initialCandidate = ResolveInitialPhaseTimelineCandidate(activePhaseTimelineSet);
        if (initialCandidate == null)
            return;

        plugin.OverlayWindow.PrepareCombatPreview(initialCandidate.Timeline, initialCandidate.Key);
        activePhaseOrdinal = initialCandidate.PhaseOrdinal;
        activePhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossSeenUtc = default;
        pendingExpectedPhaseBossDataId = 0;
        pendingExpectedPhaseBossSeenUtc = default;
    }

    // ── Manual preview (fallback for failed auto-detection) ─────────────

    /// <summary>
    /// Attempts to find and load a custom timeline for the player's current zone and job,
    /// bypassing the availableEncounterIds gate (so it works even when FFLogs IDs are wrong).
    /// Matches by CFC/zone name against timeline.EncounterName.
    /// Returns <c>true</c> if a unique match was found and the live overlay was loaded.
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
        string BuildGroupKey(int encounterId, string encounterName)
            => encounterId > 0
                ? $"id:{encounterId}"
                : $"name:{encounterName.Trim().ToLowerInvariant()}";

        List<IGrouping<string, ManualPreviewCandidate>> BuildCandidateGroups(bool requireMatchingSpec)
        {
            return customs
                .Where(entry => !requireMatchingSpec ||
                                specName == null ||
                                string.Equals(entry.Value.SpecName, specName, StringComparison.OrdinalIgnoreCase))
                .Select(entry =>
                {
                    TryGetBaseEncounterIdFromPhaseEncounterId(entry.Value.EncounterId, out var baseEncounterId);
                    var baseEncounterName = GetTimelineBaseEncounterName(entry.Value);
                    return new ManualPreviewCandidate
                    {
                        Key = entry.Key,
                        Timeline = entry.Value,
                        BaseEncounterId = baseEncounterId,
                        BaseEncounterName = baseEncounterName,
                    };
                })
                .Where(entry => zoneName == null || NamesMatch(zoneName, entry.BaseEncounterName))
                .GroupBy(entry => BuildGroupKey(entry.BaseEncounterId, entry.BaseEncounterName), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var candidates = BuildCandidateGroups(requireMatchingSpec: true);
        if (candidates.Count == 0 && zoneName != null)
            candidates = BuildCandidateGroups(requireMatchingSpec: false);

        if (candidates.Count != 1)
            return false;

        var selectedGroup = candidates[0].ToList();
        var representative = selectedGroup[0];
        var timeline = representative.Timeline;
        var resolvedSpecName = specName ?? representative.Timeline.SpecName;
        var resolvedEncounterId = (int)representative.BaseEncounterId;
        var resolvedEncounterName = (string)representative.BaseEncounterName;
        var phaseTimelineSet = resolvedEncounterId > 0
            ? BuildPhaseTimelineSet(resolvedEncounterId, resolvedSpecName, resolvedEncounterName)
            : null;
        var timelineCandidate = resolvedEncounterId > 0
            ? ResolveInitialTimelineCandidate(phaseTimelineSet, resolvedEncounterId, resolvedSpecName, resolvedEncounterName)
            : null;
        if (timelineCandidate != null)
        {
            ApplyLoadedEncounterState(resolvedEncounterId, resolvedSpecName, phaseTimelineSet, timelineCandidate);
            log.Info("EncounterTracker: manual overlay â†’ [{0}/{1}] via phase-set resolution.", timelineCandidate.Timeline.EncounterName, timelineCandidate.Timeline.SpecName);
            return true;
        }

        if (selectedGroup.Count != 1)
            return false;

        return TryLoadEncounterByTimelineKey(representative.Key);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the player's current job name in title-case (e.g. "Paladin", "Dark Knight"),
    /// matching FFLogs spec name convention. Returns null if player not yet loaded.
    /// Public wrapper used by QuickPickWindow for relevance sorting.
    /// </summary>
    public string? GetCurrentSpecNamePublic() => GetCurrentSpecName();

    public IReadOnlyList<TrackerDebugEncounterOption> GetTrackerDebugEncounterOptions()
    {
        var options = plugin.Configuration.CustomTimelines
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value.SpecName))
            .Select(entry =>
            {
                TryGetBaseEncounterIdFromPhaseEncounterId(entry.Value.EncounterId, out var baseEncounterId);
                var encounterName = GetTimelineBaseEncounterName(entry.Value);
                return new
                {
                    entry.Key,
                    Timeline = entry.Value,
                    BaseEncounterId = baseEncounterId,
                    EncounterName = encounterName,
                };
            })
            .Where(entry => entry.BaseEncounterId > 0 && !string.IsNullOrWhiteSpace(entry.EncounterName))
            .GroupBy(entry => (entry.BaseEncounterId, entry.Timeline.SpecName), entry => entry)
            .Select(group =>
            {
                var representativeName = group
                    .Select(entry => entry.EncounterName)
                    .Aggregate(string.Empty, ChooseCanonicalEncounterName);
                var resolvedPhaseSet = BuildPhaseTimelineSet(group.Key.BaseEncounterId, group.Key.SpecName, representativeName);
                var baseKey = TimelineDatabase.MakeKey(group.Key.BaseEncounterId, group.Key.SpecName);
                return new TrackerDebugEncounterOption
                {
                    EncounterId = group.Key.BaseEncounterId,
                    EncounterName = representativeName,
                    SpecName = group.Key.SpecName,
                    PhaseCount = resolvedPhaseSet?.PhaseTimelines.Count ?? 0,
                    HasFullTimeline = resolvedPhaseSet?.FullTimeline != null ||
                                      group.Any(entry => IsFullTimelineCandidate(entry.Key, entry.Timeline, group.Key.BaseEncounterId, baseKey)),
                };
            })
            .OrderBy(option => option.EncounterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.SpecName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return options;
    }

    public TrackerDebugState GetTrackerDebugState()
    {
        var phaseCount = activePhaseTimelineSet?.PhaseTimelines.Count ?? 0;
        var nextPhaseOrdinal = activePhaseOrdinal > 0 ? activePhaseOrdinal + 1 : 1;
        return new TrackerDebugState
        {
            HasActiveEncounter = activePhaseTimelineSet != null,
            EncounterId = loadedEncounterId,
            EncounterName = loadedEncounterId != 0
                ? encounterIdToName.GetValueOrDefault(loadedEncounterId, string.Empty)
                : string.Empty,
            SpecName = loadedSpecName,
            ActivePhaseOrdinal = activePhaseOrdinal,
            AvailablePhaseCount = phaseCount,
            PendingPreviewPhaseOrdinal = pendingPreviewPhaseOrdinal,
            HasNextPhase = activePhaseTimelineSet != null &&
                           activePhaseTimelineSet.PhaseTimelines.ContainsKey(nextPhaseOrdinal),
        };
    }

    public bool TryLoadEncounterByTimelineKey(string key)
    {
        if (!plugin.Configuration.CustomTimelines.TryGetValue(key, out var timeline))
            return false;

        var specName = timeline.SpecName;
        var encounterName = GetTimelineBaseEncounterName(timeline);
        TryGetBaseEncounterIdFromPhaseEncounterId(timeline.EncounterId, out var encounterId);

        if (encounterId > 0)
        {
            if (string.IsNullOrWhiteSpace(encounterName))
                encounterName = encounterIdToName.GetValueOrDefault(encounterId, timeline.EncounterName);

            var phaseTimelineSet = BuildPhaseTimelineSet(encounterId, specName, encounterName);
            var timelineCandidate = ResolveInitialTimelineCandidate(phaseTimelineSet, encounterId, specName, encounterName);
            if (timelineCandidate != null)
            {
                ApplyLoadedEncounterState(encounterId, specName, phaseTimelineSet, timelineCandidate);
                log.Info("EncounterTracker: manual encounter load → [{0}/{1}] via key {2}.", timelineCandidate.Timeline.EncounterName, timelineCandidate.Timeline.SpecName, key);
                return true;
            }
        }

        plugin.OverlayWindow.PrepareCombatPreview(timeline, key);
        loadedEncounterId = encounterId > 0 ? encounterId : timeline.EncounterId;
        loadedSpecName = specName;
        activePhaseTimelineSet = null;
        activePhaseOrdinal = 0;
        activePhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossSeenUtc = default;
        pendingExpectedPhaseBossDataId = 0;
        pendingExpectedPhaseBossSeenUtc = default;
        pendingPreviewPhaseOrdinal = 0;
        phaseTransitionClearedTimeline = false;
        log.Info("EncounterTracker: manual fallback load → [{0}/{1}] via key {2}.", timeline.EncounterName, timeline.SpecName, key);
        return true;
    }

    public bool DebugLoadEncounter(int encounterId, string specName, out string message)
    {
        var encounterName = encounterIdToName.GetValueOrDefault(encounterId, string.Empty);
        if (string.IsNullOrWhiteSpace(encounterName))
        {
            encounterName = plugin.Configuration.CustomTimelines.Values
                .Where(timeline => string.Equals(timeline.SpecName, specName, StringComparison.OrdinalIgnoreCase))
                .Where(timeline => TryGetBaseEncounterIdFromPhaseEncounterId(timeline.EncounterId, out var baseEncounterId) &&
                                   baseEncounterId == encounterId)
                .Select(GetTimelineBaseEncounterName)
                .Aggregate(string.Empty, ChooseCanonicalEncounterName);
        }

        if (string.IsNullOrWhiteSpace(encounterName))
        {
            message = $"No saved custom timeline is mapped to encounter {encounterId} / {specName}.";
            return false;
        }

        var phaseTimelineSet = BuildPhaseTimelineSet(encounterId, specName, encounterName);
        var timelineCandidate = ResolveInitialTimelineCandidate(phaseTimelineSet, encounterId, specName, encounterName);
        if (timelineCandidate == null)
        {
            message = $"No initial timeline could be resolved for {encounterName} / {specName}.";
            return false;
        }

        ApplyLoadedEncounterState(encounterId, specName, phaseTimelineSet, timelineCandidate);
        message = $"Loaded debug encounter {encounterName} / {specName} at {(timelineCandidate.PhaseOrdinal > 0 ? $"phase {timelineCandidate.PhaseOrdinal}" : "the full timeline")}.";
        return true;
    }

    public bool DebugPreviewNextPhase(out string message)
    {
        if (!TryGetDebugNextPhaseCandidate(out var timelineCandidate))
        {
            message = "No next phase is available for the current debug encounter.";
            return false;
        }

        plugin.OverlayWindow.ClearForPhaseTransition();
        plugin.OverlayWindow.PrepareCombatPreview(timelineCandidate.Timeline, timelineCandidate.Key);
        pendingPreviewPhaseOrdinal = timelineCandidate.PhaseOrdinal;
        pendingExpectedPhaseBossDataId = 0;
        pendingExpectedPhaseBossSeenUtc = default;
        phaseTransitionClearedTimeline = true;
        message = $"Previewing phase {timelineCandidate.PhaseOrdinal} for debug validation.";
        return true;
    }

    public bool DebugCommitNextPhase(out string message)
    {
        if (!TryGetDebugNextPhaseCandidate(out var timelineCandidate))
        {
            message = "No next phase is available to commit.";
            return false;
        }

        plugin.OverlayWindow.SwitchCombatTimelinePaused(timelineCandidate.Timeline, timelineCandidate.Key);
        activePhaseOrdinal = timelineCandidate.PhaseOrdinal;
        activePhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossSeenUtc = default;
        pendingExpectedPhaseBossDataId = 0;
        pendingExpectedPhaseBossSeenUtc = default;
        pendingPreviewPhaseOrdinal = 0;
        phaseTransitionClearedTimeline = false;
        message = $"Committed debug swap to phase {timelineCandidate.PhaseOrdinal} in paused state.";
        return true;
    }

    public bool DebugResetEncounter(out string message)
    {
        if (activePhaseTimelineSet == null)
        {
            message = "No debug encounter is currently loaded.";
            return false;
        }

        var initialCandidate = ResolveInitialPhaseTimelineCandidate(activePhaseTimelineSet) ?? activePhaseTimelineSet.FullTimeline;
        if (initialCandidate == null)
        {
            message = "The loaded debug encounter has no initial timeline to reset to.";
            return false;
        }

        plugin.OverlayWindow.PrepareCombatPreview(initialCandidate.Timeline, initialCandidate.Key);
        activePhaseOrdinal = initialCandidate.PhaseOrdinal;
        activePhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossSeenUtc = default;
        pendingExpectedPhaseBossDataId = 0;
        pendingExpectedPhaseBossSeenUtc = default;
        pendingPreviewPhaseOrdinal = 0;
        phaseTransitionClearedTimeline = false;
        message = $"Reset debug encounter to {(initialCandidate.PhaseOrdinal > 0 ? $"phase {initialCandidate.PhaseOrdinal}" : "the initial timeline")}.";
        return true;
    }

    public void DebugClearEncounter()
    {
        loadedEncounterId = 0;
        loadedSpecName = string.Empty;
        activePhaseTimelineSet = null;
        activePhaseOrdinal = 0;
        activePhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossSeenUtc = default;
        pendingExpectedPhaseBossDataId = 0;
        pendingExpectedPhaseBossSeenUtc = default;
        pendingPreviewPhaseOrdinal = 0;
        phaseTransitionClearedTimeline = false;
        plugin.OverlayWindow.ClearForZoneChange();
    }

    private void ApplyLoadedEncounterState(
        int encounterId,
        string specName,
        PhaseTimelineSet? phaseTimelineSet,
        PhaseTimelineCandidate timelineCandidate)
    {
        plugin.OverlayWindow.PrepareCombatPreview(timelineCandidate.Timeline, timelineCandidate.Key);
        plugin.OverlayWindow.IsOpen = true;
        loadedEncounterId = encounterId;
        loadedSpecName = specName;
        activePhaseTimelineSet = phaseTimelineSet;
        activePhaseOrdinal = timelineCandidate.PhaseOrdinal;
        activePhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossDataId = 0;
        pendingPhasePrimaryBossSeenUtc = default;
        pendingExpectedPhaseBossDataId = 0;
        pendingExpectedPhaseBossSeenUtc = default;
        pendingPreviewPhaseOrdinal = 0;
        phaseTransitionClearedTimeline = false;
    }

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
        dutyState.DutyWiped -= OnDutyReset;
        dutyState.DutyCompleted -= OnDutyReset;
        framework.Update -= OnUpdate;
    }
}
