using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ATKTip.Data;

namespace ATKTip.Windows;

public sealed class MainWindow : Window
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> ConflictIgnoredRequirementNamesBySpec =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Astrologian"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "The Balance Ready",
                "The Arrow Ready",
                "The Spire Ready",
                "Lord of Crowns Ready",
                "The Spear Ready",
                "The Ewer Ready",
                "The Bole Ready",
                "Lady of Crowns Ready",
                "Horoscope Ready",
            },
            ["Bard"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Hawk's Eye",
                "Refulgent Arrow Ready",
                "Coda",
            },
            ["Dancer"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Reverse Cascade Ready",
                "Fountainfall Ready",
                "Last Dance Ready",
                "Technical Finish Ready",
            },
            ["Dark Knight"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Blood",
            },
            ["Dragoon"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Draconian Fire",
            },
            ["Gunbreaker"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Cartridge",
            },
            ["Machinist"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Heat",
            },
            ["Monk"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Chakra",
            },
            ["Ninja"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Ninki",
                "Phantom Kamaitachi Ready",
            },
            ["Pictomancer"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Palette",
            },
            ["Reaper"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Soul",
            },
            ["Red Mage"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "BlackMana",
                "WhiteMana",
                "Verfire Ready",
                "Verstone Ready",
                "Grand Impact Ready",
            },
            ["Scholar"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Aetherflow",
            },
            ["Sage"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Eukrasia",
                "Addersgall",
            },
            ["Samurai"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Kenki",
            },
            ["Viper"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "RattlingCoil",
                "SerpentOfferings",
            },
            ["Warrior"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Beast",
            },
            ["White Mage"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Divine Grace",
            },
        };
    private static readonly IReadOnlyDictionary<string, HashSet<string>> ConflictIgnoredCooldownNamesBySpec =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Machinist"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Reassemble",
                "Double Check",
                "Checkmate",
                "Tactician",
            },
            ["Ninja"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Ten",
                "Chi",
                "Jin",
            },
            ["Red Mage"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Contre Sixte",
                "Acceleration",
                "Swiftcast",
            },
            ["Samurai"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Meikyo Shisui",
                "Hissatsu: Senei",
                "Hissatsu: Gyoten",
            },
            ["Scholar"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Energy Drain",
                "Recitation",
            },
            ["Astrologian"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Essential Dignity",
                "Celestial Intersection",
                "Lightspeed",
                "Swiftcast",
            },
            ["Black Mage"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Transpose",
                "Manafont",
                "Swiftcast",
                "Retrace",
            },
            ["Bard"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Heartbreak Shot",
                "Troubadour",
            },
            ["Gunbreaker"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Aurora",
            },
            ["Warrior"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Onslaught",
                "Infuriate",
            },
            ["Pictomancer"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Striking Muse",
                "Swiftcast",
                "Tempera Coat",
            },
            ["White Mage"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Divine Benison",
                "Tetragrammaton",
                "Swiftcast",
            },
        };
    private static readonly IReadOnlySet<string> ConflictCastLockIgnoredSpecs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Astrologian",
            "Black Mage",
            "Pictomancer",
            "Red Mage",
            "Samurai",
            "Scholar",
            "Sage",
            "Summoner",
            "White Mage",
        };

    private readonly Plugin plugin;
    private readonly IPluginLog log;

    /// <summary>Max concurrent FFLogs API requests when fetching parse events.</summary>
    private const int MaxConcurrency = 10;
    private const int SyntheticPhaseEncounterIdBase = 1_000_000;
    private const string DebugArtifactsDirectoryName = "debug";
    private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> LegacyGeneratedEncounterPhaseIds =
        new Dictionary<int, IReadOnlyDictionary<int, int>>
        {
            [104] = new Dictionary<int, int>
            {
                [2] = 105,
            },
        };

    // Zone/encounter/spec selection
    private List<Zone> zones = [];
    private List<GameClass> classes = [];
    private List<string> allSpecNames = [];
    private int selectedZone;
    private int selectedEncounter;
    private int selectedSpec;
    private bool selectorsLoaded;
    private bool isFetchingSelectors;
    private string? selectorsError;

    // Current timeline
    private AggregatedTimeline? currentTimeline;
    private Dictionary<int, bool> skillVisibility = [];
    private bool showEmbeddedTimelinePreview;
    private readonly Dictionary<int, uint> iconIdCache = [];
    private readonly Dictionary<string, int> importedBossAbilityIdCache = new(StringComparer.OrdinalIgnoreCase);

    // Update state
    private bool isUpdating;
    private string updateStatus = string.Empty;
    private float updateProgress;
    private CancellationTokenSource? updateCts;

    // Skill filter expanded state — tracks which ability IDs have their threshold slider open
    private readonly HashSet<int> expandedSkillNodes = [];
    private string cacheClearNotice = string.Empty;

    // Custom timeline editor state
    private string? selectedCustomKey;
    private AggregatedTimeline? editingTimeline;
    private string editEncounterName = string.Empty;
    private string editSpecName = string.Empty;
    private int    editEncounterIdx  = 0;
    private int    editSpecIdx       = 0;
    private float editDurationSec;
    private int editingEntryIndex = -1;   // index into player Entries; -1 = none
    private bool editingEntryIsBoss;      // true when the popup is editing a boss entry
    private float editEntryTime;
    private float editEntryEndTime;       // boss only
    private string editEntryName = string.Empty;
    private float editEntryFrequency;
    private int   editEntryAbilityIdx = 0;
    private List<string> editEntryAbilityOptions = [];
    private bool customEditorDirty;
    private readonly Dictionary<string, string> lastAutoTimelineDebugReportsBySpec = new(StringComparer.OrdinalIgnoreCase);
    private string lastConflictDebugReport = string.Empty;
    private string debugStatus = string.Empty;
    private bool debugStatusIsError;
    private DateTime debugStatusUntil = DateTime.MinValue;

    // Group management state
    private string newGroupNameBuf = string.Empty;

    // Conflict detection cache (rebuilt each frame from editingTimeline)
    private readonly HashSet<int>            conflictedPlayerIndices = [];
    private readonly Dictionary<int, string> conflictReasons         = [];

    // Export/import status banner
    private string  eiStatus     = string.Empty;
    private bool    eiIsError;
    private DateTime eiStatusUntil = DateTime.MinValue;

    // Report importer state (right panel "From FFLogs Report" section)
    private string  riUrl            = string.Empty;
    private string  riReportCode     = string.Empty;
    private string  riStatus         = string.Empty;
    private bool    riStatusIsError;
    private List<ReportFight>  riFlights        = [];
    private List<ReportPlayer> riPlayers        = [];
    private Dictionary<int, (string name, string icon)> riAbilityLookup = [];
    private int     riSelectedFight   = -1;   // index into riFlights
    private int     riSelectedPlayer  = -1;   // index into riPlayers
    private int     riSelectedPhase = 0;
    private int     riBlankSelectedZone = -1;
    private int     riBlankSelectedEncounter = -1;
    private int     riBlankSelectedPhase = 0;
    private int     riBlankSelectedSpec = -1;
    private int     riAutoSelectedZone = -1;
    private int     riAutoSelectedEncounter = -1;
    private int     riAutoSelectedPhase = 0;
    private int     riAutoSelectedSpec = -1;
    private string  riAutoPhaseStartSelectionKey = string.Empty;
    private List<int> riAutoPhaseStartBossIndices = [];
    private bool    riFetching;
    private bool    riImporting;
    private CancellationTokenSource? riCts;

    // Config tab state
    private string cfgClientId     = string.Empty;
    private string cfgClientSecret = string.Empty;
    private bool   cfgInitialized;
    private bool   focusConfigTab;

    // Hidden easter-egg: click "Config" tab 7 times rapidly to toggle auto-execute
    private int               secretClickCount;
    private DateTime          secretLastClick    = DateTime.MinValue;
    private string            secretBanner       = string.Empty;
    private DateTime          secretBannerUntil  = DateTime.MinValue;
    private IDtrBarEntry?     autoExecDtrEntry;
    private bool              pendingDeferredConfigSave;
    private DateTime          pendingDeferredConfigSaveAt = DateTime.MinValue;
    private bool              pendingDeferredUiSettingsSave;
    private DateTime          pendingDeferredUiSettingsSaveAt = DateTime.MinValue;
    private static readonly TimeSpan DeferredConfigSaveDelay = TimeSpan.FromMilliseconds(350);
    private bool              customTimelineListCacheDirty = true;
    private readonly List<string> cachedCustomTimelineKeys = [];
    private readonly Dictionary<string, int> cachedCustomTimelineGlobalIndexByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> cachedCustomTimelineGroupedKeysByGroup = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> cachedCustomTimelineUngroupedKeys = [];
    private AggregatedTimeline? cachedConflictTimeline;
    private bool                cachedConflictDataDirty = true;
    private AggregatedTimeline? cachedMergedRowsTimeline;
    private bool                cachedMergedRowsDirty = true;
    private readonly List<(bool IsBoss, int Idx, double Time)> cachedMergedRows = [];
    private sealed class TimelineAbilityRowCache
    {
        public int AbilityId { get; init; }
        public string AbilityName { get; init; } = string.Empty;
        public readonly List<TimelineEntry> Entries = [];
    }
    private AggregatedTimeline? cachedTimelineRenderTimeline;
    private int cachedTimelineRenderRevision = -1;
    private int currentTimelineRenderRevision;
    private readonly List<(int AbilityId, string AbilityName)> cachedTimelineUniqueAbilities = [];
    private readonly HashSet<int> cachedTimelineGcdIds = [];
    private readonly HashSet<int> cachedTimelineOgcdIds = [];
    private readonly List<TimelineEntry> cachedTimelineDisplayEntries = [];
    private readonly List<TimelineEntry> cachedTimelineVisibleEntries = [];
    private readonly List<TimelineAbilityRowCache> cachedTimelineAbilityRows = [];
    private string cachedAutoTimelineSkillFilterKey = string.Empty;
    private int cachedAutoTimelineSkillFilterSourceCount = -1;
    private int cachedAutoTimelineSkillFilterParseCount = -1;
    private bool autoTimelineSkillFilterCacheDirty = true;
    private readonly List<(int AbilityId, string AbilityName)> cachedAutoTimelineSkillFilterOptions = [];

    /// <summary>Opens the main window and auto-selects the Config tab on the next frame.</summary>
    public void FocusConfigTab() { IsOpen = true; focusConfigTab = true; }

    /// <summary>Creates or removes the DTR bar entry to reflect auto-execute state.</summary>
    public void ApplyAutoExecDtr(bool enabled)
    {
        if (enabled)
        {
            autoExecDtrEntry ??= plugin.DtrBar.Get("ATKTip");
            autoExecDtrEntry.Text  = new SeString(new TextPayload("ATK AUTO"));
            autoExecDtrEntry.Shown = true;
        }
        else
        {
            autoExecDtrEntry?.Remove();
            autoExecDtrEntry = null;
        }
    }

    public MainWindow(Plugin plugin, IPluginLog log)
        : base("ATKTip - Timeline##ATKTipMain")
    {
        this.plugin = plugin;
        this.log = log;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900, 600),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size          = new Vector2(1100, 700);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen()
    {
        // Load latest credentials into the Config tab fields
        cfgClientId     = plugin.Configuration.FFLogsClientId;
        cfgClientSecret = plugin.Configuration.FFLogsClientSecret;
        cfgInitialized  = true;

        // Auto-populate dropdowns on first open if credentials are set
        if (!selectorsLoaded && !isFetchingSelectors &&
            !string.IsNullOrWhiteSpace(plugin.Configuration.FFLogsClientId))
        {
            selectorsError = null;
            isFetchingSelectors = true;
            Task.Run(FetchSelectorsAsync);
        }
    }

    public override void Draw()
    {
        ProcessDeferredConfigSave();
        ProcessDeferredUiSettingsSave();

        if (!ImGui.BeginTabBar("##MainTabs"))
            return;

        if (ImGui.BeginTabItem("Encounter Timeline"))
        {
            DrawTimelineTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Custom Timelines"))
        {
            DrawCustomTimelinesTab();
            ImGui.EndTabItem();
        }

        var configFlags = focusConfigTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        focusConfigTab = false;
        var configTabOpen    = ImGui.BeginTabItem("Config", configFlags);
        var configTabClicked = ImGui.IsItemClicked();   // captured immediately — last item is the tab label
        if (configTabOpen)
        {
            DrawConfigTab();
            ImGui.EndTabItem();
        }

        // Hidden easter-egg: click "Config" tab label 7 times rapidly to toggle auto-execute
        if (configTabClicked)
        {
            var now = DateTime.UtcNow;
            if ((now - secretLastClick).TotalSeconds <= 2.5)
                secretClickCount++;
            else
                secretClickCount = 1;
            secretLastClick = now;

            if (secretClickCount >= 7)
            {
                secretClickCount = 0;
                plugin.OverlayWindow.AutoExecuteEnabled = true;
                ApplyAutoExecDtr(true);
                secretBanner      = "AUTO enabled";
                secretBannerUntil = DateTime.UtcNow.AddSeconds(4);
            }
        }

        ImGui.EndTabBar();

        // Secret banner — shown briefly after the toggle
        if (!string.IsNullOrEmpty(secretBanner) && DateTime.UtcNow < secretBannerUntil)
        {
            var col = new Vector4(0.2f, 0.9f, 0.3f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Text, col);
            ImGui.TextUnformatted(secretBanner);
            ImGui.PopStyleColor();
        }
    }

    public override void PreDraw()
    {
        if (plugin.OverlayWindow.IsEmbeddedPreviewScrubbing)
            Flags |= ImGuiWindowFlags.NoMove;
        else
            Flags &= ~ImGuiWindowFlags.NoMove;
    }

    private void RequestDeferredConfigSave()
    {
        pendingDeferredConfigSave = true;
        pendingDeferredConfigSaveAt = DateTime.UtcNow + DeferredConfigSaveDelay;
    }

    private void RequestDeferredUiSettingsSave()
    {
        pendingDeferredUiSettingsSave = true;
        pendingDeferredUiSettingsSaveAt = DateTime.UtcNow + DeferredConfigSaveDelay;
    }

    private void ProcessDeferredConfigSave()
    {
        if (!pendingDeferredConfigSave || DateTime.UtcNow < pendingDeferredConfigSaveAt)
            return;

        if (ImGui.IsAnyItemActive() ||
            ImGui.IsMouseDown(ImGuiMouseButton.Left) ||
            ImGui.IsMouseDown(ImGuiMouseButton.Right) ||
            ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            pendingDeferredConfigSaveAt = DateTime.UtcNow + DeferredConfigSaveDelay;
            return;
        }

        plugin.SaveTimelineUserState();
        pendingDeferredConfigSave = false;
    }

    private void ProcessDeferredUiSettingsSave()
    {
        if (!pendingDeferredUiSettingsSave || DateTime.UtcNow < pendingDeferredUiSettingsSaveAt)
            return;

        if (ImGui.IsAnyItemActive() ||
            ImGui.IsMouseDown(ImGuiMouseButton.Left) ||
            ImGui.IsMouseDown(ImGuiMouseButton.Right) ||
            ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            pendingDeferredUiSettingsSaveAt = DateTime.UtcNow + DeferredConfigSaveDelay;
            return;
        }

        plugin.SaveUiSettings();
        pendingDeferredUiSettingsSave = false;
    }

    private void InvalidateCustomTimelineListCache()
    {
        customTimelineListCacheDirty = true;
    }

    private void InvalidateEncounterTimelineCaches()
    {
        unchecked
        {
            currentTimelineRenderRevision++;
        }
    }

    private void InvalidateCustomEditorCaches()
    {
        cachedConflictDataDirty = true;
        cachedMergedRowsDirty = true;
    }

    private void MarkCustomEditorModified(bool invalidateListCache = false)
    {
        customEditorDirty = true;
        InvalidateCustomEditorCaches();
        if (invalidateListCache)
            InvalidateCustomTimelineListCache();
    }

    private void ClearCustomEditorCaches()
    {
        cachedConflictTimeline = null;
        cachedMergedRowsTimeline = null;
        cachedConflictDataDirty = true;
        cachedMergedRowsDirty = true;
        cachedMergedRows.Clear();
    }

    private void EnsureCustomTimelineListCache()
    {
        if (!customTimelineListCacheDirty)
            return;

        var cfg = plugin.Configuration;
        var customs = cfg.CustomTimelines;

        cachedCustomTimelineKeys.Clear();
        cachedCustomTimelineKeys.AddRange(customs.Keys);

        cachedCustomTimelineGlobalIndexByKey.Clear();
        for (var index = 0; index < cachedCustomTimelineKeys.Count; index++)
            cachedCustomTimelineGlobalIndexByKey[cachedCustomTimelineKeys[index]] = index;

        cachedCustomTimelineGroupedKeysByGroup.Clear();
        foreach (var groupName in cfg.TimelineGroups)
            cachedCustomTimelineGroupedKeysByGroup[groupName] = [];

        cachedCustomTimelineUngroupedKeys.Clear();
        foreach (var key in cachedCustomTimelineKeys)
        {
            if (cfg.TimelineGroupAssignments.TryGetValue(key, out var groupName) &&
                cachedCustomTimelineGroupedKeysByGroup.TryGetValue(groupName, out var groupedKeys))
            {
                groupedKeys.Add(key);
            }
            else
            {
                cachedCustomTimelineUngroupedKeys.Add(key);
            }
        }

        customTimelineListCacheDirty = false;
    }

    private void EnsureEncounterTimelineCaches()
    {
        var timeline = currentTimeline;
        if (timeline == null)
            return;

        if (ReferenceEquals(cachedTimelineRenderTimeline, timeline) &&
            cachedTimelineRenderRevision == currentTimelineRenderRevision)
            return;

        cachedTimelineRenderTimeline = timeline;
        cachedTimelineRenderRevision = currentTimelineRenderRevision;
        cachedTimelineUniqueAbilities.Clear();
        cachedTimelineGcdIds.Clear();
        cachedTimelineOgcdIds.Clear();
        cachedTimelineDisplayEntries.Clear();
        cachedTimelineVisibleEntries.Clear();
        cachedTimelineAbilityRows.Clear();

        var seenAbilities = new HashSet<(int AbilityId, string AbilityName)>();
        foreach (var entry in timeline.Entries)
        {
            var ability = (entry.AbilityId, entry.AbilityName ?? string.Empty);
            if (seenAbilities.Add(ability))
                cachedTimelineUniqueAbilities.Add(ability);

            if (entry.IsGcd)
                cachedTimelineGcdIds.Add(entry.AbilityId);
            else
                cachedTimelineOgcdIds.Add(entry.AbilityId);
        }

        cachedTimelineUniqueAbilities.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.AbilityName, b.AbilityName));

        cachedTimelineDisplayEntries.AddRange(TimelineJobRules.ApplyPostSelectionRules(
            timeline.SpecName,
            timeline.Entries,
            promoteMacrocosmosToVisualGcd: true));

        var rowMap = new Dictionary<(int AbilityId, string AbilityName), TimelineAbilityRowCache>();
        foreach (var entry in cachedTimelineDisplayEntries)
        {
            if (entry.Frequency < GetAbilityThreshold(entry.AbilityId))
                continue;
            if (!skillVisibility.GetValueOrDefault(entry.AbilityId, true))
                continue;

            cachedTimelineVisibleEntries.Add(entry);

            var key = GetTimelineDisplayAbilityIdentity(entry);
            if (!rowMap.TryGetValue(key, out var row))
            {
                row = new TimelineAbilityRowCache
                {
                    AbilityId = key.AbilityId,
                    AbilityName = key.AbilityName,
                };
                rowMap.Add(key, row);
                cachedTimelineAbilityRows.Add(row);
            }

            row.Entries.Add(entry);
        }

        cachedTimelineAbilityRows.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.AbilityName, b.AbilityName));
    }

    private void EnsureCustomEditorCaches(AggregatedTimeline tl)
    {
        if (cachedConflictTimeline != tl || cachedConflictDataDirty)
        {
            RebuildConflicts(tl);
            cachedConflictTimeline = tl;
            cachedConflictDataDirty = false;
        }

        if (cachedMergedRowsTimeline != tl || cachedMergedRowsDirty)
        {
            cachedMergedRows.Clear();
            cachedMergedRows.AddRange(
                tl.Entries.Select((entry, index) => (IsBoss: false, Idx: index, Time: entry.TimeOffsetSec))
                    .Concat(tl.BossEntries.Select((entry, index) => (IsBoss: true, Idx: index, Time: entry.CastStartSec)))
                    .OrderBy(row => row.Time));
            cachedMergedRowsTimeline = tl;
            cachedMergedRowsDirty = false;
        }
    }

    private void DrawTimelineTab()
    {
        DrawSelectors();
        ImGui.Separator();

        // Show update status bar (while updating or after completion/error)
        if (isUpdating)
        {
            ImGui.TextUnformatted(updateStatus);
            ImGui.ProgressBar(updateProgress, new Vector2(-1, 0), string.Empty);
            if (ImGui.Button("Cancel", default))
            {
                updateCts?.Cancel();
            }
            return;
        }

        if (!string.IsNullOrEmpty(updateStatus))
        {
            var isError = updateStatus.StartsWith("Error") || updateStatus.StartsWith("Update failed");
            var isWarning = updateStatus.StartsWith("Warning") || updateStatus.StartsWith("Update cancelled");
            if (isError)
            {
                var red = new Vector4(1f, 0.3f, 0.3f, 1f);
                ImGui.TextColored(in red, updateStatus);
            }
            else if (isWarning)
            {
                var yellow = new Vector4(1f, 0.8f, 0.2f, 1f);
                ImGui.TextColored(in yellow, updateStatus);
            }
            else
            {
                var green = new Vector4(0.3f, 1f, 0.3f, 1f);
                ImGui.TextColored(in green, updateStatus);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Dismiss"))
            {
                updateStatus = string.Empty;
            }
            ImGui.Separator();
        }

        if (currentTimeline == null)
        {
            ImGui.TextUnformatted("Select a fight and job, then click Update Logs to fetch and view the timeline.");
            return;
        }

        DrawSkillFilters();
        ImGui.Separator();
        DrawTimeline();
    }

    private void HideEmbeddedTimelinePreview()
    {
        showEmbeddedTimelinePreview = false;
        plugin.OverlayWindow.ResetEmbeddedPreview();
    }

    // ── Custom timeline export / import ──

    private void SetEiStatus(string msg, bool isError = false)
    {
        eiStatus      = msg;
        eiIsError     = isError;
        eiStatusUntil = DateTime.UtcNow.AddSeconds(4);
    }

    private void SetDebugStatus(string msg, bool isError = false)
    {
        debugStatus = msg;
        debugStatusIsError = isError;
        debugStatusUntil = DateTime.UtcNow.AddSeconds(6);
    }

    /// <summary>
    /// Export the selected custom timeline to clipboard.
    /// Format: optional ATKTip metadata comment, then FFLogs Events CSV rows.
    /// <para>
    ///   Line 1 (ATKTip only): <c># ATKTip {"encounterId":N,"encounterName":"...","specName":"..."}</c>
    ///   Line 2: <c>"Time","Event",""</c>  (standard FFLogs CSV header)
    ///   Line N: <c>"MM:SS.mmm","PlayerName casts  AbilityName",""</c>
    /// </para>
    /// Plain FFLogs CSVs (no metadata line) can still be imported unchanged.
    /// </summary>
    private void ExportSelectedTimelineToCsv()
    {
        if (editingTimeline == null || selectedCustomKey == null)
        {
            SetEiStatus("Select a timeline on the left first.", true);
            return;
        }
        try
        {
            var source = editingTimeline.SpecName;

            var sb = new System.Text.StringBuilder();

            // ── ATKTip metadata header (ignored by FFLogs CSV parsers) ────────
            var meta = System.Text.Json.JsonSerializer.Serialize(new
            {
                encounterId   = editingTimeline.EncounterId,
                encounterName = editingTimeline.EncounterName,
                specName      = editingTimeline.SpecName,
            });
            sb.AppendLine($"# ATKTip {meta}");

            // ── Standard FFLogs CSV ───────────────────────────────────────────
            sb.AppendLine("\"Time\",\"Type\",\"Ability\",\"Source → Target\",\"\"");

            IEnumerable<(double TimeSec, int SortIsBoss, TimelineEntry? PlayerEntry, BossTimelineEntry? BossEntry)> combinedRows = editingTimeline.Entries
                .Select(entry => (TimeSec: entry.TimeOffsetSec, SortIsBoss: 1, PlayerEntry: (TimelineEntry?)entry, BossEntry: (BossTimelineEntry?)null))
                .Concat(editingTimeline.BossEntries.Select(entry => (TimeSec: entry.CastStartSec, SortIsBoss: 0, PlayerEntry: (TimelineEntry?)null, BossEntry: (BossTimelineEntry?)entry)))
                .OrderBy(row => row.TimeSec)
                .ThenBy(row => row.SortIsBoss);

            foreach (var row in combinedRows)
            {
                if (row.PlayerEntry != null)
                {
                    var timeCsv = FormatCsvTime(row.PlayerEntry.TimeOffsetSec);
                    sb.AppendLine($"\"{timeCsv}\",\"Cast\",\"{row.PlayerEntry.AbilityName}\",\"{source}\",\"\"");
                    continue;
                }

                if (row.BossEntry == null)
                    continue;

                var bossTimeCsv = FormatCsvTime(row.BossEntry.CastStartSec);
                var bossAbilityCsv = BuildBossExportAbilityName(row.BossEntry);
                var bossMetadataParts = new List<string>();
                if (row.BossEntry.AbilityId > 0)
                    bossMetadataParts.Add($"ability_id={row.BossEntry.AbilityId}");
                if (row.BossEntry.SourceId > 0)
                    bossMetadataParts.Add($"source_id={row.BossEntry.SourceId}");
                if (row.BossEntry.IsPrimaryBoss)
                    bossMetadataParts.Add("primary=1");
                var bossMetadataCsv = string.Join(';', bossMetadataParts);
                sb.AppendLine($"\"{bossTimeCsv}\",\"Boss Cast\",\"{bossAbilityCsv}\",\"Boss\",\"{bossMetadataCsv}\"");
            }

            ImGui.SetClipboardText(sb.ToString().TrimEnd());
            SetEiStatus($"Exported \"{editingTimeline.EncounterName} / {editingTimeline.SpecName}\" to clipboard (CSV).");
        }
        catch (Exception ex) { SetEiStatus($"Export failed: {ex.Message}", true); }
    }

    /// <summary>Format seconds as [−]MM:SS.mmm matching the FFLogs CSV time column.</summary>
    private static string FormatCsvTime(double seconds)
    {
        var neg = seconds < 0;
        var abs = Math.Abs(seconds);
        var mm  = (int)(abs / 60);
        var ss  = abs - mm * 60.0;
        return $"{(neg ? "-" : "")}{mm:D2}:{ss:00.000}";
    }

    private static string BuildBossExportAbilityName(BossTimelineEntry entry)
    {
        var castDurationSec = Math.Max(0.0, entry.CastEndSec - entry.CastStartSec);
        return castDurationSec > 0.0005
            ? $"{entry.AbilityName} {castDurationSec:0.00} sec"
            : entry.AbilityName;
    }

    private int ResolveImportedBossAbilityId(string abilityName, string? metadata)
    {
        if (TryParseImportedBossAbilityId(metadata, out var explicitAbilityId))
            return explicitAbilityId;

        var normalizedName = NormalizeImportedAbilityName(abilityName);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return 0;

        if (importedBossAbilityIdCache.TryGetValue(normalizedName, out var cachedAbilityId))
            return cachedAbilityId;

        var recastInfo = plugin.RecastDatabase.Lookup(0, normalizedName);
        if (recastInfo != null)
        {
            cachedAbilityId = (int)recastInfo.AbilityId;
            importedBossAbilityIdCache[normalizedName] = cachedAbilityId;
            return cachedAbilityId;
        }

        cachedAbilityId = 0;
        try
        {
            var sheet = plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            if (sheet != null)
            {
                foreach (var row in sheet)
                {
                    if (!string.Equals(
                            NormalizeImportedAbilityName(row.Name.ExtractText()),
                            normalizedName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    cachedAbilityId = unchecked((int)row.RowId);
                    break;
                }
            }
        }
        catch
        {
            cachedAbilityId = 0;
        }

        importedBossAbilityIdCache[normalizedName] = cachedAbilityId;
        return cachedAbilityId;
    }

    private static bool TryParseImportedBossAbilityId(string? metadata, out int abilityId)
    {
        abilityId = ParseImportedBossMetadataInt(metadata, "ability_id");
        return abilityId > 0;
    }

    private static int ParseImportedBossMetadataInt(string? metadata, string key)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return 0;

        foreach (var part in metadata.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!part.StartsWith($"{key}=", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = part[(key.Length + 1)..];
            return int.TryParse(
                value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedValue)
                ? parsedValue
                : 0;
        }

        return 0;
    }

    /// <summary>
    /// Import a timeline from clipboard.
    /// Accepts either a plain FFLogs Events CSV or an ATKTip export (which begins
    /// with a <c># ATKTip {...}</c> metadata line).  The metadata line is parsed when
    /// present to restore encounter identity; it is silently ignored otherwise.
    /// </summary>
    private void ImportTimelineFromCsvClipboard()
    {
        try
        {
            var text = ImGui.GetClipboardText();
            if (string.IsNullOrWhiteSpace(text)) { SetEiStatus("Clipboard is empty.", true); return; }

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) { SetEiStatus("No data in clipboard.", true); return; }

            // ── ATKTip metadata line (optional) ──────────────────────────────
            // Format: # ATKTip {"encounterId":N,"encounterName":"...","specName":"..."}
            int    metaEncounterId   = 0;
            string metaEncounterName = string.Empty;
            string metaSpecName      = string.Empty;

            int lineIdx = 0;
            if (lines[lineIdx].TrimStart().StartsWith("# ATKTip ", StringComparison.Ordinal))
            {
                try
                {
                    var json = lines[lineIdx].Trim()["# ATKTip ".Length..];
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("encounterId",   out var eid)) metaEncounterId   = eid.GetInt32();
                    if (root.TryGetProperty("encounterName", out var en))  metaEncounterName = en.GetString() ?? string.Empty;
                    if (root.TryGetProperty("specName",      out var sn))  metaSpecName      = sn.GetString() ?? string.Empty;
                }
                catch { /* malformed metadata — ignore and continue */ }
                lineIdx++;
            }

            // ── Skip the CSV header row ("Time","Event","") ──────────────────
            if (lineIdx < lines.Length &&
                lines[lineIdx].TrimStart('"').StartsWith("Time", StringComparison.OrdinalIgnoreCase))
                lineIdx++;

            // ── Parse cast rows ───────────────────────────────────────────────
            var entries        = new List<TimelineEntry>();
            var bossEntries    = new List<BossTimelineEntry>();
            var importedSource = string.Empty;
            foreach (var rawLine in lines.Skip(lineIdx))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var cols = SplitCsvLine(line);
                if (TryParseImportedBossCsvRow(cols, out var bossEntry))
                {
                    bossEntries.Add(bossEntry);
                    continue;
                }

                if (!TryParseImportedCsvRow(cols, out var timeSec, out var abilityName, out var rowSourceName))
                    continue;

                if (string.IsNullOrEmpty(importedSource) && !string.IsNullOrEmpty(rowSourceName))
                    importedSource = rowSourceName;

                // Resolve ability ID from game data so icons and recast detection work.
                var recastInfo = plugin.RecastDatabase.Lookup(0, abilityName);

                entries.Add(new TimelineEntry
                {
                    AbilityId     = recastInfo != null ? (int)recastInfo.AbilityId : 0,
                    AbilityName   = abilityName,
                    AbilityIcon   = string.Empty,
                    TimeOffsetSec = timeSec,
                    Frequency     = 1.0,
                    AverageUses   = 1.0,
                });
            }

            if (entries.Count == 0 && bossEntries.Count == 0)
            {
                SetEiStatus("No valid actions or boss attacks found in clipboard.", true);
                return;
            }

            // ── Resolve encounter / spec names ────────────────────────────────
            // Prefer ATKTip metadata; fall back to inferring from the first cast row.
            var sourceName = metaSpecName;
            var encName    = metaEncounterName;

            if (string.IsNullOrEmpty(sourceName))
            {
                sourceName = string.IsNullOrEmpty(importedSource) ? "Imported" : importedSource;
            }

            if (string.IsNullOrEmpty(encName))
                encName = "Imported Timeline";

            var durationSec = Math.Max(
                entries.Select(e => e.TimeOffsetSec).DefaultIfEmpty(0.0).Max(),
                bossEntries.Select(e => Math.Max(e.CastStartSec, e.CastEndSec)).DefaultIfEmpty(0.0).Max());
            var key         = $"csv_import_{DateTime.UtcNow:yyyyMMddHHmmss}";

            var timeline = new AggregatedTimeline
            {
                EncounterId       = metaEncounterId,
                EncounterName     = encName,
                SpecName          = sourceName,
                AverageDurationMs = durationSec * 1000.0,
                ParseCount        = 1,
                Entries           = entries,
                BossEntries       = bossEntries,
            };

            plugin.CustomTimelineStore.SaveTimeline(plugin.Configuration, key, timeline);
            InvalidateCustomTimelineListCache();
            plugin.EncounterTracker.RebuildZoneMappings();

            var encounterHint = metaEncounterId != 0 ? $" (encounter {encName})" : string.Empty;
            SetEiStatus($"Imported {entries.Count} action{(entries.Count == 1 ? string.Empty : "s")} and {bossEntries.Count} boss attack{(bossEntries.Count == 1 ? string.Empty : "s")} from clipboard{encounterHint}.");
        }
        catch (Exception ex) { SetEiStatus($"Import failed: {ex.Message}", true); }
    }

    /// <summary>Parse an FFLogs CSV time string ("[−]MM:SS.mmm") to seconds.</summary>
    private static bool TryParseImportedCsvRow(List<string> cols, out double timeSec, out string abilityName, out string sourceName)
    {
        timeSec     = double.NaN;
        abilityName = string.Empty;
        sourceName  = string.Empty;

        if (cols.Count < 2)
            return false;

        timeSec = ParseCsvTime(cols[0]);
        if (double.IsNaN(timeSec))
            return false;

        if (cols.Count >= 4)
        {
            var type = cols[1].Trim();
            if (!type.Equals("Cast", StringComparison.OrdinalIgnoreCase))
                return false;

            abilityName = NormalizeImportedAbilityName(cols[2]);
            sourceName  = ExtractImportedSourceName(cols[3]);
            return !string.IsNullOrEmpty(abilityName);
        }

        var evt      = cols[1];
        var castsIdx = evt.IndexOf(" casts  ", StringComparison.OrdinalIgnoreCase);
        if (castsIdx >= 0)
        {
            sourceName = evt[..castsIdx].Trim();

            var afterCasts = evt[(castsIdx + 8)..];
            var onIdx      = afterCasts.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
            abilityName    = onIdx >= 0 ? afterCasts[..onIdx].Trim() : afterCasts.Trim();
        }
        else
        {
            abilityName = evt.Trim();
        }

        abilityName = NormalizeImportedAbilityName(abilityName);
        return !string.IsNullOrEmpty(abilityName);
    }

    private bool TryParseImportedBossCsvRow(List<string> cols, out BossTimelineEntry bossEntry, bool allowPlainCastRows = false)
    {
        bossEntry = new BossTimelineEntry();

        if (cols.Count < 4)
            return false;

        var type = cols[1].Trim();
        var isBossCastRow = type.Equals("Boss Cast", StringComparison.OrdinalIgnoreCase);
        var isPlainCastRow = type.Equals("Cast", StringComparison.OrdinalIgnoreCase);
        if (!isBossCastRow && !(allowPlainCastRows && isPlainCastRow))
            return false;

        var timeSec = ParseCsvTime(cols[0]);
        if (double.IsNaN(timeSec))
            return false;

        var rawAbilityName = cols[2].Trim();
        var abilityName = NormalizeImportedAbilityName(rawAbilityName);
        if (string.IsNullOrWhiteSpace(abilityName))
            return false;

        var castDurationSec = ExtractImportedCastDurationSec(rawAbilityName);
        var metadata = cols.Count >= 5 ? cols[4] : null;
        var abilityId = ResolveImportedBossAbilityId(abilityName, metadata);

        bossEntry = new BossTimelineEntry
        {
            AbilityId = abilityId,
            AbilityName = abilityName,
            SourceId = ParseImportedBossMetadataInt(metadata, "source_id"),
            IsPrimaryBoss = ParseImportedBossMetadataInt(metadata, "primary") == 1,
            CastStartSec = timeSec,
            CastEndSec = castDurationSec > 0.0
                ? Math.Round(timeSec + castDurationSec, 3, MidpointRounding.AwayFromZero)
                : timeSec,
        };
        return true;
    }

    private static string ExtractImportedSourceName(string sourceTarget)
    {
        var trimmed = sourceTarget.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return string.Empty;

        var arrowIdx = trimmed.IndexOf('→');
        if (arrowIdx < 0)
            arrowIdx = trimmed.IndexOf("->", StringComparison.Ordinal);

        return arrowIdx > 0 ? trimmed[..arrowIdx].Trim() : trimmed;
    }

    private static string NormalizeImportedAbilityName(string rawAbilityName)
    {
        var abilityName = rawAbilityName.Trim();
        if (string.IsNullOrEmpty(abilityName))
            return string.Empty;

        abilityName = Regex.Replace(abilityName, @"\s+Canceled$", string.Empty, RegexOptions.IgnoreCase);
        abilityName = Regex.Replace(
            abilityName,
            @"\s+\d+(?:\.\d+)?\s*sec(?:\s*\+\d+% (?:damage|healing))?$",
            string.Empty,
            RegexOptions.IgnoreCase);
        abilityName = Regex.Replace(
            abilityName,
            @"\s+\+\d+% (?:damage|healing)$",
            string.Empty,
            RegexOptions.IgnoreCase);

        return abilityName.Trim();
    }

    private static double ExtractImportedCastDurationSec(string rawAbilityName)
    {
        if (string.IsNullOrWhiteSpace(rawAbilityName))
            return 0.0;

        var match = Regex.Match(
            rawAbilityName,
            @"\s+(?<cast>\d+(?:\.\d+)?)\s*sec(?:\s*\+\d+% (?:damage|healing))?$",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return 0.0;

        return double.TryParse(
            match.Groups["cast"].Value,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var castDurationSec)
            ? castDurationSec
            : 0.0;
    }

    private static double ParseCsvTime(string s)
    {
        s = s.Trim().Trim('"');
        var neg = s.StartsWith('-');
        if (neg) s = s[1..];
        var colon = s.IndexOf(':');
        if (colon < 0) return double.NaN;
        if (!int.TryParse(s[..colon], out var mm)) return double.NaN;
        if (!double.TryParse(s[(colon + 1)..], System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var ss)) return double.NaN;
        var t = mm * 60.0 + ss;
        return neg ? -t : t;
    }

    /// <summary>Split a single CSV line respecting quoted fields.</summary>
    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var inQ    = false;
        var cur    = new System.Text.StringBuilder();
        foreach (var ch in line)
        {
            if (ch == '"') { inQ = !inQ; continue; }
            if (ch == ',' && !inQ) { result.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(ch);
        }
        result.Add(cur.ToString());
        return result;
    }

    // ── Custom Timelines tab ──

    private void ExportSelectedTimelineToBossModReborn()
    {
        if (editingTimeline == null)
        {
            SetEiStatus("Select a timeline on the left first.", true);
            return;
        }

        var flatEncounters = zones.SelectMany(z => z.Encounters).ToList();
        if (editingTimeline.EncounterId == 0 &&
            editEncounterIdx >= 0 &&
            editEncounterIdx < flatEncounters.Count)
        {
            editingTimeline.EncounterId = flatEncounters[editEncounterIdx].Id;
            editingTimeline.EncounterName = flatEncounters[editEncounterIdx].Name;
        }

        if (string.IsNullOrWhiteSpace(editingTimeline.SpecName) &&
            editSpecIdx >= 0 &&
            editSpecIdx < allSpecNames.Count)
            editingTimeline.SpecName = allSpecNames[editSpecIdx];

        if (!BossModRebornExport.TryExportUtilityPlan(editingTimeline, plugin.RecastDatabase, out var planJson, out var status))
        {
            SetEiStatus(status, true);
            return;
        }

        ImGui.SetClipboardText(planJson);
        SetEiStatus(status);
    }

    private void DrawCustomTimelinesTab()
    {
        var customs = plugin.Configuration.CustomTimelines;

        // ── Left panel: list ──
        var listWidth = 220f;
        ImGui.BeginChild("##CustomList", new Vector2(listWidth, 0), true);

        ImGui.TextDisabled("Custom Timelines");
        ImGui.SameLine();
        if (ImGui.SmallButton("Add Group"))
            ImGui.OpenPopup("##AddGroupPopup");

        if (ImGui.BeginPopup("##AddGroupPopup"))
        {
            ImGui.Text("Group name:");
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputText("##newgroup", ref newGroupNameBuf, 64,
                ImGuiInputTextFlags.EnterReturnsTrue))
            {
                var trimmed = newGroupNameBuf.Trim();
                if (trimmed.Length > 0 && !plugin.Configuration.TimelineGroups.Contains(trimmed))
                {
                    plugin.Configuration.TimelineGroups.Add(trimmed);
                    plugin.SaveTimelineUserState();
                    InvalidateCustomTimelineListCache();
                }
                newGroupNameBuf = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("OK"))
            {
                var trimmed = newGroupNameBuf.Trim();
                if (trimmed.Length > 0 && !plugin.Configuration.TimelineGroups.Contains(trimmed))
                {
                    plugin.Configuration.TimelineGroups.Add(trimmed);
                    plugin.SaveTimelineUserState();
                    InvalidateCustomTimelineListCache();
                }
                newGroupNameBuf = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        ImGui.Separator();

        if (customs.Count == 0)
        {
            ImGui.TextDisabled("No custom timelines yet.");
            ImGui.TextDisabled("Use \"Copy Timeline\" in the");
            ImGui.TextDisabled("Timeline tab menu bar.");
        }
        else
        {
            var cfg = plugin.Configuration;
            EnsureCustomTimelineListCache();
            string? timelineKeyToDelete = null;

            void DrawTimelineRow(string key, List<string> scopedKeys, int scopedIdx)
            {
                var tl         = customs[key];
                var isSelected = key == selectedCustomKey;
                var globalIdx  = cachedCustomTimelineGlobalIndexByKey[key];

                ImGui.PushID(key);

                if (ImGui.Selectable($"{tl.EncounterName} / {tl.SpecName}##{key}", isSelected,
                    ImGuiSelectableFlags.None, default))
                {
                    if (selectedCustomKey != key)
                        SelectCustomTimeline(key, tl);
                }

                // Right-click context menu
                if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    ImGui.OpenPopup("##tlctx");

                if (ImGui.BeginPopup("##tlctx"))
                {
                    ImGui.BeginDisabled(scopedIdx == 0);
                    if (ImGui.MenuItem("Move Up"))
                        ReorderCustomTimeline(cachedCustomTimelineKeys, globalIdx, cachedCustomTimelineGlobalIndexByKey[scopedKeys[scopedIdx - 1]]);
                    ImGui.EndDisabled();

                    ImGui.BeginDisabled(scopedIdx == scopedKeys.Count - 1);
                    if (ImGui.MenuItem("Move Down"))
                        ReorderCustomTimeline(cachedCustomTimelineKeys, globalIdx, cachedCustomTimelineGlobalIndexByKey[scopedKeys[scopedIdx + 1]]);
                    ImGui.EndDisabled();

                    if (ImGui.MenuItem("Delete"))
                        timelineKeyToDelete = key;

                    if (ImGui.BeginMenu("Assign to Group"))
                    {
                        if (ImGui.MenuItem("— None —"))
                        {
                            cfg.TimelineGroupAssignments.Remove(key);
                            plugin.SaveTimelineUserState();
                            InvalidateCustomTimelineListCache();
                        }
                        foreach (var g in cfg.TimelineGroups)
                        {
                            var isCurrent = cfg.TimelineGroupAssignments.TryGetValue(key, out var cur) && cur == g;
                            if (ImGui.MenuItem(g, string.Empty, isCurrent))
                            {
                                cfg.TimelineGroupAssignments[key] = g;
                                plugin.SaveTimelineUserState();
                                InvalidateCustomTimelineListCache();
                            }
                        }
                        ImGui.EndMenu();
                    }

                    if (ImGui.BeginMenu("Link Next Timeline"))
                    {
                        var currentLinkedKey = cfg.TimelineNextLinks.GetValueOrDefault(key, string.Empty);
                        if (ImGui.MenuItem("None", string.Empty, string.IsNullOrWhiteSpace(currentLinkedKey)))
                        {
                            cfg.TimelineNextLinks.Remove(key);
                            plugin.SaveTimelineUserState();
                            plugin.EncounterTracker.RebuildZoneMappings();
            SetEiStatus($"Cleared the next timeline link for {BuildTimelineLinkLabel(key, tl)}.");
                        }

                        var linkCandidates = GetLinkableCustomTimelineCandidates(key, tl).ToList();
                        if (linkCandidates.Count > 0)
                            ImGui.Separator();

                        if (linkCandidates.Count == 0)
                        {
                            ImGui.BeginDisabled();
                            ImGui.MenuItem("No compatible timelines");
                            ImGui.EndDisabled();
                        }
                        else
                        {
                            foreach (var (candidateKey, candidateTimeline) in linkCandidates)
                            {
                                var isCurrent = string.Equals(currentLinkedKey, candidateKey, StringComparison.Ordinal);
                                if (ImGui.MenuItem(BuildTimelineLinkLabel(candidateKey, candidateTimeline), string.Empty, isCurrent))
                                {
                                    SetCustomTimelineNextLink(key, candidateKey);
                                    plugin.EncounterTracker.RebuildZoneMappings();
            SetEiStatus($"Linked {BuildTimelineLinkLabel(key, tl)} to {BuildTimelineLinkLabel(candidateKey, candidateTimeline)}.");
                                }
                            }
                        }

                        ImGui.EndMenu();
                    }

                    if (ImGui.MenuItem("Auto Space"))
                        AutoSpaceTimeline(key, tl);

                    if (plugin.Configuration.DebugEnabled && ImGui.MenuItem("Auto Align"))
                        AutoAlignTimeline(key, tl);

                    ImGui.EndPopup();
                }

                ImGui.PopID();
            }

            // Grouped sections — deferred mutations to avoid modifying list during iteration
            string? groupToDelete = null;
            int     groupMoveFrom = -1;
            int     groupMoveTo   = -1;

            for (var gi = 0; gi < cfg.TimelineGroups.Count; gi++)
            {
                var group     = cfg.TimelineGroups[gi];
                var groupKeys = cachedCustomTimelineGroupedKeysByGroup.GetValueOrDefault(group) ?? [];

                var nodeOpen = ImGui.TreeNodeEx($"{group}##grpnode{gi}",
                    ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth);

                // Right-click context menu on group header
                if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    ImGui.OpenPopup($"##grpctx{gi}");

                if (ImGui.BeginPopup($"##grpctx{gi}"))
                {
                    ImGui.BeginDisabled(gi == 0);
                    if (ImGui.MenuItem("Move Up"))
                    { groupMoveFrom = gi; groupMoveTo = gi - 1; }
                    ImGui.EndDisabled();

                    ImGui.BeginDisabled(gi == cfg.TimelineGroups.Count - 1);
                    if (ImGui.MenuItem("Move Down"))
                    { groupMoveFrom = gi; groupMoveTo = gi + 1; }
                    ImGui.EndDisabled();

                    ImGui.Separator();

                    if (ImGui.MenuItem("Delete Group"))
                        groupToDelete = group;

                    ImGui.EndPopup();
                }

                if (nodeOpen)
                {
                    if (groupKeys.Count == 0)
                        ImGui.TextDisabled("  (empty)");
                    else
                        for (var groupKeyIndex = 0; groupKeyIndex < groupKeys.Count; groupKeyIndex++)
                            DrawTimelineRow(groupKeys[groupKeyIndex], groupKeys, groupKeyIndex);

                    ImGui.TreePop();
                }
            }

            // Apply deferred group mutations
            if (groupToDelete != null)
            {
                foreach (var k in cachedCustomTimelineGroupedKeysByGroup.GetValueOrDefault(groupToDelete) ?? [])
                    cfg.TimelineGroupAssignments.Remove(k);
                cfg.TimelineGroups.Remove(groupToDelete);
                plugin.SaveTimelineUserState();
                InvalidateCustomTimelineListCache();
            }
            if (groupMoveFrom >= 0 && groupMoveTo >= 0 &&
                groupMoveFrom < cfg.TimelineGroups.Count && groupMoveTo < cfg.TimelineGroups.Count)
            {
                (cfg.TimelineGroups[groupMoveFrom], cfg.TimelineGroups[groupMoveTo]) =
                    (cfg.TimelineGroups[groupMoveTo], cfg.TimelineGroups[groupMoveFrom]);
                plugin.SaveTimelineUserState();
                InvalidateCustomTimelineListCache();
            }

            if (cachedCustomTimelineUngroupedKeys.Count > 0)
            {
                if (cfg.TimelineGroups.Count > 0)
                {
                    ImGui.Spacing();
                    ImGui.TextDisabled("Ungrouped");
                }
                for (var ungroupedIndex = 0; ungroupedIndex < cachedCustomTimelineUngroupedKeys.Count; ungroupedIndex++)
                    DrawTimelineRow(cachedCustomTimelineUngroupedKeys[ungroupedIndex], cachedCustomTimelineUngroupedKeys, ungroupedIndex);
            }

            if (!string.IsNullOrWhiteSpace(timelineKeyToDelete))
                DeleteCustomTimeline(timelineKeyToDelete);

        }

        // ── Export / Import buttons ──────────────────────────────────────
        ImGui.Separator();
        if (ImGui.Button("Export to Clipboard", new Vector2(-1, 0)))
            ExportSelectedTimelineToCsv();
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("Copy the selected custom timeline to clipboard as CSV.");
            ImGui.Text("ATKTip exports include both actions and boss attacks.");
            ImGui.EndTooltip();
        }
        if (ImGui.Button("Import from Clipboard", new Vector2(-1, 0)))
            ImportTimelineFromCsvClipboard();
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("Paste a timeline from clipboard (FFLogs Events CSV format).");
            ImGui.Text("ATKTip clipboard exports can include both actions and boss attacks.");
            ImGui.EndTooltip();
        }
        if (ImGui.Button("Export Utility Plan to BMR", new Vector2(-1, 0)))
            ExportSelectedTimelineToBossModReborn();
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("Exports the selected full-timeline healer utility plan to BossModReborn.");
            ImGui.Text("The generated plan is also copied to your clipboard.");
            ImGui.EndTooltip();
        }

        // Status banner
        if (!string.IsNullOrEmpty(eiStatus) && DateTime.UtcNow < eiStatusUntil)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, eiIsError
                ? new Vector4(1f, 0.4f, 0.4f, 1f)
                : new Vector4(0.4f, 1f, 0.6f, 1f));
            ImGui.TextWrapped(eiStatus);
            ImGui.PopStyleColor();

            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(eiStatus);
                ImGui.EndTooltip();
            }
        }

        ImGui.EndChild();
        ImGui.SameLine();

        // ── Right panel: editor ──
        ImGui.BeginChild("##CustomEditor", new Vector2(0, 0), false);

        DrawBlankTimelineCreatorSection();
        DrawReportImporter();
        DrawAutoTimelineFromFetchedLogsSection();

        // Guard: if Delete was just pressed editingTimeline is now null, bail out cleanly.
        if (editingTimeline == null)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Select a custom timeline on the left to edit it,");
            ImGui.TextDisabled("or use \"From FFLogs Report\" above to import one.");
            ImGui.EndChild();
            return;
        }

        ImGui.Separator();

        // Header actions
        if (ImGui.Button("Load into Viewer"))
        {
            currentTimeline = editingTimeline;
            RebuildSkillVisibility(editingTimeline);
        }
        ImGui.SameLine();
        if (customEditorDirty)
        {
            if (ImGui.Button("Save Changes"))
                SaveEditingTimeline();
            ImGui.SameLine();
        }
        var deleteLabel = $"Delete##delCustom";
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.1f, 0.1f, 1f));
        if (ImGui.Button(deleteLabel))
            DeleteCustomTimeline(selectedCustomKey!);
        ImGui.PopStyleColor();

        // Guard: if Delete was just pressed editingTimeline is now null — bail out cleanly

        if (editingTimeline == null)
        {
            ImGui.EndChild();
            return;
        }

        if (plugin.Configuration.DebugEnabled)
        {
            ImGui.SameLine();
            var currentAutoDebugReport = GetLatestAutoTimelineDebugReport(editingTimeline.SpecName);
            var hasAutoDebugReport = !string.IsNullOrWhiteSpace(currentAutoDebugReport);
            if (!hasAutoDebugReport)
                ImGui.BeginDisabled();
            if (ImGui.Button("Copy Auto Debug"))
            {
                ImGui.SetClipboardText(currentAutoDebugReport);
                SetDebugStatus($"Copied the latest {editingTimeline.SpecName} Auto Timeline debug report to clipboard.");
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text("Copy the latest Auto Timeline reasoning report captured for this job.");
                ImGui.EndTooltip();
            }
            ImGui.SameLine();
            if (ImGui.Button("Save Auto Debug"))
                SaveAutoTimelineDebugReport(editingTimeline);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text("Write the latest Auto Timeline reasoning report for this job to the debug artifact folder.");
                ImGui.EndTooltip();
            }
            if (!hasAutoDebugReport)
                ImGui.EndDisabled();

            if (ImGui.Button("Copy Conflict Debug"))
                CopyConflictDebugReport(editingTimeline);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text("Copy a chronological conflict report for the current edited timeline.");
                ImGui.EndTooltip();
            }
            ImGui.SameLine();
            if (ImGui.Button("Save Conflict Debug"))
                SaveConflictDebugReport(editingTimeline);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text("Write a conflict report for the current edited timeline to the debug artifact folder.");
                ImGui.EndTooltip();
            }
            ImGui.SameLine();
            if (ImGui.Button("Save Timeline Snapshot"))
                SaveTimelineSnapshot(editingTimeline, "manual_snapshot");
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text("Write the current edited timeline as indented JSON to the debug artifact folder.");
                ImGui.EndTooltip();
            }
            ImGui.SameLine();
            if (ImGui.Button("Open Debug Folder"))
                OpenDebugArtifactsDirectory();
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text("Open the ATKTip artifact folder used for reports and timeline snapshots.");
                ImGui.EndTooltip();
            }

            if (!string.IsNullOrEmpty(debugStatus) && DateTime.UtcNow < debugStatusUntil)
            {
                if (debugStatusIsError)
                    ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), debugStatus);
                else
                    ImGui.TextColored(new Vector4(0.45f, 0.9f, 1f, 1f), debugStatus);
            }

            ImGui.Separator();
        }

        // Metadata fields
        var flatEncounters = zones.SelectMany(z => z.Encounters).ToList();
        var flatEncounterNames = flatEncounters.Select(e => e.Name).ToList();
        if (flatEncounterNames.Count > 0)
        {
            ImGui.SetNextItemWidth(280);
            if (ImGui.Combo("Encounter Name", ref editEncounterIdx, flatEncounterNames, -1))
            {
                editEncounterName             = flatEncounterNames[editEncounterIdx];
                editingTimeline.EncounterName = editEncounterName;
                editingTimeline.EncounterId   = flatEncounters[editEncounterIdx].Id;
                MarkCustomEditorModified(invalidateListCache: true);
            }
        }
        else
        {
            ImGui.SetNextItemWidth(280);
            if (ImGui.InputText("Encounter Name", ref editEncounterName, 128))
            {
                editingTimeline.EncounterName = editEncounterName;
                MarkCustomEditorModified(invalidateListCache: true);
            }
        }
        if (allSpecNames.Count > 0)
        {
            ImGui.SetNextItemWidth(280);
            if (ImGui.Combo("Spec / Job", ref editSpecIdx, allSpecNames, -1))
            {
                editSpecName = allSpecNames[editSpecIdx];
                editingTimeline.SpecName = editSpecName;
                MarkCustomEditorModified(invalidateListCache: true);
            }
        }
        else
        {
            ImGui.SetNextItemWidth(280);
            if (ImGui.InputText("Spec / Job", ref editSpecName, 64))
            {
                editingTimeline.SpecName = editSpecName;
                MarkCustomEditorModified(invalidateListCache: true);
            }
        }
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputFloat("Duration (s)", ref editDurationSec, 0.1f, 1f, "%.1f"))
        {
            editDurationSec = Math.Max(0f, editDurationSec);
            editingTimeline.AverageDurationMs = editDurationSec * 1000.0;
            MarkCustomEditorModified();
        }
        ImGui.Separator();

        EnsureCustomEditorCaches(editingTimeline);
        var fetchedBossSourceTimeline = GetFetchedBossAttackSourceTimeline(editingTimeline);
        var canGenerateBossAttacks = fetchedBossSourceTimeline != null;
        var timelineStructureChanged = false;

        ImGui.Text($"Player: {editingTimeline.Entries.Count}  |  Boss: {editingTimeline.BossEntries.Count}");
        ImGui.SameLine();
        if (ImGui.SmallButton("+ Player"))
        {
            editingTimeline.Entries.Add(new TimelineEntry
            {
                AbilityId     = 0,
                AbilityName   = "New Ability",
                TimeOffsetSec = 0.0,
                Frequency     = 1.0,
                AverageUses   = 1.0,
            });
            MarkCustomEditorModified();
            timelineStructureChanged = true;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("+ Boss"))
        {
            editingTimeline.BossEntries.Add(new BossTimelineEntry
            {
                AbilityId    = 0,
                AbilityName  = "New Boss Cast",
                CastStartSec = 0.0,
                CastEndSec   = 0.0,
            });
            MarkCustomEditorModified();
            timelineStructureChanged = true;
        }
        ImGui.SameLine();
        if (!canGenerateBossAttacks)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Generate Boss Attacks"))
        {
            GenerateBossAttacksFromFetchedLogs(editingTimeline, fetchedBossSourceTimeline!);
            timelineStructureChanged = true;
        }
        if (!canGenerateBossAttacks)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            if (fetchedBossSourceTimeline == null)
                ImGui.TextUnformatted("Fetch this fight from FFLogs first to cache boss attacks for this encounter and job.");
            else
                ImGui.TextUnformatted("Append cached boss attacks from fetched FFLogs data without removing current custom timeline entries.");
            ImGui.EndTooltip();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Import Boss Attacks"))
        {
            ImportBossAttacksFromCsvClipboard(editingTimeline);
            timelineStructureChanged = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted("Append boss attacks from clipboard using the FFLogs Events CSV format.");
            ImGui.EndTooltip();
        }
        ImGui.SameLine();
        if (editingTimeline.BossEntries.Count == 0)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Remove Boss Attacks"))
        {
            RemoveAllBossAttacks(editingTimeline);
            timelineStructureChanged = true;
        }
        if (editingTimeline.BossEntries.Count == 0)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted("Remove every boss attack from this custom timeline.");
            ImGui.EndTooltip();
        }

        if (timelineStructureChanged)
            EnsureCustomEditorCaches(editingTimeline);

        if (conflictedPlayerIndices.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(
                new Vector4(1f, 0.4f, 0.4f, 1f),
                $"\u26a0 {conflictedPlayerIndices.Count} conflict(s) — hover time cells for details");
        }

        ImGui.Separator();

        // Merge player entries + boss entries, sorted by time, for display
        var activeTimeline = editingTimeline;
        if (activeTimeline == null)
            return;

        var playerEntries = activeTimeline.Entries;
        var bossEntries   = activeTimeline.BossEntries;

        // Build a unified sorted row list: (isBoss, originalIndex, displayTime)
        var mergedRows = cachedMergedRows;

        if (ImGui.BeginTable("##entries", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, -1)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Time (s)",   ImGuiTableColumnFlags.WidthFixed, 78);
            ImGui.TableSetupColumn("Ability",    ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Freq",       ImGuiTableColumnFlags.WidthFixed, 48);
            ImGui.TableSetupColumn("##edit",     ImGuiTableColumnFlags.WidthFixed, 42);
            ImGui.TableSetupColumn("##del",      ImGuiTableColumnFlags.WidthFixed, 42);
            ImGui.TableHeadersRow();

            int toDeletePlayer = -1;
            int toDeleteBoss   = -1;
            var bossColor      = new Vector4(1f, 0.45f, 0.35f, 1f);   // warm red for boss rows

            foreach (var row in mergedRows)
            {
                ImGui.TableNextRow();

                if (row.IsBoss)
                {
                    var b = bossEntries[row.Idx];

                    ImGui.TableSetColumnIndex(0);
                    var timeStr = b.CastEndSec > b.CastStartSec
                        ? $"{b.CastStartSec:F1}-{b.CastEndSec:F1}"
                        : $"{b.CastStartSec:F1}";
                    ImGui.TextColored(bossColor, timeStr);

                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextColored(bossColor, $"\u2694 {b.AbilityName}");  // ⚔ prefix

                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextDisabled("--");

                    ImGui.TableSetColumnIndex(3);
                    if (ImGui.SmallButton($"Edit##b{row.Idx}"))
                    {
                        editingEntryIndex  = row.Idx;
                        editingEntryIsBoss = true;
                        editEntryTime      = (float)b.CastStartSec;
                        editEntryEndTime   = (float)b.CastEndSec;
                        editEntryName      = b.AbilityName;
                        ImGui.OpenPopup("##EditEntry");
                    }

                    ImGui.TableSetColumnIndex(4);
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.1f, 0.1f, 1f));
                    if (ImGui.SmallButton($"Del##b{row.Idx}"))
                        toDeleteBoss = row.Idx;
                    ImGui.PopStyleColor();
                }
                else
                {
                    if (row.Idx < 0 || row.Idx >= playerEntries.Count)
                        continue;

                    var e = playerEntries[row.Idx];

                    // Highlight rows with recast conflicts
                    if (conflictedPlayerIndices.Contains(row.Idx))
                    {
                        var conflictBg = ImGui.GetColorU32(new Vector4(0.8f, 0.15f, 0.15f, 0.35f));
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, conflictBg);
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, conflictBg);
                    }

                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(FormatCsvTime(e.TimeOffsetSec));
                    if (conflictedPlayerIndices.Contains(row.Idx) &&
                        conflictReasons.TryGetValue(row.Idx, out var conflictTip))
                    {
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(conflictTip);
                    }

                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text(e.AbilityName);

                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text($"{e.Frequency * 100:F0}%");

                    ImGui.TableSetColumnIndex(3);
                    if (ImGui.SmallButton($"Edit##p{row.Idx}"))
                    {
                        editingEntryIndex  = row.Idx;
                        editingEntryIsBoss = false;
                        editEntryTime      = (float)e.TimeOffsetSec;
                        editEntryName      = e.AbilityName;
                        editEntryFrequency = (float)e.Frequency;

                        // Build ability options from all timelines for the same spec:
                        // search both the API-fetched TimelineStore and saved CustomTimelines.
                        var specKey = editSpecName;
                        var storeAbilities = plugin.TimelineStore.GetAllTimelines()
                            .Where(t => t.SpecName == specKey)
                            .SelectMany(t => t.Entries)
                            .Select(t => t.AbilityName);
                        var customAbilities = plugin.Configuration.CustomTimelines.Values
                            .Where(t => t.SpecName == specKey)
                            .SelectMany(t => t.Entries)
                            .Select(t => t.AbilityName);
                        // Always include entries from the timeline currently being edited.
                        var thisTimelineAbilities = editingTimeline?.Entries
                            .Select(t => t.AbilityName) ?? [];
                        editEntryAbilityOptions = storeAbilities
                            .Concat(customAbilities)
                            .Concat(thisTimelineAbilities)
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Distinct()
                            .OrderBy(n => n)
                            .ToList();
                        if (editEntryAbilityOptions.Count == 0)
                            editEntryAbilityOptions = [editEntryName];
                        editEntryAbilityIdx = Math.Max(0, editEntryAbilityOptions.IndexOf(editEntryName));
                        ImGui.OpenPopup("##EditEntry");
                    }

                    ImGui.TableSetColumnIndex(4);
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.1f, 0.1f, 1f));
                    if (ImGui.SmallButton($"Del##p{row.Idx}"))
                        toDeletePlayer = row.Idx;
                    ImGui.PopStyleColor();
                }
            }

            // Deletions (after iteration)
            if (toDeletePlayer >= 0)
            {
                playerEntries.RemoveAt(toDeletePlayer);
                if (!editingEntryIsBoss && editingEntryIndex == toDeletePlayer)
                    editingEntryIndex = -1;
                MarkCustomEditorModified();
            }
            if (toDeleteBoss >= 0)
            {
                bossEntries.RemoveAt(toDeleteBoss);
                if (editingEntryIsBoss && editingEntryIndex == toDeleteBoss)
                    editingEntryIndex = -1;
                MarkCustomEditorModified();
            }
            // Shared edit popup — fields adapt based on editingEntryIsBoss
            if (ImGui.BeginPopup("##EditEntry") && editingEntryIndex >= 0)
            {
                if (editingEntryIsBoss && editingEntryIndex < bossEntries.Count)
                {
                    ImGui.Text("Edit Boss Cast");
                    ImGui.Separator();
                    ImGui.SetNextItemWidth(160);
                    ImGui.InputText("Name##bname", ref editEntryName, 128);
                    ImGui.SetNextItemWidth(110);
                    ImGui.InputFloat("Start (s)##bstart", ref editEntryTime, 0.1f, 1f, "%.2f");
                    ImGui.SetNextItemWidth(110);
                    ImGui.InputFloat("End (s)##bend",   ref editEntryEndTime, 0.1f, 1f, "%.2f");
                    if (ImGui.Button("Apply##bapply"))
                    {
                        var b = bossEntries[editingEntryIndex];
                        b.AbilityName  = editEntryName;
                        b.CastStartSec = editEntryTime;
                        b.CastEndSec   = editEntryEndTime;
                        MarkCustomEditorModified();
                        ImGui.CloseCurrentPopup();
                    }
                }
                else if (!editingEntryIsBoss && editingEntryIndex < playerEntries.Count)
                {
                    ImGui.Text("Edit Player Entry");
                    ImGui.Separator();
                    if (editEntryAbilityOptions.Count > 1)
                    {
                        ImGui.SetNextItemWidth(220);
                        if (ImGui.Combo("Name##pname", ref editEntryAbilityIdx, editEntryAbilityOptions, -1))
                            editEntryName = editEntryAbilityOptions[editEntryAbilityIdx];
                    }
                    else
                    {
                        ImGui.SetNextItemWidth(220);
                        ImGui.InputText("Name##pname", ref editEntryName, 128);
                    }
                    ImGui.SetNextItemWidth(110);
                    ImGui.InputFloat("Time (s)##ptime", ref editEntryTime, 1.0f, 5f, "%.1f");
                    ImGui.SetNextItemWidth(110);
                    ImGui.SliderFloat("Frequency##pfreq", ref editEntryFrequency, 0f, 1f, "%.2f");
                    if (ImGui.Button("Apply##papply"))
                    {
                        var e = playerEntries[editingEntryIndex];
                        e.AbilityName    = editEntryName;
                        e.TimeOffsetSec  = editEntryTime;
                        e.Frequency      = editEntryFrequency;
                        MarkCustomEditorModified();
                        ImGui.CloseCurrentPopup();
                    }
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel##cancel"))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }

            ImGui.EndTable();
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// Rebuilds conflictedPlayerIndices and conflictReasons for the given timeline.
    /// Uses a per-ability charge queue: each use spends one charge; a charge returns
    /// after RecastSec seconds. Conflict = no charges available at use time.
    /// Keyed by ability ID (not cooldown group) so distinct abilities with coincidentally
    /// shared Lumina cooldown groups don't produce false positives.
    /// Only abilities with recast >= 5 s are checked (excludes GCDs).
    /// </summary>
    private void RebuildConflicts(AggregatedTimeline tl)
    {
        conflictedPlayerIndices.Clear();
        conflictReasons.Clear();

        // ── Cooldown (recast) tracking ──────────────────────────────────────
        // abilityId → (sorted list of times when each spent charge recharges, last-use time)
        var abilityState = new Dictionary<string, (List<double> RechargeQueue, double Time, string AbilityName)>(
            StringComparer.OrdinalIgnoreCase);
        var timelineAbilityNames = tl.Entries
            .Select(e => e.AbilityName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── Combo chain tracking ────────────────────────────────────────────
        uint lastGcdId = 0;
        string? lastComboAbilityName = null;
        double lastGcdTime = double.NegativeInfinity;
        var comboSeenIds = new HashSet<uint>();

        // ── Gauge tracking ──────────────────────────────────────────────────
        var gaugeRules = GaugeSimulator.GetRules(tl.SpecName);
        var grantedRules = GrantedActionDatabase.GetRules(tl.SpecName);
        var gaugeState = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var grantedState = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var actionState = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var passiveGaugeProgress = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (gaugeRules != null)
        {
            foreach (var res in gaugeRules.Resources)
            {
                gaugeState[res.Name] = res.InitialValue;
                passiveGaugeProgress[res.Name] = 0.0;
            }
        }
        if (grantedRules != null)
        {
            foreach (var res in grantedRules.Resources)
                grantedState[res.Name] = res.InitialValue;
        }

        // ── AST card draw state ─────────────────────────────────────────────
        // null = no draw active yet, "Astral" = after Astral Draw, "Umbral" = after Umbral Draw
        string? cardDrawState = null;
        if (string.Equals(tl.SpecName, "Astrologian", StringComparison.OrdinalIgnoreCase) &&
            grantedRules?.CardDraw is { } initialAstCardDraw)
        {
            cardDrawState = "Astral";
            foreach (var astralCard in initialAstCardDraw.AstralCards)
            {
                var resourceName = $"{astralCard} Ready";
                if (grantedState.ContainsKey(resourceName))
                    grantedState[resourceName] = 1;
            }
        }
        var timedWindowEndByAbility = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var timedLockoutEndByAbility = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double castLockUntil = 0.0;

        double lastEntryTime = 0.0;

        var sorted = tl.Entries
            .Select((e, i) => (Index: i, Entry: e))
            .OrderBy(x => x.Entry.TimeOffsetSec)
            .ToList();

        // Appends a reason string; marks the index as conflicted.
        void AddReason(int i, string reason)
        {
            if (conflictReasons.TryGetValue(i, out var existing))
                conflictReasons[i] = existing + "\n" + reason;
            else
                conflictReasons[i] = reason;
            conflictedPlayerIndices.Add(i);
        }

        foreach (var (idx, entry) in sorted)
        {
            var info  = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
            bool isGcd = info?.IsGcdAction ?? false;
            var now   = entry.TimeOffsetSec;
            var effectiveCastTimeSec = GetEffectiveCastTimeSec(
                grantedState, grantedRules, info, entry.AbilityName, out var instantCastRule);

            // ── Passive gauge generation ──────────────────────────────────────
            // Advance time-based gauges (e.g. Lily, Addersgall, Polyglot) by the
            // elapsed time since the previous entry was processed.
            if (gaugeRules != null)
            {
                var elapsed = now - lastEntryTime;
                foreach (var res in gaugeRules.Resources)
                {
                    if (res.PassiveGenerationIntervalSec <= 0) continue;
                    var progress = passiveGaugeProgress.GetValueOrDefault(res.Name) + elapsed;
                    var ticks = (int)(progress / res.PassiveGenerationIntervalSec);
                    if (ticks > 0)
                    {
                        gaugeState[res.Name] = Math.Clamp(
                            gaugeState.GetValueOrDefault(res.Name) + ticks, 0, res.MaxValue);
                        progress -= ticks * res.PassiveGenerationIntervalSec;
                    }
                    passiveGaugeProgress[res.Name] = progress;
                }
            }
            lastEntryTime = now;

            if (!isGcd &&
                !ConflictCastLockIgnoredSpecs.Contains(tl.SpecName) &&
                now + ConflictCastLockToleranceSec < castLockUntil)
                AddReason(idx,
                    $"Cast lock conflict: {entry.AbilityName} lands during a prior cast window (free at {FormatCsvTime(castLockUntil)})");

            var timedWindowReason = GetTimedUsageWindowReason(
                tl.SpecName,
                timedWindowEndByAbility,
                entry.AbilityName,
                now);
            if (timedWindowReason != null)
                AddReason(idx, timedWindowReason);

            var timedLockoutReason = GetTimedUsageLockoutReason(
                tl.SpecName,
                timedLockoutEndByAbility,
                entry.AbilityName,
                now);
            if (timedLockoutReason != null)
                AddReason(idx, timedLockoutReason);

            var scholarBurstReservationReason = GetScholarBurstAetherflowReservationReason(
                tl.SpecName,
                timedWindowEndByAbility,
                gaugeState,
                entry.AbilityName,
                now);
            if (scholarBurstReservationReason != null)
                AddReason(idx, scholarBurstReservationReason);

            // ── Cooldown conflict (oGCDs / long-recast abilities, recast ≥ 5 s) ──
            if (ShouldTrackPersonalCooldown(info) &&
                !ShouldIgnoreConflictCooldown(tl.SpecName, entry.AbilityName) &&
                !ShouldBypassCooldown(grantedState, grantedRules, entry.AbilityName))
            {
                var cooldownKey = GetCooldownBucketKey(info, entry.AbilityId, entry.AbilityName);
                var maxCharges = Math.Max(1, info?.MaxCharges ?? 1);

                if (!abilityState.TryGetValue(cooldownKey, out var state))
                    state = ([], 0, string.Empty);

                state.RechargeQueue.RemoveAll(t => t <= now + AutoCooldownToleranceSec);

                if (state.RechargeQueue.Count >= maxCharges)
                {
                    var earliestAvail = state.RechargeQueue.Min();
                    var overlapSec    = earliestAvail - now;
                    if (overlapSec > AutoCooldownToleranceSec)
                    {
                        AddReason(idx,
                            $"Recast conflict: {overlapSec:F1}s too early — previous use at {FormatCsvTime(state.Time)}" +
                            $" (recast: {info!.RecastSec:F0}s, charges: {maxCharges})");
                    }
                }

                state.RechargeQueue.Add(now + info!.RecastSec);
                state.RechargeQueue.Sort();
                abilityState[cooldownKey] = (state.RechargeQueue, now, entry.AbilityName);
            }

            // ── Combo chain check (GCDs with a required predecessor) ─────────
            if (isGcd && lastGcdId != 0 && now - lastGcdTime >= ComboResetGapSec)
            {
                lastGcdId = 0;
                lastComboAbilityName = null;
                comboSeenIds.Clear();
            }

            if (isGcd && info != null && ShouldValidateComboRequirement(info, entry.AbilityName) && info.ComboActionId > 0)
            {
                var predecessorNames = GetComboPredecessorNames(info);
                var predecessorSatisfiedByName = !string.IsNullOrWhiteSpace(lastComboAbilityName) &&
                                                predecessorNames.Contains(lastComboAbilityName);
                if (lastGcdId != info.ComboActionId &&
                    !predecessorSatisfiedByName &&
                    comboSeenIds.Contains(info.ComboActionId))
                {
                    var prevInfo = plugin.RecastDatabase.Lookup((int)info.ComboActionId, string.Empty);
                    var prevName = prevInfo?.Name ?? $"#{info.ComboActionId}";
                    AddReason(idx, $"Broken combo: {entry.AbilityName} requires {prevName} in the active combo chain");
                }
            }

            // ── Gauge check ───────────────────────────────────────────────────
            Dictionary<string, int>? preActionGaugeState = null;

            var bypassGaugeSpendChecks = ShouldBypassRepeatableGrantedGaugeSpendChecks(grantedState, grantedRules, entry.AbilityName);

            if (gaugeRules != null &&
                gaugeRules.EffectByName.TryGetValue(entry.AbilityName, out var effects))
            {
                preActionGaugeState = new Dictionary<string, int>(gaugeState, StringComparer.OrdinalIgnoreCase);

                // Check all requirements before applying any deltas
                foreach (var effect in effects)
                {
                    if (bypassGaugeSpendChecks && (effect.MinRequired > 0 || effect.Delta < 0))
                        continue;

                    if (ShouldIgnoreConflictRequirement(tl.SpecName, effect.GaugeName))
                        continue;

                    if (effect.MinRequired > 0 &&
                        gaugeState.GetValueOrDefault(effect.GaugeName) < effect.MinRequired)
                    {
                        AddReason(idx,
                            $"Not enough gauge: {effect.GaugeName} = {gaugeState.GetValueOrDefault(effect.GaugeName)}" +
                            $" (need \u2265 {effect.MinRequired})");
                    }

                    if (effect.MaxAllowedBeforeUse < int.MaxValue)
                    {
                        var have = gaugeState.GetValueOrDefault(effect.GaugeName);
                        if (have > effect.MaxAllowedBeforeUse)
                        {
                            AddReason(idx,
                                $"Gauge refresh conflict: {entry.AbilityName} requires {effect.GaugeName} \u2264 {effect.MaxAllowedBeforeUse}" +
                                $" before use, but {have} remains");
                        }
                    }
                }

                // Apply all deltas regardless of conflict so later entries see correct state
                foreach (var effect in effects)
                {
                    if (bypassGaugeSpendChecks && effect.Delta < 0)
                        continue;

                    ApplyGaugeEffectToState(gaugeState, gaugeRules, effect);
                }
            }

            // ── AST card draw validation ──────────────────────────────────────
            var actionRule = plugin.ActionStateDatabase.Lookup(entry.AbilityId, entry.AbilityName);
            if (actionRule != null)
            {
                var effectiveGaugeState = preActionGaugeState ?? gaugeState;
                foreach (var effect in actionRule.Effects)
                {
                    var currentState = GetEffectiveStateValue(effect.StateName, actionState, effectiveGaugeState, grantedState);
                    if (effect.MinRequired > 0 &&
                        !ShouldIgnoreConflictRequirement(tl.SpecName, effect.StateName) &&
                        !ShouldIgnoreActionStateRequirement(actionRule, effect, entry.AbilityName, gaugeRules, grantedRules) &&
                        ShouldEnforceActionStateRequirement(effect.StateName, timelineAbilityNames, gaugeRules, grantedRules) &&
                        currentState < effect.MinRequired)
                    {
                        AddReason(idx,
                            $"Missing action state: {effect.StateName} = {currentState}" +
                            $" (need \u2265 {effect.MinRequired})");
                    }
                }

                ApplyActionStateEffects(actionState, actionRule);
            }

            var repeatRule = FindRepeatableGrantedActionRule(grantedRules, entry.AbilityName);
            if (repeatRule != null)
            {
                var current = grantedState.GetValueOrDefault(repeatRule.ResourceName);
                var maxValue = grantedRules?.Resources.FirstOrDefault(r =>
                    string.Equals(r.Name, repeatRule.ResourceName, StringComparison.OrdinalIgnoreCase))
                    ?.MaxValue ?? int.MaxValue;

                if (UsesRepeatableGrantedActionCharge(grantedState, repeatRule, entry.AbilityName))
                {
                    if (current < repeatRule.ConsumeCount)
                    {
                        AddReason(idx,
                            $"Not enough gauge: {repeatRule.ResourceName} = {current}" +
                            $" (need \u2265 {repeatRule.ConsumeCount})");
                    }
                    else
                    {
                        grantedState[repeatRule.ResourceName] = Math.Clamp(
                            current - repeatRule.ConsumeCount,
                            0,
                            maxValue);
                    }
                }
                else if (string.Equals(repeatRule.TriggerName, entry.AbilityName, StringComparison.OrdinalIgnoreCase))
                {
                    grantedState[repeatRule.ResourceName] = Math.Clamp(
                        current + repeatRule.GrantCount,
                        0,
                        maxValue);
                }
            }

            if (grantedRules?.CardDraw is { } cardDraw &&
                !string.Equals(tl.SpecName, "Astrologian", StringComparison.OrdinalIgnoreCase))
            {
                var name = entry.AbilityName;

                if (cardDraw.AstralCards.Contains(name))
                {
                    if (cardDrawState != "Astral")
                        AddReason(idx,
                            $"Card draw conflict: {name} requires Astral Draw" +
                            (cardDrawState == "Umbral" ? " (Umbral Draw active)" : " (no draw active)"));
                    cardDrawState = null;
                }
                else if (cardDraw.UmbralCards.Contains(name))
                {
                    if (cardDrawState != "Umbral")
                        AddReason(idx,
                            $"Card draw conflict: {name} requires Umbral Draw" +
                            (cardDrawState == "Astral" ? " (Astral Draw active)" : " (no draw active)"));
                    cardDrawState = null;
                }
                else if (string.Equals(name, cardDraw.AstralDrawName, StringComparison.OrdinalIgnoreCase))
                {
                    cardDrawState = "Astral";
                }
                else if (string.Equals(name, cardDraw.UmbralDrawName, StringComparison.OrdinalIgnoreCase))
                {
                    cardDrawState = "Umbral";
                }
            }

            // ── Advance combo state ───────────────────────────────────────────
            ApplyTimedUsageWindowState(tl.SpecName, timedWindowEndByAbility, entry.AbilityName, now);
            ApplyTimedUsageLockoutState(tl.SpecName, timedLockoutEndByAbility, entry.AbilityName, now);

            ApplyCastStateTransitions(
                grantedState, grantedRules, info, entry.AbilityName, effectiveCastTimeSec, instantCastRule);

            if (isGcd && info != null)
            {
                if (IsComboTrackedAction(info, entry.AbilityName))
                {
                    lastGcdId = info.AbilityId;
                    lastComboAbilityName = entry.AbilityName;
                    lastGcdTime = now;
                    comboSeenIds.Add(info.AbilityId);
                }
                castLockUntil = effectiveCastTimeSec > AutoCastLockToleranceSec
                    ? now + effectiveCastTimeSec
                    : now;
            }
        }
    }

    /// <summary>
    /// Swaps the custom timeline at <paramref name="fromIdx"/> with the one at
    /// <paramref name="toIdx"/> by rebuilding the dictionary in the new order.
    /// </summary>
    private void ReorderCustomTimeline(List<string> orderedKeys, int fromIdx, int toIdx)
    {
        if (fromIdx < 0 || toIdx < 0 || fromIdx >= orderedKeys.Count || toIdx >= orderedKeys.Count)
            return;

        var customs = plugin.Configuration.CustomTimelines;
        // Swap in the key list
        (orderedKeys[fromIdx], orderedKeys[toIdx]) = (orderedKeys[toIdx], orderedKeys[fromIdx]);

        // Rebuild dict in the new order (Dictionary preserves insertion order in .NET 5+)
        var reordered = new Dictionary<string, AggregatedTimeline>(orderedKeys.Count);
        foreach (var k in orderedKeys)
            reordered[k] = customs[k];

        plugin.Configuration.CustomTimelines = reordered;
        plugin.CustomTimelineStore.SaveOrder(plugin.Configuration.CustomTimelines.Keys);
        InvalidateCustomTimelineListCache();
    }

    private void SelectCustomTimeline(string key, AggregatedTimeline tl)
    {
        selectedCustomKey  = key;
        editingTimeline    = tl;
        editEncounterName  = tl.EncounterName;
        editSpecName       = tl.SpecName;
        editDurationSec    = (float)(tl.AverageDurationMs / 1000.0);
        editingEntryIndex  = -1;
        customEditorDirty  = false;
        ClearCustomEditorCaches();

        // Sync dropdown indices for encounter and spec
        var flatEncounters = zones.SelectMany(z => z.Encounters).ToList();
        editEncounterIdx = flatEncounters.FindIndex(e => e.Id == tl.EncounterId);
        if (editEncounterIdx < 0)
            editEncounterIdx = flatEncounters.FindIndex(e => string.Equals(e.Name, tl.EncounterName, StringComparison.OrdinalIgnoreCase));
        editEncounterIdx = Math.Max(0, editEncounterIdx);
        editSpecIdx      = Math.Max(0, allSpecNames.IndexOf(tl.SpecName));

        // Backfill EncounterId for old timelines that were saved before this field was set.
        // We don't mark dirty — the fix is silent and will persist on the next explicit Save.
        if (tl.EncounterId == 0 &&
            editEncounterIdx >= 0 && editEncounterIdx < flatEncounters.Count)
        {
            tl.EncounterId = flatEncounters[editEncounterIdx].Id;
            // Rebuild zone map so the overlay can pick up this timeline immediately
            plugin.EncounterTracker.RebuildZoneMappings();
        }
    }

    private void AutoSpaceTimeline(string key, AggregatedTimeline tl)
    {
        const double autoSpaceMinimumGapSec = 0.099;

        if (tl.Entries.Count == 0)
        {
            SetEiStatus($"No actions to auto-space in {BuildTimelineLinkLabel(key, tl)}.");
            return;
        }

        RefreshTimelineRuntimeMetadata(tl);

        var movedCount = 0;
        var lastGcdTime = double.NegativeInfinity;
        var lastOgcdTime = double.NegativeInfinity;
        for (var index = 0; index < tl.Entries.Count; index++)
        {
            var entry = tl.Entries[index];
            var isGcd = IsGcdEntry(entry);
            var previousLaneTime = isGcd ? lastGcdTime : lastOgcdTime;
            var minAllowedTime = double.IsNegativeInfinity(previousLaneTime)
                ? double.NegativeInfinity
                : Math.Round(previousLaneTime + autoSpaceMinimumGapSec, 3, MidpointRounding.AwayFromZero);

            if (entry.TimeOffsetSec < minAllowedTime)
            {
                entry.TimeOffsetSec = minAllowedTime;
                movedCount++;
            }

            if (isGcd)
                lastGcdTime = entry.TimeOffsetSec;
            else
                lastOgcdTime = entry.TimeOffsetSec;
        }

        if (tl.Entries.Count > 0)
            tl.AverageDurationMs = Math.Max(tl.AverageDurationMs, tl.Entries.Max(entry => entry.TimeOffsetSec) * 1000.0);

        plugin.CustomTimelineStore.SaveTimeline(plugin.Configuration, key, tl);
        plugin.EncounterTracker.RebuildZoneMappings();
        customEditorDirty = false;
        ClearCustomEditorCaches();
        SetEiStatus($"Auto spaced {movedCount} action{(movedCount == 1 ? string.Empty : "s")} in {BuildTimelineLinkLabel(key, tl)} using a 0.099s same-lane minimum gap.");
    }

    private void AutoAlignTimeline(string key, AggregatedTimeline tl)
    {
        if (tl.Entries.Count == 0)
        {
            SetDebugStatus($"No actions to auto-align in {BuildTimelineLinkLabel(key, tl)}.");
            return;
        }

        RefreshTimelineRuntimeMetadata(tl);

        var gcdSlotSpacingSec = AutoTimelineSourceBuilder.NormalizeGcdSlotSpacingSec(GetConfiguredAutoTimelineGcdRecastSec());
        var orderedEntries = tl.Entries
            .Select((entry, index) => new OrderedTimelineEntry(entry, index))
            .OrderBy(item => item.Entry.TimeOffsetSec)
            .ThenBy(item => item.Index)
            .ToList();

        int? nextGcdSlotIndex = null;
        int? nextOgcdSlotOrdinal = null;
        var movedCount = 0;

        foreach (var item in orderedEntries)
        {
            var entry = item.Entry;
            if (IsGcdEntry(entry))
            {
                var desiredSlotIndex = AutoTimelineSourceBuilder.ResolveGcdSlotIndex(entry.TimeOffsetSec, gcdSlotSpacingSec);
                var resolvedSlotIndex = nextGcdSlotIndex.HasValue
                    ? Math.Max(nextGcdSlotIndex.Value + 1, desiredSlotIndex)
                    : desiredSlotIndex;
                var alignedTimeSec = Math.Round(
                    AutoTimelineSourceBuilder.GetGcdSlotTimeSec(resolvedSlotIndex, gcdSlotSpacingSec),
                    3,
                    MidpointRounding.AwayFromZero);

                if (Math.Abs(entry.TimeOffsetSec - alignedTimeSec) > 0.0005)
                {
                    entry.TimeOffsetSec = alignedTimeSec;
                    movedCount++;
                }

                nextGcdSlotIndex = resolvedSlotIndex;
                continue;
            }

            var (cycleIndex, subslotIndex) = AutoTimelineSourceBuilder.ResolveOgcdSlotKey(entry.TimeOffsetSec, gcdSlotSpacingSec);
            var desiredSlotOrdinal = cycleIndex * 2 + Math.Clamp(subslotIndex, 0, 1);
            var resolvedSlotOrdinal = nextOgcdSlotOrdinal.HasValue
                ? Math.Max(nextOgcdSlotOrdinal.Value + 1, desiredSlotOrdinal)
                : desiredSlotOrdinal;
            var resolvedCycleIndex = (int)Math.Floor(resolvedSlotOrdinal / 2.0);
            var resolvedSubslotIndex = resolvedSlotOrdinal - resolvedCycleIndex * 2;
            var alignedOgcdTimeSec = Math.Round(
                AutoTimelineSourceBuilder.GetOgcdSlotTimeSec(resolvedCycleIndex, resolvedSubslotIndex, gcdSlotSpacingSec),
                3,
                MidpointRounding.AwayFromZero);

            if (Math.Abs(entry.TimeOffsetSec - alignedOgcdTimeSec) > 0.0005)
            {
                entry.TimeOffsetSec = alignedOgcdTimeSec;
                movedCount++;
            }

            nextOgcdSlotOrdinal = resolvedSlotOrdinal;
        }

        tl.Entries = tl.Entries
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();

        if (tl.Entries.Count > 0)
            tl.AverageDurationMs = Math.Max(tl.AverageDurationMs, tl.Entries.Max(entry => entry.TimeOffsetSec) * 1000.0);

        plugin.CustomTimelineStore.SaveTimeline(plugin.Configuration, key, tl);
        plugin.EncounterTracker.RebuildZoneMappings();
        customEditorDirty = false;
        InvalidateEncounterTimelineCaches();
        plugin.OverlayWindow.InvalidateTimelineCaches();
        ClearCustomEditorCaches();
        SetDebugStatus($"Auto aligned {movedCount} action{(movedCount == 1 ? string.Empty : "s")} in {BuildTimelineLinkLabel(key, tl)} to Auto Timeline GCD/oGCD slots.");
    }

    private sealed record OrderedTimelineEntry(TimelineEntry Entry, int Index);

    private const double AutoCooldownToleranceSec = 0.35;
    private const double AutoCastLockToleranceSec = 0.05;
    private const double ConflictCastLockToleranceSec = 1.00;
    private const double BaseActionAnimationLockSec = 0.60;
    private const double SimulatedAnimationLockLatencySec = 0.02;
    private const double AutoOgcdLockSec = BaseActionAnimationLockSec + SimulatedAnimationLockLatencySec;
    private const double ComboResetGapSec = 30.0;
    private const double AutoTargetGcdSec = 2.50;
    private const double AutoFirstGcdWindowSec = 3.0;
    private const double AutoGcdSearchBeforeSec = 0.80;
    private const double AutoGcdSearchAfterSec = 1.35;
    private const double AutoGcdDowntimeGapSec = 6.0;
    private const double AutoMaxForcedGcdGapSec = 3.0;
    private const double AutoBurstCadenceSec = 120.0;
    private const double AutoBurstEarlyWindowSec = 10.0;
    private const double AutoBurstLateWindowSec = 15.0;
    private const double AutoStructuredLookaheadSec = 1.75;
    private const double AutoSafeFallbackLookaheadSec = 4.5;
private const double AutoFutureLegalLookaheadSec = 6.0;
private const double AutoHighConfidenceGcdFrequencyPct = 25.0;
    private const double AutoStateDrivenOgcdMinFrequency = 0.00;
    private const double AutoGcdMinFrequency = 0.00;
    private const double AutoGcdSlotMatchToleranceSec = 0.01;

    private double GetConfiguredAutoTimelineGcdRecastSec()
        => Math.Round(Math.Clamp((double)plugin.Configuration.AutoTimelineGcdRecastSec, 2.0, 2.5), 2);

    private double GetConfiguredAutoTimelineDotRefreshBufferSec()
        => Math.Round(Math.Clamp((double)plugin.Configuration.AutoTimelineDotRefreshBufferSec, 0.0, 15.0), 1);

    private double GetDotRefreshReadySec(DotDatabase.DotRule dotRule)
        => Math.Max(0.0, dotRule.DurationSec - GetConfiguredAutoTimelineDotRefreshBufferSec());

    private static double GetAutoGcdSlotWindowStartSec(double slotTimeSec)
        => Math.Max(0.0, slotTimeSec - 1.0);

    private static double GetAutoGcdSlotWindowEndSec(double slotTimeSec)
        => slotTimeSec + 1.0;

    private sealed class AutoTimelineState
    {
        public List<TimelineEntry> SelectedEntries { get; set; } = [];
        public Dictionary<string, List<double>> CooldownQueues { get; set; } = [];
        public Dictionary<string, int> GaugeState { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> GrantedState { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ActionState { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> PassiveGaugeProgress { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> TimedWindowEndByAbility { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> TimedLockoutEndByAbility { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AvailableAbilityNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string SpecName { get; set; } = string.Empty;
        public string? CardDrawState { get; set; }
        public uint LastGcdId { get; set; }
        public string? LastComboAbilityName { get; set; }
        public string? LastSelectedGcdAbilityName { get; set; }
        public double LastComboTimeSec { get; set; } = double.NegativeInfinity;
        public double LastOgcdTimeSec { get; set; } = double.NegativeInfinity;
        public double CastLockUntilSec { get; set; }
        public double LastTimeSec { get; set; }
        public int NextOgcdIndex { get; set; }
        public double Score { get; set; }

        public AutoTimelineState Clone()
        {
            var cooldowns = new Dictionary<string, List<double>>(CooldownQueues.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (cooldownKey, queue) in CooldownQueues)
                cooldowns[cooldownKey] = [.. queue];

            return new AutoTimelineState
            {
                SelectedEntries = [.. SelectedEntries],
                CooldownQueues = cooldowns,
                GaugeState = new Dictionary<string, int>(GaugeState, StringComparer.OrdinalIgnoreCase),
                GrantedState = new Dictionary<string, int>(GrantedState, StringComparer.OrdinalIgnoreCase),
                ActionState = new Dictionary<string, int>(ActionState, StringComparer.OrdinalIgnoreCase),
                PassiveGaugeProgress = new Dictionary<string, double>(PassiveGaugeProgress, StringComparer.OrdinalIgnoreCase),
                TimedWindowEndByAbility = new Dictionary<string, double>(TimedWindowEndByAbility, StringComparer.OrdinalIgnoreCase),
                TimedLockoutEndByAbility = new Dictionary<string, double>(TimedLockoutEndByAbility, StringComparer.OrdinalIgnoreCase),
                AvailableAbilityNames = new HashSet<string>(AvailableAbilityNames, StringComparer.OrdinalIgnoreCase),
                SpecName = SpecName,
                CardDrawState = CardDrawState,
                LastGcdId = LastGcdId,
                LastComboAbilityName = LastComboAbilityName,
                LastSelectedGcdAbilityName = LastSelectedGcdAbilityName,
                LastComboTimeSec = LastComboTimeSec,
                LastOgcdTimeSec = LastOgcdTimeSec,
                CastLockUntilSec = CastLockUntilSec,
                LastTimeSec = LastTimeSec,
                NextOgcdIndex = NextOgcdIndex,
                Score = Score,
            };
        }
    }

    private sealed class AutoTimelineBuildResult
    {
        public required List<TimelineEntry> Entries { get; init; }
        public string DebugReport { get; init; } = string.Empty;
    }

    private sealed class AutoGaugeLegalitySegmentDecision
    {
        public required TimelineEntry Entry { get; init; }
        public required bool Keep { get; init; }
        public required string Summary { get; init; }
        public string? BeforeGaugeState { get; init; }
        public string? AfterGaugeState { get; init; }
        public bool UsedOpenerBorrow { get; init; }
    }

    private sealed class AutoGaugeLegalityBranch
    {
        public required AutoTimelineState State { get; init; }
        public required List<AutoGaugeLegalitySegmentDecision> Decisions { get; init; }
        public required double Score { get; init; }
    }

    private sealed class AutoGrantedChildRule
    {
        public string ChildAbilityName { get; init; } = string.Empty;
        public HashSet<string> ParentAbilityNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ConsumerAbilityNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public int AllowedUsesPerParentWindow { get; init; } = 1;
        public double? ParentWindowDurationSec { get; init; }
        public AutoGrantedWindowSelectionMode WindowSelectionMode { get; init; } = AutoGrantedWindowSelectionMode.FirstChronological;
    }

    private enum AutoGrantedWindowSelectionMode
    {
        FirstChronological,
        HighestFrequencyPerParentWindow,
    }

    private sealed class AutoComboBranchContext
    {
        public string StarterAbilityName { get; init; } = string.Empty;
        public int StarterSlotIndex { get; init; }
        public double LastMatchedTimeSec { get; init; }
        public double LastMatchedRecastSec { get; init; } = AutoTargetGcdSec;
        public int NextStepIndex { get; init; }
        public IReadOnlyList<string> ChosenLine { get; init; } = [];
        public HashSet<string> BlockedAbilityNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string ChosenLineSummary { get; init; } = string.Empty;
    }

    private sealed class AutoOpenerBufferInfo
    {
        public bool Enabled { get; init; }
        public string VariantName { get; init; } = string.Empty;
        public int StartSlotOffset { get; init; }
        public int GcdSlotCount { get; init; }
        public double EndTimeSec { get; init; }
        public IReadOnlyList<string> GcdSequence { get; init; } = [];
        public IReadOnlyList<BalanceOpenerDatabase.OpenerStep> Steps { get; init; } = [];
    }

    private sealed class AutoTimelineDebugRecorder
    {
        private readonly List<string> lines = [];

        public void Add(string line = "")
            => lines.Add(line);

        public string Build()
            => string.Join(Environment.NewLine, lines);
    }

    private sealed class AutoGcdCandidateEvaluation
    {
        public required TimelineEntry Entry { get; init; }
        public required double ScheduledTimeSec { get; init; }
        public required bool IsLegal { get; init; }
        public required bool ComboMismatch { get; init; }
        public required bool CooldownBlocked { get; init; }
        public required double CooldownEarlyBySec { get; init; }
        public required int GaugePenalty { get; init; }
        public required int ActionPenalty { get; init; }
        public required int CardPenalty { get; init; }
        public required int Penalty { get; init; }
        public required double Score { get; init; }
        public string FailureReason { get; init; } = string.Empty;
    }

    private sealed class AutoGrantedCandidateDecision
    {
        public bool IsTracked { get; init; }
        public bool IsAllowed { get; init; }
        public string Summary { get; init; } = string.Empty;
        public AutoGrantedChildRule? ChildRule { get; init; }
        public TimelineEntry? ParentEntry { get; init; }
        public TimelineEntry? ExistingWindowConsumerEntry { get; init; }
        public bool UsesVirtualParentWindow { get; init; }
    }

    private sealed class AutoDelayedParentRequirementRule
    {
        public string SpecName { get; init; } = string.Empty;
        public string DelayedParentAbilityName { get; init; } = string.Empty;
        public string RequiredAbilityName { get; init; } = string.Empty;
        public string RequiredBuffAbilityName { get; init; } = string.Empty;
        public double RequiredBuffDurationSec { get; init; }
    }

    private static readonly AutoDelayedParentRequirementRule[] AutoDelayedParentRequirementRules =
    {
        new()
        {
            SpecName = "Astrologian",
            DelayedParentAbilityName = "Umbral Draw",
            RequiredAbilityName = "Lord of Crowns",
            RequiredBuffAbilityName = "Divination",
            RequiredBuffDurationSec = 20.0,
        },
        new()
        {
            SpecName = "Scholar",
            DelayedParentAbilityName = "Aetherflow",
            RequiredAbilityName = "Energy Drain",
            RequiredBuffAbilityName = "Chain Stratagem",
            RequiredBuffDurationSec = 20.0,
        },
        new()
        {
            SpecName = "Scholar",
            DelayedParentAbilityName = "Dissipation",
            RequiredAbilityName = "Energy Drain",
            RequiredBuffAbilityName = "Chain Stratagem",
            RequiredBuffDurationSec = 20.0,
        },
    };

    private static List<AutoDelayedParentRequirementRule> FindDelayedParentRequirementRules(
        string specName,
        string delayedParentAbilityName,
        IEnumerable<string>? requiredAbilityNames = null)
    {
        var requiredAbilitySet = requiredAbilityNames?
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return AutoDelayedParentRequirementRules
            .Where(rule =>
                string.Equals(rule.SpecName, specName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(rule.DelayedParentAbilityName, delayedParentAbilityName, StringComparison.OrdinalIgnoreCase) &&
                (requiredAbilitySet == null || requiredAbilitySet.Contains(rule.RequiredAbilityName)))
            .ToList();
    }

    private static readonly Dictionary<string, string[]> StateAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hawk's Eye"] = ["Refulgent Arrow Ready"],
        ["Threefold Fan Dance"] = ["Fan Dance III Ready"],
        ["Fourfold Fan Dance"] = ["Fan Dance III Ready"],
        ["Hypervelocity Ready"] = ["Ready to Blast"],
        ["Jugular Rip Ready"] = ["Ready to Rip"],
        ["Abdomen Tear Ready"] = ["Ready to Tear"],
        ["Eye Gouge Ready"] = ["Ready to Gouge"],
        ["Oracle Ready"] = ["Divining"],
        ["Glare IV Ready"] = ["Sacred Sight"],
    };

    private static readonly Dictionary<string, string[]> ComboPredecessorAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["True Thrust"] = ["Raiden Thrust"],
    };

    private static readonly HashSet<string> ComboValidationExemptAbilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "Drakesbane",
        "Gekko",
        "Kasha",
        "Solid Barrel",
    };

    private static IEnumerable<string> EnumerateEquivalentStateNames(string stateName)
    {
        yield return stateName;
        if (StateAliases.TryGetValue(stateName, out var aliases))
        {
            foreach (var alias in aliases)
                yield return alias;
        }
    }

    private bool ShouldIgnoreConflictRequirement(string specName, string requirementName)
    {
        if (!ConflictIgnoredRequirementNamesBySpec.TryGetValue(specName, out var ignoredNames))
            return false;

        foreach (var equivalentStateName in EnumerateEquivalentStateNames(requirementName))
        {
            if (ignoredNames.Contains(equivalentStateName))
                return true;
        }

        return false;
    }

    private bool ShouldIgnoreConflictCooldown(string specName, string abilityName)
    {
        return ConflictIgnoredCooldownNamesBySpec.TryGetValue(specName, out var ignoredNames) &&
               ignoredNames.Contains(abilityName);
    }

    private static string GetCooldownBucketKey(
        Data.RecastDatabase.RecastInfo? info,
        int fallbackAbilityId = 0,
        string fallbackAbilityName = "")
    {
        if (info != null && info.AbilityId > 0)
            return $"ability:{info.AbilityId}";
        if (fallbackAbilityId > 0)
            return $"ability:{fallbackAbilityId}";

        return $"name:{fallbackAbilityName}";
    }

    private void ApplyAutoTimeline(AggregatedTimeline tl)
    {
        var timelineKey = TimelineDatabase.MakeKey(tl.EncounterId, tl.SpecName);
        var isCustomTimeline = plugin.Configuration.CustomTimelines.ContainsKey(timelineKey);
        var allowedOutputAbilityIds = tl.Entries
            .Select(entry => entry.AbilityId)
            .ToHashSet();
        var sourceTimeline = PrepareAutoTimelineSourceClone(tl);
        var debugEnabled = plugin.Configuration.DebugEnabled;
        var runToken = debugEnabled ? DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") : null;
        var result = BuildAutoTimelineResult(sourceTimeline, captureDebug: debugEnabled);
        var visibleEntries = FilterVisibleAutoTimelineResultEntries(result.Entries);
        var finalEntries = isCustomTimeline
            ? FilterAutoTimelineResultEntries(visibleEntries, allowedOutputAbilityIds)
            : visibleEntries;
        if (debugEnabled)
        {
            var autoDebugReport = finalEntries.Count != result.Entries.Count
                ? string.Concat(
                    result.DebugReport,
                    Environment.NewLine,
                    Environment.NewLine,
                    $"Output Filter{Environment.NewLine}  filtered {result.Entries.Count - finalEntries.Count} reasoning-only entries not present in the copied custom timeline skill set.")
                : result.DebugReport;
            SetLatestAutoTimelineDebugReport(tl.SpecName, autoDebugReport);
            SaveTimelineSnapshot(sourceTimeline, "pre_auto_timeline", runToken);
            if (!string.IsNullOrWhiteSpace(autoDebugReport))
                SaveDebugTextArtifact(tl, autoDebugReport, "auto_timeline_debug", runToken);
            else
                SetDebugStatus("Auto Timeline completed, but no debug report was generated.", true);
        }
        tl.Entries = finalEntries;
        if (debugEnabled)
        {
            SaveTimelineSnapshot(tl, "post_auto_timeline", runToken);
            SaveCachedFflogsDebugArtifacts(sourceTimeline, runToken);
        }
    }

    private AutoTimelineBuildResult BuildAutoTimelineResult(AggregatedTimeline tl, bool captureDebug)
    {
        var debug = captureDebug ? new AutoTimelineDebugRecorder() : null;
        var gcdEntries = tl.Entries
            .Where(IsGcdEntry)
            .OrderBy(e => e.TimeOffsetSec)
            .ThenByDescending(e => e.Frequency)
            .ToList();

        var ogcdEntries = tl.Entries
            .Where(e => !IsGcdEntry(e))
            .OrderBy(e => e.TimeOffsetSec)
            .ThenByDescending(e => e.Frequency)
            .ToList();

        debug?.Add($"Auto Timeline | {tl.EncounterName} / {tl.SpecName}");
        debug?.Add($"Duration: {tl.AverageDurationMs / 1000.0:F3}s");
        debug?.Add($"Source entries: {tl.Entries.Count} | GCD: {gcdEntries.Count} | oGCD: {ogcdEntries.Count}");
        debug?.Add($"Rules: GCD >= {(AutoGcdMinFrequency * 100.0):F1}% | oGCD >= {(AutoStateDrivenOgcdMinFrequency * 100.0):F1}% | slot width {GetConfiguredAutoTimelineGcdRecastSec():F2}s | cooldown tolerance {AutoCooldownToleranceSec:F2}s");
        debug?.Add("Legend: keep = selected by the current pass, prune/block = removed by a rule, lose = eligible but beaten by another candidate in the same choice.");
        debug?.Add();
        var rawAbilityNames = tl.Entries
            .Select(entry => entry.AbilityName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fightDurationSec = Math.Max(0.0, tl.AverageDurationMs / 1000.0);
        var openerBuffer = BuildAutoOpenerBufferInfo(gcdEntries, tl.SpecName, debug);

        var selectedOgcdEntries = SelectAutoOgcdEntries(
            ogcdEntries,
            tl.SpecName,
            openerBuffer,
            debug);
        var normalizedSelectedOgcdEntries = string.Equals(tl.SpecName, "Scholar", StringComparison.OrdinalIgnoreCase)
            ? ApplyScholarOpenerDissipationSequence(ogcdEntries, selectedOgcdEntries, debug)
            : selectedOgcdEntries;
        var selectedGcdEntries = SelectAutoGcdEntries(
            gcdEntries,
            normalizedSelectedOgcdEntries,
            tl.SpecName,
            fightDurationSec,
            openerBuffer,
            debug);
        (selectedGcdEntries, selectedOgcdEntries) = ApplyGrantedActionSelections(
            selectedGcdEntries,
            ogcdEntries,
            normalizedSelectedOgcdEntries,
            tl.SpecName,
            openerBuffer,
            debug);
        if (string.Equals(tl.SpecName, "Scholar", StringComparison.OrdinalIgnoreCase))
        {
            selectedOgcdEntries = ApplyScholarEnergyDrainWeaveSlotRule(selectedGcdEntries, selectedOgcdEntries, debug);
            selectedOgcdEntries = RebaseMovedOgcdCooldownChains(ogcdEntries, selectedOgcdEntries, debug);
            selectedOgcdEntries = RebaseMovedScholarTimedLockoutEntries(ogcdEntries, selectedOgcdEntries, debug);
            selectedOgcdEntries = RebaseMovedOgcdCooldownChains(ogcdEntries, selectedOgcdEntries, debug);
            selectedOgcdEntries = ApplyScholarDissipationSelectionRules(ogcdEntries, selectedOgcdEntries, openerBuffer, debug);
            selectedOgcdEntries = RebaseMovedOgcdCooldownChains(ogcdEntries, selectedOgcdEntries, debug);
        }
        selectedGcdEntries = TrimAutoTimelineEntriesToFightDuration(selectedGcdEntries, fightDurationSec, "GCD", debug);
        selectedOgcdEntries = TrimAutoTimelineEntriesToFightDuration(selectedOgcdEntries, fightDurationSec, "oGCD", debug);
        var finalEntries = BuildAutoTimelineWithFixedGcds(
            selectedGcdEntries,
            selectedOgcdEntries,
            tl.SpecName,
            rawAbilityNames,
            openerBuffer,
            debug);
        finalEntries = TrimAutoTimelineEntriesToFightDuration(finalEntries, fightDurationSec, "scheduled", debug);

        debug?.Add();
        debug?.Add($"Final selected GCDs: {selectedGcdEntries.Count}");
        debug?.Add($"Final selected oGCDs: {selectedOgcdEntries.Count}");
        debug?.Add($"Final scheduled entries: {finalEntries.Count}");

        return new AutoTimelineBuildResult
        {
            Entries = finalEntries,
            DebugReport = debug?.Build() ?? string.Empty,
        };
    }

    private static List<TimelineEntry> TrimAutoTimelineEntriesToFightDuration(
        IEnumerable<TimelineEntry> entries,
        double fightDurationSec,
        string debugLabel,
        AutoTimelineDebugRecorder? debug)
    {
        var orderedEntries = entries
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
        if (fightDurationSec <= 0.0 || orderedEntries.Count == 0)
            return orderedEntries;

        var keptEntries = orderedEntries
            .Where(entry => entry.TimeOffsetSec <= fightDurationSec + AutoCooldownToleranceSec)
            .ToList();
        var trimmedCount = orderedEntries.Count - keptEntries.Count;
        if (trimmedCount > 0)
        {
            debug?.Add($"  duration clamp | trimmed {trimmedCount} {debugLabel} entr{(trimmedCount == 1 ? "y" : "ies")} beyond fight end {FormatTime(fightDurationSec)}");
        }

        return keptEntries;
    }

    private List<TimelineEntry> SelectAutoGcdEntries(
        List<TimelineEntry> gcdEntries,
        List<TimelineEntry> selectedOgcdEntries,
        string specName,
        double fightDurationSec,
        AutoOpenerBufferInfo openerBuffer,
        AutoTimelineDebugRecorder? debug = null)
    {
        debug?.Add("GCD Selection");
        var comboHints = JobComboDatabase.GetHints(specName);
        var gaugeRules = GaugeSimulator.GetRules(specName);
        var grantedRules = GrantedActionDatabase.GetRules(specName);
        var grantedChildRules = BuildGrantedChildRules(gcdEntries.Concat(selectedOgcdEntries), grantedRules);
        var keptEntries = new List<TimelineEntry>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockedUntilByAbility = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        AutoComboBranchContext? activeComboBranch = null;
        var sortedGcdEntries = gcdEntries
            .Where(entry => entry.TimeOffsetSec <= fightDurationSec + AutoCooldownToleranceSec)
            .OrderBy(e => e.TimeOffsetSec)
            .ThenByDescending(e => e.Frequency)
            .ToList();
        if (sortedGcdEntries.Count == 0)
        {
            debug?.Add("  No GCD entries available.");
            debug?.Add();
            return keptEntries;
        }

        debug?.Add(openerBuffer.Enabled
            ? $"  opener guideline active; variant {openerBuffer.VariantName} starts at opener slot {openerBuffer.StartSlotOffset} and runs until {FormatTime(openerBuffer.EndTimeSec)}"
            : "  opener guideline inactive; no opener buffer is active");

        var rawAbilityNames = sortedGcdEntries
            .Concat(selectedOgcdEntries)
            .Select(entry => entry.AbilityName)
            .Where(abilityName => !string.IsNullOrWhiteSpace(abilityName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectionState = CreateAutoTimelineState(specName, gaugeRules, grantedRules, rawAbilityNames);

        var nextSlotTimeSec = 0.0;
        var slotIndex = 0;
        while (nextSlotTimeSec <= fightDurationSec + AutoCooldownToleranceSec)
        {
            AdvanceAutoTimelineState(selectionState, gaugeRules, grantedRules, selectedOgcdEntries, nextSlotTimeSec);

            if (IsAutoComboContextExpired(activeComboBranch, nextSlotTimeSec))
                activeComboBranch = null;

            var openerRequirement = GetAutoOpenerSlotRequirement(openerBuffer, slotIndex);
            var exactSlotCandidates = GetAutoGcdSlotCandidates(sortedGcdEntries, usedKeys, nextSlotTimeSec);
            var rawSlotCandidates = new List<TimelineEntry>(exactSlotCandidates);
            var openerLookaroundCount = 0;
            if (!string.IsNullOrWhiteSpace(openerRequirement))
            {
                foreach (var openerCandidate in GetAutoOpenerRequirementCandidates(sortedGcdEntries, usedKeys, nextSlotTimeSec, openerRequirement))
                {
                    if (rawSlotCandidates.Any(existing =>
                            string.Equals(GetAutoEntryIdentityKey(existing), GetAutoEntryIdentityKey(openerCandidate), StringComparison.OrdinalIgnoreCase)))
                        continue;

                    rawSlotCandidates.Add(openerCandidate);
                    openerLookaroundCount++;
                }
            }
            var comboLookaroundCount = 0;
            foreach (var comboCandidate in GetAutoComboContinuationCandidates(sortedGcdEntries, usedKeys, activeComboBranch, nextSlotTimeSec))
            {
                if (rawSlotCandidates.Any(existing =>
                        string.Equals(GetAutoEntryIdentityKey(existing), GetAutoEntryIdentityKey(comboCandidate), StringComparison.OrdinalIgnoreCase)))
                    continue;

                rawSlotCandidates.Add(comboCandidate);
                comboLookaroundCount++;
            }

            debug?.Add($"  slot | #{slotIndex:00} | {FormatAutoDebugSlotWindow(nextSlotTimeSec)} | raw candidates {exactSlotCandidates.Count}");
            if (IsWithinAutoOpenerBuffer(openerBuffer, nextSlotTimeSec))
                debug?.Add($"    opener buffer | active until {FormatTime(openerBuffer.EndTimeSec)}; shifted opener, granted, and gauge rules may borrow pre-pull state here");
            if (!string.IsNullOrWhiteSpace(openerRequirement))
                debug?.Add($"    opener requirement | expects {openerRequirement} from {openerBuffer.VariantName}");
            if (openerLookaroundCount > 0)
                debug?.Add($"    opener lookaround | added {openerLookaroundCount} matching candidate(s) from {FormatTime(Math.Max(0.0, GetAutoGcdSlotWindowStartSec(nextSlotTimeSec) - GetConfiguredAutoTimelineGcdRecastSec()))}-{FormatTime(GetAutoGcdSlotWindowEndSec(nextSlotTimeSec) + GetConfiguredAutoTimelineGcdRecastSec())}");
            if (TryGetAutoComboContinuationWindow(activeComboBranch, out var comboWindowStartSec, out var comboWindowEndSec) &&
                GetAutoComboNextAbility(activeComboBranch) is { } comboContinuationAbility)
                debug?.Add($"    combo window | next {comboContinuationAbility} is favored in {FormatTime(comboWindowStartSec)}-{FormatTime(comboWindowEndSec)}");
            if (comboLookaroundCount > 0)
                debug?.Add($"    combo lookaround | added {comboLookaroundCount} continuation candidate(s) from the active combo window");
            if (activeComboBranch != null && !string.IsNullOrWhiteSpace(activeComboBranch.ChosenLineSummary))
                debug?.Add($"    combo context | {activeComboBranch.ChosenLineSummary}");

            var openerMatchingSlotCandidates = new List<TimelineEntry>();
            var fallbackSlotCandidates = new List<TimelineEntry>();
            foreach (var candidate in rawSlotCandidates)
            {
                var blockers = GetGcdCandidateBlockers(
                    specName,
                    selectionState,
                    candidate,
                    nextSlotTimeSec,
                    keptEntries,
                    selectedOgcdEntries,
                    grantedChildRules,
                    activeComboBranch,
                    blockedUntilByAbility,
                    openerBuffer,
                    openerRequirement,
                    sortedGcdEntries,
                    usedKeys);
                if (blockers.Count > 0)
                {
                    debug?.Add($"    block | {FormatAutoDebugEntry(candidate)} | {string.Join("; ", blockers)}");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(openerRequirement) &&
                    !DoesOpenerAbilityMatch(candidate.AbilityName, openerRequirement))
                {
                    fallbackSlotCandidates.Add(candidate);
                    debug?.Add($"    allow-fallback | {FormatAutoDebugEntry(candidate)} | opener preference did not match {openerRequirement}, but the candidate remains available if no opener match survives");
                    continue;
                }

                openerMatchingSlotCandidates.Add(candidate);
                debug?.Add($"    allow | {FormatAutoDebugEntry(candidate)} | {GetGcdCandidateEligibilitySummary(candidate, nextSlotTimeSec, keptEntries, selectedOgcdEntries, grantedChildRules, comboHints, activeComboBranch, openerBuffer, openerRequirement)}");
            }

            var slotCandidates = openerMatchingSlotCandidates.Count > 0
                ? openerMatchingSlotCandidates
                : fallbackSlotCandidates;
            if (!string.IsNullOrWhiteSpace(openerRequirement) &&
                openerMatchingSlotCandidates.Count == 0 &&
                fallbackSlotCandidates.Count > 0)
            {
                debug?.Add($"    opener fallback | no surviving match for {openerRequirement}; using {fallbackSlotCandidates.Count} non-opener candidate(s) instead of dropping the slot");
            }

            TimelineEntry? chosenEntry = null;
            if (slotCandidates.Count > 0)
            {
                chosenEntry = FindForcedDrkBloodspillerCandidate(
                    specName,
                    selectionState,
                    slotCandidates,
                    keptEntries,
                    selectedOgcdEntries,
                    sortedGcdEntries,
                    usedKeys,
                    nextSlotTimeSec);
            }

            if (slotCandidates.Count > 0)
            {
                chosenEntry ??= FindDueComboContinuationCandidate(
                    slotCandidates,
                    activeComboBranch,
                    nextSlotTimeSec);
            }

            if (chosenEntry == null && slotCandidates.Count > 0)
            {
                chosenEntry = FindDueGrantedChildCandidate(
                    slotCandidates,
                    keptEntries,
                    selectedOgcdEntries,
                    grantedChildRules,
                    openerBuffer,
                    nextSlotTimeSec);
            }

            if (chosenEntry == null)
            {
                chosenEntry = FindDueDotCandidate(slotCandidates, keptEntries);
            }

            if (chosenEntry == null)
            {
                chosenEntry = slotCandidates
                    .OrderByDescending(entry => entry.Frequency)
                    .ThenBy(entry => entry.TimeOffsetSec)
                    .FirstOrDefault();
            }

            if (chosenEntry != null)
            {
                var isComboContinuation = IsComboContinuationMatch(chosenEntry, activeComboBranch);
                var winnerReason = BuildGcdSelectionReason(
                    specName,
                    chosenEntry,
                    nextSlotTimeSec,
                    slotCandidates,
                    keptEntries,
                    selectedOgcdEntries,
                    grantedChildRules,
                    comboHints,
                    activeComboBranch,
                    openerBuffer,
                    openerRequirement);
                var scheduledChosenEntry = KeepAutoGcdSelection(
                    chosenEntry,
                    nextSlotTimeSec,
                    keptEntries,
                    usedKeys,
                    blockedUntilByAbility,
                    comboHints,
                    debug,
                    isComboContinuation
                        ? "combo follow"
                        : string.Equals(specName, "Dark Knight", StringComparison.OrdinalIgnoreCase) &&
                          string.Equals(chosenEntry.AbilityName, "Bloodspiller", StringComparison.OrdinalIgnoreCase)
                            ? "forced gauge spend"
                        : grantedChildRules.ContainsKey(chosenEntry.AbilityName)
                        ? "granted"
                        : DotDatabase.Lookup(chosenEntry.AbilityName) != null
                            ? "dot"
                            : JobComboDatabase.IsComboStarter(chosenEntry.AbilityName)
                                ? "combo start"
                                : "generic",
                    winnerReason);

                var chosenInfo = plugin.RecastDatabase.Lookup(scheduledChosenEntry.AbilityId, scheduledChosenEntry.AbilityName);
                AdvancePassiveResources(selectionState, gaugeRules, scheduledChosenEntry.TimeOffsetSec);
                ApplyAutoEntry(selectionState, scheduledChosenEntry, chosenInfo, gaugeRules, grantedRules, scheduledChosenEntry.TimeOffsetSec, isGcd: true);

                foreach (var losingCandidate in slotCandidates.Where(candidate =>
                             !string.Equals(GetAutoEntryIdentityKey(candidate), GetAutoEntryIdentityKey(chosenEntry), StringComparison.OrdinalIgnoreCase)))
                {
                    debug?.Add($"    lose | {FormatAutoDebugEntry(losingCandidate)} | eligible, but {chosenEntry.AbilityName} won this slot because {winnerReason}");
                }

                if (JobComboDatabase.IsComboStarter(chosenEntry.AbilityName))
                {
                    activeComboBranch = SelectAutoComboBranchContext(
                        comboHints,
                        chosenEntry.AbilityName,
                        slotIndex,
                        scheduledChosenEntry.TimeOffsetSec,
                        sortedGcdEntries,
                        usedKeys,
                        debug);
                }
                else
                {
                    activeComboBranch = AdvanceAutoComboBranchContext(
                        activeComboBranch,
                        scheduledChosenEntry);
                }
            }
            else
            {
                debug?.Add(rawSlotCandidates.Count == 0
                    ? $"    result | no in-window entries above {(AutoGcdMinFrequency * 100.0):F1}% remained for this slot"
                    : !string.IsNullOrWhiteSpace(openerRequirement)
                        ? $"    result | opener requirement expected {openerRequirement}, but no matching candidate survived this slot"
                        : "    result | every in-window candidate was blocked by an active pruning rule");
                debug?.Add($"  slot-drop | {FormatTime(nextSlotTimeSec)} | no candidate");
            }

            var chosenSlotIntervalSec = chosenEntry != null
                ? GetAutoTimelineGcdRecastSec(chosenEntry)
                : GetConfiguredAutoTimelineGcdRecastSec();
            var nextSlotAnchorSec = GetNextAutoGcdSlotStartSec(nextSlotTimeSec, chosenEntry, chosenSlotIntervalSec);
            if (chosenEntry != null &&
                Math.Abs(nextSlotAnchorSec - (nextSlotTimeSec + GetConfiguredAutoTimelineGcdRecastSec())) > 0.01)
            {
                debug?.Add($"  slot-anchor | next slot re-anchored to {FormatTime(nextSlotAnchorSec)} from kept {FormatAutoDebugEntry(chosenEntry)} to preserve GCD cadence");
            }

            slotIndex++;
            nextSlotTimeSec = nextSlotAnchorSec;
        }

        debug?.Add();
        return keptEntries
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private (List<TimelineEntry> GcdEntries, List<TimelineEntry> OgcdEntries) ApplyGrantedActionSelections(
        List<TimelineEntry> baseSelectedGcdEntries,
        List<TimelineEntry> sourceOgcdEntries,
        List<TimelineEntry> baseSelectedOgcdEntries,
        string specName,
        AutoOpenerBufferInfo openerBuffer,
        AutoTimelineDebugRecorder? debug = null)
    {
        debug?.Add("Granted Actions");
        var grantedRules = GrantedActionDatabase.GetRules(specName);
        if (string.Equals(specName, "Astrologian", StringComparison.OrdinalIgnoreCase))
            baseSelectedOgcdEntries = ApplyAstrologianDrawCardSelections(sourceOgcdEntries, baseSelectedOgcdEntries, grantedRules, debug);

        var grantedChildRules = BuildGrantedChildRules(baseSelectedGcdEntries.Concat(baseSelectedOgcdEntries), grantedRules);
        RemoveAstrologianCardDrawGrantedRules(grantedChildRules, grantedRules);

        if (grantedChildRules.Count == 0)
        {
            debug?.Add("  none configured");
            debug?.Add();
            return (baseSelectedGcdEntries, baseSelectedOgcdEntries);
        }

        if (openerBuffer.Enabled)
            debug?.Add($"  opener buffer | missing parents before {FormatTime(openerBuffer.EndTimeSec)} may be borrowed from pre-pull state once per child window");

        var keptEntries = new List<TimelineEntry>();
        foreach (var entry in baseSelectedGcdEntries
                     .Concat(baseSelectedOgcdEntries)
                     .OrderBy(item => item.TimeOffsetSec)
                     .ThenByDescending(item => item.Frequency))
        {
            var grantedDecision = AnalyzeGrantedCandidateDecision(entry, keptEntries, grantedChildRules, openerBuffer);
            if (!grantedDecision.IsTracked)
            {
                keptEntries.Add(entry);
                continue;
            }

            if (!grantedDecision.IsAllowed)
            {
                debug?.Add($"  prune | {FormatAutoDebugEntry(entry)} | {grantedDecision.Summary}");
                continue;
            }

            keptEntries.Add(entry);
            debug?.Add($"  keep | {FormatAutoDebugEntry(entry)} | {grantedDecision.Summary}");
        }

        debug?.Add();
        return (
            keptEntries.Where(IsGcdEntry).OrderBy(entry => entry.TimeOffsetSec).ThenByDescending(entry => entry.Frequency).ToList(),
            keptEntries.Where(entry => !IsGcdEntry(entry)).OrderBy(entry => entry.TimeOffsetSec).ThenByDescending(entry => entry.Frequency).ToList());
    }

    private static void RemoveAstrologianCardDrawGrantedRules(
        Dictionary<string, AutoGrantedChildRule> grantedChildRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        if (grantedRules?.CardDraw is not { } astCardDraw)
            return;

        grantedChildRules.Remove(astCardDraw.AstralDrawName);
        grantedChildRules.Remove(astCardDraw.UmbralDrawName);
        foreach (var abilityName in astCardDraw.AstralCards)
            grantedChildRules.Remove(abilityName);
        foreach (var abilityName in astCardDraw.UmbralCards)
            grantedChildRules.Remove(abilityName);
    }

    private List<TimelineEntry> ApplyAstrologianDrawCardSelections(
        List<TimelineEntry> sourceOgcdEntries,
        List<TimelineEntry> baseSelectedOgcdEntries,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        AutoTimelineDebugRecorder? debug)
    {
        if (grantedRules?.CardDraw is not { } cardDraw)
            return baseSelectedOgcdEntries;

        var astralCards = cardDraw.AstralCards;
        var umbralCards = cardDraw.UmbralCards;
        var drawNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            cardDraw.AstralDrawName,
            cardDraw.UmbralDrawName,
        };
        var astAbilityNames = new HashSet<string>(drawNames, StringComparer.OrdinalIgnoreCase);
        foreach (var cardName in astralCards)
            astAbilityNames.Add(cardName);
        foreach (var cardName in umbralCards)
            astAbilityNames.Add(cardName);

        var result = baseSelectedOgcdEntries
            .Where(entry => !astAbilityNames.Contains(entry.AbilityName))
            .ToList();
        var astSourceEntries = sourceOgcdEntries
            .Where(entry => astAbilityNames.Contains(entry.AbilityName))
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();

        debug?.Add("  astrologian draw hands");
        const double AstrologianDrawCooldownSec = 55.0;
        const double AstrologianDrawReadyWindowSec = 5.0;
        var selectedOgcdEntriesByAbility = baseSelectedOgcdEntries
            .GroupBy(entry => entry.AbilityName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(entry => entry.TimeOffsetSec).ThenByDescending(entry => entry.Frequency).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var selectedAstEntries = new List<TimelineEntry>();

        TimelineEntry? FindSelectedOgcdEntryInWindow(
            string abilityName,
            double minimumTimeSec,
            double maximumTimeSec)
        {
            if (!selectedOgcdEntriesByAbility.TryGetValue(abilityName, out var entries))
                return null;

            return entries.FirstOrDefault(entry =>
                entry.TimeOffsetSec >= minimumTimeSec - AutoCooldownToleranceSec &&
                entry.TimeOffsetSec <= maximumTimeSec + AutoCooldownToleranceSec);
        }

        TimelineEntry? FindNextSelectedOgcdEntry(
            string abilityName,
            double minimumTimeSec)
        {
            if (!selectedOgcdEntriesByAbility.TryGetValue(abilityName, out var entries))
                return null;

            return entries.FirstOrDefault(entry =>
                entry.TimeOffsetSec >= minimumTimeSec - AutoCooldownToleranceSec);
        }

        TimelineEntry? ChooseWindowAbility(
            string abilityName,
            double windowStartSec,
            double windowEndSec,
            double minimumTimeSec = double.NegativeInfinity,
            double maximumTimeSec = double.PositiveInfinity)
        {
            var candidates = astSourceEntries
                .Where(entry =>
                    string.Equals(entry.AbilityName, abilityName, StringComparison.OrdinalIgnoreCase) &&
                    entry.TimeOffsetSec >= windowStartSec - AutoCooldownToleranceSec &&
                    entry.TimeOffsetSec <= windowEndSec + AutoCooldownToleranceSec &&
                    entry.TimeOffsetSec >= minimumTimeSec - AutoCooldownToleranceSec &&
                    entry.TimeOffsetSec <= maximumTimeSec + AutoCooldownToleranceSec)
                .ToList();
            if (candidates.Count == 0)
                return null;

            var aboveThresholdCandidates = candidates
                .Where(entry => entry.Frequency >= AutoStateDrivenOgcdMinFrequency)
                .ToList();
            var pool = aboveThresholdCandidates.Count > 0
                ? aboveThresholdCandidates
                : candidates;
            return pool
                .OrderByDescending(entry => entry.Frequency)
                .ThenBy(entry => entry.TimeOffsetSec)
                .FirstOrDefault();
        }

        bool TryBuildHandSelections(
            string handLabel,
            IReadOnlyList<string> handCards,
            double windowStartSec,
            double windowEndSec,
            IReadOnlyList<AutoDelayedParentRequirementRule> delayedParentRequirementRules,
            out List<TimelineEntry> chosenCards)
        {
            chosenCards = [];
            var requiredBuffEntries = delayedParentRequirementRules
                .Select(rule => new
                {
                    Rule = rule,
                    BuffEntry = FindSelectedOgcdEntryInWindow(
                        rule.RequiredBuffAbilityName,
                        windowStartSec,
                        windowEndSec),
                })
                .ToDictionary(item => item.Rule, item => item.BuffEntry);

            foreach (var cardName in handCards)
            {
                var minimumTimeSec = windowStartSec;
                var maximumTimeSec = double.PositiveInfinity;
                var delayedParentRequirementRule = delayedParentRequirementRules.FirstOrDefault(rule =>
                    string.Equals(cardName, rule.RequiredAbilityName, StringComparison.OrdinalIgnoreCase));
                if (delayedParentRequirementRule != null)
                {
                    var requiredBuffEntry = requiredBuffEntries[delayedParentRequirementRule];
                    if (requiredBuffEntry == null)
                    {
                        debug?.Add($"  prune | {handLabel} hand | {delayedParentRequirementRule.RequiredAbilityName} requires {delayedParentRequirementRule.RequiredBuffAbilityName} inside {FormatTime(windowStartSec)}-{FormatTime(windowEndSec)}");
                        return false;
                    }

                    minimumTimeSec = requiredBuffEntry.TimeOffsetSec + AutoCooldownToleranceSec;
                    maximumTimeSec = requiredBuffEntry.TimeOffsetSec + delayedParentRequirementRule.RequiredBuffDurationSec;
                }

                var chosenCard = ChooseWindowAbility(cardName, windowStartSec, windowEndSec, minimumTimeSec, maximumTimeSec);
                if (chosenCard == null)
                {
                    debug?.Add($"  prune | {handLabel} hand | no {cardName} candidate inside {FormatTime(windowStartSec)}-{FormatTime(windowEndSec)}");
                    return false;
                }

                chosenCards.Add(chosenCard);
            }

            foreach (var chosenCard in chosenCards
                         .OrderBy(entry => entry.TimeOffsetSec)
                         .ThenByDescending(entry => entry.Frequency))
            {
                selectedAstEntries.Add(chosenCard);
                var keepReason = string.Equals(chosenCard.AbilityName, "Lord of Crowns", StringComparison.OrdinalIgnoreCase)
                    ? $"{handLabel} hand kept Lord of Crowns after Divination"
                    : $"{handLabel} hand kept this granted card inside the active 55s window";
                debug?.Add($"  keep | {FormatAutoDebugEntry(chosenCard)} | {keepReason}");
            }

            return true;
        }

        TimelineEntry? FindNextDrawEntry(string drawName, double minimumTimeSec)
        {
            var windowStartSec = minimumTimeSec - AutoCooldownToleranceSec;
            var windowEndSec = minimumTimeSec + AstrologianDrawReadyWindowSec;
            var windowCandidates = astSourceEntries
                .Where(entry =>
                    string.Equals(entry.AbilityName, drawName, StringComparison.OrdinalIgnoreCase) &&
                    entry.TimeOffsetSec >= windowStartSec &&
                    entry.TimeOffsetSec <= windowEndSec + AutoCooldownToleranceSec)
                .ToList();
            if (windowCandidates.Count == 0)
                return null;

            var aboveThresholdCandidates = windowCandidates
                .Where(entry => entry.Frequency >= AutoStateDrivenOgcdMinFrequency)
                .ToList();
            var pool = aboveThresholdCandidates.Count > 0
                ? aboveThresholdCandidates
                : windowCandidates;
            return pool
                .OrderByDescending(entry => entry.Frequency)
                .ThenBy(entry => entry.TimeOffsetSec)
                .FirstOrDefault();
        }

        TimelineEntry? FindOpeningUmbralDrawEntry()
        {
            foreach (var candidate in astSourceEntries
                         .Where(entry => string.Equals(entry.AbilityName, cardDraw.UmbralDrawName, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(entry => entry.TimeOffsetSec)
                         .ThenByDescending(entry => entry.Frequency))
            {
                if (TryBuildHandSelections(
                        "Astral opener",
                        ["the Balance", "the Arrow", "the Spire", "Lord of Crowns"],
                        0.0,
                        candidate.TimeOffsetSec,
                        FindDelayedParentRequirementRules("Astrologian", cardDraw.UmbralDrawName, ["the Balance", "the Arrow", "the Spire", "Lord of Crowns"]),
                        out _))
                {
                    return candidate;
                }
            }

            return FindNextDrawEntry(cardDraw.UmbralDrawName, 0.0);
        }

        var openingUmbralDraw = FindOpeningUmbralDrawEntry();
        if (openingUmbralDraw == null)
            return result
                .OrderBy(entry => entry.TimeOffsetSec)
                .ThenByDescending(entry => entry.Frequency)
                .ToList();

        if (TryBuildHandSelections(
                "Astral opener",
                ["the Balance", "the Arrow", "the Spire", "Lord of Crowns"],
                0.0,
                openingUmbralDraw.TimeOffsetSec,
                FindDelayedParentRequirementRules("Astrologian", cardDraw.UmbralDrawName, ["the Balance", "the Arrow", "the Spire", "Lord of Crowns"]),
                out _))
        {
            selectedAstEntries.Add(openingUmbralDraw);
            debug?.Add($"  keep | {FormatAutoDebugEntry(openingUmbralDraw)} | opener Astral hand spent first, then Umbral Draw swapped the active hand");
        }

        var currentDrawEntry = openingUmbralDraw;
        var expectAstralDraw = true;
        while (currentDrawEntry != null)
        {
            string[] currentHandCards = expectAstralDraw
                ? ["the Spear", "the Bole", "the Ewer", "Lady of Crowns"]
                : ["the Balance", "the Arrow", "the Spire", "Lord of Crowns"];
            var currentHandLabel = expectAstralDraw ? "Umbral" : "Astral";
            var baseHandEndSec = currentDrawEntry.TimeOffsetSec + AstrologianDrawCooldownSec;
            var nextDrawName = expectAstralDraw
                ? cardDraw.AstralDrawName
                : cardDraw.UmbralDrawName;
            var delayedParentRequirementRules = FindDelayedParentRequirementRules("Astrologian", nextDrawName, currentHandCards);
            var currentHandEndSec = baseHandEndSec;
            foreach (var delayedParentRequirementRule in delayedParentRequirementRules)
            {
                var nextRequiredBuffEntry = FindNextSelectedOgcdEntry(
                    delayedParentRequirementRule.RequiredBuffAbilityName,
                    baseHandEndSec);
                if (nextRequiredBuffEntry != null)
                    currentHandEndSec = Math.Max(currentHandEndSec, nextRequiredBuffEntry.TimeOffsetSec + delayedParentRequirementRule.RequiredBuffDurationSec);
            }

            if (!TryBuildHandSelections(
                currentHandLabel,
                currentHandCards,
                currentDrawEntry.TimeOffsetSec,
                currentHandEndSec,
                delayedParentRequirementRules,
                out var chosenCards))
            {
                break;
            }

            var handCompletionTimeSec = Math.Max(
                baseHandEndSec,
                chosenCards.Count == 0
                    ? currentHandEndSec
                    : chosenCards.Max(entry => entry.TimeOffsetSec));
            var nextDrawEntry = FindNextDrawEntry(nextDrawName, handCompletionTimeSec);
            if (nextDrawEntry == null)
                break;

            selectedAstEntries.Add(nextDrawEntry);
            debug?.Add(delayedParentRequirementRules.Count == 0
                ? $"  keep | {FormatAutoDebugEntry(nextDrawEntry)} | {nextDrawName} reopened after the prior 55s hand window"
                : $"  keep | {FormatAutoDebugEntry(nextDrawEntry)} | {nextDrawName} was delayed until after {string.Join(", ", delayedParentRequirementRules.Select(rule => $"{rule.RequiredAbilityName} landed in {rule.RequiredBuffAbilityName}"))}");
            currentDrawEntry = nextDrawEntry;
            expectAstralDraw = !expectAstralDraw;
        }

        result.AddRange(selectedAstEntries
            .DistinctBy(GetAutoEntryIdentityKey));
        return result
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private void RemoveConflictingSelectedGcdEntries(
        List<TimelineEntry> selected,
        HashSet<string> usedKeys,
        TimelineEntry replacement,
        AutoTimelineDebugRecorder? debug)
    {
        var removals = selected
            .Where(entry =>
                IsGcdEntry(entry) &&
                !string.Equals(entry.AbilityName, replacement.AbilityName, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(entry.TimeOffsetSec - replacement.TimeOffsetSec) < GetConfiguredAutoTimelineGcdRecastSec() - AutoCooldownToleranceSec)
            .ToList();

        foreach (var removal in removals)
        {
            selected.Remove(removal);
            usedKeys.Remove(GetAutoEntryIdentityKey(removal));
            debug?.Add($"  prune | {removal.AbilityName} @ {FormatTime(removal.TimeOffsetSec)} | replaced by granted child {replacement.AbilityName}");
        }
    }

    private List<TimelineEntry> BuildAutoTimelineWithFixedGcds(
        List<TimelineEntry> gcdEntries,
        List<TimelineEntry> selectedOgcdEntries,
        string specName,
        ISet<string> rawAbilityNames,
        AutoOpenerBufferInfo openerBuffer,
        AutoTimelineDebugRecorder? debug = null)
    {
        debug?.Add("Gauge Legality");
        var gaugeRules = GaugeSimulator.GetRules(specName);
        var grantedRules = GrantedActionDatabase.GetRules(specName);
        var state = CreateAutoTimelineState(specName, gaugeRules, grantedRules, rawAbilityNames);
        var finalEntries = new List<TimelineEntry>();
        var orderedEntries = gcdEntries
            .Concat(selectedOgcdEntries)
            .OrderBy(item => item.TimeOffsetSec)
            .ThenByDescending(item => item.Frequency)
            .ToList();
        if (openerBuffer.Enabled)
            debug?.Add($"  opener buffer | numeric gauge minimums before {FormatTime(openerBuffer.EndTimeSec)} may borrow pre-pull resources");

        if (gaugeRules != null && orderedEntries.Any(entry => IsAutoTimelineTrueGaugeAbility(entry.AbilityName, gaugeRules)))
        {
            for (var index = 0; index < orderedEntries.Count; index++)
            {
                var entry = orderedEntries[index];
                var evaluationState = state.Clone();
                var decision = EvaluateAutoGaugeLegalityDecision(evaluationState, gaugeRules, grantedRules, specName, openerBuffer, entry);
                if (decision.Keep &&
                    TryGetDelayedParentRequirementReason(orderedEntries, index, state, gaugeRules, entry, out var delayedParentReason))
                {
                    decision = new AutoGaugeLegalitySegmentDecision
                    {
                        Entry = entry,
                        Keep = false,
                        Summary = delayedParentReason,
                        BeforeGaugeState = decision.BeforeGaugeState,
                    };
                }

                if (decision.Keep)
                {
                    state = evaluationState;
                    finalEntries.Add(entry);
                    if (decision.BeforeGaugeState == null)
                        debug?.Add($"  keep | {FormatAutoDebugEntry(entry)} | {decision.Summary}");
                    else
                        debug?.Add($"  keep | {FormatAutoDebugEntry(entry)} | {decision.Summary} | before {decision.BeforeGaugeState} -> after {decision.AfterGaugeState}");
                }
                else
                {
                    var stateText = decision.BeforeGaugeState == null
                        ? string.Empty
                        : $" | state {decision.BeforeGaugeState}";
                    debug?.Add($"  prune | {FormatAutoDebugEntry(entry)} | {decision.Summary}{stateText}");
                }

                if (!decision.Keep ||
                    !TryGetAutoGaugeRefillResourceNames(entry.AbilityName, gaugeRules, out var refillGaugeNames))
                    continue;

                var nextRefillIndex = FindNextAutoGaugeRefillIndex(orderedEntries, index + 1, gaugeRules, refillGaugeNames);
                if (nextRefillIndex <= index + 1)
                    continue;

                var (segmentEntries, updatedState) = OptimizeAutoGaugeSegment(
                    orderedEntries,
                    index + 1,
                    nextRefillIndex,
                    state,
                    gaugeRules,
                    grantedRules,
                    openerBuffer,
                    refillGaugeNames);
                foreach (var segmentDecision in segmentEntries)
                {
                    if (segmentDecision.Keep)
                    {
                        finalEntries.Add(segmentDecision.Entry);
                        if (segmentDecision.BeforeGaugeState == null)
                            debug?.Add($"  keep | {FormatAutoDebugEntry(segmentDecision.Entry)} | {segmentDecision.Summary}");
                        else
                            debug?.Add($"  keep | {FormatAutoDebugEntry(segmentDecision.Entry)} | {segmentDecision.Summary} | before {segmentDecision.BeforeGaugeState} -> after {segmentDecision.AfterGaugeState}");
                    }
                    else
                    {
                        var stateText = segmentDecision.BeforeGaugeState == null
                            ? string.Empty
                            : $" | state {segmentDecision.BeforeGaugeState}";
                        debug?.Add($"  prune | {FormatAutoDebugEntry(segmentDecision.Entry)} | {segmentDecision.Summary}{stateText}");
                    }
                }

                state = updatedState;
                index = nextRefillIndex - 1;
            }
        }
        else
        {
            foreach (var entry in orderedEntries)
            {
                var decision = EvaluateAutoGaugeLegalityDecision(state, gaugeRules, grantedRules, specName, openerBuffer, entry);
                if (!decision.Keep)
                {
                    var stateText = decision.BeforeGaugeState == null
                        ? string.Empty
                        : $" | state {decision.BeforeGaugeState}";
                    debug?.Add($"  prune | {FormatAutoDebugEntry(entry)} | {decision.Summary}{stateText}");
                    continue;
                }

                finalEntries.Add(entry);
                if (decision.BeforeGaugeState == null)
                    debug?.Add($"  keep | {FormatAutoDebugEntry(entry)} | {decision.Summary}");
                else
                    debug?.Add($"  keep | {FormatAutoDebugEntry(entry)} | {decision.Summary} | before {decision.BeforeGaugeState} -> after {decision.AfterGaugeState}");
            }
        }

        debug?.Add();
        List<string>? postSelectionDebugNotes = debug == null ? null : new List<string>();
        var postSelectionEntries = TimelineJobRules.ApplyPostSelectionRules(
            specName,
            finalEntries,
            promoteMacrocosmosToVisualGcd: false,
            debugNotes: postSelectionDebugNotes);
        if (debug != null && postSelectionDebugNotes is { Count: > 0 })
        {
            debug.Add("Post-Selection Job Rules");
            foreach (var note in postSelectionDebugNotes)
                debug.Add(note);
            debug.Add();
        }

        return postSelectionEntries
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private AutoGaugeLegalitySegmentDecision EvaluateAutoGaugeLegalityDecision(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string specName,
        AutoOpenerBufferInfo openerBuffer,
        TimelineEntry entry)
    {
        AdvancePassiveResources(state, gaugeRules, entry.TimeOffsetSec);
        var relevantGaugeNames = GetRelevantGaugeNames(state, gaugeRules, grantedRules, entry.AbilityName);
        var beforeGaugeState = relevantGaugeNames.Count == 0
            ? null
            : FormatGaugeStateForDebug(state, relevantGaugeNames);
        var insufficiencyReason = GetNumericGaugeInsufficiencyReason(state, gaugeRules, grantedRules, entry.AbilityName);
        var timedWindowReason = GetTimedUsageWindowReason(
            specName,
            state.TimedWindowEndByAbility,
            entry.AbilityName,
            entry.TimeOffsetSec);
        var timedLockoutReason = GetTimedUsageLockoutReason(
            specName,
            state.TimedLockoutEndByAbility,
            entry.AbilityName,
            entry.TimeOffsetSec);
        var scholarBurstReservationReason = GetScholarBurstAetherflowReservationReason(
            specName,
            state.TimedWindowEndByAbility,
            state.GaugeState,
            entry.AbilityName,
            entry.TimeOffsetSec);
        var rejectionReason = insufficiencyReason ?? timedWindowReason ?? timedLockoutReason ?? scholarBurstReservationReason;
        var canBorrowPrepullGauge = insufficiencyReason != null &&
                                    IsWithinAutoOpenerBuffer(openerBuffer, entry.TimeOffsetSec) &&
                                    !IsAutoTimelineTrueGaugeSpender(entry.AbilityName, gaugeRules) &&
                                    CanBorrowAutoOpenerPrepullGauge(state, gaugeRules, entry.AbilityName);
        if (rejectionReason != null)
        {
            if (canBorrowPrepullGauge)
            {
                ApplyGaugeEffects(state, gaugeRules, grantedRules, entry.AbilityName, allowPrepullSeed: true);
                ApplyTimedUsageWindowState(specName, state.TimedWindowEndByAbility, entry.AbilityName, entry.TimeOffsetSec);
                ApplyTimedUsageLockoutState(specName, state.TimedLockoutEndByAbility, entry.AbilityName, entry.TimeOffsetSec);
                return new AutoGaugeLegalitySegmentDecision
                {
                    Entry = entry,
                    Keep = true,
                    Summary = $"opener buffer borrowed pre-pull gauge because {insufficiencyReason}",
                    BeforeGaugeState = beforeGaugeState,
                    AfterGaugeState = relevantGaugeNames.Count == 0 ? null : FormatGaugeStateForDebug(state, relevantGaugeNames),
                    UsedOpenerBorrow = true,
                };
            }

            return new AutoGaugeLegalitySegmentDecision
            {
                Entry = entry,
                Keep = false,
                Summary = rejectionReason,
                BeforeGaugeState = beforeGaugeState,
            };
        }

        ApplyGaugeEffects(state, gaugeRules, grantedRules, entry.AbilityName);
        ApplyTimedUsageWindowState(specName, state.TimedWindowEndByAbility, entry.AbilityName, entry.TimeOffsetSec);
        ApplyTimedUsageLockoutState(specName, state.TimedLockoutEndByAbility, entry.AbilityName, entry.TimeOffsetSec);
        return new AutoGaugeLegalitySegmentDecision
        {
            Entry = entry,
            Keep = true,
            Summary = relevantGaugeNames.Count == 0 ? "no numeric gauge rule" : "gauge ok",
            BeforeGaugeState = beforeGaugeState,
            AfterGaugeState = relevantGaugeNames.Count == 0 ? null : FormatGaugeStateForDebug(state, relevantGaugeNames),
        };
    }

    private static bool TryGetAutoGaugeRefillResourceNames(
        string abilityName,
        GaugeSimulator.JobGaugeRules gaugeRules,
        out IReadOnlyList<string> gaugeNames)
    {
        gaugeNames = [];
        if (!gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
            return false;

        var refillGaugeNames = effects
            .Where(effect =>
                GaugeSimulator.IsTrueGaugeResource(effect.GaugeName) &&
                (effect.SetValue is int || effect.Delta > 0))
            .Select(effect => effect.GaugeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (refillGaugeNames.Count == 0)
            return false;

        gaugeNames = refillGaugeNames;
        return true;
    }

    private static bool IsAutoGaugeSegmentSpender(
        string abilityName,
        GaugeSimulator.JobGaugeRules gaugeRules,
        IReadOnlyCollection<string> gaugeNames)
    {
        if (!gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
            return false;

        return effects.Any(effect =>
            gaugeNames.Contains(effect.GaugeName) &&
            GaugeSimulator.IsTrueGaugeResource(effect.GaugeName) &&
            (effect.MinRequired > 0 || effect.Delta < 0));
    }

    private static int FindNextAutoGaugeRefillIndex(
        IReadOnlyList<TimelineEntry> orderedEntries,
        int startIndex,
        GaugeSimulator.JobGaugeRules gaugeRules,
        IReadOnlyCollection<string> gaugeNames)
    {
        for (var index = startIndex; index < orderedEntries.Count; index++)
        {
            if (!TryGetAutoGaugeRefillResourceNames(orderedEntries[index].AbilityName, gaugeRules, out var refillGaugeNames))
                continue;

            if (refillGaugeNames.Any(gaugeNames.Contains))
                return index;
        }

        return orderedEntries.Count;
    }

    private (List<AutoGaugeLegalitySegmentDecision> Decisions, AutoTimelineState UpdatedState) OptimizeAutoGaugeSegment(
        IReadOnlyList<TimelineEntry> orderedEntries,
        int startIndexInclusive,
        int endIndexExclusive,
        AutoTimelineState startingState,
        GaugeSimulator.JobGaugeRules gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        AutoOpenerBufferInfo openerBuffer,
        IReadOnlyCollection<string> gaugeNames)
    {
        var requiredSpendCount = GetRequiredAutoGaugeSegmentSpendCount(startingState, gaugeNames);
        var branches = new List<AutoGaugeLegalityBranch>
        {
            new()
            {
                State = startingState.Clone(),
                Decisions = [],
                Score = 0.0,
            },
        };

        for (var index = startIndexInclusive; index < endIndexExclusive; index++)
        {
            var entry = orderedEntries[index];
            var nextBranches = new List<AutoGaugeLegalityBranch>();
            foreach (var branch in branches)
            {
                var evaluationState = branch.State.Clone();
                var decision = EvaluateAutoGaugeLegalityDecision(
                    evaluationState,
                    gaugeRules,
                    grantedRules,
                    startingState.SpecName,
                    openerBuffer,
                    entry);

                if (!IsAutoGaugeSegmentSpender(entry.AbilityName, gaugeRules, gaugeNames))
                {
                    var decisions = new List<AutoGaugeLegalitySegmentDecision>(branch.Decisions.Count + 1);
                    decisions.AddRange(branch.Decisions);
                    decisions.Add(decision);
                    nextBranches.Add(new AutoGaugeLegalityBranch
                    {
                        State = evaluationState,
                        Decisions = decisions,
                        Score = branch.Score,
                    });
                    continue;
                }

                var prunedDecisions = new List<AutoGaugeLegalitySegmentDecision>(branch.Decisions.Count + 1);
                prunedDecisions.AddRange(branch.Decisions);
                prunedDecisions.Add(new AutoGaugeLegalitySegmentDecision
                {
                    Entry = entry,
                    Keep = false,
                    Summary = "stronger gauge spenders won this refill window",
                    BeforeGaugeState = decision.BeforeGaugeState,
                });
                nextBranches.Add(new AutoGaugeLegalityBranch
                {
                    State = branch.State.Clone(),
                    Decisions = prunedDecisions,
                    Score = branch.Score,
                });

                if (!decision.Keep)
                {
                    var illegalDecisions = new List<AutoGaugeLegalitySegmentDecision>(branch.Decisions.Count + 1);
                    illegalDecisions.AddRange(branch.Decisions);
                    illegalDecisions.Add(decision);
                    nextBranches.Add(new AutoGaugeLegalityBranch
                    {
                        State = branch.State.Clone(),
                        Decisions = illegalDecisions,
                        Score = branch.Score - 5000.0,
                    });
                    continue;
                }

                var keptDecisions = new List<AutoGaugeLegalitySegmentDecision>(branch.Decisions.Count + 1);
                keptDecisions.AddRange(branch.Decisions);
                keptDecisions.Add(decision);
                nextBranches.Add(new AutoGaugeLegalityBranch
                {
                    State = evaluationState,
                    Decisions = keptDecisions,
                    Score = branch.Score + ScoreAutoGaugeSegmentEntry(branch.State, entry, gaugeRules, grantedRules),
                });
            }

            branches = CollapseAutoGaugeSegmentBranches(nextBranches);
        }

        var bestBranch = branches
            .OrderByDescending(branch => GetAutoGaugeSegmentRequiredSpendScore(branch, gaugeRules, gaugeNames, requiredSpendCount))
            .ThenByDescending(branch => branch.Score)
            .ThenByDescending(branch => branch.Decisions.Count(decision => decision.Keep))
            .First();
        return (bestBranch.Decisions, bestBranch.State);
    }

    private static int? GetRequiredAutoGaugeSegmentSpendCount(
        AutoTimelineState startingState,
        IReadOnlyCollection<string> gaugeNames)
    {
        if (!string.Equals(startingState.SpecName, "Scholar", StringComparison.OrdinalIgnoreCase) ||
            gaugeNames.Count != 1 ||
            !gaugeNames.Contains("Aetherflow"))
        {
            return null;
        }

        return Math.Max(0, startingState.GaugeState.GetValueOrDefault("Aetherflow"));
    }

    private static int CountKeptAutoGaugeSegmentSpenders(
        AutoGaugeLegalityBranch branch,
        GaugeSimulator.JobGaugeRules gaugeRules,
        IReadOnlyCollection<string> gaugeNames)
        => branch.Decisions.Count(decision =>
            decision.Keep &&
            IsAutoGaugeSegmentSpender(decision.Entry.AbilityName, gaugeRules, gaugeNames));

    private static int GetAutoGaugeSegmentRequiredSpendScore(
        AutoGaugeLegalityBranch branch,
        GaugeSimulator.JobGaugeRules gaugeRules,
        IReadOnlyCollection<string> gaugeNames,
        int? requiredSpendCount)
    {
        if (!requiredSpendCount.HasValue)
            return 0;

        var keptSpenders = CountKeptAutoGaugeSegmentSpenders(branch, gaugeRules, gaugeNames);
        return keptSpenders == requiredSpendCount.Value
            ? 2
            : keptSpenders < requiredSpendCount.Value
                ? 1
                : 0;
    }

    private List<AutoGaugeLegalityBranch> CollapseAutoGaugeSegmentBranches(
        IReadOnlyList<AutoGaugeLegalityBranch> branches)
    {
        var bestByKey = new Dictionary<string, AutoGaugeLegalityBranch>(StringComparer.OrdinalIgnoreCase);
        foreach (var branch in branches)
        {
            var key = BuildAutoGaugeSegmentBranchKey(branch.State);
            if (!bestByKey.TryGetValue(key, out var existing) || branch.Score > existing.Score)
                bestByKey[key] = branch;
        }

        return bestByKey.Values
            .OrderByDescending(branch => branch.Score)
            .Take(24)
            .ToList();
    }

    private string BuildAutoGaugeSegmentBranchKey(AutoTimelineState state)
    {
        var gaugeSnapshot = string.Join(
            ";",
            state.GaugeState
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        var timedWindowSnapshot = string.Join(
            ";",
            state.TimedWindowEndByAbility
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value:F3}"));
        var timedLockoutSnapshot = string.Join(
            ";",
            state.TimedLockoutEndByAbility
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value:F3}"));
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{state.LastTimeSec:F3}|{gaugeSnapshot}|{timedWindowSnapshot}|{timedLockoutSnapshot}");
    }

    private bool TryGetDelayedParentRequirementReason(
        IReadOnlyList<TimelineEntry> orderedEntries,
        int currentIndex,
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        TimelineEntry entry,
        out string reason)
    {
        reason = string.Empty;

        var delayedParentRequirementRules = FindDelayedParentRequirementRules(
            state.SpecName,
            entry.AbilityName);
        if (delayedParentRequirementRules.Count == 0)
            return false;

        foreach (var rule in delayedParentRequirementRules)
        {
            if (state.TimedWindowEndByAbility.TryGetValue(rule.RequiredAbilityName, out var activeWindowEndSec) &&
                entry.TimeOffsetSec <= activeWindowEndSec + AutoCooldownToleranceSec)
                continue;

            if (CanPreloadDelayedParentRequirement(entry.AbilityName, rule.RequiredAbilityName, gaugeRules))
                continue;

            var nextBuffEntry = orderedEntries
                .Skip(currentIndex + 1)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.AbilityName, rule.RequiredBuffAbilityName, StringComparison.OrdinalIgnoreCase) &&
                    candidate.TimeOffsetSec > entry.TimeOffsetSec + AutoCooldownToleranceSec);
            if (nextBuffEntry == null)
                continue;

            var requiredWindowStartSec = nextBuffEntry.TimeOffsetSec + AutoCooldownToleranceSec;
            var requiredWindowEndSec = nextBuffEntry.TimeOffsetSec + rule.RequiredBuffDurationSec;
            var requiredAbilityEntry = orderedEntries
                .Skip(currentIndex + 1)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.AbilityName, rule.RequiredAbilityName, StringComparison.OrdinalIgnoreCase) &&
                    candidate.TimeOffsetSec >= requiredWindowStartSec - AutoCooldownToleranceSec &&
                    candidate.TimeOffsetSec <= requiredWindowEndSec + AutoCooldownToleranceSec);
            if (requiredAbilityEntry == null)
                continue;

            var delayedParentCandidate = orderedEntries
                .Skip(currentIndex + 1)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.AbilityName, entry.AbilityName, StringComparison.OrdinalIgnoreCase) &&
                    candidate.TimeOffsetSec > entry.TimeOffsetSec + AutoCooldownToleranceSec &&
                    candidate.TimeOffsetSec <= requiredWindowEndSec + AutoCooldownToleranceSec);
            if (delayedParentCandidate == null)
                continue;

            reason = $"{rule.RequiredAbilityName} has a 2 minute buff requirement in {rule.RequiredBuffAbilityName}, so {entry.AbilityName} is delayed toward the upcoming {rule.RequiredBuffAbilityName} window";
            return true;
        }

        return false;
    }

    private static bool CanPreloadDelayedParentRequirement(
        string parentAbilityName,
        string requiredAbilityName,
        GaugeSimulator.JobGaugeRules? gaugeRules)
    {
        if (gaugeRules == null ||
            !gaugeRules.EffectByName.TryGetValue(parentAbilityName, out var parentEffects) ||
            !gaugeRules.EffectByName.TryGetValue(requiredAbilityName, out var requiredEffects))
            return false;

        var grantedGaugeNames = parentEffects
            .Where(effect =>
                GaugeSimulator.IsTrueGaugeResource(effect.GaugeName) &&
                (effect.SetValue is int || effect.Delta > 0))
            .Select(effect => effect.GaugeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (grantedGaugeNames.Count == 0)
            return false;

        return requiredEffects.Any(effect =>
            grantedGaugeNames.Contains(effect.GaugeName) &&
            GaugeSimulator.IsTrueGaugeResource(effect.GaugeName) &&
            (effect.MinRequired > 0 || effect.Delta < 0));
    }

    private double ScoreAutoGaugeSegmentEntry(
        AutoTimelineState state,
        TimelineEntry entry,
        GaugeSimulator.JobGaugeRules gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        var score = entry.Frequency * 1000.0;
        score += entry.AverageUses * 10.0;
        score -= GetGaugePenalty(state, gaugeRules, grantedRules, entry.AbilityName) * 10.0;

        if (TryGetTimedUsageWindowRule(state.SpecName, entry.AbilityName, out _, out _) &&
            state.TimedWindowEndByAbility.TryGetValue(entry.AbilityName, out var timedWindowEndSec) &&
            entry.TimeOffsetSec <= timedWindowEndSec + AutoCooldownToleranceSec)
        {
            score += 250.0;
        }

        if (GetScholarBurstAetherflowReservationReason(
                state.SpecName,
                state.TimedWindowEndByAbility,
                state.GaugeState,
                entry.AbilityName,
                entry.TimeOffsetSec) != null)
            score -= 250.0;

        if (IsAutoTimelineTrueGaugeSpender(entry.AbilityName, gaugeRules))
        {
            score += HasOvercapPressure(state, gaugeRules) ? 1200.0 : 40.0;
            if (string.Equals(state.SpecName, "Scholar", StringComparison.OrdinalIgnoreCase) &&
                state.GaugeState.GetValueOrDefault("Aetherflow") > 0 &&
                IsScholarAetherflowSpender(entry.AbilityName))
            {
                score += 250000.0;
            }
        }

        return score;
    }

    private IEnumerable<TimelineEntry> OrderReplayEntriesForEvaluation(
        IEnumerable<TimelineEntry> entries,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        var sorted = entries
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
        if (sorted.Count <= 1)
            return sorted;

        var ordered = new List<TimelineEntry>(sorted.Count);
        var cluster = new List<TimelineEntry>();
        double? clusterStartTime = null;

        foreach (var entry in sorted)
        {
            if (clusterStartTime == null ||
                Math.Abs(entry.TimeOffsetSec - clusterStartTime.Value) <= AutoCooldownToleranceSec)
            {
                cluster.Add(entry);
                clusterStartTime ??= entry.TimeOffsetSec;
                continue;
            }

            ordered.AddRange(OrderReplayCluster(cluster, gaugeRules, grantedRules));
            cluster.Clear();
            cluster.Add(entry);
            clusterStartTime = entry.TimeOffsetSec;
        }

        if (cluster.Count > 0)
            ordered.AddRange(OrderReplayCluster(cluster, gaugeRules, grantedRules));

        return ordered;
    }

    private IEnumerable<TimelineEntry> OrderReplayCluster(
        List<TimelineEntry> cluster,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        if (cluster.Count <= 1)
            return cluster;

        var remaining = new List<TimelineEntry>(cluster);
        var ordered = new List<TimelineEntry>(cluster.Count);

        while (remaining.Count > 0)
        {
            TimelineEntry? best = null;
            var bestBlockingCount = int.MaxValue;
            var bestDependencyCount = int.MinValue;

            foreach (var candidate in remaining)
            {
                var blockingCount = 0;
                var dependencyCount = 0;
                foreach (var other in remaining)
                {
                    if (ReferenceEquals(candidate, other))
                        continue;

                    if (ShouldReplayEntryPrecede(other, candidate, gaugeRules, grantedRules))
                        blockingCount++;

                    if (ShouldReplayEntryPrecede(candidate, other, gaugeRules, grantedRules))
                        dependencyCount++;
                }

                if (best == null ||
                    blockingCount < bestBlockingCount ||
                    (blockingCount == bestBlockingCount && dependencyCount > bestDependencyCount) ||
                    (blockingCount == bestBlockingCount && dependencyCount == bestDependencyCount &&
                     candidate.Frequency > best.Frequency) ||
                    (blockingCount == bestBlockingCount && dependencyCount == bestDependencyCount &&
                     Math.Abs(candidate.Frequency - best.Frequency) < 0.0001 &&
                     string.Compare(candidate.AbilityName, best.AbilityName, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    best = candidate;
                    bestBlockingCount = blockingCount;
                    bestDependencyCount = dependencyCount;
                }
            }

            ordered.Add(best!);
            remaining.Remove(best!);
        }

        return ordered;
    }

    private bool ShouldReplayEntryPrecede(
        TimelineEntry first,
        TimelineEntry second,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        var secondPredecessors = JobComboDatabase.GetPredecessors(second.AbilityName);
        if (secondPredecessors.Contains(first.AbilityName))
            return true;

        return AbilityGrantsRequiredState(first.AbilityName, second.AbilityName, gaugeRules, grantedRules);
    }

    private bool AbilityGrantsRequiredState(
        string grantorAbilityName,
        string consumerAbilityName,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        var grantorRule = plugin.ActionStateDatabase.Lookup(0, grantorAbilityName);
        var consumerRule = plugin.ActionStateDatabase.Lookup(0, consumerAbilityName);
        if (grantorRule == null || consumerRule == null)
            return false;

        var grantedStates = grantorRule.Effects
            .Where(effect => effect.Delta > 0)
            .SelectMany(effect => EnumerateEquivalentStateNames(effect.StateName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (grantedStates.Count == 0)
            return false;

        foreach (var effect in consumerRule.Effects)
        {
            if (effect.MinRequired <= 0)
                continue;

            if (ShouldIgnoreActionStateRequirement(consumerRule, effect, consumerAbilityName, gaugeRules, grantedRules))
                continue;

            foreach (var requiredState in EnumerateEquivalentStateNames(effect.StateName))
            {
                if (grantedStates.Contains(requiredState))
                    return true;
            }
        }

        return false;
    }

    private List<TimelineEntry> SelectAutoOgcdEntries(
        List<TimelineEntry> ogcdEntries,
        string specName,
        AutoOpenerBufferInfo openerBuffer,
        AutoTimelineDebugRecorder? debug = null)
    {
        debug?.Add("oGCD Selection");
        var grantedRules = GrantedActionDatabase.GetRules(specName);
        var cooldownKeptEntries = new List<TimelineEntry>();
        foreach (var group in ogcdEntries
                     .GroupBy(entry => entry.AbilityName, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sample = group.First();
            var info = plugin.RecastDatabase.Lookup(sample.AbilityId, sample.AbilityName);
            var recastSec = Math.Max(0.1, info?.RecastSec ?? 0.0);
            var orderedCandidates = group
                .OrderBy(entry => entry.TimeOffsetSec)
                .ThenByDescending(entry => entry.Frequency)
                .ToList();
            var aboveThresholdCandidates = orderedCandidates
                .Where(entry => entry.Frequency >= AutoStateDrivenOgcdMinFrequency)
                .ToList();
            var belowThresholdCount = group.Count() - aboveThresholdCandidates.Count;
            debug?.Add($"  ability | {group.Key} | recast {recastSec:F1}s | above threshold {aboveThresholdCandidates.Count} | below threshold {belowThresholdCount}");
            if (orderedCandidates.Count == 0)
                continue;

            var repeatableRule = grantedRules?.RepeatableGrantedActionRules
                .FirstOrDefault(rule =>
                    rule.SkipCooldownWhenConsuming &&
                    rule.ConsumerNames.Contains(group.Key));
            if (repeatableRule != null)
            {
                cooldownKeptEntries.AddRange(orderedCandidates);
                debug?.Add($"    keep all | granted parent windows handle {group.Key}, so cooldown pruning is bypassed for this repeatable consumer");
                continue;
            }

            var currentWindowStartSec = orderedCandidates[0].TimeOffsetSec;
            var finalCandidateTimeSec = orderedCandidates[^1].TimeOffsetSec;
            TimelineEntry? previousKeptCandidate = null;
            var nextReadyTimeSec = currentWindowStartSec;
            var thresholdText = $"{AutoStateDrivenOgcdMinFrequency * 100.0:F1}%";

            while (currentWindowStartSec <= finalCandidateTimeSec + AutoCooldownToleranceSec)
            {
                var currentWindowEndSec = currentWindowStartSec + recastSec;
                var windowCandidates = orderedCandidates
                    .Where(candidate =>
                        candidate.TimeOffsetSec >= currentWindowStartSec - 0.001 &&
                        candidate.TimeOffsetSec < currentWindowEndSec - AutoCooldownToleranceSec)
                    .ToList();

                if (windowCandidates.Count == 0)
                {
                    currentWindowStartSec += recastSec;
                    continue;
                }

                var windowAboveThresholdCandidates = windowCandidates
                    .Where(candidate => candidate.Frequency >= AutoStateDrivenOgcdMinFrequency)
                    .ToList();
                var usedFallback = windowAboveThresholdCandidates.Count == 0;
                var chosenCandidate = previousKeptCandidate == null
                    ? ChooseCooldownBaselineCandidate(windowCandidates, windowAboveThresholdCandidates)
                    : ChooseNextReadyCooldownCandidate(windowCandidates, windowAboveThresholdCandidates, nextReadyTimeSec);
                var chosenKey = GetAutoEntryIdentityKey(chosenCandidate);
                var windowText = $"{FormatTime(currentWindowStartSec)}-{FormatTime(currentWindowEndSec)}";

                cooldownKeptEntries.Add(chosenCandidate);

                if (previousKeptCandidate == null)
                {
                    var initialReason = usedFallback
                        ? $"fallback highest instance within initial cooldown window {windowText}; no candidate met {thresholdText} in this window; establishes cooldown baseline"
                        : $"strongest instance within initial cooldown window {windowText}; establishes cooldown baseline";
                    nextReadyTimeSec = chosenCandidate.TimeOffsetSec + recastSec;
                    debug?.Add($"    keep | {FormatAutoDebugEntry(chosenCandidate)} | {initialReason} | next ready {FormatTime(nextReadyTimeSec)}");
                }
                else
                {
                    var keepReasonPrefix = $"cooldown reopened after {FormatTime(nextReadyTimeSec)} from prior keep {FormatAutoDebugEntry(previousKeptCandidate)}";
                    var keepReason = usedFallback
                        ? $"{keepReasonPrefix} | fallback strongest near-ready instance within cooldown window {windowText}; no candidate met {thresholdText} in this window"
                        : $"{keepReasonPrefix} | strongest near-ready above-threshold instance within cooldown window {windowText}";
                    nextReadyTimeSec = chosenCandidate.TimeOffsetSec + recastSec;
                    debug?.Add($"    keep | {FormatAutoDebugEntry(chosenCandidate)} | {keepReason} | next ready {FormatTime(nextReadyTimeSec)}");
                }

                foreach (var candidate in windowCandidates)
                {
                    if (string.Equals(GetAutoEntryIdentityKey(candidate), chosenKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var pruneReason = usedFallback
                        ? $"cooldown window {windowText} fell back to earliest keep {FormatAutoDebugEntry(chosenCandidate)} because no candidate met {thresholdText}"
                        : $"cooldown window {windowText} already committed to next ready keep {FormatAutoDebugEntry(chosenCandidate)}";
                    debug?.Add($"    prune | {FormatAutoDebugEntry(candidate)} | {pruneReason}");
                }

                previousKeptCandidate = chosenCandidate;
                currentWindowStartSec = nextReadyTimeSec - AutoCooldownToleranceSec;
            }
        }

        var grantedChildRules = BuildGrantedChildRules(ogcdEntries, grantedRules);
        RemoveAstrologianCardDrawGrantedRules(grantedChildRules, grantedRules);
        if (grantedChildRules.Count == 0)
        {
            debug?.Add();
            return cooldownKeptEntries
                .OrderBy(entry => entry.TimeOffsetSec)
                .ThenByDescending(entry => entry.Frequency)
                .ToList();
        }

        debug?.Add("  granted windows");
        if (openerBuffer.Enabled)
            debug?.Add($"    opener buffer | missing parents before {FormatTime(openerBuffer.EndTimeSec)} may be borrowed from pre-pull state once per child window");

        var grantedKeptEntries = new List<TimelineEntry>();
        foreach (var entry in cooldownKeptEntries
                     .OrderBy(item => item.TimeOffsetSec)
                     .ThenByDescending(item => item.Frequency))
        {
            var grantedDecision = AnalyzeGrantedCandidateDecision(entry, grantedKeptEntries, grantedChildRules, openerBuffer);
            if (!grantedDecision.IsTracked)
            {
                grantedKeptEntries.Add(entry);
                continue;
            }

            if (!grantedDecision.IsAllowed)
            {
                if (TryReplaceGrantedWindowAlternative(entry, grantedDecision, grantedKeptEntries, debug))
                    continue;

                debug?.Add($"    prune | {FormatAutoDebugEntry(entry)} | {grantedDecision.Summary}");
                continue;
            }

            grantedKeptEntries.Add(entry);
            debug?.Add($"    keep | {FormatAutoDebugEntry(entry)} | {grantedDecision.Summary}");
        }

        debug?.Add();
        var finalOgcdEntries = ApplyAutoOpenerOgcdGuideline(
            ogcdEntries,
            grantedKeptEntries,
            specName,
            openerBuffer,
            debug);
        return finalOgcdEntries
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private List<TimelineEntry> ApplyAutoOpenerOgcdGuideline(
        List<TimelineEntry> sourceOgcdEntries,
        List<TimelineEntry> selectedOgcdEntries,
        string specName,
        AutoOpenerBufferInfo openerBuffer,
        AutoTimelineDebugRecorder? debug)
    {
        if (!openerBuffer.Enabled || openerBuffer.Steps.Count == 0)
            return selectedOgcdEntries;

        var openerOgcdSteps = openerBuffer.Steps
            .Where(step => step.IsOgcd && !string.IsNullOrWhiteSpace(step.AbilityName))
            .Select(step => step.AbilityName)
            .ToList();
        if (openerOgcdSteps.Count == 0)
            return selectedOgcdEntries;

        var gaugeRules = GaugeSimulator.GetRules(specName);
        var grantedRules = GrantedActionDatabase.GetRules(specName);
        var keptKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<TimelineEntry>(selectedOgcdEntries.Count);
        var chosenEntriesByKey = new Dictionary<string, TimelineEntry>(StringComparer.OrdinalIgnoreCase);
        var lastChosenTimeSec = double.NegativeInfinity;
        var openerCandidatePool = sourceOgcdEntries
            .Where(entry =>
                entry.TimeOffsetSec <= openerBuffer.EndTimeSec + AutoCooldownToleranceSec &&
                entry.Frequency >= AutoStateDrivenOgcdMinFrequency)
            .ToList();
        var availableAbilityNames = openerCandidatePool
            .Select(entry => entry.AbilityName)
            .Where(static abilityName => !string.IsNullOrWhiteSpace(abilityName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? GetPriorChosenLegalityRejectionReason(TimelineEntry candidate)
        {
            var state = CreateAutoTimelineState(specName, gaugeRules, grantedRules, availableAbilityNames);
            foreach (var priorEntry in chosenEntriesByKey.Values
                         .Where(entry => entry.TimeOffsetSec <= candidate.TimeOffsetSec + AutoCooldownToleranceSec)
                         .OrderBy(entry => entry.TimeOffsetSec)
                         .ThenByDescending(entry => entry.Frequency))
            {
                AdvancePassiveResources(state, gaugeRules, priorEntry.TimeOffsetSec);
                var priorInfo = plugin.RecastDatabase.Lookup(priorEntry.AbilityId, priorEntry.AbilityName);
                ApplyAutoEntry(state, priorEntry, priorInfo, gaugeRules, grantedRules, priorEntry.TimeOffsetSec, isGcd: false);
            }

            var candidateInfo = plugin.RecastDatabase.Lookup(candidate.AbilityId, candidate.AbilityName);
            AdvancePassiveResources(state, gaugeRules, candidate.TimeOffsetSec);
            return GetAutoEntryRejectionReason(state, candidate, candidateInfo, gaugeRules, grantedRules, candidate.TimeOffsetSec);
        }

        bool CanCompleteOrderedOpenerOgcdSequence(
            int stepIndex,
            double minimumTimeSec,
            HashSet<string> usedKeys)
        {
            if (stepIndex >= openerOgcdSteps.Count)
                return true;

            var remainingAbilityName = openerOgcdSteps[stepIndex];
            var candidates = openerCandidatePool
                .Where(entry =>
                {
                    var key = GetAutoEntryIdentityKey(entry);
                    return !usedKeys.Contains(key) &&
                           DoesOpenerAbilityMatch(entry.AbilityName, remainingAbilityName) &&
                           entry.TimeOffsetSec + AutoCooldownToleranceSec >= minimumTimeSec;
                })
                .OrderByDescending(entry => entry.Frequency)
                .ThenBy(entry => entry.TimeOffsetSec)
                .ToList();
            if (candidates.Count == 0)
                return false;

            foreach (var candidate in candidates)
            {
                var key = GetAutoEntryIdentityKey(candidate);
                usedKeys.Add(key);
                if (CanCompleteOrderedOpenerOgcdSequence(stepIndex + 1, candidate.TimeOffsetSec, usedKeys))
                    return true;

                usedKeys.Remove(key);
            }

            return false;
        }

        debug?.Add("  opener oGCD guideline");
        for (var stepIndex = 0; stepIndex < openerOgcdSteps.Count; stepIndex++)
        {
            var abilityName = openerOgcdSteps[stepIndex];
            var priorChosenTimeSec = lastChosenTimeSec;
            var candidates = openerCandidatePool
                .Where(entry =>
                    !keptKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
                    DoesOpenerAbilityMatch(entry.AbilityName, abilityName))
                .OrderByDescending(entry => entry.Frequency)
                .ThenBy(entry => entry.TimeOffsetSec)
                .ToList();
            if (candidates.Count == 0)
            {
                debug?.Add($"    miss | slot {stepIndex + 1} | {abilityName} | no raw opener-buffer candidate survived for this ordered opener step");
                continue;
            }

            TimelineEntry? keptEntry = null;
            var legalityRejectionByKey = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                var candidateKey = GetAutoEntryIdentityKey(candidate);
                if (candidate.TimeOffsetSec + AutoCooldownToleranceSec < priorChosenTimeSec)
                    continue;

                var legalityRejectionReason = GetPriorChosenLegalityRejectionReason(candidate);
                legalityRejectionByKey[candidateKey] = legalityRejectionReason;
                if (legalityRejectionReason != null)
                    continue;

                var simulatedUsedKeys = new HashSet<string>(keptKeys, StringComparer.OrdinalIgnoreCase)
                {
                    candidateKey
                };
                if (!CanCompleteOrderedOpenerOgcdSequence(stepIndex + 1, candidate.TimeOffsetSec, simulatedUsedKeys))
                    continue;

                keptEntry = candidate;
                break;
            }

            keptEntry ??= candidates
                .FirstOrDefault(entry =>
                {
                    var candidateKey = GetAutoEntryIdentityKey(entry);
                    legalityRejectionByKey.TryAdd(candidateKey, GetPriorChosenLegalityRejectionReason(entry));
                    return entry.TimeOffsetSec + AutoCooldownToleranceSec >= priorChosenTimeSec &&
                           legalityRejectionByKey[candidateKey] == null;
                })
                ?? candidates.First();
            var keptKey = GetAutoEntryIdentityKey(keptEntry);
            keptKeys.Add(keptKey);
            chosenEntriesByKey[keptKey] = keptEntry;
            var preservedSequence = keptEntry.TimeOffsetSec + AutoCooldownToleranceSec >= priorChosenTimeSec;
            lastChosenTimeSec = keptEntry.TimeOffsetSec;
            var keepReason = preservedSequence
                ? "strongest unique raw opener-buffer candidate kept in ordered opener sequence"
                : "strongest unique raw opener-buffer candidate kept after ordered sequence ran out; earlier fallback preserved this opener step";
            debug?.Add($"    keep | {FormatAutoDebugEntry(keptEntry)} | opener slot {stepIndex + 1} requires {abilityName}; {keepReason}");

            foreach (var prunedEntry in candidates
                         .Where(entry => !string.Equals(GetAutoEntryIdentityKey(entry), keptKey, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(entry => entry.Frequency)
                         .ThenBy(entry => entry.TimeOffsetSec))
            {
                var prunedKey = GetAutoEntryIdentityKey(prunedEntry);
                legalityRejectionByKey.TryAdd(prunedKey, GetPriorChosenLegalityRejectionReason(prunedEntry));
                var legalityReason = legalityRejectionByKey[prunedKey];
                var pruneReason = legalityReason != null
                    ? $"opener slot {stepIndex + 1} would be pruned later by legality checks: {legalityReason}"
                    : prunedEntry.TimeOffsetSec + AutoCooldownToleranceSec >= priorChosenTimeSec
                    ? $"opener slot {stepIndex + 1} already committed to a stronger {abilityName} candidate in the ordered opener sequence"
                    : $"opener slot {stepIndex + 1} already committed to a stronger {abilityName} fallback candidate after in-order options ran out";
                debug?.Add($"    prune | {FormatAutoDebugEntry(prunedEntry)} | {pruneReason}");
            }
        }

        foreach (var entry in chosenEntriesByKey.Values
                     .OrderBy(item => item.TimeOffsetSec)
                     .ThenByDescending(item => item.Frequency))
        {
            var key = GetAutoEntryIdentityKey(entry);
            if (keptKeys.Contains(key))
                result.Add(entry);
        }

        foreach (var entry in selectedOgcdEntries
                     .OrderBy(item => item.TimeOffsetSec)
                     .ThenByDescending(item => item.Frequency))
        {
            if (entry.TimeOffsetSec > openerBuffer.EndTimeSec + AutoCooldownToleranceSec)
            {
                result.Add(entry);
                continue;
            }

            var key = GetAutoEntryIdentityKey(entry);
            if (result.Any(existing => GetAutoEntryIdentityKey(existing) == key))
                continue;

            if (keptKeys.Contains(key))
            {
                result.Add(entry);
                continue;
            }

            if (openerOgcdSteps.Any(openerAbilityName => DoesOpenerAbilityMatch(entry.AbilityName, openerAbilityName)))
                continue;

            if (IsAutoTimelineTrueGaugeAbility(entry.AbilityName, gaugeRules))
            {
                debug?.Add($"    prune | {FormatAutoDebugEntry(entry)} | opener-buffer true-gauge action is not part of the configured opener oGCD roster");
                continue;
            }

            result.Add(entry);
        }

        debug?.Add();
        return result;
    }

    private sealed class ScholarOpenerDissipationSequencePlan
    {
        public required TimelineEntry Dissipation { get; init; }
        public required List<TimelineEntry> EnergyDrains { get; init; }
        public required TimelineEntry ReplacementAetherflow { get; init; }
        public required double SequenceEndTimeSec { get; init; }
    }

    private ScholarOpenerDissipationSequencePlan? TryBuildScholarOpenerDissipationSequencePlan(
        TimelineEntry openerDissipation,
        IReadOnlyList<TimelineEntry> sourceOgcdEntries,
        IReadOnlyList<TimelineEntry> selectedOgcdEntries,
        double openerWindowEndSec,
        AutoTimelineDebugRecorder? debug)
    {
        var openerChainStratagem = selectedOgcdEntries.FirstOrDefault(entry =>
            string.Equals(entry.AbilityName, "Chain Stratagem", StringComparison.OrdinalIgnoreCase) &&
            entry.TimeOffsetSec > openerDissipation.TimeOffsetSec + AutoCooldownToleranceSec);
        var energyDrainStartSec = openerChainStratagem?.TimeOffsetSec ?? openerDissipation.TimeOffsetSec;
        var gcdRecastSec = GetConfiguredAutoTimelineGcdRecastSec();
        var localSearchEndSec = Math.Max(
            openerWindowEndSec + gcdRecastSec * 1.5,
            openerDissipation.TimeOffsetSec + gcdRecastSec * 6.0);

        var openerEnergyDrainCandidates = sourceOgcdEntries
            .Where(entry =>
                string.Equals(entry.AbilityName, "Energy Drain", StringComparison.OrdinalIgnoreCase) &&
                entry.TimeOffsetSec > energyDrainStartSec + AutoCooldownToleranceSec &&
                entry.TimeOffsetSec <= localSearchEndSec + AutoCooldownToleranceSec)
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
        var uniqueEnergyDrainCandidates = openerEnergyDrainCandidates
            .GroupBy(entry => Math.Round(entry.TimeOffsetSec, 3))
            .Select(group => group.OrderByDescending(entry => entry.Frequency).ThenBy(entry => entry.TimeOffsetSec).First())
            .OrderBy(entry => entry.TimeOffsetSec)
            .ToList();
        var chosenEnergyDrains = new List<TimelineEntry>(3);
        foreach (var candidate in uniqueEnergyDrainCandidates)
        {
            if (chosenEnergyDrains.Count > 0 &&
                candidate.TimeOffsetSec < chosenEnergyDrains[^1].TimeOffsetSec + gcdRecastSec - AutoCooldownToleranceSec)
            {
                debug?.Add($"    prune | {FormatAutoDebugEntry(candidate)} | Dissipation opener rule only allows Energy Drain in the first oGCD slot after each weave window");
                continue;
            }

            chosenEnergyDrains.Add(candidate);
            if (chosenEnergyDrains.Count >= 3)
                break;
        }
        if (chosenEnergyDrains.Count < 3)
            return null;

        var replacementAetherflow = sourceOgcdEntries
            .Where(entry =>
                string.Equals(entry.AbilityName, "Aetherflow", StringComparison.OrdinalIgnoreCase) &&
                entry.TimeOffsetSec >= chosenEnergyDrains[^1].TimeOffsetSec + gcdRecastSec - AutoCooldownToleranceSec &&
                entry.TimeOffsetSec <= localSearchEndSec + AutoCooldownToleranceSec)
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .FirstOrDefault();
        if (replacementAetherflow == null)
            return null;

        return new ScholarOpenerDissipationSequencePlan
        {
            Dissipation = openerDissipation,
            EnergyDrains = chosenEnergyDrains,
            ReplacementAetherflow = replacementAetherflow,
            SequenceEndTimeSec = replacementAetherflow.TimeOffsetSec,
        };
    }

    private List<TimelineEntry> ApplyScholarOpenerDissipationSequence(
        List<TimelineEntry> sourceOgcdEntries,
        List<TimelineEntry> selectedOgcdEntries,
        AutoTimelineDebugRecorder? debug)
    {
        var result = selectedOgcdEntries
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
        var openerDissipation = result.FirstOrDefault(entry =>
            string.Equals(entry.AbilityName, "Dissipation", StringComparison.OrdinalIgnoreCase));
        if (openerDissipation == null)
            return result;

        debug?.Add("  scholar opener dissipation");

        var openerWindowEndSec = result
            .Where(entry => entry.IsGcd)
            .OrderBy(entry => entry.TimeOffsetSec)
            .Take(12)
            .Select(entry => entry.TimeOffsetSec)
            .DefaultIfEmpty(openerDissipation.TimeOffsetSec + GetConfiguredAutoTimelineGcdRecastSec() * 10.0)
            .Max();
        var openerSequencePlan = TryBuildScholarOpenerDissipationSequencePlan(
            openerDissipation,
            sourceOgcdEntries,
            result,
            openerWindowEndSec,
            debug);
        if (openerSequencePlan == null)
        {
            result.Remove(openerDissipation);
            debug?.Add($"    prune | {FormatAutoDebugEntry(openerDissipation)} | Dissipation opener rule could not find a local three-Energy-Drain sequence and immediate replacement Aetherflow");
            return result;
        }

        var chosenEnergyDrainKeys = openerSequencePlan.EnergyDrains
            .Select(GetAutoEntryIdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var prunedEntry in result
                     .Where(entry =>
                         entry.TimeOffsetSec >= openerDissipation.TimeOffsetSec - AutoCooldownToleranceSec &&
                         entry.TimeOffsetSec <= openerSequencePlan.SequenceEndTimeSec + AutoCooldownToleranceSec &&
                         (string.Equals(entry.AbilityName, "Aetherflow", StringComparison.OrdinalIgnoreCase) ||
                          IsScholarAetherflowSpender(entry.AbilityName)) &&
                         !(string.Equals(entry.AbilityName, "Energy Drain", StringComparison.OrdinalIgnoreCase) &&
                           chosenEnergyDrainKeys.Contains(GetAutoEntryIdentityKey(entry))))
                     .ToList())
        {
            result.Remove(prunedEntry);
            var pruneReason = string.Equals(prunedEntry.AbilityName, "Aetherflow", StringComparison.OrdinalIgnoreCase)
                ? "Dissipation opener rule requires three Energy Drains before the next Aetherflow"
                : "Dissipation opener rule reserves opener Aetherflow spends for Energy Drain only until the post-sequence Aetherflow";
            debug?.Add($"    prune | {FormatAutoDebugEntry(prunedEntry)} | {pruneReason}");
        }

        foreach (var energyDrain in openerSequencePlan.EnergyDrains)
        {
            if (result.Any(existing => string.Equals(GetAutoEntryIdentityKey(existing), GetAutoEntryIdentityKey(energyDrain), StringComparison.OrdinalIgnoreCase)))
                continue;

            result.Add(energyDrain);
            debug?.Add($"    keep | {FormatAutoDebugEntry(energyDrain)} | Dissipation opener rule forced this Energy Drain as one of three opener gauge spends");
        }
        if (result.All(existing => !string.Equals(GetAutoEntryIdentityKey(existing), GetAutoEntryIdentityKey(openerSequencePlan.ReplacementAetherflow), StringComparison.OrdinalIgnoreCase)))
        {
            result.Add(openerSequencePlan.ReplacementAetherflow);
            debug?.Add($"    keep | {FormatAutoDebugEntry(openerSequencePlan.ReplacementAetherflow)} | Dissipation opener rule reopened Aetherflow immediately after the third opener Energy Drain");
        }

        var aetherflowInfo = plugin.RecastDatabase.Lookup(openerSequencePlan.ReplacementAetherflow.AbilityId, openerSequencePlan.ReplacementAetherflow.AbilityName);
        var aetherflowRecastSec = Math.Max(0.1, aetherflowInfo?.RecastSec ?? 60.0);
        var aetherflowCandidates = sourceOgcdEntries
            .Where(entry =>
                string.Equals(entry.AbilityName, "Aetherflow", StringComparison.OrdinalIgnoreCase) &&
                entry.TimeOffsetSec > openerSequencePlan.ReplacementAetherflow.TimeOffsetSec + AutoCooldownToleranceSec)
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();

        foreach (var prunedAetherflow in result
                     .Where(entry =>
                         string.Equals(entry.AbilityName, "Aetherflow", StringComparison.OrdinalIgnoreCase) &&
                         entry.TimeOffsetSec > openerSequencePlan.ReplacementAetherflow.TimeOffsetSec + AutoCooldownToleranceSec)
                     .ToList())
        {
            result.Remove(prunedAetherflow);
            debug?.Add($"    prune | {FormatAutoDebugEntry(prunedAetherflow)} | Scholar opener rebased the Aetherflow cooldown chain from the corrected first post-opener Aetherflow");
        }

        var nextReadyTimeSec = openerSequencePlan.ReplacementAetherflow.TimeOffsetSec + aetherflowRecastSec;
        while (aetherflowCandidates.Count > 0)
        {
            if (!TryFindCooldownWindowCandidate(
                    aetherflowCandidates,
                    nextReadyTimeSec,
                    aetherflowRecastSec,
                    AutoStateDrivenOgcdMinFrequency,
                    useBaselineSelection: false,
                    out var chosenCandidate,
                    out _,
                    out _,
                    out _))
            {
                break;
            }
            result.Add(chosenCandidate);
            debug?.Add($"    keep | {FormatAutoDebugEntry(chosenCandidate)} | Scholar opener rebased later Aetherflow windows from {FormatAutoDebugEntry(openerSequencePlan.ReplacementAetherflow)}");

            nextReadyTimeSec = chosenCandidate.TimeOffsetSec + aetherflowRecastSec;
            aetherflowCandidates = aetherflowCandidates
                .Where(candidate =>
                    !string.Equals(GetAutoEntryIdentityKey(candidate), GetAutoEntryIdentityKey(chosenCandidate), StringComparison.OrdinalIgnoreCase) &&
                    candidate.TimeOffsetSec > chosenCandidate.TimeOffsetSec + AutoCooldownToleranceSec)
                .ToList();
        }

        return result
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private List<TimelineEntry> ApplyScholarEnergyDrainWeaveSlotRule(
        List<TimelineEntry> selectedGcdEntries,
        List<TimelineEntry> selectedOgcdEntries,
        AutoTimelineDebugRecorder? debug)
    {
        var orderedOgcdEntries = selectedOgcdEntries
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
        if (orderedOgcdEntries.Count == 0 || selectedGcdEntries.Count == 0)
            return orderedOgcdEntries;

        var orderedGcdTimes = selectedGcdEntries
            .OrderBy(entry => entry.TimeOffsetSec)
            .Select(entry => entry.TimeOffsetSec)
            .Distinct()
            .ToList();
        if (orderedGcdTimes.Count == 0)
            return orderedOgcdEntries;

        var firstGcdTimeSec = orderedGcdTimes[0];
        var protectedPrefix = orderedOgcdEntries
            .Where(entry => entry.TimeOffsetSec < firstGcdTimeSec - AutoCooldownToleranceSec)
            .ToList();
        var adjustableEntries = orderedOgcdEntries
            .Where(entry => entry.TimeOffsetSec >= firstGcdTimeSec - AutoCooldownToleranceSec)
            .ToList();
        if (adjustableEntries.Count == 0)
            return orderedOgcdEntries;

        var gcdRecastSec = GetConfiguredAutoTimelineGcdRecastSec();
        var slotSpacingSec = gcdRecastSec / 3.0;
        if (slotSpacingSec <= AutoCooldownToleranceSec)
            return orderedOgcdEntries;

        debug?.Add("  scholar energy drain weave slots");

        var slotTimes = new List<double>(Math.Max(8, orderedGcdTimes.Count * 2 + 4));
        void AppendWindowSlots(double gcdTimeSec)
        {
            slotTimes.Add(Math.Round(gcdTimeSec + slotSpacingSec, 3));
            slotTimes.Add(Math.Round(gcdTimeSec + slotSpacingSec * 2.0, 3));
        }

        foreach (var gcdTimeSec in orderedGcdTimes)
            AppendWindowSlots(gcdTimeSec);

        var generatedGcdTimeSec = orderedGcdTimes[^1];
        var maxRequestedTimeSec = adjustableEntries.Max(entry => entry.TimeOffsetSec);
        while (slotTimes.Count == 0 || slotTimes[^1] < maxRequestedTimeSec + gcdRecastSec + AutoCooldownToleranceSec)
        {
            generatedGcdTimeSec += gcdRecastSec;
            AppendWindowSlots(generatedGcdTimeSec);
        }

        var slotCursor = 0;
        foreach (var entry in adjustableEntries)
        {
            while (slotCursor < slotTimes.Count &&
                   slotTimes[slotCursor] < entry.TimeOffsetSec - AutoCooldownToleranceSec)
            {
                slotCursor++;
            }

            if (slotCursor >= slotTimes.Count)
            {
                generatedGcdTimeSec += gcdRecastSec;
                AppendWindowSlots(generatedGcdTimeSec);
            }

            var assignedSlotIndex = slotCursor;
            if (string.Equals(entry.AbilityName, "Energy Drain", StringComparison.OrdinalIgnoreCase) &&
                assignedSlotIndex % 2 == 0)
            {
                assignedSlotIndex++;
                while (assignedSlotIndex >= slotTimes.Count)
                {
                    generatedGcdTimeSec += gcdRecastSec;
                    AppendWindowSlots(generatedGcdTimeSec);
                }
            }

            var originalTimeSec = entry.TimeOffsetSec;
            var updatedTimeSec = slotTimes[assignedSlotIndex];
            if (Math.Abs(updatedTimeSec - originalTimeSec) > AutoCooldownToleranceSec)
            {
                var moveReason = string.Equals(entry.AbilityName, "Energy Drain", StringComparison.OrdinalIgnoreCase)
                    ? "Scholar Energy Drain can only occupy the second oGCD slot in a weave bucket"
                    : "shifted later because Scholar Energy Drain reserved the second oGCD slot ahead of it";
                debug?.Add($"    move | {entry.AbilityName} | {FormatTime(originalTimeSec)} -> {FormatTime(updatedTimeSec)} | {moveReason}");
                entry.TimeOffsetSec = updatedTimeSec;
            }

            slotCursor = assignedSlotIndex + 1;
        }

        return protectedPrefix
            .Concat(adjustableEntries)
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private List<TimelineEntry> RebaseMovedOgcdCooldownChains(
        List<TimelineEntry> sourceOgcdEntries,
        List<TimelineEntry> selectedOgcdEntries,
        AutoTimelineDebugRecorder? debug = null)
    {
        if (sourceOgcdEntries.Count == 0 || selectedOgcdEntries.Count < 2)
            return selectedOgcdEntries
                .OrderBy(entry => entry.TimeOffsetSec)
                .ThenByDescending(entry => entry.Frequency)
                .ToList();

        var rebasedEntries = selectedOgcdEntries
            .Select(CloneTimelineEntry)
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
        var didRebaseAny = false;

        foreach (var abilityGroup in rebasedEntries
                     .Select((entry, index) => (Entry: entry, Index: index))
                     .GroupBy(
                         item => item.Entry.AbilityName,
                         item => item,
                         StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var firstEntry = abilityGroup.First().Entry;
            var recastInfo = plugin.RecastDatabase.Lookup(firstEntry.AbilityId, firstEntry.AbilityName);
            if (recastInfo == null ||
                recastInfo.IsGcdAction ||
                recastInfo.MaxCharges > 1 ||
                recastInfo.RecastSec < 5.0)
            {
                continue;
            }

            var groupItems = abilityGroup
                .OrderBy(item => item.Entry.TimeOffsetSec)
                .ToList();
            var sourceCandidates = sourceOgcdEntries
                .Where(entry => string.Equals(entry.AbilityName, firstEntry.AbilityName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.TimeOffsetSec)
                .ThenByDescending(entry => entry.Frequency)
                .ToList();
            if (sourceCandidates.Count == 0)
                continue;

            var usedCandidateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                GetAutoEntryIdentityKey(groupItems[0].Entry),
            };
            var previousEntry = groupItems[0].Entry;

            for (var i = 1; i < groupItems.Count; i++)
            {
                var currentItem = groupItems[i];
                var currentEntry = currentItem.Entry;
                var readyTimeSec = previousEntry.TimeOffsetSec + recastInfo.RecastSec;
                if (currentEntry.TimeOffsetSec >= readyTimeSec - AutoCooldownToleranceSec)
                {
                    usedCandidateKeys.Add(GetAutoEntryIdentityKey(currentEntry));
                    previousEntry = currentEntry;
                    continue;
                }

                var replacementCandidates = sourceCandidates
                    .Where(candidate => !usedCandidateKeys.Contains(GetAutoEntryIdentityKey(candidate)))
                    .ToList();
                if (!TryFindCooldownWindowCandidate(
                        replacementCandidates,
                        readyTimeSec,
                        recastInfo.RecastSec,
                        AutoStateDrivenOgcdMinFrequency,
                        useBaselineSelection: false,
                        out var replacement,
                        out _,
                        out _,
                        out _))
                {
                    continue;
                }

                var replacementClone = CloneTimelineEntry(replacement);
                rebasedEntries[currentItem.Index] = replacementClone;
                usedCandidateKeys.Add(GetAutoEntryIdentityKey(replacementClone));
                if (!didRebaseAny)
                    debug?.Add("  scholar cooldown rebase");
                debug?.Add($"    rebase | {currentEntry.AbilityName} | {FormatTime(currentEntry.TimeOffsetSec)} -> {FormatTime(replacementClone.TimeOffsetSec)} | prior moved use now sets next ready at {FormatTime(readyTimeSec)}");
                previousEntry = replacementClone;
                didRebaseAny = true;
            }
        }

        return rebasedEntries
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private List<TimelineEntry> RebaseMovedScholarTimedLockoutEntries(
        List<TimelineEntry> sourceOgcdEntries,
        List<TimelineEntry> selectedOgcdEntries,
        AutoTimelineDebugRecorder? debug = null)
    {
        if (sourceOgcdEntries.Count == 0 || selectedOgcdEntries.Count == 0)
        {
            return selectedOgcdEntries
                .OrderBy(entry => entry.TimeOffsetSec)
                .ThenByDescending(entry => entry.Frequency)
                .ToList();
        }

        var rebasedEntries = selectedOgcdEntries
            .Select(CloneTimelineEntry)
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
        var sourceEntriesByAbility = sourceOgcdEntries
            .GroupBy(entry => entry.AbilityName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(entry => entry.TimeOffsetSec)
                    .ThenByDescending(entry => entry.Frequency)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
        var timedLockoutEndByAbility = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var finalizedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastChosenByAbility = new Dictionary<string, TimelineEntry>(StringComparer.OrdinalIgnoreCase);
        var didRebaseAny = false;

        for (var index = 0; index < rebasedEntries.Count; index++)
        {
            var currentEntry = rebasedEntries[index];
            var timedLockoutReason = GetTimedUsageLockoutReason(
                "Scholar",
                timedLockoutEndByAbility,
                currentEntry.AbilityName,
                currentEntry.TimeOffsetSec);
            if (timedLockoutReason != null &&
                timedLockoutEndByAbility.TryGetValue(currentEntry.AbilityName, out var lockoutEndSec) &&
                sourceEntriesByAbility.TryGetValue(currentEntry.AbilityName, out var sourceCandidates))
            {
                var recastInfo = plugin.RecastDatabase.Lookup(currentEntry.AbilityId, currentEntry.AbilityName);
                var windowStartSec = lockoutEndSec;
                double? windowEndSec = null;
                if (recastInfo != null &&
                    !recastInfo.IsGcdAction &&
                    recastInfo.MaxCharges <= 1 &&
                    recastInfo.RecastSec >= 5.0 &&
                    lastChosenByAbility.TryGetValue(currentEntry.AbilityName, out var previousAbilityEntry))
                {
                    windowStartSec = Math.Max(windowStartSec, previousAbilityEntry.TimeOffsetSec + recastInfo.RecastSec);
                    windowEndSec = previousAbilityEntry.TimeOffsetSec + recastInfo.RecastSec * 2.0;
                }

                var replacementCandidates = sourceCandidates
                    .Where(candidate =>
                        !finalizedKeys.Contains(GetAutoEntryIdentityKey(candidate)) &&
                        candidate.TimeOffsetSec >= windowStartSec - AutoCooldownToleranceSec &&
                        (!windowEndSec.HasValue || candidate.TimeOffsetSec <= windowEndSec.Value + AutoCooldownToleranceSec))
                    .ToList();
                if (replacementCandidates.Count > 0)
                {
                    var aboveThresholdCandidates = replacementCandidates
                        .Where(candidate => candidate.Frequency >= AutoStateDrivenOgcdMinFrequency)
                        .ToList();
                    var selectedPool = aboveThresholdCandidates.Count > 0
                        ? aboveThresholdCandidates
                        : replacementCandidates;
                    var replacement = CloneTimelineEntry(selectedPool
                        .OrderByDescending(candidate => candidate.Frequency)
                        .ThenBy(candidate => candidate.TimeOffsetSec)
                        .First());
                    if (!didRebaseAny)
                        debug?.Add("  scholar timed lockout rebase");
                    debug?.Add($"    rebase | {currentEntry.AbilityName} | {FormatTime(currentEntry.TimeOffsetSec)} -> {FormatTime(replacement.TimeOffsetSec)} | moved entry became illegal because {timedLockoutReason}");
                    rebasedEntries[index] = replacement;
                    currentEntry = replacement;
                    didRebaseAny = true;
                }
            }

            finalizedKeys.Add(GetAutoEntryIdentityKey(currentEntry));
            lastChosenByAbility[currentEntry.AbilityName] = currentEntry;
            ApplyTimedUsageLockoutState("Scholar", timedLockoutEndByAbility, currentEntry.AbilityName, currentEntry.TimeOffsetSec);
        }

        return rebasedEntries
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private List<TimelineEntry> ApplyScholarDissipationSelectionRules(
        List<TimelineEntry> sourceOgcdEntries,
        List<TimelineEntry> selectedOgcdEntries,
        AutoOpenerBufferInfo openerBuffer,
        AutoTimelineDebugRecorder? debug = null)
    {
        var dissipationCandidates = sourceOgcdEntries
            .Where(entry => string.Equals(entry.AbilityName, "Dissipation", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
        if (dissipationCandidates.Count == 0)
            return selectedOgcdEntries;

        var nonDissipationEntries = selectedOgcdEntries
            .Where(entry => !string.Equals(entry.AbilityName, "Dissipation", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
        var dissipationInfo = plugin.RecastDatabase.Lookup(0, "Dissipation");
        var dissipationRecastSec = Math.Max(0.1, dissipationInfo?.RecastSec ?? 180.0);
        var gaugeRules = GaugeSimulator.GetRules("Scholar");
        var grantedRules = GrantedActionDatabase.GetRules("Scholar");
        var availableAbilityNames = selectedOgcdEntries
            .Select(entry => entry.AbilityName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chosenDissipationEntries = new List<TimelineEntry>();

        debug?.Add("  scholar dissipation timing");

        var currentWindowStartSec = dissipationCandidates[0].TimeOffsetSec;
        var finalCandidateTimeSec = dissipationCandidates[^1].TimeOffsetSec;
        while (currentWindowStartSec <= finalCandidateTimeSec + AutoCooldownToleranceSec)
        {
            var currentWindowEndSec = currentWindowStartSec + dissipationRecastSec;
            var windowCandidates = dissipationCandidates
                .Where(candidate =>
                    candidate.TimeOffsetSec >= currentWindowStartSec - 0.001 &&
                    candidate.TimeOffsetSec < currentWindowEndSec - AutoCooldownToleranceSec)
                .ToList();
            if (windowCandidates.Count == 0)
            {
                currentWindowStartSec += dissipationRecastSec;
                continue;
            }

            var orderedWindowCandidates = windowCandidates
                .OrderByDescending(candidate => candidate.Frequency)
                .ThenBy(candidate => candidate.TimeOffsetSec)
                .ToList();
            TimelineEntry? chosenCandidate = null;
            foreach (var candidate in orderedWindowCandidates)
            {
                var rejectionReason = GetScholarDissipationCandidateRestrictionReason(
                    candidate,
                    nonDissipationEntries,
                    chosenDissipationEntries,
                    gaugeRules,
                    grantedRules,
                    availableAbilityNames,
                    openerBuffer);
                if (rejectionReason != null)
                {
                    debug?.Add($"    prune | {FormatAutoDebugEntry(candidate)} | {rejectionReason}");
                    continue;
                }

                chosenCandidate = candidate;
                if (IsWithinAutoOpenerBuffer(openerBuffer, candidate.TimeOffsetSec))
                    debug?.Add($"    keep | {FormatAutoDebugEntry(candidate)} | opener buffer waived Scholar Dissipation timing checks in cooldown window {FormatTime(currentWindowStartSec)}-{FormatTime(currentWindowEndSec)}");
                else
                    debug?.Add($"    keep | {FormatAutoDebugEntry(candidate)} | Scholar Dissipation timing rule satisfied in cooldown window {FormatTime(currentWindowStartSec)}-{FormatTime(currentWindowEndSec)}");
                break;
            }

            if (chosenCandidate == null)
            {
                currentWindowStartSec += dissipationRecastSec;
                continue;
            }

            foreach (var candidate in windowCandidates)
            {
                if (string.Equals(GetAutoEntryIdentityKey(candidate), GetAutoEntryIdentityKey(chosenCandidate), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (orderedWindowCandidates.IndexOf(candidate) > orderedWindowCandidates.IndexOf(chosenCandidate))
                    debug?.Add($"    prune | {FormatAutoDebugEntry(candidate)} | cooldown window {FormatTime(currentWindowStartSec)}-{FormatTime(currentWindowEndSec)} already committed to earlier valid Dissipation keep {FormatAutoDebugEntry(chosenCandidate)}");
            }

            chosenDissipationEntries.Add(chosenCandidate);
            currentWindowStartSec = chosenCandidate.TimeOffsetSec + dissipationRecastSec - AutoCooldownToleranceSec;
        }

        debug?.Add();
        return nonDissipationEntries
            .Concat(chosenDissipationEntries)
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private List<TimelineEntry> RepairAutoGcdEntries(
        List<TimelineEntry> sourceGcdEntries,
        List<TimelineEntry> selectedGcdEntries,
        List<TimelineEntry> selectedOgcdEntries,
        string specName,
        ISet<string> rawAbilityNames,
        AutoTimelineDebugRecorder? debug = null)
    {
        debug?.Add("GCD Repair");
        debug?.Add("  Archived.");
        debug?.Add();
        return selectedGcdEntries
            .OrderBy(e => e.TimeOffsetSec)
            .ThenByDescending(e => e.Frequency)
            .ToList();
    }

    private (BalanceOpenerDatabase.JobOpenerVariant? Variant, int StartSlotOffset, List<TimelineEntry> Entries) SelectAutoOpenerEntries(
        List<TimelineEntry> gcdEntries,
        string specName,
        AutoTimelineDebugRecorder? debug)
    {
        var hints = BalanceOpenerDatabase.GetHints(specName);
        if (hints == null || hints.Variants.Count == 0)
        {
            debug?.Add("  opener | none configured");
            return (null, 0, []);
        }

        BalanceOpenerDatabase.JobOpenerVariant? bestVariant = null;
        var bestStartSlotOffset = 0;
        var bestEntries = new List<TimelineEntry>();
        var bestMatchedCount = int.MinValue;
        var bestScore = double.NegativeInfinity;
        foreach (var variant in hints.Variants)
        {
            var variantGcdSequence = variant.GcdSequence.ToArray();
            for (var startOffset = 0; startOffset < variantGcdSequence.Length; startOffset++)
            {
                var bestVariantEntries = new List<TimelineEntry>();
                var bestVariantScore = 0.0;
                var matchedCount = 0;
                var usedVariantKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var gcdIndex = 0; gcdIndex + startOffset < variantGcdSequence.Length; gcdIndex++)
                {
                    var abilityName = variantGcdSequence[gcdIndex + startOffset];
                    if (!IsConcreteOpenerAbilityName(abilityName) || !IsStandardTimelineGcdAbility(abilityName))
                        continue;

                    var slotTimeSec = gcdIndex * GetConfiguredAutoTimelineGcdRecastSec();
                    var candidate = GetAutoOpenerRequirementCandidates(gcdEntries, usedVariantKeys, slotTimeSec, abilityName)
                        .FirstOrDefault();
                    if (candidate == null)
                        continue;

                    usedVariantKeys.Add(GetAutoEntryIdentityKey(candidate));
                    bestVariantEntries.Add(candidate);
                    bestVariantScore += candidate.Frequency * 100.0;
                    matchedCount++;
                }

                var remainingConcreteCount = variantGcdSequence
                    .Skip(startOffset)
                    .Count(abilityName => IsConcreteOpenerAbilityName(abilityName) && IsStandardTimelineGcdAbility(abilityName));
                var startAbilityName = variantGcdSequence[startOffset];
                debug?.Add($"  opener-variant | {variant.Name} | start {startOffset:00} ({startAbilityName}) | matched {matchedCount}/{Math.Max(1, remainingConcreteCount)} | score {bestVariantScore:F1}");
                var isBetter = matchedCount > bestMatchedCount ||
                               (matchedCount == bestMatchedCount && bestVariantScore > bestScore) ||
                               (matchedCount == bestMatchedCount && Math.Abs(bestVariantScore - bestScore) < 0.0001 && startOffset < bestStartSlotOffset);
                if (!isBetter)
                    continue;

                bestVariant = variant;
                bestStartSlotOffset = startOffset;
                bestEntries = bestVariantEntries;
                bestMatchedCount = matchedCount;
                bestScore = bestVariantScore;
            }
        }

        if (bestVariant != null)
            debug?.Add($"  opener-picked | {bestVariant.Name} | start {bestStartSlotOffset:00} ({bestVariant.GcdSequence[bestStartSlotOffset]}) | GCDs {bestEntries.Count}");

        return (bestVariant, bestStartSlotOffset, bestEntries);
    }

    private static bool IsConcreteOpenerAbilityName(string abilityName)
    {
        if (string.IsNullOrWhiteSpace(abilityName))
            return false;

        return !string.Equals(abilityName, "Step", StringComparison.OrdinalIgnoreCase) &&
               !abilityName.Contains("Priority GCD", StringComparison.OrdinalIgnoreCase) &&
               !abilityName.Contains(" / ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DoesOpenerAbilityMatch(string entryAbilityName, string openerAbilityName)
    {
        if (string.Equals(entryAbilityName, openerAbilityName, StringComparison.OrdinalIgnoreCase))
            return true;

        return EnumerateEquivalentOpenerAbilityNames(openerAbilityName)
            .Any(name => string.Equals(name, entryAbilityName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateEquivalentOpenerAbilityNames(string abilityName)
    {
        yield return abilityName;

        if (string.Equals(abilityName, "Standard Finish", StringComparison.OrdinalIgnoreCase))
            yield return "Double Standard Finish";
        else if (string.Equals(abilityName, "Double Standard Finish", StringComparison.OrdinalIgnoreCase))
            yield return "Standard Finish";

        if (string.Equals(abilityName, "Technical Finish", StringComparison.OrdinalIgnoreCase))
            yield return "Quadruple Technical Finish";
        else if (string.Equals(abilityName, "Quadruple Technical Finish", StringComparison.OrdinalIgnoreCase))
            yield return "Technical Finish";
    }

    private List<TimelineEntry> GetAutoOpenerRequirementCandidates(
        List<TimelineEntry> gcdEntries,
        ISet<string> usedKeys,
        double slotTimeSec,
        string openerAbilityName)
    {
        var exactWindowStartSec = GetAutoGcdSlotWindowStartSec(slotTimeSec);
        var exactWindowEndSec = GetAutoGcdSlotWindowEndSec(slotTimeSec);
        var searchStartSec = Math.Max(0.0, exactWindowStartSec - GetConfiguredAutoTimelineGcdRecastSec());
        var searchEndSec = exactWindowEndSec + GetConfiguredAutoTimelineGcdRecastSec() + AutoCooldownToleranceSec;
        return gcdEntries
            .Where(entry =>
                !usedKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
                entry.Frequency >= AutoGcdMinFrequency &&
                DoesOpenerAbilityMatch(entry.AbilityName, openerAbilityName) &&
                entry.TimeOffsetSec >= searchStartSec - 0.001 &&
                entry.TimeOffsetSec < searchEndSec - 0.001)
            .OrderByDescending(entry => IsWithinAutoSlotWindow(entry.TimeOffsetSec, slotTimeSec))
            .ThenBy(entry => Math.Abs(entry.TimeOffsetSec - slotTimeSec))
            .ThenByDescending(entry => entry.Frequency)
            .ThenBy(entry => entry.TimeOffsetSec)
            .ToList();
    }

    private List<TimelineEntry> GetAutoGcdSlotCandidates(
        List<TimelineEntry> gcdEntries,
        HashSet<string> usedKeys,
        double slotTimeSec)
    {
        var windowStartSec = GetAutoGcdSlotWindowStartSec(slotTimeSec);
        var windowEndSec = GetAutoGcdSlotWindowEndSec(slotTimeSec);
        return gcdEntries
            .Where(entry =>
                !usedKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
                entry.Frequency >= AutoGcdMinFrequency &&
                entry.TimeOffsetSec >= windowStartSec - AutoGcdSlotMatchToleranceSec &&
                entry.TimeOffsetSec <= windowEndSec + AutoGcdSlotMatchToleranceSec)
            .OrderByDescending(entry => IsWithinAutoSlotWindow(entry.TimeOffsetSec, slotTimeSec))
            .ThenBy(entry => Math.Abs(entry.TimeOffsetSec - slotTimeSec))
            .ThenByDescending(entry => entry.Frequency)
            .ThenBy(entry => entry.TimeOffsetSec)
            .ToList();
    }

    private static bool IsWithinAutoSlotWindow(double entryTimeSec, double slotTimeSec)
        => entryTimeSec >= GetAutoGcdSlotWindowStartSec(slotTimeSec) - AutoGcdSlotMatchToleranceSec &&
           entryTimeSec <= GetAutoGcdSlotWindowEndSec(slotTimeSec) + AutoGcdSlotMatchToleranceSec;

    private static string BuildOpenerRequirementSummary(
        TimelineEntry entry,
        double slotTimeSec,
        string? openerRequirement)
    {
        if (string.IsNullOrWhiteSpace(openerRequirement))
            return string.Empty;

        return IsWithinAutoSlotWindow(entry.TimeOffsetSec, slotTimeSec)
            ? $"matches opener requirement {openerRequirement}; "
            : $"matches opener requirement {openerRequirement} via opener timing tolerance from {FormatTime(entry.TimeOffsetSec)}; ";
    }

    private TimelineEntry? ChooseAutoGcdSlotEntry(
        List<TimelineEntry> allGcdEntries,
        List<TimelineEntry> slotCandidates,
        HashSet<string> usedKeys,
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        JobComboDatabase.JobComboHints? comboHints,
        string? burstPreferredAbility,
        double slotTimeSec,
        Dictionary<string, double> lastAbilityTimes,
        Dictionary<string, double> pseudoCooldowns,
        AutoTimelineDebugRecorder? debug)
    {
        var dueDotCandidate = slotCandidates
            .Where(entry => IsDotCandidateDue(entry, slotTimeSec, lastAbilityTimes))
            .OrderByDescending(entry => entry.Frequency)
            .ThenBy(entry => entry.TimeOffsetSec)
            .FirstOrDefault(entry => IsAutoGcdCandidateLegal(
                state,
                entry,
                gaugeRules,
                grantedRules,
                slotTimeSec,
                lastAbilityTimes,
                pseudoCooldowns));
        if (dueDotCandidate != null)
        {
            debug?.Add($"  slot-choice | due DoT | {dueDotCandidate.AbilityName}");
            return dueDotCandidate;
        }

        var grantedCandidate = FindNextStateDrivenAutoGcdCandidate(
            allGcdEntries,
            usedKeys,
            state,
            gaugeRules,
            grantedRules,
            slotTimeSec,
            lastAbilityTimes,
            pseudoCooldowns);
        if (grantedCandidate != null)
        {
            debug?.Add($"  slot-choice | granted/state | {grantedCandidate.AbilityName}");
            return grantedCandidate;
        }

        var overcapAvoidingCandidate = FindOvercapAvoidingGcdCandidate(
            slotCandidates,
            state,
            gaugeRules,
            grantedRules,
            slotTimeSec,
            lastAbilityTimes,
            pseudoCooldowns);
        if (overcapAvoidingCandidate != null)
        {
            debug?.Add($"  slot-choice | overcap avoid | {overcapAvoidingCandidate.AbilityName}");
            return overcapAvoidingCandidate;
        }

        if (!string.IsNullOrWhiteSpace(burstPreferredAbility))
        {
            var burstCandidate = slotCandidates
                .Where(entry => string.Equals(entry.AbilityName, burstPreferredAbility, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.Frequency)
                .ThenBy(entry => entry.TimeOffsetSec)
                .FirstOrDefault(entry => IsAutoGcdCandidateLegal(
                    state,
                    entry,
                    gaugeRules,
                    grantedRules,
                    slotTimeSec,
                    lastAbilityTimes,
                    pseudoCooldowns));
            if (burstCandidate != null)
            {
                debug?.Add($"  slot-choice | burst bias | {burstCandidate.AbilityName}");
                return burstCandidate;
            }
        }

        if (comboHints != null)
        {
            var comboStarterCandidate = slotCandidates
                .Where(entry => comboHints.ComboStarters.Contains(entry.AbilityName))
                .OrderByDescending(entry => entry.Frequency)
                .ThenBy(entry => entry.TimeOffsetSec)
                .FirstOrDefault(entry => IsAutoGcdCandidateLegal(
                    state,
                    entry,
                    gaugeRules,
                    grantedRules,
                    slotTimeSec,
                    lastAbilityTimes,
                    pseudoCooldowns));
            if (comboStarterCandidate != null)
            {
                debug?.Add($"  slot-choice | combo start | {comboStarterCandidate.AbilityName}");
                return comboStarterCandidate;
            }
        }

        return slotCandidates
            .OrderByDescending(entry => entry.Frequency)
            .ThenBy(entry => entry.TimeOffsetSec)
            .FirstOrDefault(entry => IsAutoGcdCandidateLegal(
                state,
                entry,
                gaugeRules,
                grantedRules,
                slotTimeSec,
                lastAbilityTimes,
                pseudoCooldowns));
    }

    private TimelineEntry? FindOvercapAvoidingGcdCandidate(
        IReadOnlyList<TimelineEntry> slotCandidates,
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        double slotTimeSec,
        Dictionary<string, double> lastAbilityTimes,
        Dictionary<string, double> pseudoCooldowns)
    {
        if (gaugeRules == null)
            return null;

        var legalCandidates = slotCandidates
            .Where(entry => IsAutoGcdCandidateLegal(
                state,
                entry,
                gaugeRules,
                grantedRules,
                slotTimeSec,
                lastAbilityTimes,
                pseudoCooldowns))
            .Select(entry => new
            {
                Entry = entry,
                Penalty = GetGaugePenalty(state, gaugeRules, grantedRules, entry.AbilityName),
                IsSpender = IsAutoTimelineTrueGaugeSpender(entry.AbilityName, gaugeRules),
            })
            .ToList();
        if (legalCandidates.Count == 0 || legalCandidates.All(candidate => candidate.Penalty == 0))
            return null;

        var best = legalCandidates
            .OrderBy(candidate => candidate.Penalty)
            .ThenByDescending(candidate => candidate.IsSpender)
            .ThenByDescending(candidate => candidate.Entry.Frequency)
            .ThenBy(candidate => candidate.Entry.TimeOffsetSec)
            .First();
        var worstPenalty = legalCandidates.Max(candidate => candidate.Penalty);
        return best.Penalty < worstPenalty ? best.Entry : null;
    }

    private TimelineEntry? FindNextExpectedComboCandidate(
        List<TimelineEntry> allGcdEntries,
        HashSet<string> usedKeys,
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        HashSet<string> expectedComboAbilities,
        double slotTimeSec,
        Dictionary<string, double> lastAbilityTimes,
        Dictionary<string, double> pseudoCooldowns)
    {
        if (expectedComboAbilities.Count == 0)
            return null;

        return allGcdEntries
            .Where(entry =>
                entry.Frequency >= AutoStateDrivenOgcdMinFrequency &&
                !usedKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
                expectedComboAbilities.Contains(entry.AbilityName) &&
                entry.TimeOffsetSec >= slotTimeSec - AutoCooldownToleranceSec)
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .FirstOrDefault(entry => IsAutoGcdCandidateLegal(
                state,
                entry,
                gaugeRules,
                grantedRules,
                slotTimeSec,
                lastAbilityTimes,
                pseudoCooldowns));
    }

    private TimelineEntry? FindNextStateDrivenAutoGcdCandidate(
        List<TimelineEntry> allGcdEntries,
        HashSet<string> usedKeys,
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        double slotTimeSec,
        Dictionary<string, double> lastAbilityTimes,
        Dictionary<string, double> pseudoCooldowns)
    {
        return allGcdEntries
            .Where(entry =>
                entry.Frequency >= AutoStateDrivenOgcdMinFrequency &&
                !usedKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
                entry.TimeOffsetSec >= slotTimeSec - AutoCooldownToleranceSec &&
                IsStateDrivenGcdCandidateReady(state, entry, gaugeRules, grantedRules))
            .OrderByDescending(entry => GetStateDrivenSelectionPriority(entry, slotTimeSec, lastAbilityTimes))
            .ThenByDescending(entry => GetRecentGrantorTimeSec(entry, slotTimeSec, lastAbilityTimes))
            .ThenBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .FirstOrDefault(entry => IsAutoGcdCandidateLegal(
                state,
                entry,
                gaugeRules,
                grantedRules,
                slotTimeSec,
                lastAbilityTimes,
                pseudoCooldowns));
    }

    private bool IsStateDrivenGcdCandidateReady(
        AutoTimelineState state,
        TimelineEntry entry,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        var actionRule = plugin.ActionStateDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        if (actionRule != null)
        {
            foreach (var effect in actionRule.Effects)
            {
                if (effect.MinRequired <= 0)
                    continue;

                if (!ShouldEnforceActionStateRequirement(effect.StateName, state.AvailableAbilityNames, gaugeRules, grantedRules))
                    continue;

                if (GetEffectiveStateValue(effect.StateName, state.ActionState, state.GaugeState, state.GrantedState) >= effect.MinRequired)
                    return true;
            }
        }

        if (grantedRules != null &&
            grantedRules.EffectByName.TryGetValue(entry.AbilityName, out var grantedEffects) &&
            grantedEffects.Any(effect => effect.MinRequired > 0 && state.GrantedState.GetValueOrDefault(effect.ResourceName) >= effect.MinRequired))
        {
            return true;
        }

        return false;
    }

    private int GetStateDrivenSelectionPriority(
        TimelineEntry entry,
        double slotTimeSec,
        Dictionary<string, double> lastAbilityTimes)
    {
        var actionRule = plugin.ActionStateDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        if (actionRule == null)
            return 0;

        var hasRecentGrantor = false;
        var grantsAnotherState = actionRule.Effects.Any(effect => effect.Delta > 0);
        foreach (var effect in actionRule.Effects)
        {
            if (effect.MinRequired <= 0)
                continue;

            foreach (var grantorName in plugin.ActionStateDatabase.GetGrantors(effect.StateName))
            {
                if (lastAbilityTimes.TryGetValue(grantorName, out var grantorTimeSec) &&
                    grantorTimeSec <= slotTimeSec + AutoCooldownToleranceSec)
                {
                    hasRecentGrantor = true;
                    break;
                }
            }

            if (hasRecentGrantor)
                break;
        }

        if (!hasRecentGrantor)
            return 0;

        if (JobComboDatabase.IsComboStarter(entry.AbilityName))
            return 3;

        if (grantsAnotherState)
            return 2;

        return 1;
    }

    private double GetRecentGrantorTimeSec(
        TimelineEntry entry,
        double slotTimeSec,
        Dictionary<string, double> lastAbilityTimes)
    {
        var actionRule = plugin.ActionStateDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        if (actionRule == null)
            return double.NegativeInfinity;

        var best = double.NegativeInfinity;
        foreach (var effect in actionRule.Effects)
        {
            if (effect.MinRequired <= 0)
                continue;

            foreach (var grantorName in plugin.ActionStateDatabase.GetGrantors(effect.StateName))
            {
                if (!lastAbilityTimes.TryGetValue(grantorName, out var grantorTimeSec))
                    continue;

                if (grantorTimeSec > slotTimeSec + AutoCooldownToleranceSec)
                    continue;

                best = Math.Max(best, grantorTimeSec);
            }
        }

        return best;
    }

    private bool IsAutoGcdCandidateLegal(
        AutoTimelineState state,
        TimelineEntry entry,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        double slotTimeSec,
        Dictionary<string, double> lastAbilityTimes,
        Dictionary<string, double> pseudoCooldowns)
    {
        if (pseudoCooldowns.TryGetValue(entry.AbilityName, out var pseudoCooldownSec) &&
            lastAbilityTimes.TryGetValue(entry.AbilityName, out var lastTimeSec) &&
            entry.TimeOffsetSec < lastTimeSec + pseudoCooldownSec - AutoCooldownToleranceSec)
            return false;

        if (DotDatabase.Lookup(entry.AbilityName) is { } dotRule &&
            lastAbilityTimes.TryGetValue(entry.AbilityName, out var lastDotTimeSec) &&
            slotTimeSec < lastDotTimeSec + GetDotRefreshReadySec(dotRule) - AutoCooldownToleranceSec)
            return false;

        var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        return CanAcceptAutoEntry(state, entry, info, gaugeRules, grantedRules, entry.TimeOffsetSec);
    }

    private bool IsDotCandidateDue(
        TimelineEntry entry,
        double slotTimeSec,
        Dictionary<string, double> lastAbilityTimes)
    {
        var dotRule = DotDatabase.Lookup(entry.AbilityName);
        if (dotRule == null)
            return false;

        return !lastAbilityTimes.TryGetValue(entry.AbilityName, out var lastTimeSec) ||
               slotTimeSec >= lastTimeSec + GetDotRefreshReadySec(dotRule) - AutoCooldownToleranceSec;
    }

    private bool IsPseudoCooldownBlocked(
        TimelineEntry entry,
        double slotTimeSec,
        List<TimelineEntry> keptEntries,
        Dictionary<string, double> pseudoCooldowns)
    {
        if (!pseudoCooldowns.TryGetValue(entry.AbilityName, out var pseudoCooldownSec))
            return false;

        for (var i = keptEntries.Count - 1; i >= 0; i--)
        {
            var previous = keptEntries[i];
            if (!string.Equals(previous.AbilityName, entry.AbilityName, StringComparison.OrdinalIgnoreCase))
                continue;

            return slotTimeSec < previous.TimeOffsetSec + pseudoCooldownSec - AutoCooldownToleranceSec;
        }

        return false;
    }

    private static Dictionary<string, double> BuildLastSelectedAbilityTimes(List<TimelineEntry> entries)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.OrderBy(entry => entry.TimeOffsetSec))
            result[entry.AbilityName] = entry.TimeOffsetSec;

        return result;
    }

    private bool HasGrantedActionRequirement(string abilityName)
    {
        var rule = plugin.ActionStateDatabase.Lookup(0, abilityName);
        return rule != null && rule.Effects.Any(effect => effect.MinRequired > 0);
    }

    private HashSet<string> SelectAutoDotGcdEntryKeys(List<TimelineEntry> gcdEntries)
    {
        var keptKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in gcdEntries
                     .Where(entry => DotDatabase.Lookup(entry.AbilityName) != null)
                     .GroupBy(entry => entry.AbilityName, StringComparer.OrdinalIgnoreCase))
        {
            var dotRule = DotDatabase.Lookup(group.Key);
            if (dotRule == null)
                continue;

            double? nextReadyTimeSec = null;
            foreach (var candidate in group
                         .Where(entry => entry.Frequency >= AutoGcdMinFrequency)
                         .OrderBy(entry => entry.TimeOffsetSec)
                         .ThenByDescending(entry => entry.Frequency))
            {
                if (nextReadyTimeSec != null &&
                    candidate.TimeOffsetSec < nextReadyTimeSec.Value - AutoCooldownToleranceSec)
                    continue;

                keptKeys.Add(GetAutoEntryIdentityKey(candidate));
                nextReadyTimeSec = candidate.TimeOffsetSec + GetDotRefreshReadySec(dotRule);
            }
        }

        return keptKeys;
    }

    private static bool IsAbilityBlocked(
        string abilityName,
        double slotTimeSec,
        IReadOnlyDictionary<string, double> blockedUntilByAbility)
        => blockedUntilByAbility.TryGetValue(abilityName, out var blockedUntilSec) &&
           slotTimeSec < blockedUntilSec - AutoCooldownToleranceSec;

    private Dictionary<string, AutoGrantedChildRule> BuildGrantedChildRules(
        IEnumerable<TimelineEntry> entries,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        var result = new Dictionary<string, AutoGrantedChildRule>(StringComparer.OrdinalIgnoreCase);
        var trackedAbilityNames = entries
            .Select(entry => entry.AbilityName)
            .Where(abilityName => !string.IsNullOrWhiteSpace(abilityName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (trackedAbilityNames.Length == 0)
            return result;

        var actionRulesByAbility = trackedAbilityNames.ToDictionary(
            abilityName => abilityName,
            abilityName => plugin.ActionStateDatabase.Lookup(0, abilityName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var abilityName in trackedAbilityNames)
        {
            if (TryBuildDrkDeliriumGrantedRule(abilityName, out var drkRule))
            {
                result[abilityName] = drkRule;
                continue;
            }

            if (TryBuildCardDrawGrantedRule(abilityName, grantedRules, out var cardDrawRule))
            {
                result[abilityName] = cardDrawRule;
                continue;
            }

            var repeatableRule = grantedRules?.RepeatableGrantedActionRules
                .FirstOrDefault(rule => rule.ConsumerNames.Contains(abilityName));
            if (repeatableRule != null &&
                repeatableRule.BypassGaugeSpendChecksWhenConsuming)
            {
                result[abilityName] = new AutoGrantedChildRule
                {
                    ChildAbilityName = abilityName,
                    ParentAbilityNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        repeatableRule.TriggerName,
                    },
                    ConsumerAbilityNames = new HashSet<string>(repeatableRule.ConsumerNames, StringComparer.OrdinalIgnoreCase),
                    AllowedUsesPerParentWindow = Math.Max(1, repeatableRule.GrantCount / Math.Max(1, repeatableRule.ConsumeCount)),
                    ParentWindowDurationSec = repeatableRule.WindowDurationSec,
                    WindowSelectionMode = repeatableRule.SkipCooldownWhenConsuming
                        ? AutoGrantedWindowSelectionMode.HighestFrequencyPerParentWindow
                        : AutoGrantedWindowSelectionMode.FirstChronological,
                };
                continue;
            }

            var actionRule = actionRulesByAbility[abilityName];
            if (actionRule == null)
                continue;

            var requiredStateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var effect in actionRule.Effects.Where(effect => effect.MinRequired > 0))
            {
                foreach (var equivalentStateName in EnumerateEquivalentStateNames(effect.StateName))
                {
                    requiredStateNames.Add(equivalentStateName);
                    foreach (var grantorName in plugin.ActionStateDatabase.GetGrantors(equivalentStateName))
                        parentNames.Add(grantorName);
                }
            }

            if (ShouldSkipAutoGrantedChildRule(abilityName, requiredStateNames))
                continue;

            if (parentNames.Count == 0)
                continue;

            var consumerNames = trackedAbilityNames
                .Where(otherAbilityName =>
                    !ShouldSkipAutoGrantedChildConsumer(otherAbilityName, requiredStateNames) &&
                    actionRulesByAbility.TryGetValue(otherAbilityName, out var otherActionRule) &&
                    ActionConsumesAnyGrantedState(otherActionRule, requiredStateNames))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (consumerNames.Count == 0)
                consumerNames.Add(abilityName);

            result[abilityName] = new AutoGrantedChildRule
            {
                ChildAbilityName = abilityName,
                ParentAbilityNames = parentNames,
                ConsumerAbilityNames = consumerNames,
                AllowedUsesPerParentWindow = 1,
            };
        }

        return result;
    }

    private static bool TryBuildCardDrawGrantedRule(
        string abilityName,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        out AutoGrantedChildRule rule)
    {
        rule = null!;
        if (grantedRules?.CardDraw is not { } cardDraw)
            return false;

        if (cardDraw.AstralCards.Contains(abilityName))
        {
            rule = new AutoGrantedChildRule
            {
                ChildAbilityName = abilityName,
                ParentAbilityNames = [cardDraw.AstralDrawName],
                ConsumerAbilityNames = [abilityName],
                AllowedUsesPerParentWindow = 1,
            };
            return true;
        }

        if (cardDraw.UmbralCards.Contains(abilityName))
        {
            rule = new AutoGrantedChildRule
            {
                ChildAbilityName = abilityName,
                ParentAbilityNames = [cardDraw.UmbralDrawName],
                ConsumerAbilityNames = [abilityName],
                AllowedUsesPerParentWindow = 1,
            };
            return true;
        }

        return false;
    }

    private static bool TryBuildDrkDeliriumGrantedRule(
        string abilityName,
        out AutoGrantedChildRule rule)
    {
        rule = null!;
        if (string.Equals(abilityName, "Scarlet Delirium", StringComparison.OrdinalIgnoreCase))
        {
            rule = new AutoGrantedChildRule
            {
                ChildAbilityName = abilityName,
                ParentAbilityNames = ["Delirium"],
                ConsumerAbilityNames = ["Scarlet Delirium"],
                AllowedUsesPerParentWindow = 1,
            };
            return true;
        }

        if (string.Equals(abilityName, "Comeuppance", StringComparison.OrdinalIgnoreCase))
        {
            rule = new AutoGrantedChildRule
            {
                ChildAbilityName = abilityName,
                ParentAbilityNames = ["Scarlet Delirium"],
                ConsumerAbilityNames = ["Comeuppance"],
                AllowedUsesPerParentWindow = 1,
            };
            return true;
        }

        if (string.Equals(abilityName, "Torcleaver", StringComparison.OrdinalIgnoreCase))
        {
            rule = new AutoGrantedChildRule
            {
                ChildAbilityName = abilityName,
                ParentAbilityNames = ["Comeuppance"],
                ConsumerAbilityNames = ["Torcleaver"],
                AllowedUsesPerParentWindow = 1,
            };
            return true;
        }

        return false;
    }

    private static bool ShouldSkipAutoGrantedChildRule(
        string abilityName,
        IReadOnlySet<string> requiredStateNames)
    {
        if (!string.Equals(abilityName, "Bloodspiller", StringComparison.OrdinalIgnoreCase))
            return false;

        return IsDrkDeliriumGrantedStateSet(requiredStateNames);
    }

    private static bool ShouldSkipAutoGrantedChildConsumer(
        string abilityName,
        IReadOnlySet<string> requiredStateNames)
    {
        if (!string.Equals(abilityName, "Bloodspiller", StringComparison.OrdinalIgnoreCase))
            return false;

        return IsDrkDeliriumGrantedStateSet(requiredStateNames);
    }

    private static bool IsDrkDeliriumGrantedStateSet(
        IReadOnlySet<string> requiredStateNames)
        => requiredStateNames.Contains("Delirium") ||
           requiredStateNames.Contains("Scarlet Delirium Ready") ||
           requiredStateNames.Contains("Comeuppance Ready") ||
           requiredStateNames.Contains("Torcleaver Ready");

    private bool ActionConsumesAnyGrantedState(
        ActionStateDatabase.ActionStateRule? actionRule,
        IReadOnlySet<string> requiredStateNames)
    {
        if (actionRule == null || requiredStateNames.Count == 0)
            return false;

        foreach (var effect in actionRule.Effects)
        {
            if (effect.MinRequired <= 0)
                continue;

            foreach (var equivalentStateName in EnumerateEquivalentStateNames(effect.StateName))
            {
                if (requiredStateNames.Contains(equivalentStateName))
                    return true;
            }
        }

        return false;
    }

    private bool IsGrantedCandidateAllowed(
        TimelineEntry entry,
        IReadOnlyList<TimelineEntry> keptGcdEntries,
        IReadOnlyList<TimelineEntry> selectedOgcdEntries,
        IReadOnlyDictionary<string, AutoGrantedChildRule> grantedChildRules,
        AutoOpenerBufferInfo openerBuffer)
    {
        if (!grantedChildRules.ContainsKey(entry.AbilityName))
            return true;

        var earlierSelectedEntries = keptGcdEntries
            .Concat(selectedOgcdEntries)
            .Where(selected => selected.TimeOffsetSec <= entry.TimeOffsetSec + 0.001)
            .OrderBy(selected => selected.TimeOffsetSec)
            .ThenByDescending(selected => selected.Frequency)
            .ToList();
        return AnalyzeGrantedCandidateDecision(entry, earlierSelectedEntries, grantedChildRules, openerBuffer).IsAllowed;
    }

    private bool IsGrantedSelectedEntryAllowed(
        TimelineEntry entry,
        IReadOnlyList<TimelineEntry> earlierSelectedEntries,
        IReadOnlyDictionary<string, AutoGrantedChildRule> grantedChildRules,
        AutoOpenerBufferInfo openerBuffer)
    {
        return AnalyzeGrantedCandidateDecision(entry, earlierSelectedEntries, grantedChildRules, openerBuffer).IsAllowed;
    }

    private AutoGrantedCandidateDecision AnalyzeGrantedCandidateDecision(
        TimelineEntry entry,
        IReadOnlyList<TimelineEntry> earlierSelectedEntries,
        IReadOnlyDictionary<string, AutoGrantedChildRule> grantedChildRules,
        AutoOpenerBufferInfo openerBuffer)
    {
        if (!grantedChildRules.TryGetValue(entry.AbilityName, out var childRule))
        {
            return new AutoGrantedCandidateDecision
            {
                IsTracked = false,
                IsAllowed = true,
            };
        }

        var lastParentEntry = GetLastGrantedParentEntry(
            childRule.ParentAbilityNames,
            earlierSelectedEntries,
            entry.TimeOffsetSec);
        var parentNamesText = string.Join(", ", childRule.ParentAbilityNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        var consumerNamesText = string.Join(", ", childRule.ConsumerAbilityNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        var previousTrackedChildEntry = earlierSelectedEntries
            .LastOrDefault(selected =>
                childRule.ConsumerAbilityNames.Contains(selected.AbilityName));
        if (lastParentEntry == null)
        {
            if (IsWithinAutoOpenerBuffer(openerBuffer, entry.TimeOffsetSec))
            {
                var virtualWindowUses = CountGrantedWindowUses(
                    childRule,
                    earlierSelectedEntries,
                    windowStartTimeSec: null);
                if (virtualWindowUses >= childRule.AllowedUsesPerParentWindow)
                {
                    return new AutoGrantedCandidateDecision
                    {
                        IsTracked = true,
                        IsAllowed = false,
                        Summary = $"opener buffer already spent its virtual pre-pull parent [{parentNamesText}] on {virtualWindowUses}/{childRule.AllowedUsesPerParentWindow} tracked use(s) from [{consumerNamesText}]",
                        ChildRule = childRule,
                        ExistingWindowConsumerEntry = FindLatestGrantedWindowConsumerEntry(childRule, earlierSelectedEntries, windowStartTimeSec: null),
                        UsesVirtualParentWindow = true,
                    };
                }

                return new AutoGrantedCandidateDecision
                {
                    IsTracked = true,
                    IsAllowed = true,
                    Summary = $"opener buffer assumed a pre-pull parent from [{parentNamesText}] before pull; tracked use {virtualWindowUses + 1}/{childRule.AllowedUsesPerParentWindow} from [{consumerNamesText}] may borrow that missing parent window",
                    ChildRule = childRule,
                    UsesVirtualParentWindow = true,
                };
            }

            return new AutoGrantedCandidateDecision
            {
                IsTracked = true,
                IsAllowed = false,
                Summary = $"requires a parent before this child; expected one of [{parentNamesText}] before {FormatTime(entry.TimeOffsetSec)}, but none was selected",
                ChildRule = childRule,
            };
        }

        var windowEndTimeSec = GetGrantedWindowEndTimeSec(childRule, lastParentEntry.TimeOffsetSec);
        if (windowEndTimeSec.HasValue &&
            entry.TimeOffsetSec > windowEndTimeSec.Value + AutoCooldownToleranceSec)
        {
            return new AutoGrantedCandidateDecision
            {
                IsTracked = true,
                IsAllowed = false,
                Summary = $"latest parent window {FormatAutoDebugEntry(lastParentEntry)} expired at {FormatTime(windowEndTimeSec.Value)} before this child",
                ChildRule = childRule,
                ParentEntry = lastParentEntry,
                ExistingWindowConsumerEntry = FindLatestGrantedWindowConsumerEntry(
                    childRule,
                    earlierSelectedEntries,
                    lastParentEntry.TimeOffsetSec,
                    windowEndTimeSec),
            };
        }

        var priorWindowUses = CountGrantedWindowUses(
            childRule,
            earlierSelectedEntries,
            lastParentEntry.TimeOffsetSec,
            windowEndTimeSec);
        if (priorWindowUses >= childRule.AllowedUsesPerParentWindow)
        {
            return new AutoGrantedCandidateDecision
            {
                IsTracked = true,
                IsAllowed = false,
                Summary = $"latest parent window is {FormatAutoDebugEntry(lastParentEntry)}; [{consumerNamesText}] already spent {priorWindowUses}/{childRule.AllowedUsesPerParentWindow} allowed use(s) in that window",
                ChildRule = childRule,
                ParentEntry = lastParentEntry,
                ExistingWindowConsumerEntry = FindLatestGrantedWindowConsumerEntry(
                    childRule,
                    earlierSelectedEntries,
                    lastParentEntry.TimeOffsetSec,
                    windowEndTimeSec),
            };
        }

        var keepSummary = priorWindowUses == 0
            ? $"first tracked child from [{consumerNamesText}] after parent {FormatAutoDebugEntry(lastParentEntry)}"
            : $"latest parent window {FormatAutoDebugEntry(lastParentEntry)} is spending tracked use {priorWindowUses + 1}/{childRule.AllowedUsesPerParentWindow} from [{consumerNamesText}]";
        return new AutoGrantedCandidateDecision
        {
            IsTracked = true,
            IsAllowed = true,
            Summary = keepSummary,
            ChildRule = childRule,
            ParentEntry = lastParentEntry,
        };
    }

    private static int CountGrantedWindowUses(
        AutoGrantedChildRule childRule,
        IReadOnlyList<TimelineEntry> selectedEntries,
        double? windowStartTimeSec,
        double? windowEndTimeSec = null)
    {
        var count = 0;
        foreach (var selectedEntry in selectedEntries)
        {
            if (windowStartTimeSec.HasValue &&
                selectedEntry.TimeOffsetSec < windowStartTimeSec.Value - AutoCooldownToleranceSec)
            {
                continue;
            }

            if (windowEndTimeSec.HasValue &&
                selectedEntry.TimeOffsetSec > windowEndTimeSec.Value + AutoCooldownToleranceSec)
            {
                continue;
            }

            if (childRule.ConsumerAbilityNames.Contains(selectedEntry.AbilityName))
                count++;
        }

        return count;
    }

    private static TimelineEntry? FindLatestGrantedWindowConsumerEntry(
        AutoGrantedChildRule childRule,
        IReadOnlyList<TimelineEntry> selectedEntries,
        double? windowStartTimeSec,
        double? windowEndTimeSec = null)
    {
        TimelineEntry? best = null;
        foreach (var selectedEntry in selectedEntries)
        {
            if (windowStartTimeSec.HasValue &&
                selectedEntry.TimeOffsetSec < windowStartTimeSec.Value - AutoCooldownToleranceSec)
            {
                continue;
            }

            if (windowEndTimeSec.HasValue &&
                selectedEntry.TimeOffsetSec > windowEndTimeSec.Value + AutoCooldownToleranceSec)
            {
                continue;
            }

            if (childRule.ConsumerAbilityNames.Contains(selectedEntry.AbilityName))
                best = selectedEntry;
        }

        return best;
    }

    private bool TryReplaceGrantedWindowAlternative(
        TimelineEntry candidate,
        AutoGrantedCandidateDecision decision,
        List<TimelineEntry> grantedKeptEntries,
        AutoTimelineDebugRecorder? debug)
    {
        if (decision.ChildRule is not { } childRule ||
            childRule.WindowSelectionMode != AutoGrantedWindowSelectionMode.HighestFrequencyPerParentWindow)
        {
            return false;
        }

        var windowStartTimeSec = decision.UsesVirtualParentWindow
            ? (double?)null
            : decision.ParentEntry?.TimeOffsetSec;
        var windowEndTimeSec = decision.ParentEntry == null
            ? null
            : GetGrantedWindowEndTimeSec(childRule, decision.ParentEntry.TimeOffsetSec);
        var existingWindowEntries = grantedKeptEntries
            .Where(entry =>
                childRule.ConsumerAbilityNames.Contains(entry.AbilityName) &&
                (!windowStartTimeSec.HasValue || entry.TimeOffsetSec >= windowStartTimeSec.Value - AutoCooldownToleranceSec) &&
                (!windowEndTimeSec.HasValue || entry.TimeOffsetSec <= windowEndTimeSec.Value + AutoCooldownToleranceSec))
            .OrderBy(entry => entry.Frequency)
            .ThenByDescending(entry => entry.TimeOffsetSec)
            .ToList();
        if (existingWindowEntries.Count == 0)
            return false;

        var existingEntry = existingWindowEntries[0];
        var candidateIsStronger = candidate.Frequency > existingEntry.Frequency + 0.001 ||
                                  (Math.Abs(candidate.Frequency - existingEntry.Frequency) <= 0.001 &&
                                   candidate.TimeOffsetSec < existingEntry.TimeOffsetSec - 0.001);
        if (!candidateIsStronger)
            return false;

        grantedKeptEntries.Remove(existingEntry);
        grantedKeptEntries.Add(candidate);

        var parentSummary = decision.UsesVirtualParentWindow
            ? "the opener buffer's virtual pre-pull parent window"
            : decision.ParentEntry == null
                ? "the active parent window"
                : $"parent window {FormatAutoDebugEntry(decision.ParentEntry)}";
        debug?.Add($"    lose | {FormatAutoDebugEntry(existingEntry)} | replaced within {parentSummary} by stronger alternative {FormatAutoDebugEntry(candidate)}");
        debug?.Add($"    keep | {FormatAutoDebugEntry(candidate)} | strongest tracked alternative kept for {parentSummary}; replaced {FormatAutoDebugEntry(existingEntry)}");
        return true;
    }

    private static double? GetGrantedWindowEndTimeSec(
        AutoGrantedChildRule childRule,
        double parentTimeSec)
        => childRule.ParentWindowDurationSec.HasValue
            ? parentTimeSec + childRule.ParentWindowDurationSec.Value
            : null;

    private static TimelineEntry? GetLastGrantedParentEntry(
        ISet<string> parentAbilityNames,
        IReadOnlyList<TimelineEntry> selectedEntries,
        double maxTimeSec)
    {
        TimelineEntry? best = null;
        foreach (var selectedEntry in selectedEntries)
        {
            if (selectedEntry.TimeOffsetSec > maxTimeSec + 0.001)
                break;

            if (parentAbilityNames.Contains(selectedEntry.AbilityName))
                best = selectedEntry;
        }

        return best;
    }

    private TimelineEntry? FindDueGrantedChildCandidate(
        IReadOnlyList<TimelineEntry> slotCandidates,
        IReadOnlyList<TimelineEntry> keptGcdEntries,
        IReadOnlyList<TimelineEntry> selectedOgcdEntries,
        IReadOnlyDictionary<string, AutoGrantedChildRule> grantedChildRules,
        AutoOpenerBufferInfo openerBuffer,
        double slotTimeSec)
    {
        return slotCandidates
            .Where(candidate =>
                grantedChildRules.ContainsKey(candidate.AbilityName) &&
                IsGrantedCandidateAllowed(candidate, keptGcdEntries, selectedOgcdEntries, grantedChildRules, openerBuffer))
            .OrderBy(candidate => IsWithinAutoSlotWindow(candidate.TimeOffsetSec, slotTimeSec) ? 0 : 1)
            .ThenBy(candidate => Math.Abs(candidate.TimeOffsetSec - slotTimeSec))
            .ThenByDescending(candidate => candidate.Frequency)
            .ThenBy(candidate => candidate.TimeOffsetSec)
            .FirstOrDefault();
    }

    private bool IsDotCandidateReady(
        TimelineEntry entry,
        IReadOnlyList<TimelineEntry> keptEntries)
    {
        var dotRule = DotDatabase.Lookup(entry.AbilityName);
        if (dotRule == null)
            return true;

        for (var i = keptEntries.Count - 1; i >= 0; i--)
        {
            var previous = keptEntries[i];
            if (!string.Equals(previous.AbilityName, entry.AbilityName, StringComparison.OrdinalIgnoreCase))
                continue;

            return entry.TimeOffsetSec >= previous.TimeOffsetSec + GetDotRefreshReadySec(dotRule) - AutoCooldownToleranceSec;
        }

        return true;
    }

    private TimelineEntry? FindDueDotCandidate(
        IReadOnlyList<TimelineEntry> slotCandidates,
        IReadOnlyList<TimelineEntry> keptEntries)
    {
        return slotCandidates
            .Where(candidate =>
                DotDatabase.Lookup(candidate.AbilityName) != null &&
                IsDotCandidateReady(candidate, keptEntries))
            .OrderBy(candidate => candidate.TimeOffsetSec)
            .ThenByDescending(candidate => candidate.Frequency)
            .FirstOrDefault();
    }

    private TimelineEntry? FindDueComboContinuationCandidate(
        IReadOnlyList<TimelineEntry> slotCandidates,
        AutoComboBranchContext? activeComboBranch,
        double slotTimeSec)
    {
        var expectedAbilityName = GetAutoComboNextAbility(activeComboBranch);
        if (string.IsNullOrWhiteSpace(expectedAbilityName) ||
            !TryGetAutoComboContinuationWindow(activeComboBranch, out var comboWindowStartSec, out var comboWindowEndSec))
        {
            return null;
        }

        return slotCandidates
            .Where(candidate =>
                string.Equals(candidate.AbilityName, expectedAbilityName, StringComparison.OrdinalIgnoreCase) &&
                candidate.TimeOffsetSec >= comboWindowStartSec - AutoCooldownToleranceSec &&
                candidate.TimeOffsetSec <= comboWindowEndSec + AutoCooldownToleranceSec)
            .OrderBy(candidate => IsWithinAutoSlotWindow(candidate.TimeOffsetSec, slotTimeSec) ? 0 : 1)
            .ThenBy(candidate => Math.Abs(candidate.TimeOffsetSec - slotTimeSec))
            .ThenByDescending(candidate => candidate.Frequency)
            .ThenBy(candidate => candidate.TimeOffsetSec)
            .FirstOrDefault();
    }

    private List<string> GetGcdCandidateBlockers(
        string specName,
        AutoTimelineState selectionState,
        TimelineEntry entry,
        double slotTimeSec,
        IReadOnlyList<TimelineEntry> keptEntries,
        IReadOnlyList<TimelineEntry> selectedOgcdEntries,
        IReadOnlyDictionary<string, AutoGrantedChildRule> grantedChildRules,
        AutoComboBranchContext? activeComboBranch,
        IReadOnlyDictionary<string, double> blockedUntilByAbility,
        AutoOpenerBufferInfo openerBuffer,
        string? openerRequirement,
        IReadOnlyList<TimelineEntry> sourceGcdEntries,
        ISet<string> usedKeys)
    {
        var blockers = new List<string>();
        var isWithinOpenerBuffer = IsWithinAutoOpenerBuffer(openerBuffer, entry.TimeOffsetSec);
        if (blockedUntilByAbility.TryGetValue(entry.AbilityName, out var blockedUntilSec) &&
            slotTimeSec < blockedUntilSec - AutoCooldownToleranceSec)
        {
            blockers.Add($"pseudo cooldown still active until {FormatTime(blockedUntilSec)}");
        }

        if (!isWithinOpenerBuffer && IsComboBranchBlocked(entry.AbilityName, activeComboBranch))
        {
            var comboContextText = string.IsNullOrWhiteSpace(activeComboBranch?.ChosenLineSummary)
                ? $"active combo branch from {activeComboBranch?.StarterAbilityName}"
                : activeComboBranch!.ChosenLineSummary;
            blockers.Add($"blocked by combo branch: {comboContextText}");
        }

        var comboBlocker = GetComboCandidateBlocker(entry, keptEntries, activeComboBranch, openerBuffer, openerRequirement);
        if (!string.IsNullOrWhiteSpace(comboBlocker))
            blockers.Add(comboBlocker);

        if (DotDatabase.Lookup(entry.AbilityName) != null && !IsDotCandidateReady(entry, keptEntries))
            blockers.Add(BuildDotCandidateNotReadyReason(entry, keptEntries));

        var drkBloodspillerBlocker = GetDrkBloodspillerCandidateBlocker(
            specName,
            selectionState,
            entry,
            slotTimeSec,
            keptEntries,
            selectedOgcdEntries,
            sourceGcdEntries,
            usedKeys);
        if (!string.IsNullOrWhiteSpace(drkBloodspillerBlocker))
            blockers.Add(drkBloodspillerBlocker);

        if (grantedChildRules.ContainsKey(entry.AbilityName))
        {
            var earlierSelectedEntries = keptEntries
                .Concat(selectedOgcdEntries)
                .Where(selected => selected.TimeOffsetSec <= entry.TimeOffsetSec + 0.001)
                .OrderBy(selected => selected.TimeOffsetSec)
                .ThenByDescending(selected => selected.Frequency)
                .ToList();
            var grantedDecision = AnalyzeGrantedCandidateDecision(entry, earlierSelectedEntries, grantedChildRules, openerBuffer);
            if (!grantedDecision.IsAllowed)
                blockers.Add(grantedDecision.Summary);
        }

        return blockers;
    }

    private string GetGcdCandidateEligibilitySummary(
        TimelineEntry entry,
        double slotTimeSec,
        IReadOnlyList<TimelineEntry> keptEntries,
        IReadOnlyList<TimelineEntry> selectedOgcdEntries,
        IReadOnlyDictionary<string, AutoGrantedChildRule> grantedChildRules,
        JobComboDatabase.JobComboHints? comboHints,
        AutoComboBranchContext? activeComboBranch,
        AutoOpenerBufferInfo openerBuffer,
        string? openerRequirement)
    {
        var openerPrefix = BuildOpenerRequirementSummary(entry, slotTimeSec, openerRequirement);
        var comboPrefix = BuildComboCandidateEligibilitySummary(entry, slotTimeSec, keptEntries, comboHints, activeComboBranch, openerBuffer, openerRequirement);
        if (grantedChildRules.ContainsKey(entry.AbilityName))
        {
            var earlierSelectedEntries = keptEntries
                .Concat(selectedOgcdEntries)
                .Where(selected => selected.TimeOffsetSec <= entry.TimeOffsetSec + 0.001)
                .OrderBy(selected => selected.TimeOffsetSec)
                .ThenByDescending(selected => selected.Frequency)
                .ToList();
            return openerPrefix + comboPrefix + AnalyzeGrantedCandidateDecision(entry, earlierSelectedEntries, grantedChildRules, openerBuffer).Summary;
        }

        if (DotDatabase.Lookup(entry.AbilityName) != null)
        {
            var dotReason = BuildDotCandidateReadyReason(entry, keptEntries);
            return IsWithinAutoOpenerBuffer(openerBuffer, entry.TimeOffsetSec)
                ? $"{openerPrefix}{comboPrefix}{dotReason}; opener buffer is active for this timestamp"
                : $"{openerPrefix}{comboPrefix}{dotReason}";
        }

        return IsWithinAutoOpenerBuffer(openerBuffer, entry.TimeOffsetSec)
            ? $"{openerPrefix}{comboPrefix}eligible under current slot rules; opener buffer is active for shifted opener and pre-pull allowances"
            : $"{openerPrefix}{comboPrefix}eligible under current slot rules";
    }

    private string BuildGcdSelectionReason(
        string specName,
        TimelineEntry chosenEntry,
        double slotTimeSec,
        IReadOnlyList<TimelineEntry> allowedSlotCandidates,
        IReadOnlyList<TimelineEntry> keptEntries,
        IReadOnlyList<TimelineEntry> selectedOgcdEntries,
        IReadOnlyDictionary<string, AutoGrantedChildRule> grantedChildRules,
        JobComboDatabase.JobComboHints? comboHints,
        AutoComboBranchContext? activeComboBranch,
        AutoOpenerBufferInfo openerBuffer,
        string? openerRequirement)
    {
        var openerSuffix = IsWithinAutoOpenerBuffer(openerBuffer, chosenEntry.TimeOffsetSec)
            ? "; opener buffer active for this timestamp"
            : string.Empty;
        var openerReasonPrefix = BuildOpenerRequirementSummary(chosenEntry, slotTimeSec, openerRequirement);
        var comboReasonPrefix = BuildComboCandidateEligibilitySummary(chosenEntry, slotTimeSec, keptEntries, comboHints, activeComboBranch, openerBuffer, openerRequirement);
        if (grantedChildRules.ContainsKey(chosenEntry.AbilityName))
        {
            var earlierSelectedEntries = keptEntries
                .Concat(selectedOgcdEntries)
                .Where(selected => selected.TimeOffsetSec <= chosenEntry.TimeOffsetSec + 0.001)
                .OrderBy(selected => selected.TimeOffsetSec)
                .ThenByDescending(selected => selected.Frequency)
                .ToList();
            var grantedDecision = AnalyzeGrantedCandidateDecision(chosenEntry, earlierSelectedEntries, grantedChildRules, openerBuffer);
            return $"{openerReasonPrefix}{comboReasonPrefix}{grantedDecision.Summary}; granted children have priority over generic GCDs in the slot{openerSuffix}";
        }

        if (DotDatabase.Lookup(chosenEntry.AbilityName) != null)
            return $"{openerReasonPrefix}{comboReasonPrefix}{BuildDotCandidateReadyReason(chosenEntry, keptEntries)}; due DoTs have priority over generic GCDs in the slot{openerSuffix}";

        if (string.Equals(specName, "Dark Knight", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(chosenEntry.AbilityName, "Bloodspiller", StringComparison.OrdinalIgnoreCase))
            return $"{openerReasonPrefix}{comboReasonPrefix}DRK forced Bloodspiller here to satisfy the Delirium bridge / Blood overcap spender rule{openerSuffix}";

        var selectedBecause = IsComboContinuationMatch(chosenEntry, activeComboBranch)
            ? $"active combo continuation held the slot against {allowedSlotCandidates.Count} allowed candidate(s)"
            : JobComboDatabase.IsComboStarter(chosenEntry.AbilityName)
            ? $"highest-frequency eligible combo starter ({chosenEntry.Frequency * 100.0:F1}%)"
            : $"highest-frequency eligible candidate ({chosenEntry.Frequency * 100.0:F1}%)";
        if (activeComboBranch != null &&
            activeComboBranch.BlockedAbilityNames.Count > 0 &&
            activeComboBranch.ChosenLine.Any(name => string.Equals(name, chosenEntry.AbilityName, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(activeComboBranch.ChosenLineSummary))
            return $"{openerReasonPrefix}{comboReasonPrefix}{selectedBecause}; active combo context: {activeComboBranch.ChosenLineSummary}{openerSuffix}";

        return $"{openerReasonPrefix}{comboReasonPrefix}{selectedBecause} among {allowedSlotCandidates.Count} allowed candidate(s){openerSuffix}";
    }

    private AutoOpenerBufferInfo BuildAutoOpenerBufferInfo(
        List<TimelineEntry> gcdEntries,
        string specName,
        AutoTimelineDebugRecorder? debug)
    {
        debug?.Add("Opener Buffer");
        var hints = BalanceOpenerDatabase.GetHints(specName);
        if (hints == null || hints.Variants.Count == 0)
        {
            debug?.Add("  disabled | no opener data is configured for this job");
            debug?.Add();
            return new AutoOpenerBufferInfo();
        }

        var (variant, startSlotOffset, matchedEntries) = SelectAutoOpenerEntries(gcdEntries, specName, debug);
        if (variant == null)
        {
            debug?.Add("  disabled | no opener variant could be identified");
            debug?.Add();
            return new AutoOpenerBufferInfo();
        }

        var concreteGcdCount = variant.GcdSequence.Count(IsConcreteOpenerAbilityName);
        if (matchedEntries.Count == 0 || variant.GcdSequence.Count <= startSlotOffset)
        {
            debug?.Add($"  disabled | {variant.Name} was the closest opener match, but no concrete opener GCDs appeared above threshold");
            debug?.Add();
            return new AutoOpenerBufferInfo();
        }

        var remainingSequence = variant.GcdSequence.Skip(startSlotOffset).ToArray();
        var openerBuffer = new AutoOpenerBufferInfo
        {
            Enabled = true,
            VariantName = variant.Name,
            StartSlotOffset = startSlotOffset,
            GcdSlotCount = remainingSequence.Length,
            EndTimeSec = remainingSequence.Length * GetConfiguredAutoTimelineGcdRecastSec(),
            GcdSequence = variant.GcdSequence.ToArray(),
            Steps = variant.Steps.ToArray(),
        };

        debug?.Add($"  active | variant {variant.Name} | start slot {startSlotOffset:00} ({variant.GcdSequence[startSlotOffset]}) | matched concrete GCDs {matchedEntries.Count}/{Math.Max(1, concreteGcdCount)} | buffer 0:00.0-{FormatTime(openerBuffer.EndTimeSec)}");
        debug?.Add("  relaxed rules | shifted opener slots may borrow pre-pull combo predecessors; granted parents and numeric gauge minimums may also seed from pre-pull state");
        debug?.Add("  opener requirement | concrete opener GCD slots are enforced against the shifted opener start; abstract slots still use normal selection");
        debug?.Add();
        return openerBuffer;
    }

    private string? GetAutoOpenerSlotRequirement(
        AutoOpenerBufferInfo openerBuffer,
        int slotIndex)
    {
        if (!openerBuffer.Enabled || openerBuffer.GcdSequence.Count == 0)
            return null;

        var sequenceIndex = openerBuffer.StartSlotOffset + slotIndex;
        if (sequenceIndex < 0 || sequenceIndex >= openerBuffer.GcdSequence.Count)
            return null;

        var abilityName = openerBuffer.GcdSequence[sequenceIndex];
        return IsConcreteOpenerAbilityName(abilityName) && IsStandardTimelineGcdAbility(abilityName)
            ? abilityName
            : null;
    }

    private static bool IsWithinAutoOpenerBuffer(
        AutoOpenerBufferInfo openerBuffer,
        double timeSec)
        => openerBuffer.Enabled &&
           timeSec <= openerBuffer.EndTimeSec + AutoCooldownToleranceSec;

    private string BuildDotCandidateReadyReason(
        TimelineEntry entry,
        IReadOnlyList<TimelineEntry> keptEntries)
    {
        var dotRule = DotDatabase.Lookup(entry.AbilityName);
        if (dotRule == null)
            return "not a DoT";

        var previousEntry = keptEntries
            .LastOrDefault(selected =>
                string.Equals(selected.AbilityName, entry.AbilityName, StringComparison.OrdinalIgnoreCase));
        if (previousEntry == null)
            return $"first observed DoT application; refresh window uses {GetConfiguredAutoTimelineDotRefreshBufferSec():F1}s buffer on {dotRule.DurationSec:F1}s duration";

        var readyTimeSec = previousEntry.TimeOffsetSec + GetDotRefreshReadySec(dotRule);
        return $"refresh window is open after prior {FormatAutoDebugEntry(previousEntry)}; ready since {FormatTime(readyTimeSec)}";
    }

    private string BuildDotCandidateNotReadyReason(
        TimelineEntry entry,
        IReadOnlyList<TimelineEntry> keptEntries)
    {
        var dotRule = DotDatabase.Lookup(entry.AbilityName);
        if (dotRule == null)
            return "DoT rule unavailable";

        var previousEntry = keptEntries
            .LastOrDefault(selected =>
                string.Equals(selected.AbilityName, entry.AbilityName, StringComparison.OrdinalIgnoreCase));
        if (previousEntry == null)
            return "DoT unexpectedly lacked a prior application";

        var readyTimeSec = previousEntry.TimeOffsetSec + GetDotRefreshReadySec(dotRule);
        return $"DoT refresh window stays closed until {FormatTime(readyTimeSec)} after prior {FormatAutoDebugEntry(previousEntry)}";
    }

    private static bool IsComboBranchBlocked(
        string abilityName,
        AutoComboBranchContext? comboBranch)
        => comboBranch != null &&
           comboBranch.BlockedAbilityNames.Contains(abilityName);

    private static bool IsDrkBloodspillerComboBridgeAbility(string abilityName)
        => string.Equals(abilityName, "Bloodspiller", StringComparison.OrdinalIgnoreCase);

    private static string? GetDrkBloodspillerResumeAbilityName(TimelineEntry? previousKeptGcd)
    {
        if (previousKeptGcd == null)
            return null;

        return previousKeptGcd.AbilityName switch
        {
            "Hard Slash" => "Syphon Strike",
            "Syphon Strike" => "Souleater",
            "Souleater" => "Hard Slash",
            _ => null,
        };
    }

    private static TimelineEntry? GetLastNonBloodspillerKeptGcd(IReadOnlyList<TimelineEntry> keptEntries)
    {
        for (var i = keptEntries.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(keptEntries[i].AbilityName, "Bloodspiller", StringComparison.OrdinalIgnoreCase))
                return keptEntries[i];
        }

        return null;
    }

    private bool HasDrkBloodspillerResumeCandidate(
        IReadOnlyList<TimelineEntry> sourceGcdEntries,
        ISet<string> usedKeys,
        string expectedResumeAbilityName,
        double slotTimeSec)
    {
        var nextSlotTimeSec = slotTimeSec + GetConfiguredAutoTimelineGcdRecastSec();
        return sourceGcdEntries.Any(entry =>
            !usedKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
            string.Equals(entry.AbilityName, expectedResumeAbilityName, StringComparison.OrdinalIgnoreCase) &&
            entry.TimeOffsetSec >= nextSlotTimeSec - AutoGcdSlotMatchToleranceSec &&
            entry.TimeOffsetSec <= GetAutoGcdSlotWindowEndSec(nextSlotTimeSec) + AutoGcdSlotMatchToleranceSec);
    }

    private static int CountDrkWindowUses(
        IReadOnlyList<TimelineEntry> entries,
        string abilityName,
        double windowStartTimeSec)
        => entries.Count(entry =>
            entry.TimeOffsetSec >= windowStartTimeSec - AutoCooldownToleranceSec &&
            string.Equals(entry.AbilityName, abilityName, StringComparison.OrdinalIgnoreCase));

    private bool ShouldForceDrkBloodspiller(
        AutoTimelineState selectionState,
        IReadOnlyList<TimelineEntry> keptEntries,
        IReadOnlyList<TimelineEntry> selectedOgcdEntries,
        IReadOnlyList<TimelineEntry> sourceGcdEntries,
        ISet<string> usedKeys,
        double slotTimeSec)
    {
        if (keptEntries.LastOrDefault() is { } lastKept &&
            string.Equals(lastKept.AbilityName, "Bloodspiller", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var previousComboGcd = GetLastNonBloodspillerKeptGcd(keptEntries);
        var expectedResumeAbilityName = GetDrkBloodspillerResumeAbilityName(previousComboGcd);
        if (string.IsNullOrWhiteSpace(expectedResumeAbilityName) ||
            !HasDrkBloodspillerResumeCandidate(sourceGcdEntries, usedKeys, expectedResumeAbilityName, slotTimeSec))
        {
            return false;
        }

        var currentBlood = selectionState.GaugeState.GetValueOrDefault("Blood");
        var latestDelirium = selectedOgcdEntries
            .Where(entry =>
                entry.TimeOffsetSec <= slotTimeSec + AutoCooldownToleranceSec &&
                string.Equals(entry.AbilityName, "Delirium", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.TimeOffsetSec)
            .FirstOrDefault();

        var forceForDeliriumBridge = latestDelirium != null &&
                                     string.Equals(expectedResumeAbilityName, "Hard Slash", StringComparison.OrdinalIgnoreCase) &&
                                     CountDrkWindowUses(keptEntries, "Bloodspiller", latestDelirium.TimeOffsetSec) < 2;
        var forceForHighBlood = currentBlood >= 80;
        var forceBeforeSouleater = currentBlood >= 60 &&
                                   string.Equals(expectedResumeAbilityName, "Souleater", StringComparison.OrdinalIgnoreCase);
        return forceForDeliriumBridge || forceForHighBlood || forceBeforeSouleater;
    }

    private string? GetDrkBloodspillerCandidateBlocker(
        string specName,
        AutoTimelineState selectionState,
        TimelineEntry candidate,
        double slotTimeSec,
        IReadOnlyList<TimelineEntry> keptEntries,
        IReadOnlyList<TimelineEntry> selectedOgcdEntries,
        IReadOnlyList<TimelineEntry> sourceGcdEntries,
        ISet<string> usedKeys,
        bool detailed = false)
    {
        if (!string.Equals(specName, "Dark Knight", StringComparison.OrdinalIgnoreCase) ||
            !IsDrkBloodspillerComboBridgeAbility(candidate.AbilityName))
        {
            return null;
        }

        if (ShouldForceDrkBloodspiller(selectionState, keptEntries, selectedOgcdEntries, sourceGcdEntries, usedKeys, slotTimeSec))
            return null;

        if (!detailed)
            return "DRK Bloodspiller is reserved for explicit Delirium bridge or Blood overcap spender windows";

        var currentBlood = selectionState.GaugeState.GetValueOrDefault("Blood");
        var previousComboGcd = GetLastNonBloodspillerKeptGcd(keptEntries);
        var expectedResumeAbilityName = GetDrkBloodspillerResumeAbilityName(previousComboGcd);
        if (keptEntries.LastOrDefault() is { } lastKept &&
            string.Equals(lastKept.AbilityName, "Bloodspiller", StringComparison.OrdinalIgnoreCase))
        {
            return "DRK Bloodspiller may not be used twice in a row";
        }

        if (string.IsNullOrWhiteSpace(expectedResumeAbilityName))
            return $"DRK Bloodspiller needs a resumable combo context, but last combo GCD was {previousComboGcd?.AbilityName ?? "none"}";

        if (!HasDrkBloodspillerResumeCandidate(sourceGcdEntries, usedKeys, expectedResumeAbilityName, slotTimeSec))
            return $"DRK Bloodspiller needs {expectedResumeAbilityName} available in the next GCD slot to resume the combo";

        return $"DRK Bloodspiller only forces at Blood 80+/60-before-Souleater or as one of two post-Delirium bridge uses; current Blood is {currentBlood}";
    }

    private TimelineEntry? FindForcedDrkBloodspillerCandidate(
        string specName,
        AutoTimelineState selectionState,
        IReadOnlyList<TimelineEntry> slotCandidates,
        IReadOnlyList<TimelineEntry> keptEntries,
        IReadOnlyList<TimelineEntry> selectedOgcdEntries,
        IReadOnlyList<TimelineEntry> sourceGcdEntries,
        ISet<string> usedKeys,
        double slotTimeSec)
    {
        if (!string.Equals(specName, "Dark Knight", StringComparison.OrdinalIgnoreCase))
            return null;

        var bridgeCandidates = slotCandidates
            .Where(candidate =>
                string.Equals(candidate.AbilityName, "Bloodspiller", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(GetDrkBloodspillerCandidateBlocker(
                    specName,
                    selectionState,
                    candidate,
                    slotTimeSec,
                    keptEntries,
                    selectedOgcdEntries,
                    sourceGcdEntries,
                    usedKeys)))
            .OrderByDescending(candidate => candidate.Frequency)
            .ThenBy(candidate => Math.Abs(candidate.TimeOffsetSec - slotTimeSec))
            .ThenBy(candidate => candidate.TimeOffsetSec)
            .ToList();

        return bridgeCandidates.FirstOrDefault();
    }

    private string? GetComboCandidateBlocker(
        TimelineEntry entry,
        IReadOnlyList<TimelineEntry> keptEntries,
        AutoComboBranchContext? activeComboBranch,
        AutoOpenerBufferInfo openerBuffer,
        string? openerRequirement)
    {
        if (IsComboContinuationMatch(entry, activeComboBranch))
            return null;

        var predecessors = JobComboDatabase.GetPredecessors(entry.AbilityName);
        if (predecessors.Count == 0)
            return null;

        var previousKeptGcd = keptEntries.LastOrDefault();
        var previousComboContextGcd = previousKeptGcd != null &&
                                      string.Equals(previousKeptGcd.AbilityName, "Bloodspiller", StringComparison.OrdinalIgnoreCase)
            ? GetLastNonBloodspillerKeptGcd(keptEntries)
            : previousKeptGcd;
        if (previousKeptGcd != null &&
            predecessors.Contains(previousKeptGcd.AbilityName))
        {
            return null;
        }

        if (previousComboContextGcd != null &&
            predecessors.Contains(previousComboContextGcd.AbilityName))
        {
            return null;
        }

        if (CanBorrowPrepullComboPredecessor(entry.AbilityName, openerBuffer, openerRequirement))
            return null;

        return previousKeptGcd == null
            ? $"requires combo predecessor {FormatAbilityChoices(predecessors)} in the prior kept GCD slot, but no earlier GCD was kept"
            : $"requires combo predecessor {FormatAbilityChoices(predecessors)} in the prior kept GCD slot, but last kept GCD was {FormatAutoDebugEntry(previousKeptGcd)}";
    }

    private string BuildComboCandidateEligibilitySummary(
        TimelineEntry entry,
        double slotTimeSec,
        IReadOnlyList<TimelineEntry> keptEntries,
        JobComboDatabase.JobComboHints? comboHints,
        AutoComboBranchContext? activeComboBranch,
        AutoOpenerBufferInfo openerBuffer,
        string? openerRequirement)
    {
        var comboPseudoCooldownSec = GetComboPseudoCooldownSec(comboHints, entry.AbilityName);
        var parts = new List<string>();
        if (comboPseudoCooldownSec > 0.0)
            parts.Add($"combo pseudo cooldown spans {comboPseudoCooldownSec:F1}s between repeats");

        if (IsComboContinuationMatch(entry, activeComboBranch) &&
            TryGetAutoComboContinuationWindow(activeComboBranch, out var comboWindowStartSec, out var comboWindowEndSec))
        {
            var timingText = IsWithinAutoSlotWindow(entry.TimeOffsetSec, slotTimeSec)
                ? $"active combo window favors this continuation in {FormatTime(comboWindowStartSec)}-{FormatTime(comboWindowEndSec)}"
                : $"active combo window favors this continuation in {FormatTime(comboWindowStartSec)}-{FormatTime(comboWindowEndSec)} via combo timing tolerance from {FormatTime(entry.TimeOffsetSec)}";
            parts.Add(timingText);
        }

        var predecessors = JobComboDatabase.GetPredecessors(entry.AbilityName);
        if (predecessors.Count > 0)
        {
            var previousKeptGcd = keptEntries.LastOrDefault();
            if (previousKeptGcd != null &&
                predecessors.Contains(previousKeptGcd.AbilityName))
            {
                parts.Add($"combo predecessor {previousKeptGcd.AbilityName} was the previous kept GCD");
            }
            else if (IsComboContinuationMatch(entry, activeComboBranch) &&
                     activeComboBranch != null &&
                     activeComboBranch.NextStepIndex > 0)
            {
                parts.Add($"prior combo step {activeComboBranch.ChosenLine[activeComboBranch.NextStepIndex - 1]} already started this continuation window");
            }
            else if (CanBorrowPrepullComboPredecessor(entry.AbilityName, openerBuffer, openerRequirement))
            {
                parts.Add("shifted opener borrowed a pre-pull combo predecessor for this step");
            }
        }
        else if (JobComboDatabase.IsComboStarter(entry.AbilityName))
        {
            parts.Add("combo starter can reopen combo branch selection");
        }

        if (activeComboBranch != null &&
            activeComboBranch.BlockedAbilityNames.Count > 0 &&
            activeComboBranch.ChosenLine.Any(name => string.Equals(name, entry.AbilityName, StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add("active combo branch keeps this line and prunes alternate followups until the next starter");
        }

        return parts.Count == 0
            ? string.Empty
            : string.Join("; ", parts) + "; ";
    }

    private static bool CanBorrowPrepullComboPredecessor(
        string abilityName,
        AutoOpenerBufferInfo openerBuffer,
        string? openerRequirement)
        => openerBuffer.Enabled &&
           !string.IsNullOrWhiteSpace(openerRequirement) &&
           DoesOpenerAbilityMatch(abilityName, openerRequirement) &&
           JobComboDatabase.GetPredecessors(abilityName).Count > 0;

    private static string FormatAbilityChoices(IEnumerable<string> abilityNames)
        => string.Join(" / ", abilityNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

    private AutoComboBranchContext? SelectAutoComboBranchContext(
        JobComboDatabase.JobComboHints? comboHints,
        string starterAbilityName,
        int starterSlotIndex,
        double starterTimeSec,
        List<TimelineEntry> sourceGcdEntries,
        ISet<string> usedKeys,
        AutoTimelineDebugRecorder? debug)
    {
        if (comboHints == null)
            return null;

        var candidateLines = comboHints.ComboLines
            .Where(line => line.Count > 0 &&
                           string.Equals(line[0], starterAbilityName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidateLines.Count == 0)
            return null;

        if (candidateLines.Count == 1)
            return new AutoComboBranchContext
            {
                StarterAbilityName = starterAbilityName,
                StarterSlotIndex = starterSlotIndex,
                LastMatchedTimeSec = starterTimeSec,
                LastMatchedRecastSec = GetAutoTimelineGcdRecastSec(starterAbilityName),
                NextStepIndex = 1,
                ChosenLine = candidateLines[0].ToArray(),
                ChosenLineSummary = $"single combo line after {starterAbilityName}: {string.Join(" > ", candidateLines[0])}",
            };

        var divergenceIndex = GetComboBranchDivergenceIndex(candidateLines);
        if (divergenceIndex < 0)
            return new AutoComboBranchContext
            {
                StarterAbilityName = starterAbilityName,
                StarterSlotIndex = starterSlotIndex,
                LastMatchedTimeSec = starterTimeSec,
                LastMatchedRecastSec = GetAutoTimelineGcdRecastSec(starterAbilityName),
                NextStepIndex = 1,
                ChosenLine = candidateLines[0].ToArray(),
                ChosenLineSummary = $"single combo line after {starterAbilityName}: {string.Join(" > ", candidateLines[0])}",
            };

        IReadOnlyList<string>? chosenLine = null;
        var bestBranchFrequency = double.NegativeInfinity;
        var bestMatchedSteps = int.MinValue;
        var bestTotalScore = double.NegativeInfinity;
        var branchSummaries = new List<string>();
        foreach (var line in candidateLines)
        {
            var branchAbilityName = line[divergenceIndex];
            var branchSlotTimeSec = starterTimeSec + GetAutoComboLineOffsetSec(line, divergenceIndex);
            var branchFrequency = sourceGcdEntries
                .Where(entry =>
                    !usedKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
                    string.Equals(entry.AbilityName, branchAbilityName, StringComparison.OrdinalIgnoreCase) &&
                    entry.TimeOffsetSec >= branchSlotTimeSec - AutoGcdSlotMatchToleranceSec &&
                    entry.TimeOffsetSec <= GetAutoGcdSlotWindowEndSec(branchSlotTimeSec) + AutoGcdSlotMatchToleranceSec)
                .Select(entry => entry.Frequency)
                .DefaultIfEmpty(0.0)
                .Max();

            var (matchedSteps, totalScore) = GetComboBranchSelectionScore(
                line,
                starterTimeSec,
                sourceGcdEntries,
                usedKeys);
            branchSummaries.Add($"{branchAbilityName} {branchFrequency * 100.0:F1}% | matched {matchedSteps} | score {totalScore:F1}");
            if (chosenLine == null ||
                branchFrequency > bestBranchFrequency ||
                (Math.Abs(branchFrequency - bestBranchFrequency) < 0.0001 && matchedSteps > bestMatchedSteps) ||
                (Math.Abs(branchFrequency - bestBranchFrequency) < 0.0001 && matchedSteps == bestMatchedSteps && totalScore > bestTotalScore))
            {
                chosenLine = line;
                bestBranchFrequency = branchFrequency;
                bestMatchedSteps = matchedSteps;
                bestTotalScore = totalScore;
            }
        }

        if (chosenLine == null)
            return null;

        var chosenLineAbilityNames = chosenLine.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blockedAbilityNames = candidateLines
            .Where(line => !ReferenceEquals(line, chosenLine))
            .SelectMany(line => line.Skip(divergenceIndex))
            .Where(abilityName => !chosenLineAbilityNames.Contains(abilityName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chosenLineSummary = $"{starterAbilityName} branch -> {string.Join(" > ", chosenLine)} | compared {string.Join(" ; ", branchSummaries)}";
        if (blockedAbilityNames.Count > 0)
            debug?.Add($"  combo-branch | {chosenLineSummary}");

        return new AutoComboBranchContext
        {
            StarterAbilityName = starterAbilityName,
            StarterSlotIndex = starterSlotIndex,
            LastMatchedTimeSec = starterTimeSec,
            LastMatchedRecastSec = GetAutoTimelineGcdRecastSec(starterAbilityName),
            NextStepIndex = 1,
            ChosenLine = chosenLine.ToArray(),
            BlockedAbilityNames = blockedAbilityNames,
            ChosenLineSummary = chosenLineSummary,
        };
    }

    private static string? GetAutoComboNextAbility(
        AutoComboBranchContext? comboBranch)
    {
        if (comboBranch == null ||
            comboBranch.NextStepIndex < 0 ||
            comboBranch.NextStepIndex >= comboBranch.ChosenLine.Count)
        {
            return null;
        }

        return comboBranch.ChosenLine[comboBranch.NextStepIndex];
    }

    private bool TryGetAutoComboContinuationWindow(
        AutoComboBranchContext? comboBranch,
        out double windowStartSec,
        out double windowEndSec)
    {
        windowStartSec = 0.0;
        windowEndSec = 0.0;
        if (comboBranch == null || string.IsNullOrWhiteSpace(GetAutoComboNextAbility(comboBranch)))
            return false;

        var comboStepRecastSec = comboBranch.LastMatchedRecastSec > 0.0
            ? comboBranch.LastMatchedRecastSec
            : GetConfiguredAutoTimelineGcdRecastSec();
        windowStartSec = comboBranch.LastMatchedTimeSec + comboStepRecastSec;
        windowEndSec = windowStartSec + comboStepRecastSec;
        return true;
    }

    private bool IsAutoComboContextExpired(
        AutoComboBranchContext? comboBranch,
        double slotTimeSec)
    {
        if (comboBranch == null)
            return false;

        if (!TryGetAutoComboContinuationWindow(comboBranch, out _, out var comboWindowEndSec))
            return true;

        return slotTimeSec > comboWindowEndSec + AutoCooldownToleranceSec;
    }

    private bool IsComboContinuationMatch(
        TimelineEntry entry,
        AutoComboBranchContext? comboBranch)
    {
        var expectedAbilityName = GetAutoComboNextAbility(comboBranch);
        return !string.IsNullOrWhiteSpace(expectedAbilityName) &&
               TryGetAutoComboContinuationWindow(comboBranch, out var comboWindowStartSec, out var comboWindowEndSec) &&
               string.Equals(entry.AbilityName, expectedAbilityName, StringComparison.OrdinalIgnoreCase) &&
               entry.TimeOffsetSec >= comboWindowStartSec - AutoCooldownToleranceSec &&
               entry.TimeOffsetSec <= comboWindowEndSec + AutoCooldownToleranceSec;
    }

    private IEnumerable<TimelineEntry> GetAutoComboContinuationCandidates(
        IReadOnlyList<TimelineEntry> sourceGcdEntries,
        ISet<string> usedKeys,
        AutoComboBranchContext? comboBranch,
        double slotTimeSec)
    {
        var expectedAbilityName = GetAutoComboNextAbility(comboBranch);
        if (string.IsNullOrWhiteSpace(expectedAbilityName) ||
            !TryGetAutoComboContinuationWindow(comboBranch, out var comboWindowStartSec, out var comboWindowEndSec))
        {
            return [];
        }

        var slotWindowEndSec = GetAutoGcdSlotWindowEndSec(slotTimeSec);
        if (slotWindowEndSec < comboWindowStartSec - AutoCooldownToleranceSec ||
            slotTimeSec > comboWindowEndSec + AutoCooldownToleranceSec)
        {
            return [];
        }

        return sourceGcdEntries
            .Where(entry =>
                !usedKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
                string.Equals(entry.AbilityName, expectedAbilityName, StringComparison.OrdinalIgnoreCase) &&
                entry.TimeOffsetSec >= comboWindowStartSec - AutoCooldownToleranceSec &&
                entry.TimeOffsetSec <= comboWindowEndSec + AutoCooldownToleranceSec)
            .OrderByDescending(entry => entry.Frequency)
            .ThenBy(entry => Math.Abs(entry.TimeOffsetSec - comboWindowStartSec))
            .ThenBy(entry => entry.TimeOffsetSec)
            .ToList();
    }

    private AutoComboBranchContext? AdvanceAutoComboBranchContext(
        AutoComboBranchContext? comboBranch,
        TimelineEntry chosenEntry)
    {
        if (!IsComboContinuationMatch(chosenEntry, comboBranch) ||
            comboBranch == null)
        {
            return comboBranch;
        }

        var nextStepIndex = comboBranch.NextStepIndex + 1;
        if (nextStepIndex >= comboBranch.ChosenLine.Count)
            return null;

        return new AutoComboBranchContext
        {
            StarterAbilityName = comboBranch.StarterAbilityName,
            StarterSlotIndex = comboBranch.StarterSlotIndex,
            LastMatchedTimeSec = chosenEntry.TimeOffsetSec,
            LastMatchedRecastSec = GetAutoTimelineGcdRecastSec(chosenEntry),
            NextStepIndex = nextStepIndex,
            ChosenLine = comboBranch.ChosenLine,
            BlockedAbilityNames = comboBranch.BlockedAbilityNames,
            ChosenLineSummary = comboBranch.ChosenLineSummary,
        };
    }

    private static int GetComboBranchDivergenceIndex(
        IReadOnlyList<IReadOnlyList<string>> candidateLines)
    {
        if (candidateLines.Count <= 1)
            return -1;

        var maxLength = candidateLines.Max(line => line.Count);
        for (var index = 0; index < maxLength; index++)
        {
            var abilityNames = candidateLines
                .Where(line => line.Count > index)
                .Select(line => line[index])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (abilityNames.Count > 1)
                return index;
        }

        return -1;
    }

    private (int MatchedSteps, double TotalScore) GetComboBranchSelectionScore(
        IReadOnlyList<string> line,
        double starterTimeSec,
        IReadOnlyList<TimelineEntry> sourceGcdEntries,
        ISet<string> usedKeys)
    {
        var matchedSteps = 0;
        var totalScore = 0.0;
        for (var stepIndex = 0; stepIndex < line.Count; stepIndex++)
        {
            var slotTimeSec = starterTimeSec + GetAutoComboLineOffsetSec(line, stepIndex);
            var candidate = sourceGcdEntries
                .Where(entry =>
                    !usedKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
                    string.Equals(entry.AbilityName, line[stepIndex], StringComparison.OrdinalIgnoreCase) &&
                    entry.TimeOffsetSec >= GetAutoGcdSlotWindowStartSec(slotTimeSec) - AutoGcdSlotMatchToleranceSec &&
                    entry.TimeOffsetSec <= GetAutoGcdSlotWindowEndSec(slotTimeSec) + AutoGcdSlotMatchToleranceSec)
                .OrderByDescending(entry => entry.Frequency)
                .ThenBy(entry => entry.TimeOffsetSec)
                .FirstOrDefault();
            if (candidate == null)
                continue;

            matchedSteps++;
            totalScore += candidate.Frequency * 100.0;
        }

        return (matchedSteps, totalScore);
    }

    private TimelineEntry KeepAutoGcdSelection(
        TimelineEntry chosenEntry,
        double slotTimeSec,
        List<TimelineEntry> keptEntries,
        HashSet<string> usedKeys,
        Dictionary<string, double> blockedUntilByAbility,
        JobComboDatabase.JobComboHints? comboHints,
        AutoTimelineDebugRecorder? debug,
        string reason,
        string detail)
    {
        var chosenKey = GetAutoEntryIdentityKey(chosenEntry);
        if (!usedKeys.Add(chosenKey))
            return chosenEntry;

        var scheduledEntry = CreateScheduledEntry(chosenEntry, slotTimeSec);
        keptEntries.Add(scheduledEntry);
        var comboDurationSec = GetComboPseudoCooldownSec(comboHints, chosenEntry.AbilityName);
        if (comboDurationSec > 0.0)
        {
            blockedUntilByAbility[chosenEntry.AbilityName] = Math.Max(
                blockedUntilByAbility.GetValueOrDefault(chosenEntry.AbilityName),
                scheduledEntry.TimeOffsetSec + comboDurationSec);
        }

        debug?.Add($"  slot-choice | {reason} | {chosenEntry.AbilityName}");
        debug?.Add($"  slot-keep | {FormatAutoDebugEntry(scheduledEntry)} | slot {FormatAutoDebugSlotWindow(slotTimeSec)} | {detail}");
        return scheduledEntry;
    }

    private static string FormatAutoDebugEntry(TimelineEntry entry)
        => $"{entry.AbilityName} @ {FormatTime(entry.TimeOffsetSec)} | freq {(entry.Frequency * 100.0):F1}% | avg uses {entry.AverageUses:F2}";

    private static string FormatAutoDebugSlotWindow(double slotTimeSec)
        => $"{FormatTime(GetAutoGcdSlotWindowStartSec(slotTimeSec))}-{FormatTime(GetAutoGcdSlotWindowEndSec(slotTimeSec))}";

    private static double GetNextAutoGcdSlotStartSec(
        double currentSlotTimeSec,
        TimelineEntry? chosenEntry,
        double slotIntervalSec)
    {
        _ = chosenEntry;
        return currentSlotTimeSec + slotIntervalSec;
    }

    private IReadOnlyList<string> GetRelevantGaugeNames(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (gaugeRules != null && gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
        {
            foreach (var gaugeName in effects.Select(effect => effect.GaugeName))
                names.Add(gaugeName);
        }

        foreach (var gaugeName in GetRepeatableConsumeBonusEffects(state.GrantedState, grantedRules, abilityName)
                     .Select(effect => effect.GaugeName))
        {
            names.Add(gaugeName);
        }

        return names
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FormatGaugeStateForDebug(
        AutoTimelineState state,
        IReadOnlyList<string> relevantGaugeNames)
    {
        if (relevantGaugeNames.Count == 0)
            return "no tracked gauge";

        return string.Join(
            ", ",
            relevantGaugeNames.Select(name => $"{name}={state.GaugeState.GetValueOrDefault(name)}"));
    }

    private double GetComboPseudoCooldownSec(
        JobComboDatabase.JobComboHints? comboHints,
        string abilityName)
    {
        if (comboHints == null)
            return 0.0;

        var maxLineDurationSec = comboHints.ComboLines
            .Where(line => line.Any(name => string.Equals(name, abilityName, StringComparison.OrdinalIgnoreCase)))
            .Select(GetAutoComboLineDurationSec)
            .DefaultIfEmpty(0.0)
            .Max();

        return maxLineDurationSec <= 0.0
            ? 0.0
            : maxLineDurationSec;
    }

    private Dictionary<string, double> BuildGcdPseudoCooldowns(string specName)
    {
        var pseudoCooldowns = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (JobComboDatabase.GetHints(specName) is { } comboHints)
        {
            foreach (var line in comboHints.ComboLines)
            {
                var pseudoCooldownSec = GetAutoComboLineDurationSec(line);
                foreach (var abilityName in line)
                {
                    pseudoCooldowns[abilityName] = Math.Max(
                        pseudoCooldowns.GetValueOrDefault(abilityName),
                        pseudoCooldownSec);
                }
            }
        }

        foreach (var abilityName in DotDatabase.AbilityNames)
        {
            if (DotDatabase.Lookup(abilityName) is not { } dotRule)
                continue;

            pseudoCooldowns[abilityName] = Math.Max(
                pseudoCooldowns.GetValueOrDefault(abilityName),
                GetDotRefreshReadySec(dotRule));
        }

        return pseudoCooldowns;
    }

    private double GetAutoTimelineGcdRecastSec(TimelineEntry? entry)
    {
        if (entry == null)
            return GetConfiguredAutoTimelineGcdRecastSec();

        var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        return GetAutoTimelineGcdRecastSec(info);
    }

    private double GetAutoTimelineGcdRecastSec(string abilityName)
        => GetAutoTimelineGcdRecastSec(plugin.RecastDatabase.Lookup(0, abilityName));

    private double GetAutoTimelineGcdRecastSec(Data.RecastDatabase.RecastInfo? info)
    {
        var configuredRecastSec = GetConfiguredAutoTimelineGcdRecastSec();
        if (info == null || !info.IsGcdAction)
            return configuredRecastSec;

        if (info.IsStandardTimelineGcd)
            return configuredRecastSec;

        return info.RecastSec >= 1.5 && info.RecastSec <= 4.5
            ? info.RecastSec
            : configuredRecastSec;
    }

    private double GetAutoComboLineOffsetSec(
        IReadOnlyList<string> line,
        int stepIndex)
    {
        var offsetSec = 0.0;
        for (var index = 0; index < stepIndex && index < line.Count; index++)
            offsetSec += GetAutoTimelineGcdRecastSec(line[index]);

        return offsetSec;
    }

    private double GetAutoComboLineDurationSec(IReadOnlyList<string> line)
        => line.Count <= 1
            ? 0.0
            : GetAutoComboLineOffsetSec(line, line.Count - 1);

    private string? GetSoftBurstPreferredAbility(
        BalanceOpenerDatabase.JobOpenerVariant? openerVariant,
        double slotTimeSec)
    {
        if (openerVariant == null)
            return null;

        var burstSequence = openerVariant.GcdSequence
            .Where(IsStandardTimelineGcdAbility)
            .Take(12)
            .ToArray();
        if (burstSequence.Length == 0)
            return null;

        for (var burstIndex = 1; burstIndex < 10; burstIndex++)
        {
            var burstStartSec = burstIndex * AutoBurstCadenceSec;
            if (slotTimeSec < burstStartSec - AutoBurstEarlyWindowSec ||
                slotTimeSec > burstStartSec + AutoBurstLateWindowSec)
                continue;

            var relativeSec = Math.Max(0.0, slotTimeSec - burstStartSec);
            var gcdIndex = Math.Clamp((int)Math.Round(relativeSec / GetConfiguredAutoTimelineGcdRecastSec()), 0, burstSequence.Length - 1);
            return burstSequence[gcdIndex];
        }

        return null;
    }

    private bool IsStandardTimelineGcdAbility(string abilityName)
    {
        var info = plugin.RecastDatabase.Lookup(0, abilityName);
        return IsAutoTimelineGcd(info);
    }

    private double GetEarliestAutoScheduleTime(
        AutoTimelineState state,
        TimelineEntry entry,
        Data.RecastDatabase.RecastInfo? info,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        double requestedTimeSec)
    {
        var scheduledTimeSec = Math.Max(requestedTimeSec, state.CastLockUntilSec);
        if (!double.IsNegativeInfinity(state.LastOgcdTimeSec))
            scheduledTimeSec = Math.Max(scheduledTimeSec, state.LastOgcdTimeSec + AutoOgcdLockSec);

        return GetCooldownReadyTime(state, info, grantedRules, entry.AbilityName, scheduledTimeSec, AutoCooldownToleranceSec);
    }

    private double ScoreAutoEntryCandidate(
        AutoTimelineState state,
        TimelineEntry entry,
        Data.RecastDatabase.RecastInfo? info,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        double scheduledTimeSec)
    {
        var score = entry.Frequency * 42.0;
        score += entry.AverageUses * 2.0;
        score -= Math.Abs(scheduledTimeSec - entry.TimeOffsetSec) * 8.0;
        return score;
    }

    private bool IsGcdEntry(TimelineEntry entry)
    {
        var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        return IsAutoTimelineGcd(info);
    }

    private static bool IsAutoTimelineGcd(Data.RecastDatabase.RecastInfo? info)
        => info?.IsGcdAction == true;

    private bool IsComboTrackedAction(Data.RecastDatabase.RecastInfo? info, string abilityName)
    {
        if (info == null || !info.IsGcdAction)
            return false;

        if (ShouldValidateComboRequirement(info, abilityName) && info.ComboActionId > 0)
            return true;

        var knownFollowers = JobComboDatabase.GetFollowers(abilityName);
        if (knownFollowers.Count > 0)
            return true;

        return plugin.RecastDatabase.GetAllActions().Any(action =>
            action.IsGcdAction &&
            action.ComboActionId == info.AbilityId);
    }

    private HashSet<string> GetComboPredecessorNames(Data.RecastDatabase.RecastInfo info)
    {
        var names = JobComboDatabase.GetPredecessors(info.Name);
        if (info.ComboActionId <= 0)
            return names;

        foreach (var name in plugin.RecastDatabase.GetAllActions()
            .Where(action =>
                action.IsGcdAction &&
                action.AbilityId == info.ComboActionId)
            .Select(action => action.Name))
        {
            names.Add(name);
        }

        foreach (var name in names.ToArray())
        {
            if (!ComboPredecessorAliases.TryGetValue(name, out var aliases))
                continue;

            foreach (var alias in aliases)
                names.Add(alias);
        }

        return names;
    }

    private bool ShouldValidateComboRequirement(
        Data.RecastDatabase.RecastInfo info,
        string abilityName)
    {
        if (ComboValidationExemptAbilities.Contains(abilityName))
            return false;

        if (!info.IsGcdAction || info.ActionCategory != 3 || info.ComboActionId <= 0)
            return false;

        var actionRule = plugin.ActionStateDatabase.Lookup((int)info.AbilityId, abilityName);
        return actionRule == null ||
               !actionRule.Effects.Any(effect =>
                   effect.MinRequired > 0 &&
                   !effect.StateName.StartsWith("Action Grant::", StringComparison.OrdinalIgnoreCase));
    }

    private bool ShouldIgnoreActionStateRequirement(
        ActionStateDatabase.ActionStateRule actionRule,
        ActionStateDatabase.ActionStateEffect effect,
        string abilityName,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        if (!effect.StateName.StartsWith("Action Grant::", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<GaugeSimulator.GaugeEffect>? gaugeEffects = null;
            IReadOnlyList<GrantedActionDatabase.GrantedActionEffect>? grantedEffects = null;
            var hasGaugeEffects = gaugeRules?.EffectByName.TryGetValue(abilityName, out gaugeEffects) == true;
            var hasGrantedEffects = grantedRules?.EffectByName.TryGetValue(abilityName, out grantedEffects) == true;
            if (hasGaugeEffects || hasGrantedEffects)
            {
                foreach (var equivalentStateName in EnumerateEquivalentStateNames(effect.StateName))
                {
                    var matchesKnownGauge = gaugeRules?.Resources.Any(resource =>
                        string.Equals(resource.Name, equivalentStateName, StringComparison.OrdinalIgnoreCase));
                    var matchesKnownGrant = grantedRules?.Resources.Any(resource =>
                        string.Equals(resource.Name, equivalentStateName, StringComparison.OrdinalIgnoreCase));
                    if (matchesKnownGauge != true && matchesKnownGrant != true)
                        continue;

                    var explicitlyRequiredByGaugeRule = (gaugeEffects ?? []).Any(gaugeEffect =>
                        gaugeEffect.MinRequired > 0 &&
                        EnumerateEquivalentStateNames(gaugeEffect.GaugeName).Any(gaugeStateName =>
                            string.Equals(gaugeStateName, equivalentStateName, StringComparison.OrdinalIgnoreCase)));
                    var explicitlyRequiredByGrantRule = (grantedEffects ?? []).Any(grantedEffect =>
                        grantedEffect.MinRequired > 0 &&
                        EnumerateEquivalentStateNames(grantedEffect.ResourceName).Any(grantedStateName =>
                            string.Equals(grantedStateName, equivalentStateName, StringComparison.OrdinalIgnoreCase)));
                    if (!explicitlyRequiredByGaugeRule && !explicitlyRequiredByGrantRule)
                        return true;
                }
            }

            return false;
        }

        if (gaugeRules?.EffectByName.ContainsKey(abilityName) == true ||
            grantedRules?.EffectByName.ContainsKey(abilityName) == true)
            return true;

        return actionRule.Effects.Any(other =>
            !ReferenceEquals(other, effect) &&
            other.MinRequired > 0 &&
            !other.StateName.StartsWith("Action Grant::", StringComparison.OrdinalIgnoreCase));
    }

    private bool ShouldEnforceActionStateRequirement(
        string stateName,
        ISet<string> abilityNames,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        foreach (var equivalentStateName in EnumerateEquivalentStateNames(stateName))
        {
            if (gaugeRules?.Resources.Any(resource =>
                    string.Equals(resource.Name, equivalentStateName, StringComparison.OrdinalIgnoreCase)) == true)
                return true;

            if (grantedRules?.Resources.Any(resource =>
                    string.Equals(resource.Name, equivalentStateName, StringComparison.OrdinalIgnoreCase)) == true)
                return true;
        }

        return plugin.ActionStateDatabase.HasGrantorInSet(stateName, abilityNames);
    }

    private double GetNextAutoGcdReadyTime(
        AutoTimelineState state,
        Data.RecastDatabase.RecastInfo? info,
        double scheduledTimeSec,
        double baseGcdRecastSec)
    {
        var actionRecastSec = info?.RecastSec is > 0.0 and <= 4.5 ? info.RecastSec : baseGcdRecastSec;
        var castSec = Math.Max(0.0, info?.CastTimeSec ?? 0.0);
        return Math.Max(scheduledTimeSec + Math.Max(baseGcdRecastSec, Math.Max(actionRecastSec, castSec)), state.CastLockUntilSec);
    }

    private double GetAutoBaseGcdRecastSec(IReadOnlyList<TimelineEntry> gcdEntries)
    {
        if (gcdEntries.Count == 0)
            return GetConfiguredAutoTimelineGcdRecastSec();

        var weightedRecasts = gcdEntries
            .Select(entry => plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName))
            .Where(info => info != null && info.IsGcdAction && info.RecastSec >= 1.5 && info.RecastSec <= 4.5)
            .GroupBy(info => Math.Round(info!.RecastSec, 2))
            .Select(group => new
            {
                RecastSec = group.Key,
                Weight = group.Count(),
            })
            .OrderByDescending(group => group.Weight)
            .ThenBy(group => group.RecastSec)
            .ToList();

        if (weightedRecasts.Count == 0)
            return GetConfiguredAutoTimelineGcdRecastSec();

        return weightedRecasts[0].RecastSec;
    }

    private static TimelineEntry CreateScheduledEntry(TimelineEntry entry, double timeOffsetSec)
        => new()
        {
            TimeOffsetSec = timeOffsetSec,
            AbilityId = entry.AbilityId,
            AbilityName = entry.AbilityName,
            AbilityIcon = entry.AbilityIcon,
            Frequency = entry.Frequency,
            AverageUses = entry.AverageUses,
            IsGcd = entry.IsGcd,
        };

    private static string GetAutoEntryIdentityKey(TimelineEntry entry)
        => string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0}|{1}|{2:F3}|{3:F4}|{4:F4}",
            entry.AbilityId,
            entry.AbilityName,
            entry.TimeOffsetSec,
            entry.Frequency,
            entry.AverageUses);

    private static TimelineEntry ChooseCooldownBaselineCandidate(
        IReadOnlyList<TimelineEntry> windowCandidates,
        IReadOnlyList<TimelineEntry> windowAboveThresholdCandidates)
    {
        var selectedWindowPool = windowAboveThresholdCandidates.Count > 0
            ? windowAboveThresholdCandidates
            : windowCandidates;
        return selectedWindowPool
            .OrderByDescending(candidate => candidate.Frequency)
            .ThenBy(candidate => candidate.TimeOffsetSec)
            .First();
    }

    private static TimelineEntry ChooseNextReadyCooldownCandidate(
        IReadOnlyList<TimelineEntry> windowCandidates,
        IReadOnlyList<TimelineEntry> windowAboveThresholdCandidates,
        double nextReadyTimeSec)
    {
        var selectedWindowPool = windowAboveThresholdCandidates.Count > 0
            ? windowAboveThresholdCandidates
            : windowCandidates;
        var nearReadyCandidates = selectedWindowPool
            .Where(candidate => candidate.TimeOffsetSec <= nextReadyTimeSec + 6.0 + AutoCooldownToleranceSec)
            .ToList();
        if (nearReadyCandidates.Count > 0)
        {
            return nearReadyCandidates
                .OrderByDescending(candidate => candidate.Frequency)
                .ThenBy(candidate => candidate.TimeOffsetSec)
                .First();
        }

        return selectedWindowPool
            .OrderBy(candidate => candidate.TimeOffsetSec)
            .ThenByDescending(candidate => candidate.Frequency)
            .First();
    }

    private static bool TryFindCooldownWindowCandidate(
        IReadOnlyList<TimelineEntry> orderedCandidates,
        double nextReadyTimeSec,
        double recastSec,
        double minimumFrequency,
        bool useBaselineSelection,
        out TimelineEntry chosenCandidate,
        out List<TimelineEntry> windowCandidates,
        out double windowStartSec,
        out double windowEndSec)
    {
        chosenCandidate = null!;
        windowCandidates = [];
        windowStartSec = nextReadyTimeSec - AutoCooldownToleranceSec;
        windowEndSec = windowStartSec;
        if (orderedCandidates.Count == 0)
            return false;

        var currentWindowStartSec = nextReadyTimeSec - AutoCooldownToleranceSec;
        var finalCandidateTimeSec = orderedCandidates[^1].TimeOffsetSec;
        while (currentWindowStartSec <= finalCandidateTimeSec + AutoCooldownToleranceSec)
        {
            var currentWindowEndSec = currentWindowStartSec + recastSec;
            var currentWindowCandidates = orderedCandidates
                .Where(candidate =>
                    candidate.TimeOffsetSec >= currentWindowStartSec - 0.001 &&
                    candidate.TimeOffsetSec < currentWindowEndSec - AutoCooldownToleranceSec)
                .ToList();
            if (currentWindowCandidates.Count == 0)
            {
                currentWindowStartSec += recastSec;
                continue;
            }

            var currentWindowAboveThresholdCandidates = currentWindowCandidates
                .Where(candidate => candidate.Frequency >= minimumFrequency)
                .ToList();
            chosenCandidate = useBaselineSelection
                ? ChooseCooldownBaselineCandidate(currentWindowCandidates, currentWindowAboveThresholdCandidates)
                : ChooseNextReadyCooldownCandidate(currentWindowCandidates, currentWindowAboveThresholdCandidates, nextReadyTimeSec);
            windowCandidates = currentWindowCandidates;
            windowStartSec = currentWindowStartSec;
            windowEndSec = currentWindowEndSec;
            return true;
        }

        return false;
    }

    private static bool MatchesCastRule(
        GrantedActionDatabase.InstantCastRule rule,
        string abilityName,
        bool isCastTimeSpell)
        => (rule.AppliesToAnyCastTimeSpell && isCastTimeSpell) ||
           rule.AbilityNames.Contains(abilityName);

    private static bool MatchesCastRule(
        GrantedActionDatabase.HardcastGrantRule rule,
        string abilityName,
        bool isCastTimeSpell)
        => (rule.AppliesToAnyCastTimeSpell && isCastTimeSpell) ||
           rule.AbilityNames.Contains(abilityName);

    private GrantedActionDatabase.RepeatableGrantedActionRule? FindRepeatableGrantedActionRule(
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        if (grantedRules == null)
            return null;

        return grantedRules.RepeatableGrantedActionRules.FirstOrDefault(rule =>
            string.Equals(rule.TriggerName, abilityName, StringComparison.OrdinalIgnoreCase) ||
            rule.ConsumerNames.Contains(abilityName));
    }

    private bool UsesRepeatableGrantedActionCharge(
        Dictionary<string, int> grantedState,
        GrantedActionDatabase.RepeatableGrantedActionRule rule,
        string abilityName)
    {
        if (rule.ConsumerNames.Contains(abilityName))
            return grantedState.GetValueOrDefault(rule.ResourceName) >= rule.ConsumeCount;

        return rule.TriggerConsumesWhenResourcePresent &&
               string.Equals(rule.TriggerName, abilityName, StringComparison.OrdinalIgnoreCase) &&
               grantedState.GetValueOrDefault(rule.ResourceName) >= rule.ConsumeCount;
    }

    private bool HasRepeatableGrantedActionCharge(
        Dictionary<string, int> grantedState,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        var repeatRule = FindRepeatableGrantedActionRule(grantedRules, abilityName);
        return repeatRule != null &&
               UsesRepeatableGrantedActionCharge(grantedState, repeatRule, abilityName);
    }

    private bool ShouldBypassRepeatableGrantedGaugeSpendChecks(
        Dictionary<string, int> grantedState,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        var repeatRule = FindRepeatableGrantedActionRule(grantedRules, abilityName);
        return repeatRule != null &&
               repeatRule.BypassGaugeSpendChecksWhenConsuming &&
               UsesRepeatableGrantedActionCharge(grantedState, repeatRule, abilityName);
    }

    private bool ShouldBypassCooldown(
        Dictionary<string, int> grantedState,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        var repeatRule = FindRepeatableGrantedActionRule(grantedRules, abilityName);
        return repeatRule != null &&
               repeatRule.SkipCooldownWhenConsuming &&
               UsesRepeatableGrantedActionCharge(grantedState, repeatRule, abilityName);
    }

    private GrantedActionDatabase.InstantCastRule? FindInstantCastRule(
        Dictionary<string, int> grantedState,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        Data.RecastDatabase.RecastInfo? info,
        string abilityName)
    {
        if (grantedRules == null || info == null || info.CastTimeSec <= AutoCastLockToleranceSec)
            return null;

        foreach (var rule in grantedRules.InstantCastRules)
        {
            if (!MatchesCastRule(rule, abilityName, isCastTimeSpell: true))
                continue;

            if (grantedState.GetValueOrDefault(rule.ResourceName) >= rule.MinRequired)
                return rule;
        }

        return null;
    }

    private double GetEffectiveCastTimeSec(
        Dictionary<string, int> grantedState,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        Data.RecastDatabase.RecastInfo? info,
        string abilityName,
        out GrantedActionDatabase.InstantCastRule? instantCastRule)
    {
        instantCastRule = null;
        if (info == null || info.CastTimeSec <= AutoCastLockToleranceSec)
            return 0.0;

        instantCastRule = FindInstantCastRule(grantedState, grantedRules, info, abilityName);
        return instantCastRule != null ? 0.0 : info.CastTimeSec;
    }

    private void ApplyCastStateTransitions(
        Dictionary<string, int> grantedState,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        Data.RecastDatabase.RecastInfo? info,
        string abilityName,
        double effectiveCastTimeSec,
        GrantedActionDatabase.InstantCastRule? instantCastRule)
    {
        if (grantedRules == null || info == null)
            return;

        if (instantCastRule != null)
        {
            var maxValue = grantedRules.Resources
                .FirstOrDefault(r => string.Equals(r.Name, instantCastRule.ResourceName, StringComparison.OrdinalIgnoreCase))
                ?.MaxValue ?? int.MaxValue;
            grantedState[instantCastRule.ResourceName] = Math.Clamp(
                grantedState.GetValueOrDefault(instantCastRule.ResourceName) - instantCastRule.Consume,
                0,
                maxValue);
            return;
        }

        if (effectiveCastTimeSec <= AutoCastLockToleranceSec)
            return;

        foreach (var rule in grantedRules.HardcastGrantRules)
        {
            if (!MatchesCastRule(rule, abilityName, isCastTimeSpell: true))
                continue;

            var maxValue = grantedRules.Resources
                .FirstOrDefault(r => string.Equals(r.Name, rule.ResourceName, StringComparison.OrdinalIgnoreCase))
                ?.MaxValue ?? int.MaxValue;
            grantedState[rule.ResourceName] = Math.Clamp(
                grantedState.GetValueOrDefault(rule.ResourceName) + rule.Delta,
                0,
                maxValue);
        }
    }

    private AutoTimelineState CreateAutoTimelineState(
        string specName,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        ISet<string>? availableAbilityNames = null)
    {
        var state = new AutoTimelineState
        {
            SpecName = specName,
        };
        if (availableAbilityNames != null)
            state.AvailableAbilityNames = new HashSet<string>(availableAbilityNames, StringComparer.OrdinalIgnoreCase);
        if (gaugeRules != null)
        {
            foreach (var resource in gaugeRules.Resources)
            {
                state.GaugeState[resource.Name] = resource.InitialValue;
                state.PassiveGaugeProgress[resource.Name] = 0.0;
            }
        }

        if (grantedRules != null)
        {
            foreach (var resource in grantedRules.Resources)
                state.GrantedState[resource.Name] = resource.InitialValue;

            if (string.Equals(specName, "Astrologian", StringComparison.OrdinalIgnoreCase) &&
                grantedRules.CardDraw is { } astCardDraw)
            {
                state.CardDrawState = "Astral";
                foreach (var astralCard in astCardDraw.AstralCards)
                {
                    var resourceName = $"{astralCard} Ready";
                    if (state.GrantedState.ContainsKey(resourceName))
                        state.GrantedState[resourceName] = 1;
                }
            }
        }

        return state;
    }

    private static bool TryGetTimedUsageWindowRule(
        string specName,
        string abilityName,
        out string parentAbilityName,
        out double durationSec)
    {
        if (string.Equals(specName, "Scholar", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(abilityName, "Energy Drain", StringComparison.OrdinalIgnoreCase))
        {
            parentAbilityName = "Chain Stratagem";
            durationSec = 20.0;
            return true;
        }

        parentAbilityName = string.Empty;
        durationSec = 0.0;
        return false;
    }

    private static bool TryGetTimedUsageLockoutRule(
        string specName,
        string abilityName,
        out string sourceAbilityName,
        out double durationSec)
    {
        if (string.Equals(specName, "Scholar", StringComparison.OrdinalIgnoreCase) &&
            abilityName is "Fey Blessing" or "Whispering Dawn" or "Fey Illumination" or "Summon Seraph" or "Seraphism")
        {
            sourceAbilityName = "Dissipation";
            durationSec = 30.0;
            return true;
        }

        sourceAbilityName = string.Empty;
        durationSec = 0.0;
        return false;
    }

    private static bool IsScholarAetherflowSpender(string abilityName)
        => abilityName is "Energy Drain" or "Energy Siphon" or "Lustrate" or "Indomitability" or "Excogitation" or "Sacred Soil";

    private static bool IsScholarBurstReservedAetherflowSpender(string abilityName)
        => IsScholarAetherflowSpender(abilityName) &&
           !string.Equals(abilityName, "Energy Drain", StringComparison.OrdinalIgnoreCase);

    private static string? GetScholarBurstAetherflowReservationReason(
        string specName,
        IReadOnlyDictionary<string, double> timedWindowEndByAbility,
        IReadOnlyDictionary<string, int> gaugeState,
        string abilityName,
        double timeSec)
    {
        if (!string.Equals(specName, "Scholar", StringComparison.OrdinalIgnoreCase) ||
            !IsScholarBurstReservedAetherflowSpender(abilityName))
            return null;

        if (!timedWindowEndByAbility.TryGetValue("Energy Drain", out var energyDrainWindowEndSec) ||
            timeSec > energyDrainWindowEndSec + AutoCooldownToleranceSec)
            return null;

        var aetherflow = gaugeState.GetValueOrDefault("Aetherflow");
        if (aetherflow <= 0)
            return null;

        return $"Aetherflow is reserved for Energy Drain during the active Chain Stratagem window through {FormatCsvTime(energyDrainWindowEndSec)}";
    }

    private string? GetScholarDissipationCandidateRestrictionReason(
        TimelineEntry candidate,
        IReadOnlyList<TimelineEntry> selectedNonDissipationEntries,
        IReadOnlyList<TimelineEntry> priorChosenDissipationEntries,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        ISet<string> availableAbilityNames,
        AutoOpenerBufferInfo openerBuffer)
    {
        var state = CreateAutoTimelineState("Scholar", gaugeRules, grantedRules, availableAbilityNames);
        var replayEntries = selectedNonDissipationEntries
            .Concat(priorChosenDissipationEntries)
            .Where(entry => entry.TimeOffsetSec <= candidate.TimeOffsetSec + AutoCooldownToleranceSec)
            .ToList();
        foreach (var priorEntry in OrderReplayEntriesForEvaluation(replayEntries, gaugeRules, grantedRules))
        {
            AdvancePassiveResources(state, gaugeRules, priorEntry.TimeOffsetSec);
            var priorInfo = plugin.RecastDatabase.Lookup(priorEntry.AbilityId, priorEntry.AbilityName);
            ApplyAutoEntry(state, priorEntry, priorInfo, gaugeRules, grantedRules, priorEntry.TimeOffsetSec, isGcd: false);
        }

        AdvancePassiveResources(state, gaugeRules, candidate.TimeOffsetSec);
        if (state.GaugeState.GetValueOrDefault("Aetherflow") > 0)
            return "Scholar Dissipation requires Aetherflow gauge to be empty";

        var aetherflowInfo = plugin.RecastDatabase.Lookup(0, "Aetherflow");
        if (aetherflowInfo != null)
        {
            var cooldownState = state.Clone();
            if (HasPersonalCooldownReady(
                    cooldownState,
                    aetherflowInfo,
                    grantedRules,
                    "Aetherflow",
                    candidate.TimeOffsetSec,
                    AutoCooldownToleranceSec,
                    out var earlyBySec))
            {
                return "Scholar Dissipation requires Aetherflow to be on cooldown for more than 6.0s, but Aetherflow is ready";
            }

            if (earlyBySec <= 6.0 + AutoCooldownToleranceSec)
                return $"Scholar Dissipation requires Aetherflow to remain on cooldown for more than 6.0s, but it is ready in {earlyBySec:F1}s";
        }

        var chainActive = state.TimedWindowEndByAbility.TryGetValue("Energy Drain", out var activeChainWindowEndSec) &&
                          candidate.TimeOffsetSec <= activeChainWindowEndSec + AutoCooldownToleranceSec;
        if (chainActive)
            return null;

        var chainStartsSoon = selectedNonDissipationEntries.Any(entry =>
            string.Equals(entry.AbilityName, "Chain Stratagem", StringComparison.OrdinalIgnoreCase) &&
            entry.TimeOffsetSec >= candidate.TimeOffsetSec - AutoCooldownToleranceSec &&
            entry.TimeOffsetSec <= candidate.TimeOffsetSec + 6.0 + AutoCooldownToleranceSec);
        if (IsWithinAutoOpenerBuffer(openerBuffer, candidate.TimeOffsetSec))
            return null;

        return chainStartsSoon
            ? null
            : "Scholar Dissipation requires Chain Stratagem to be active or to begin within the next 6.0s";
    }

    private static string? GetTimedUsageLockoutReason(
        string specName,
        IReadOnlyDictionary<string, double> timedLockoutEndByAbility,
        string abilityName,
        double timeSec)
    {
        if (!TryGetTimedUsageLockoutRule(specName, abilityName, out var sourceAbilityName, out _))
            return null;

        if (!timedLockoutEndByAbility.TryGetValue(abilityName, out var lockoutEndSec))
            return null;

        if (timeSec > lockoutEndSec + AutoCooldownToleranceSec)
            return null;

        return $"blocked by {sourceAbilityName} through {FormatCsvTime(lockoutEndSec)}";
    }

    private static string? GetTimedUsageWindowReason(
        string specName,
        IReadOnlyDictionary<string, double> timedWindowEndByAbility,
        string abilityName,
        double timeSec)
    {
        if (!TryGetTimedUsageWindowRule(specName, abilityName, out var parentAbilityName, out var durationSec))
            return null;

        if (!timedWindowEndByAbility.TryGetValue(abilityName, out var windowEndSec))
            return $"requires {parentAbilityName} window (within {durationSec:F0}s after use), but no active window exists";

        if (timeSec <= windowEndSec + AutoCooldownToleranceSec)
            return null;

        return $"requires {parentAbilityName} window through {FormatCsvTime(windowEndSec)}, but it expired before {FormatCsvTime(timeSec)}";
    }

    private static void ApplyTimedUsageWindowState(
        string specName,
        IDictionary<string, double> timedWindowEndByAbility,
        string abilityName,
        double timeSec)
    {
        if (string.Equals(specName, "Scholar", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(abilityName, "Chain Stratagem", StringComparison.OrdinalIgnoreCase))
        {
            timedWindowEndByAbility["Energy Drain"] = timeSec + 20.0;
        }
    }

    private static void ApplyTimedUsageLockoutState(
        string specName,
        IDictionary<string, double> timedLockoutEndByAbility,
        string abilityName,
        double timeSec)
    {
        if (!string.Equals(specName, "Scholar", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(abilityName, "Dissipation", StringComparison.OrdinalIgnoreCase))
            return;

        var lockoutEndSec = timeSec + 30.0;
        foreach (var lockedAbilityName in new[] { "Fey Blessing", "Whispering Dawn", "Fey Illumination", "Summon Seraph", "Seraphism" })
            timedLockoutEndByAbility[lockedAbilityName] = lockoutEndSec;
    }

    private void AdvanceAutoTimelineState(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        List<TimelineEntry> ogcdEntries,
        double targetTimeSec,
        AutoTimelineDebugRecorder? debug = null)
    {
        while (state.NextOgcdIndex < ogcdEntries.Count &&
               ogcdEntries[state.NextOgcdIndex].TimeOffsetSec <= targetTimeSec + 0.001)
        {
            var entry = ogcdEntries[state.NextOgcdIndex++];
            var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
            var scheduledTimeSec = GetEarliestAutoScheduleTime(
                state,
                entry,
                info,
                grantedRules,
                entry.TimeOffsetSec);
            if (scheduledTimeSec > targetTimeSec - AutoCastLockToleranceSec)
            {
                debug?.Add($"  oGCD deferred | {entry.AbilityName} | requested {FormatTime(entry.TimeOffsetSec)} | earliest {FormatTime(scheduledTimeSec)}");
                continue;
            }

            AdvancePassiveResources(state, gaugeRules, scheduledTimeSec);
            var rejectionReason = GetAutoEntryRejectionReason(state, entry, info, gaugeRules, grantedRules, scheduledTimeSec);
            if (rejectionReason != null)
            {
                debug?.Add($"  oGCD dropped | {entry.AbilityName} @ {FormatTime(scheduledTimeSec)} | {rejectionReason}");
                continue;
            }

            var score = ScoreAutoEntryCandidate(state, entry, info, gaugeRules, scheduledTimeSec);
            var scheduledEntry = CreateScheduledEntry(entry, scheduledTimeSec);
            ApplyAutoEntry(state, scheduledEntry, info, gaugeRules, grantedRules, scheduledTimeSec, isGcd: false);
            state.SelectedEntries.Add(scheduledEntry);
            state.Score += score;
            debug?.Add($"  oGCD kept | {scheduledEntry.AbilityName} @ {FormatTime(scheduledEntry.TimeOffsetSec)} | score {score:F1}");
        }

        AdvancePassiveResources(state, gaugeRules, targetTimeSec);
    }

    private void DrainRemainingAutoOgcds(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        List<TimelineEntry> ogcdEntries,
        AutoTimelineDebugRecorder? debug = null)
    {
        while (state.NextOgcdIndex < ogcdEntries.Count)
        {
            var entry = ogcdEntries[state.NextOgcdIndex++];
            var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
            var scheduledTimeSec = GetEarliestAutoScheduleTime(
                state,
                entry,
                info,
                grantedRules,
                entry.TimeOffsetSec);
            AdvancePassiveResources(state, gaugeRules, scheduledTimeSec);
            var rejectionReason = GetAutoEntryRejectionReason(state, entry, info, gaugeRules, grantedRules, scheduledTimeSec);
            if (rejectionReason != null)
            {
                debug?.Add($"  oGCD tail-drop | {entry.AbilityName} @ {FormatTime(scheduledTimeSec)} | {rejectionReason}");
                continue;
            }

            var score = ScoreAutoEntryCandidate(state, entry, info, gaugeRules, scheduledTimeSec);
            var scheduledEntry = CreateScheduledEntry(entry, scheduledTimeSec);
            ApplyAutoEntry(state, scheduledEntry, info, gaugeRules, grantedRules, scheduledTimeSec, isGcd: false);
            state.SelectedEntries.Add(scheduledEntry);
            state.Score += score;
            debug?.Add($"  oGCD tail-keep | {scheduledEntry.AbilityName} @ {FormatTime(scheduledEntry.TimeOffsetSec)} | score {score:F1}");
        }
    }

    private void AdvancePassiveResources(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        double targetTimeSec)
    {
        if (targetTimeSec <= state.LastTimeSec)
        {
            state.LastTimeSec = Math.Max(state.LastTimeSec, targetTimeSec);
            return;
        }

        var elapsed = targetTimeSec - state.LastTimeSec;
        if (state.LastGcdId != 0 &&
            targetTimeSec - state.LastComboTimeSec >= ComboResetGapSec)
        {
            state.LastGcdId = 0;
            state.LastComboAbilityName = null;
        }

        if (gaugeRules != null)
        {
            foreach (var resource in gaugeRules.Resources)
            {
                if (resource.PassiveGenerationIntervalSec <= 0)
                    continue;

                var progress = state.PassiveGaugeProgress.GetValueOrDefault(resource.Name) + elapsed;
                var ticks = (int)(progress / resource.PassiveGenerationIntervalSec);
                if (ticks > 0)
                {
                    state.GaugeState[resource.Name] = Math.Clamp(
                        state.GaugeState.GetValueOrDefault(resource.Name) + ticks,
                        0,
                        resource.MaxValue);
                    progress -= ticks * resource.PassiveGenerationIntervalSec;
                }

                state.PassiveGaugeProgress[resource.Name] = progress;
            }
        }

        state.LastTimeSec = targetTimeSec;
    }

    private static bool ShouldTrackPersonalCooldown(Data.RecastDatabase.RecastInfo? info)
        => info != null &&
           !info.IsGcdAction &&
           (info.RecastSec >= 5.0 || info.MaxCharges > 1);

    private static void RefreshCooldownQueue(List<double> queue, double nowSec, double toleranceSec)
        => queue.RemoveAll(t => t <= nowSec + toleranceSec);

    private double GetCooldownReadyTime(
        AutoTimelineState state,
        Data.RecastDatabase.RecastInfo? info,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName,
        double requestedTimeSec,
        double toleranceSec)
    {
        if (!ShouldTrackPersonalCooldown(info) ||
            ShouldBypassCooldown(state.GrantedState, grantedRules, abilityName))
            return requestedTimeSec;

        var cooldownKey = GetCooldownBucketKey(info, (int)info!.AbilityId, info.Name);
        if (!state.CooldownQueues.TryGetValue(cooldownKey, out var queue))
        {
            queue = [];
            state.CooldownQueues[cooldownKey] = queue;
        }

        RefreshCooldownQueue(queue, requestedTimeSec, toleranceSec);
        return queue.Count < info.MaxCharges
            ? requestedTimeSec
            : Math.Max(requestedTimeSec, queue.Min());
    }

    private bool HasPersonalCooldownReady(
        AutoTimelineState state,
        Data.RecastDatabase.RecastInfo? info,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName,
        double nowSec,
        double toleranceSec,
        out double earlyBySec)
    {
        earlyBySec = 0.0;
        if (!ShouldTrackPersonalCooldown(info) ||
            ShouldBypassCooldown(state.GrantedState, grantedRules, abilityName))
            return true;

        var cooldownKey = GetCooldownBucketKey(info, (int)info!.AbilityId, info.Name);
        if (!state.CooldownQueues.TryGetValue(cooldownKey, out var queue))
        {
            queue = [];
            state.CooldownQueues[cooldownKey] = queue;
        }

        RefreshCooldownQueue(queue, nowSec, toleranceSec);
        if (queue.Count < info.MaxCharges)
            return true;

        var nextReady = queue.Min();
        earlyBySec = Math.Max(0.0, nextReady - nowSec);
        return false;
    }

    private void RegisterCooldownUse(
        AutoTimelineState state,
        Data.RecastDatabase.RecastInfo? info,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName,
        double nowSec)
    {
        if (!ShouldTrackPersonalCooldown(info) ||
            ShouldBypassCooldown(state.GrantedState, grantedRules, abilityName))
            return;

        var cooldownKey = GetCooldownBucketKey(info, (int)info!.AbilityId, info.Name);
        if (!state.CooldownQueues.TryGetValue(cooldownKey, out var queue))
        {
            queue = [];
            state.CooldownQueues[cooldownKey] = queue;
        }

        RefreshCooldownQueue(queue, nowSec, AutoCooldownToleranceSec);
        queue.Add(nowSec + info.RecastSec);
        queue.Sort();
    }

    private int GetActionStatePenalty(
        AutoTimelineState state,
        Data.RecastDatabase.RecastInfo? info,
        string abilityName,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        var penalty = 0;
        var actionRule = plugin.ActionStateDatabase.Lookup((int)(info?.AbilityId ?? 0), abilityName);
        if (actionRule == null)
            return 0;

        foreach (var effect in actionRule.Effects)
        {
            if (effect.MinRequired > 0)
            {
                if (ShouldIgnoreActionStateRequirement(actionRule, effect, abilityName, gaugeRules, grantedRules))
                    continue;

                if (!ShouldEnforceActionStateRequirement(effect.StateName, state.AvailableAbilityNames, gaugeRules, grantedRules))
                    continue;

                var have = GetEffectiveStateValue(effect.StateName, state.ActionState, state.GaugeState, state.GrantedState);
                if (have < effect.MinRequired)
                    penalty += 320 + ((effect.MinRequired - have) * 140);
            }
        }

        return penalty;
    }

    private static void ApplyActionStateEffects(
        Dictionary<string, int> actionState,
        ActionStateDatabase.ActionStateRule actionRule)
    {
        foreach (var effect in actionRule.Effects)
        {
            if (effect.Delta == 0)
                continue;

            actionState[effect.StateName] = Math.Clamp(
                actionState.GetValueOrDefault(effect.StateName) + effect.Delta,
                0,
                effect.MaxValue);
        }
    }

    private static int GetEffectiveStateValue(
        string stateName,
        params IReadOnlyDictionary<string, int>[] stateSets)
    {
        var best = 0;
        foreach (var equivalentStateName in EnumerateEquivalentStateNames(stateName))
        {
            foreach (var stateSet in stateSets)
                best = Math.Max(best, stateSet.GetValueOrDefault(equivalentStateName));
        }

        return best;
    }

    private int GetGaugePenalty(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        var penalty = 0;
        var ignoreRefreshMaximum = ShouldKeepAutoGaugeGeneratorOnCooldown(abilityName, gaugeRules);
        if (gaugeRules != null && gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
        {
            var bypassGaugeSpendChecks = ShouldBypassRepeatableGrantedGaugeSpendChecks(state.GrantedState, grantedRules, abilityName);
            foreach (var effect in effects)
            {
                if (bypassGaugeSpendChecks && (effect.MinRequired > 0 || effect.Delta < 0))
                    continue;

                if (effect.MinRequired > 0)
                {
                    var have = state.GaugeState.GetValueOrDefault(effect.GaugeName);
                    if (have < effect.MinRequired)
                        penalty += 180 + (effect.MinRequired - have) * 50;
                }

                if (!ignoreRefreshMaximum && effect.MaxAllowedBeforeUse < int.MaxValue)
                {
                    var have = state.GaugeState.GetValueOrDefault(effect.GaugeName);
                    if (have > effect.MaxAllowedBeforeUse)
                        penalty += 420 + ((have - effect.MaxAllowedBeforeUse) * 220);
                }

                if (effect.Delta > 0)
                {
                    var resource = gaugeRules.Resources.FirstOrDefault(r =>
                        string.Equals(r.Name, effect.GaugeName, StringComparison.OrdinalIgnoreCase));
                    if (resource?.AvoidOvercap == true)
                    {
                        var have = state.GaugeState.GetValueOrDefault(effect.GaugeName);
                        var overflow = Math.Max(0, (have + effect.Delta) - resource.MaxValue);
                        if (overflow > 0)
                            penalty += 300 + overflow * 200;
                    }
                }
            }
        }

        foreach (var effect in GetRepeatableConsumeBonusEffects(state.GrantedState, grantedRules, abilityName))
        {
            if (!GaugeSimulator.IsTrueGaugeResource(effect.GaugeName))
                continue;

            var resource = gaugeRules?.Resources.FirstOrDefault(r =>
                string.Equals(r.Name, effect.GaugeName, StringComparison.OrdinalIgnoreCase));
            if (resource?.AvoidOvercap == true && effect.Delta > 0)
            {
                var have = state.GaugeState.GetValueOrDefault(effect.GaugeName);
                var overflow = Math.Max(0, (have + effect.Delta) - resource.MaxValue);
                if (overflow > 0)
                    penalty += 300 + overflow * 200;
            }
        }

        var repeatRule = FindRepeatableGrantedActionRule(grantedRules, abilityName);
        if (repeatRule != null && UsesRepeatableGrantedActionCharge(state.GrantedState, repeatRule, abilityName))
        {
            var have = state.GrantedState.GetValueOrDefault(repeatRule.ResourceName);
            if (have < repeatRule.ConsumeCount)
                penalty += 220 + ((repeatRule.ConsumeCount - have) * 80);
        }

        return penalty;
    }

    private int GetGaugeInsufficiencyPenalty(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        var penalty = 0;
        var ignoreRefreshMaximum = ShouldKeepAutoGaugeGeneratorOnCooldown(abilityName, gaugeRules);
        if (gaugeRules != null && gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
        {
            var bypassGaugeSpendChecks = ShouldBypassRepeatableGrantedGaugeSpendChecks(state.GrantedState, grantedRules, abilityName);
            foreach (var effect in effects)
            {
                if (bypassGaugeSpendChecks && (effect.MinRequired > 0 || effect.Delta < 0))
                    continue;

                var have = state.GaugeState.GetValueOrDefault(effect.GaugeName);
                if (effect.MinRequired > 0 && have < effect.MinRequired)
                    penalty += 180 + (effect.MinRequired - have) * 50;

                if (!ignoreRefreshMaximum &&
                    effect.MaxAllowedBeforeUse < int.MaxValue &&
                    have > effect.MaxAllowedBeforeUse)
                    penalty += 420 + ((have - effect.MaxAllowedBeforeUse) * 220);
            }
        }

        var repeatRule = FindRepeatableGrantedActionRule(grantedRules, abilityName);
        if (repeatRule != null && UsesRepeatableGrantedActionCharge(state.GrantedState, repeatRule, abilityName))
        {
            var have = state.GrantedState.GetValueOrDefault(repeatRule.ResourceName);
            if (have < repeatRule.ConsumeCount)
                penalty += 220 + ((repeatRule.ConsumeCount - have) * 80);
        }

        return penalty;
    }

    private string? GetNumericGaugeInsufficiencyReason(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        if (gaugeRules == null || !gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
            return null;

        foreach (var effect in effects)
        {
            var have = state.GaugeState.GetValueOrDefault(effect.GaugeName);
            if (effect.MinRequired > 0 && have < effect.MinRequired)
                return $"requires {effect.GaugeName} >= {effect.MinRequired}, but only {have} is available";
        }

        return null;
    }

    private static bool HasOvercapPressure(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules)
    {
        if (gaugeRules == null)
            return false;

        foreach (var resource in gaugeRules.Resources)
        {
            if (!resource.AvoidOvercap)
                continue;

            if (state.GaugeState.GetValueOrDefault(resource.Name) >= resource.MaxValue)
                return true;
        }

        return false;
    }

    private static bool ShouldKeepAutoGaugeGeneratorOnCooldown(
        string abilityName,
        GaugeSimulator.JobGaugeRules? gaugeRules)
    {
        if (gaugeRules == null || !gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
            return false;

        var hasTrueGaugeGain = false;
        foreach (var effect in effects)
        {
            if (!GaugeSimulator.IsTrueGaugeResource(effect.GaugeName))
                continue;

            if (effect.MaxAllowedBeforeUse < int.MaxValue)
                return false;

            if (effect.Delta < 0 || effect.MinRequired > 0)
                return false;

            if (effect.Delta > 0 || effect.SetValue is int)
                hasTrueGaugeGain = true;
        }

        return hasTrueGaugeGain;
    }

    private static bool IsAutoTimelineTrueGaugeAbility(
        string abilityName,
        GaugeSimulator.JobGaugeRules? gaugeRules)
    {
        if (gaugeRules == null || !gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
            return false;

        return effects.Any(effect => GaugeSimulator.IsTrueGaugeResource(effect.GaugeName));
    }

    private static bool IsAutoTimelineTrueGaugeSpender(
        string abilityName,
        GaugeSimulator.JobGaugeRules? gaugeRules)
    {
        if (gaugeRules == null || !gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
            return false;

        return effects.Any(effect =>
            GaugeSimulator.IsTrueGaugeResource(effect.GaugeName) &&
            (effect.MinRequired > 0 || effect.Delta < 0));
    }

    private static bool CanBorrowAutoOpenerPrepullGauge(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        string abilityName)
    {
        if (gaugeRules == null || !gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
            return false;

        var canBorrowForShortage = false;
        foreach (var effect in effects)
        {
            var have = state.GaugeState.GetValueOrDefault(effect.GaugeName);
            if (effect.MaxAllowedBeforeUse < int.MaxValue && have > effect.MaxAllowedBeforeUse)
                return false;

            if (effect.MinRequired > 0 && have < effect.MinRequired)
                canBorrowForShortage = true;
        }

        return canBorrowForShortage;
    }

    private static void ApplyGaugeEffectToState(
        IDictionary<string, int> gaugeState,
        GaugeSimulator.JobGaugeRules gaugeRules,
        GaugeSimulator.GaugeEffect effect,
        bool allowPrepullSeed = false)
    {
        var resource = gaugeRules.Resources.FirstOrDefault(r =>
            string.Equals(r.Name, effect.GaugeName, StringComparison.OrdinalIgnoreCase));

        if (allowPrepullSeed && effect.MinRequired > 0)
        {
            var preSeedValue = Math.Clamp(
                effect.MinRequired,
                0,
                resource?.MaxValue ?? int.MaxValue);
            var currentGaugeValue = gaugeState.TryGetValue(effect.GaugeName, out var existingValue)
                ? existingValue
                : 0;
            if (currentGaugeValue < preSeedValue)
                gaugeState[effect.GaugeName] = preSeedValue;
        }

        if (effect.SetValue is int setValue)
        {
            gaugeState[effect.GaugeName] = Math.Clamp(
                setValue,
                0,
                resource?.MaxValue ?? int.MaxValue);
        }

        if (effect.Delta == 0)
            return;

        gaugeState[effect.GaugeName] = Math.Clamp(
            (gaugeState.TryGetValue(effect.GaugeName, out var currentValue) ? currentValue : 0) + effect.Delta,
            0,
            resource?.MaxValue ?? int.MaxValue);
    }

    private void ApplyGaugeEffects(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName,
        bool allowPrepullSeed = false)
    {
        if (gaugeRules != null && gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
        {
            var bypassGaugeSpendChecks = ShouldBypassRepeatableGrantedGaugeSpendChecks(state.GrantedState, grantedRules, abilityName);
            foreach (var effect in effects)
            {
                if (bypassGaugeSpendChecks && effect.Delta < 0)
                    continue;

                ApplyGaugeEffectToState(state.GaugeState, gaugeRules, effect, allowPrepullSeed);
            }
        }

        if (grantedRules != null && grantedRules.EffectByName.TryGetValue(abilityName, out var grantedEffects))
        {
            foreach (var effect in grantedEffects)
            {
                if (effect.Delta == 0)
                    continue;

                var resource = grantedRules.Resources.FirstOrDefault(r =>
                    string.Equals(r.Name, effect.ResourceName, StringComparison.OrdinalIgnoreCase));

                state.GrantedState[effect.ResourceName] = Math.Clamp(
                    state.GrantedState.GetValueOrDefault(effect.ResourceName) + effect.Delta,
                    0,
                    resource?.MaxValue ?? int.MaxValue);
            }
        }

        var repeatRule = FindRepeatableGrantedActionRule(grantedRules, abilityName);
        if (repeatRule == null)
            return;

        var resourceDef = grantedRules?.Resources.FirstOrDefault(r =>
            string.Equals(r.Name, repeatRule.ResourceName, StringComparison.OrdinalIgnoreCase));
        var maxValue = resourceDef?.MaxValue ?? int.MaxValue;
        if (UsesRepeatableGrantedActionCharge(state.GrantedState, repeatRule, abilityName))
        {
            if (gaugeRules != null)
            {
                foreach (var effect in repeatRule.ConsumeBonusEffects)
                    ApplyGaugeEffectToState(state.GaugeState, gaugeRules, effect, allowPrepullSeed);
            }

            state.GrantedState[repeatRule.ResourceName] = Math.Clamp(
                state.GrantedState.GetValueOrDefault(repeatRule.ResourceName) - repeatRule.ConsumeCount,
                0,
                maxValue);
        }
        else if (string.Equals(repeatRule.TriggerName, abilityName, StringComparison.OrdinalIgnoreCase))
        {
            state.GrantedState[repeatRule.ResourceName] = Math.Clamp(
                state.GrantedState.GetValueOrDefault(repeatRule.ResourceName) + repeatRule.GrantCount,
                0,
                maxValue);
        }
    }

    private IReadOnlyList<GaugeSimulator.GaugeEffect> GetRepeatableConsumeBonusEffects(
        Dictionary<string, int> grantedState,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        var repeatRule = FindRepeatableGrantedActionRule(grantedRules, abilityName);
        return repeatRule != null &&
               UsesRepeatableGrantedActionCharge(grantedState, repeatRule, abilityName)
            ? repeatRule.ConsumeBonusEffects
            : [];
    }

    private int GetCardDrawPenalty(
        AutoTimelineState state,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        if (grantedRules?.CardDraw is not { } cardDraw)
            return 0;

        if (cardDraw.AstralCards.Contains(abilityName))
            return string.Equals(state.CardDrawState, "Astral", StringComparison.OrdinalIgnoreCase) ? 0 : 150;

        if (cardDraw.UmbralCards.Contains(abilityName))
            return string.Equals(state.CardDrawState, "Umbral", StringComparison.OrdinalIgnoreCase) ? 0 : 150;

        return 0;
    }

    private void ApplyCardDrawState(
        AutoTimelineState state,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        if (grantedRules?.CardDraw is not { } cardDraw)
            return;

        if (string.Equals(abilityName, cardDraw.AstralDrawName, StringComparison.OrdinalIgnoreCase))
            state.CardDrawState = "Astral";
        else if (string.Equals(abilityName, cardDraw.UmbralDrawName, StringComparison.OrdinalIgnoreCase))
            state.CardDrawState = "Umbral";
    }

    private string? GetAutoEntryRejectionReason(
        AutoTimelineState state,
        TimelineEntry entry,
        Data.RecastDatabase.RecastInfo? info,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        double scheduledTimeSec)
    {
        if (!HasPersonalCooldownReady(state, info, grantedRules, entry.AbilityName, scheduledTimeSec, AutoCooldownToleranceSec, out _))
            return "cooldown not ready";

        var gaugePenalty = GetGaugeInsufficiencyPenalty(state, gaugeRules, grantedRules, entry.AbilityName);
        if (gaugePenalty > 0)
            return $"gauge penalty {gaugePenalty}";

        var actionPenalty = GetActionStatePenalty(state, info, entry.AbilityName, gaugeRules, grantedRules);
        if (actionPenalty > 0)
            return $"action-state penalty {actionPenalty}";

        var timedWindowReason = GetTimedUsageWindowReason(
            state.SpecName,
            state.TimedWindowEndByAbility,
            entry.AbilityName,
            scheduledTimeSec);
        if (timedWindowReason != null)
            return timedWindowReason;

        var timedLockoutReason = GetTimedUsageLockoutReason(
            state.SpecName,
            state.TimedLockoutEndByAbility,
            entry.AbilityName,
            scheduledTimeSec);
        if (timedLockoutReason != null)
            return timedLockoutReason;

        var scholarBurstReservationReason = GetScholarBurstAetherflowReservationReason(
            state.SpecName,
            state.TimedWindowEndByAbility,
            state.GaugeState,
            entry.AbilityName,
            scheduledTimeSec);
        if (scholarBurstReservationReason != null)
            return scholarBurstReservationReason;

        var cardPenalty = GetCardDrawPenalty(state, grantedRules, entry.AbilityName);
        return cardPenalty == 0 ? null : $"card-state penalty {cardPenalty}";
    }

    private bool CanAcceptAutoEntry(
        AutoTimelineState state,
        TimelineEntry entry,
        Data.RecastDatabase.RecastInfo? info,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        double scheduledTimeSec)
    {
        return GetAutoEntryRejectionReason(state, entry, info, gaugeRules, grantedRules, scheduledTimeSec) == null;
    }

    private void ApplyAutoEntry(
        AutoTimelineState state,
        TimelineEntry entry,
        Data.RecastDatabase.RecastInfo? info,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        double timeSec,
        bool isGcd)
    {
        var effectiveCastTimeSec = GetEffectiveCastTimeSec(
            state.GrantedState, grantedRules, info, entry.AbilityName, out var instantCastRule);

        RegisterCooldownUse(state, info, grantedRules, entry.AbilityName, timeSec);
        ApplyCastStateTransitions(
            state.GrantedState, grantedRules, info, entry.AbilityName, effectiveCastTimeSec, instantCastRule);
        ApplyGaugeEffects(state, gaugeRules, grantedRules, entry.AbilityName);
        var actionRule = plugin.ActionStateDatabase.Lookup((int)(info?.AbilityId ?? 0), entry.AbilityName);
        if (actionRule != null)
            ApplyActionStateEffects(state.ActionState, actionRule);
        ApplyCardDrawState(state, grantedRules, entry.AbilityName);
        ApplyTimedUsageWindowState(state.SpecName, state.TimedWindowEndByAbility, entry.AbilityName, timeSec);
        ApplyTimedUsageLockoutState(state.SpecName, state.TimedLockoutEndByAbility, entry.AbilityName, timeSec);

        if (isGcd && info != null)
        {
            state.LastSelectedGcdAbilityName = entry.AbilityName;
            if (IsComboTrackedAction(info, entry.AbilityName))
            {
                state.LastGcdId = info.AbilityId;
                state.LastComboAbilityName = entry.AbilityName;
                state.LastComboTimeSec = timeSec;
            }
            state.CastLockUntilSec = effectiveCastTimeSec > AutoCastLockToleranceSec
                ? timeSec + effectiveCastTimeSec
                : timeSec;
        }
        else if (!isGcd)
        {
            state.LastOgcdTimeSec = timeSec;
        }
    }

    private void SaveEditingTimeline()
    {
        if (editingTimeline == null || selectedCustomKey == null)
            return;

        // Re-key if name changed (key stays same — keys are immutable after copy)
        plugin.CustomTimelineStore.SaveTimeline(plugin.Configuration, selectedCustomKey, editingTimeline);
        customEditorDirty = false;

        // Refresh zone → encounter mappings so the overlay activates immediately
        plugin.EncounterTracker.RebuildZoneMappings();
    }

    // ── Report Importer ──

    /// <summary>Split CamelCase job name into display name: "BlackMage" → "Black Mage".</summary>
    private static string SplitCamelCase(string s)
        => string.IsNullOrEmpty(s) ? s : Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ");

    private void DrawReportImporter()
    {
        if (!ImGui.CollapsingHeader("From FFLogs Report##riHeader"))
            return;

        ImGui.SetNextItemWidth(300);
        ImGui.InputText("Report URL##riUrl", ref riUrl, 256);
        ImGui.SameLine();

        var canFetch = !riFetching && !riImporting && !string.IsNullOrWhiteSpace(riUrl);
        if (!canFetch) ImGui.BeginDisabled();
        if (ImGui.Button("Load Report##riFetch") && canFetch)
            StartReportFetch();
        if (!canFetch) ImGui.EndDisabled();

        if (riFetching)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Fetching...");
        }

        if (!string.IsNullOrEmpty(riStatus))
        {
            if (riStatusIsError)
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), riStatus);
            else
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.6f, 1f), riStatus);
        }

        if (riFlights.Count == 0) return;

        // Fight selector
        ImGui.SetNextItemWidth(320);
        var fightPreview = riSelectedFight >= 0 && riSelectedFight < riFlights.Count
            ? riFlights[riSelectedFight].Name
            : "Select fight...";
        if (ImGui.BeginCombo("Fight##riCombo", fightPreview))
        {
            for (var i = 0; i < riFlights.Count; i++)
            {
                var f   = riFlights[i];
                var dur = TimeSpan.FromMilliseconds(f.DurationMs);
                // Show fight ID so the user can cross-reference with the URL's ?fight=N
                if (ImGui.Selectable($"[{f.Id}]  {f.Name}  ({dur:m\\:ss})##{i}", i == riSelectedFight))
                {
                    riSelectedFight = i;
                    riSelectedPhase = 0;
                }
            }
            ImGui.EndCombo();
        }

        var reportPhaseWindows = riSelectedFight >= 0 && riSelectedFight < riFlights.Count
            ? BuildEncounterPhaseWindows(CreateFightPhaseInfo(riFlights[riSelectedFight]))
            : [];
        var reportPhaseLabels = BuildPhaseDropdownOptions(reportPhaseWindows);
        if (riSelectedPhase < 0 || riSelectedPhase >= reportPhaseLabels.Count)
            riSelectedPhase = 0;
        ImGui.SetNextItemWidth(320);
        ImGui.Combo("Phase##riPhase", ref riSelectedPhase, reportPhaseLabels, -1);

        if (riPlayers.Count == 0) return;

        // Player list — show actor ID so user can cross-reference with URL's source=N
        ImGui.TextUnformatted("Players  (ID matches ?source=N in the FFLogs URL):");
        ImGui.BeginChild("##riPlayerList", new Vector2(0, 120), true);
        for (var i = 0; i < riPlayers.Count; i++)
        {
            var p          = riPlayers[i];
            var jobDisplay = SplitCamelCase(p.SubType);
            if (ImGui.Selectable($"[{p.Id}]  {p.Name}  ({jobDisplay})##{i}", i == riSelectedPlayer))
                riSelectedPlayer = i;
        }
        ImGui.EndChild();

        var canCreate = !riImporting && !riFetching
            && riSelectedFight  >= 0 && riSelectedFight  < riFlights.Count
            && riSelectedPlayer >= 0 && riSelectedPlayer < riPlayers.Count;
        if (!canCreate) ImGui.BeginDisabled();
        if (ImGui.Button("Create Timeline##riCreate") && canCreate)
            StartReportImport();
        if (!canCreate) ImGui.EndDisabled();

        if (riImporting)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Importing...");
        }
    }

    private void DrawBlankTimelineCreatorSection()
    {
        if (!ImGui.CollapsingHeader("Blank Timeline##riBlankHeader"))
            return;

        ImGui.TextDisabled("Create a blank custom timeline scaffold from cached fight data.");
        ImGui.TextDisabled("This keeps boss attacks, duration, and raw FFLogs source while clearing player actions.");

        if (zones.Count == 0 || allSpecNames.Count == 0)
        {
            ImGui.TextDisabled("Load fight and job lists first, then fetch logs for the encounter you want.");
            return;
        }

        if (riBlankSelectedZone < 0 || riBlankSelectedZone >= zones.Count)
            riBlankSelectedZone = Math.Clamp(selectedZone, 0, Math.Max(0, zones.Count - 1));
        if (riBlankSelectedSpec < 0 || riBlankSelectedSpec >= allSpecNames.Count)
            riBlankSelectedSpec = Math.Clamp(selectedSpec, 0, Math.Max(0, allSpecNames.Count - 1));

        var blankZones = zones.Select(zone => zone.Name).ToList();
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Zone##riBlankZone", ref riBlankSelectedZone, blankZones, -1))
        {
            riBlankSelectedEncounter = 0;
            riBlankSelectedPhase = 0;
        }

        var blankEncounters = riBlankSelectedZone >= 0 && riBlankSelectedZone < zones.Count
            ? zones[riBlankSelectedZone].Encounters
            : [];
        var blankEncounterNames = blankEncounters.Select(encounter => encounter.Name).ToList();
        if (riBlankSelectedEncounter < 0 || riBlankSelectedEncounter >= blankEncounterNames.Count)
            riBlankSelectedEncounter = blankEncounterNames.Count > 0 ? 0 : -1;

        ImGui.SetNextItemWidth(220);
        if (blankEncounterNames.Count > 0)
        {
            if (ImGui.Combo("Fight##riBlankFight", ref riBlankSelectedEncounter, blankEncounterNames, -1))
                riBlankSelectedPhase = 0;
        }
        else
        {
            var noEncounter = 0;
            ImGui.Combo("Fight##riBlankFight", ref noEncounter, new List<string> { "(none)" }, -1);
        }

        var selectedBlankEncounter = riBlankSelectedEncounter >= 0 && riBlankSelectedEncounter < blankEncounters.Count
            ? blankEncounters[riBlankSelectedEncounter]
            : null;
        var selectedBlankSpecName = riBlankSelectedSpec >= 0 && riBlankSelectedSpec < allSpecNames.Count
            ? allSpecNames[riBlankSelectedSpec]
            : string.Empty;
        var blankSourceTimeline = selectedBlankEncounter != null && !string.IsNullOrWhiteSpace(selectedBlankSpecName)
            ? plugin.TimelineStore.GetTimeline(selectedBlankEncounter.Id, selectedBlankSpecName)
            : null;
        RefreshTimelineRuntimeMetadata(blankSourceTimeline);

        var blankPhaseWindows = BuildEncounterPhaseWindows(blankSourceTimeline?.PhaseInfo);
        var blankPhaseLabels = BuildPhaseDropdownOptions(blankPhaseWindows);
        if (riBlankSelectedPhase < 0 || riBlankSelectedPhase >= blankPhaseLabels.Count)
            riBlankSelectedPhase = 0;
        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Phase##riBlankPhase", ref riBlankSelectedPhase, blankPhaseLabels, -1);

        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Job##riBlankJob", ref riBlankSelectedSpec, allSpecNames, -1))
            riBlankSelectedPhase = 0;

        var hasBlankSelection =
            riBlankSelectedZone >= 0 && riBlankSelectedZone < zones.Count &&
            riBlankSelectedEncounter >= 0 && riBlankSelectedEncounter < blankEncounters.Count &&
            riBlankSelectedSpec >= 0 && riBlankSelectedSpec < allSpecNames.Count;
        var selectedBlankPhaseWindow = GetSelectedPhaseWindow(blankPhaseWindows, riBlankSelectedPhase);
        var canCreateBlankTimeline = hasBlankSelection &&
                                     !riFetching &&
                                     !riImporting &&
                                     blankSourceTimeline != null;

        if (!canCreateBlankTimeline)
            ImGui.BeginDisabled();
        if (ImGui.Button("Create Blank Timeline##riBlankCreate"))
            CreateBlankTimelineFromFetchedLogs(blankEncounters[riBlankSelectedEncounter], selectedBlankSpecName, selectedBlankPhaseWindow);
        if (!canCreateBlankTimeline)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            if (!hasBlankSelection)
            {
                ImGui.TextDisabled("Choose a valid fight and job first.");
            }
            else if (blankSourceTimeline == null)
            {
                ImGui.TextDisabled("No fetched logs are cached for this fight/job yet.");
                ImGui.TextDisabled("Use \"Update from FFLogs\" on the Encounter Timeline tab first.");
            }
            else
            {
                ImGui.Text("Create a blank player timeline with boss attacks and cached fight data intact.");
            }
            ImGui.EndTooltip();
        }

        ImGui.Spacing();
    }

    private void DrawAutoTimelineFromFetchedLogsSection()
    {
        if (!ImGui.CollapsingHeader("Auto Timeline from Fetched FFLogs##riAutoHeader"))
            return;

        ImGui.TextDisabled("Create a custom Auto Timeline directly from cached raw FFLogs parse data.");

        if (zones.Count == 0 || allSpecNames.Count == 0)
        {
            ImGui.TextDisabled("Load fight and job lists first, then fetch logs for the encounter you want.");
            return;
        }

        if (riAutoSelectedZone < 0 || riAutoSelectedZone >= zones.Count)
            riAutoSelectedZone = Math.Clamp(selectedZone, 0, Math.Max(0, zones.Count - 1));
        if (riAutoSelectedSpec < 0 || riAutoSelectedSpec >= allSpecNames.Count)
            riAutoSelectedSpec = Math.Clamp(selectedSpec, 0, Math.Max(0, allSpecNames.Count - 1));

        var autoZones = zones.Select(zone => zone.Name).ToList();
        var autoEncounters = riAutoSelectedZone >= 0 && riAutoSelectedZone < zones.Count
            ? zones[riAutoSelectedZone].Encounters
            : [];
        var autoEncounterNames = autoEncounters.Select(encounter => encounter.Name).ToList();
        if (riAutoSelectedEncounter < 0 || riAutoSelectedEncounter >= autoEncounterNames.Count)
            riAutoSelectedEncounter = autoEncounterNames.Count > 0 ? 0 : -1;

        if (ImGui.BeginTable("##riAutoSelectors", 2, ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("left", ImGuiTableColumnFlags.WidthFixed, 280f);
            ImGui.TableSetupColumn("right", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            ImGui.SetNextItemWidth(220);
            if (ImGui.Combo("Zone##riAutoZone", ref riAutoSelectedZone, autoZones, -1))
            {
                riAutoSelectedEncounter = 0;
                riAutoSelectedPhase = 0;
                riAutoPhaseStartSelectionKey = string.Empty;
                riAutoPhaseStartBossIndices.Clear();
            }

            autoEncounters = riAutoSelectedZone >= 0 && riAutoSelectedZone < zones.Count
                ? zones[riAutoSelectedZone].Encounters
                : [];
            autoEncounterNames = autoEncounters.Select(encounter => encounter.Name).ToList();
            if (riAutoSelectedEncounter < 0 || riAutoSelectedEncounter >= autoEncounterNames.Count)
                riAutoSelectedEncounter = autoEncounterNames.Count > 0 ? 0 : -1;

            ImGui.SetNextItemWidth(220);
            if (autoEncounterNames.Count > 0)
            {
                if (ImGui.Combo("Fight##riAutoFight", ref riAutoSelectedEncounter, autoEncounterNames, -1))
                {
                    riAutoSelectedPhase = 0;
                    riAutoPhaseStartSelectionKey = string.Empty;
                    riAutoPhaseStartBossIndices.Clear();
                }
            }
            else
            {
                var noEncounter = 0;
                ImGui.Combo("Fight##riAutoFight", ref noEncounter, new List<string> { "(none)" }, -1);
            }

            var selectedAutoEncounter = riAutoSelectedEncounter >= 0 && riAutoSelectedEncounter < autoEncounters.Count
                ? autoEncounters[riAutoSelectedEncounter]
                : null;
            var selectedAutoSpecName = riAutoSelectedSpec >= 0 && riAutoSelectedSpec < allSpecNames.Count
                ? allSpecNames[riAutoSelectedSpec]
                : string.Empty;
            var fullSourceTimeline = selectedAutoEncounter != null && !string.IsNullOrWhiteSpace(selectedAutoSpecName)
                ? plugin.TimelineStore.GetTimeline(selectedAutoEncounter.Id, selectedAutoSpecName)
                : null;
            var autoPhaseWindows = BuildEncounterPhaseWindows(fullSourceTimeline?.PhaseInfo);
            var autoPhaseLabels = autoPhaseWindows.Count > 0
                ? new List<string> { "Full Fight", "Per Phase" }
                : new List<string> { "Full Fight" };
            if (riAutoSelectedPhase < 0 || riAutoSelectedPhase >= autoPhaseLabels.Count)
                riAutoSelectedPhase = 0;
            ImGui.SetNextItemWidth(220);
            if (ImGui.Combo("Phase##riAutoPhase", ref riAutoSelectedPhase, autoPhaseLabels, -1))
            {
                riAutoPhaseStartSelectionKey = string.Empty;
                riAutoPhaseStartBossIndices.Clear();
            }

            ImGui.SetNextItemWidth(220);
            if (ImGui.Combo("Job##riAutoJob", ref riAutoSelectedSpec, allSpecNames, -1))
            {
                riAutoSelectedPhase = 0;
                riAutoPhaseStartSelectionKey = string.Empty;
                riAutoPhaseStartBossIndices.Clear();
            }

            var hasAutoSelectionForUi =
                riAutoSelectedZone >= 0 && riAutoSelectedZone < zones.Count &&
                riAutoSelectedEncounter >= 0 && riAutoSelectedEncounter < autoEncounters.Count &&
                riAutoSelectedSpec >= 0 && riAutoSelectedSpec < allSpecNames.Count;
            var autoEncounterIdForUi = hasAutoSelectionForUi ? autoEncounters[riAutoSelectedEncounter].Id : 0;
            var autoSpecNameForUi = hasAutoSelectionForUi ? allSpecNames[riAutoSelectedSpec] : string.Empty;
            var sourceTimelineForUi = hasAutoSelectionForUi
                ? plugin.TimelineStore.GetTimeline(autoEncounterIdForUi, autoSpecNameForUi)
                : null;
            var createPerPhaseAutoTimelineForUi = riAutoSelectedPhase > 0 && autoPhaseWindows.Count > 0;
            var bossPhaseStartEntries = GetAutoTimelineBossPhaseStartEntries(sourceTimelineForUi);
            var bossPhaseStartOptions = BuildAutoTimelineBossPhaseStartOptions(bossPhaseStartEntries);

            ImGui.TableNextColumn();

            if (createPerPhaseAutoTimelineForUi)
            {
                if (bossPhaseStartOptions.Count > 0)
                {
                    EnsureAutoTimelineBossPhaseSelections(
                        $"{autoEncounterIdForUi}_{autoSpecNameForUi}_{autoPhaseWindows.Count}_{bossPhaseStartOptions.Count}",
                        autoPhaseWindows,
                        bossPhaseStartEntries);

                    ImGui.Text("Phase Starts");
                    ImGui.TextDisabled("Choose the first boss attack shown in each split timeline after phase I.");

                    for (var phaseIndex = 1; phaseIndex < autoPhaseWindows.Count; phaseIndex++)
                    {
                        var selectionIndex = phaseIndex - 1;
                        var selectedBossIndex = selectionIndex < riAutoPhaseStartBossIndices.Count
                            ? riAutoPhaseStartBossIndices[selectionIndex]
                            : 0;
                        selectedBossIndex = Math.Clamp(selectedBossIndex, 0, Math.Max(0, bossPhaseStartOptions.Count - 1));
                        ImGui.SetNextItemWidth(340);
                        if (ImGui.Combo(
                                $"{autoPhaseWindows[phaseIndex].DisplayName}##riAutoPhaseStart{phaseIndex}",
                                ref selectedBossIndex,
                                bossPhaseStartOptions,
                                -1))
                        {
                            riAutoPhaseStartBossIndices[selectionIndex] = selectedBossIndex;
                        }
                    }
                }
                else
                {
                    ImGui.TextDisabled("No boss attack markers are available yet. Per-phase auto timelines will fall back to the FFLogs phase windows.");
                }
            }

            ImGui.EndTable();
        }

        var hasAutoSelection =
            riAutoSelectedZone >= 0 && riAutoSelectedZone < zones.Count &&
            riAutoSelectedEncounter >= 0 && riAutoSelectedEncounter < autoEncounters.Count &&
            riAutoSelectedSpec >= 0 && riAutoSelectedSpec < allSpecNames.Count;
        var autoEncounterId = hasAutoSelection ? autoEncounters[riAutoSelectedEncounter].Id : 0;
        var autoSpecName = hasAutoSelection ? allSpecNames[riAutoSelectedSpec] : string.Empty;
        var sourceTimeline = hasAutoSelection
            ? plugin.TimelineStore.GetTimeline(autoEncounterId, autoSpecName)
            : null;
        var resolvedAutoPhaseWindows = BuildEncounterPhaseWindows(sourceTimeline?.PhaseInfo);
        var createPerPhaseAutoTimeline = riAutoSelectedPhase > 0 && resolvedAutoPhaseWindows.Count > 0;
        var autoTimelineKey = hasAutoSelection
            ? TimelineDatabase.MakeKey(autoEncounterId, autoSpecName)
            : string.Empty;
        var autoTimelineFilterKey = hasAutoSelection ? TimelineDatabase.MakeKey(autoEncounterId, autoSpecName) : string.Empty;
        var customAutoPhaseWindows = createPerPhaseAutoTimeline
            ? BuildCustomAutoTimelinePhaseWindows(sourceTimeline, resolvedAutoPhaseWindows)
            : null;
        var autoPhaseConfigurationIsValid = !createPerPhaseAutoTimeline ||
                                            customAutoPhaseWindows != null ||
                                            sourceTimeline?.BossEntries.Count == 0;
        var canCreateAutoTimeline = hasAutoSelection &&
                                    !riFetching &&
                                    !riImporting &&
                                    sourceTimeline != null &&
                                    autoPhaseConfigurationIsValid;

        if (hasAutoSelection && sourceTimeline != null)
        {
            var autoTimelineSkillOptions = GetCachedAutoTimelineSkillFilterOptions(autoTimelineKey, sourceTimeline);
            var disabledAbilityIds = plugin.Configuration.AutoTimelineDisabledAbilities.GetValueOrDefault(autoTimelineFilterKey);
            if (disabledAbilityIds == null)
            {
                disabledAbilityIds = [];
                plugin.Configuration.AutoTimelineDisabledAbilities[autoTimelineFilterKey] = disabledAbilityIds;
            }

            ImGui.Text("Skill Filters");
            if (ImGui.Button("Enable All##riAutoSkillsEnable"))
            {
                disabledAbilityIds.Clear();
                RequestDeferredUiSettingsSave();
            }
            ImGui.SameLine();
            if (ImGui.Button("Disable All##riAutoSkillsDisable"))
            {
                disabledAbilityIds.Clear();
                foreach (var (abilityId, _) in autoTimelineSkillOptions)
                    disabledAbilityIds.Add(abilityId);
                RequestDeferredUiSettingsSave();
            }

            ImGui.BeginChild("##riAutoSkillFilters", new Vector2(0, 180), true);
            foreach (var (abilityId, abilityName) in autoTimelineSkillOptions)
            {
                var enabled = !disabledAbilityIds.Contains(abilityId);
                if (ImGui.Checkbox($"{abilityName}##riAutoSkill{abilityId}", ref enabled))
                {
                    if (enabled)
                        disabledAbilityIds.Remove(abilityId);
                    else
                        disabledAbilityIds.Add(abilityId);
                    RequestDeferredUiSettingsSave();
                }
            }
            ImGui.EndChild();

            ImGui.Spacing();
        }

        ImGui.Text("GCD Recast Time");
        ImGui.SetNextItemWidth(220);
        var autoTimelineGcdRecastSec = plugin.Configuration.AutoTimelineGcdRecastSec;
        if (ImGui.SliderFloat("##riAutoGcdRecast", ref autoTimelineGcdRecastSec, 2.0f, 2.5f, "%.2f s"))
        {
            plugin.Configuration.AutoTimelineGcdRecastSec = MathF.Round(autoTimelineGcdRecastSec * 100f) / 100f;
            InvalidateAutoTimelineSkillFilterCache();
            RequestDeferredUiSettingsSave();
        }

        ImGui.Text("DOT Recast Time");
        ImGui.SetNextItemWidth(220);
        var autoTimelineDotRefreshBufferSec = plugin.Configuration.AutoTimelineDotRefreshBufferSec;
        if (ImGui.SliderFloat("##riAutoDotRefresh", ref autoTimelineDotRefreshBufferSec, 0.0f, 15.0f, "%.1f s"))
        {
            plugin.Configuration.AutoTimelineDotRefreshBufferSec = MathF.Round(autoTimelineDotRefreshBufferSec * 10f) / 10f;
            RequestDeferredUiSettingsSave();
        }

        if (createPerPhaseAutoTimeline && sourceTimeline != null && sourceTimeline.BossEntries.Count > 0 && customAutoPhaseWindows == null)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), "Phase starts must move forward in time with one boss attack per split.");

        if (!canCreateAutoTimeline)
            ImGui.BeginDisabled();
        if (ImGui.Button("Create Auto Timeline##riAutoCreate"))
            CreateAutoTimelineFromFetchedLogs(autoEncounters[riAutoSelectedEncounter], autoSpecName, createPerPhaseAutoTimeline, customAutoPhaseWindows);
        if (!canCreateAutoTimeline)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            if (!hasAutoSelection)
            {
                ImGui.TextDisabled("Choose a valid fight and job first.");
            }
            else if (plugin.TimelineStore.GetTimeline(autoEncounterId, autoSpecName) == null)
            {
                ImGui.TextDisabled("No fetched logs are cached for this fight/job yet.");
                ImGui.TextDisabled("Use \"Update from FFLogs\" on the Encounter Timeline tab first.");
            }
            else
            {
                ImGui.Text("Build a custom Auto Timeline straight from cached raw FFLogs parse data.");
            }
            ImGui.EndTooltip();
        }
    }

    private void CreateBlankTimelineFromFetchedLogs(
        Encounter encounter,
        string specName,
        EncounterPhaseWindow? selectedPhaseWindow)
    {
        var sourceTimeline = plugin.TimelineStore.GetTimeline(encounter.Id, specName);
        RefreshTimelineRuntimeMetadata(sourceTimeline);
        RefreshEncounterTimelineAggregationFromCachedParses(sourceTimeline);
        if (sourceTimeline == null)
        {
            riStatus = $"No fetched logs are cached for {encounter.Name} / {specName}.";
            riStatusIsError = true;
            return;
        }

        var scopedSourceTimeline = BuildPhaseScopedTimeline(sourceTimeline, selectedPhaseWindow);
        if (scopedSourceTimeline == null)
        {
            riStatus = selectedPhaseWindow == null
                ? $"No fetched logs are cached for {encounter.Name} / {specName}."
                : $"No cached FFLogs data remains for {encounter.Name} / {specName} in phase {GetRomanNumeral(selectedPhaseWindow.Ordinal)}.";
            riStatusIsError = true;
            return;
        }

        var customTimeline = CloneTimeline(scopedSourceTimeline);
        customTimeline.Entries = [];

        var key = BuildUniqueBlankCustomTimelineKey(encounter.Id, specName, selectedPhaseWindow);
        plugin.CustomTimelineStore.SaveTimeline(plugin.Configuration, key, customTimeline);
        InvalidateCustomTimelineListCache();
        plugin.EncounterTracker.RebuildZoneMappings();

        SelectCustomTimeline(key, customTimeline);
        customEditorDirty = false;
        riStatus = selectedPhaseWindow == null
            ? $"Created blank timeline for {encounter.Name} / {specName} with {customTimeline.BossEntries.Count} boss attacks."
            : $"Created blank timeline for {encounter.Name} / {specName} / {GetRomanNumeral(selectedPhaseWindow.Ordinal)} with {customTimeline.BossEntries.Count} boss attacks.";
        riStatusIsError = false;
    }

    private AggregatedTimeline? GetFetchedBossAttackSourceTimeline(AggregatedTimeline? timeline)
    {
        if (timeline == null ||
            timeline.EncounterId == 0 ||
            string.IsNullOrWhiteSpace(timeline.SpecName))
            return null;

        var sourceTimeline = plugin.TimelineStore.GetTimeline(timeline.EncounterId, timeline.SpecName);
        if (sourceTimeline == null ||
            sourceTimeline.CachedFflogsParses.Count == 0 ||
            sourceTimeline.BossEntries.Count == 0)
            return null;

        return sourceTimeline;
    }

    private void GenerateBossAttacksFromFetchedLogs(AggregatedTimeline targetTimeline, AggregatedTimeline sourceTimeline)
    {
        var stitchedBossEntries = BuildGeneratedBossAttackTimeline(sourceTimeline);
        if (stitchedBossEntries.Count == 0)
        {
            SetEiStatus($"No cached boss attacks are available for {sourceTimeline.EncounterName} / {sourceTimeline.SpecName}.", true);
            return;
        }

        int addedCount;
        double insertedDeadSpaceSec;
        if (targetTimeline.BossEntries.Count == 0)
        {
            targetTimeline.BossEntries = stitchedBossEntries
                .Select(CloneBossTimelineEntry)
                .ToList();
            targetTimeline.DeadSpaceRanges.Clear();
            addedCount = targetTimeline.BossEntries.Count;
            insertedDeadSpaceSec = 0.0;
        }
        else
        {
            addedCount = MergeGeneratedBossAttacksIntoTimeline(targetTimeline, stitchedBossEntries, out insertedDeadSpaceSec);
        }

        var maxBossTimeSec = targetTimeline.BossEntries
            .Select(entry => Math.Max(entry.CastStartSec, entry.CastEndSec))
            .DefaultIfEmpty(0.0)
            .Max();
        var maxActionTimeSec = targetTimeline.Entries
            .Select(entry => entry.TimeOffsetSec)
            .DefaultIfEmpty(0.0)
            .Max();
        if (maxBossTimeSec * 1000.0 > targetTimeline.AverageDurationMs)
        {
            targetTimeline.AverageDurationMs = Math.Max(maxBossTimeSec, maxActionTimeSec) * 1000.0;
            if (ReferenceEquals(targetTimeline, editingTimeline))
                editDurationSec = (float)(targetTimeline.AverageDurationMs / 1000.0);
        }

        MarkCustomEditorModified();
        SetEiStatus(insertedDeadSpaceSec > 0.0005
            ? $"Added {addedCount} boss attack{(addedCount == 1 ? string.Empty : "s")} from fetched FFLogs data and inserted {insertedDeadSpaceSec:F3}s of dead space."
            : $"Added {addedCount} boss attack{(addedCount == 1 ? string.Empty : "s")} from fetched FFLogs data.");
    }

    private List<BossTimelineEntry> BuildGeneratedBossAttackTimeline(AggregatedTimeline sourceTimeline)
    {
        var cachedBossParses = sourceTimeline.CachedFflogsParses
            .Where(parse => parse.BossEntries.Count > 0)
            .OrderBy(parse => parse.ParseIndex)
            .ToList();
        if (cachedBossParses.Count == 0)
        {
            return sourceTimeline.BossEntries
                .Select(CloneBossTimelineEntry)
                .ToList();
        }

        var phaseWindows = sourceTimeline.PhaseInfo != null &&
                           sourceTimeline.EncounterId == sourceTimeline.PhaseInfo.EncounterId
            ? BuildEncounterPhaseWindows(sourceTimeline.PhaseInfo)
            : [];
        if (phaseWindows.Count == 0)
        {
            return cachedBossParses
                .OrderByDescending(parse => parse.DurationSec)
                .ThenBy(parse => parse.ParseIndex)
                .Select(parse => parse.BossEntries.Select(CloneBossTimelineEntry).ToList())
                .FirstOrDefault()
                ?? sourceTimeline.BossEntries.Select(CloneBossTimelineEntry).ToList();
        }

        var stitchedBossEntries = new List<BossTimelineEntry>();
        var cumulativeOffsetSec = 0.0;

        foreach (var phaseWindow in phaseWindows)
        {
            CachedFflogsParseTimeline? bestParse = null;
            EncounterPhaseWindow? bestParseWindow = null;
            double bestPhaseDurationSec = double.NegativeInfinity;

            foreach (var parse in cachedBossParses)
            {
                var parseWindow = BuildEncounterPhaseWindows(parse.PhaseInfo ?? sourceTimeline.PhaseInfo)
                    .FirstOrDefault(window => window.Ordinal == phaseWindow.Ordinal);
                if (parseWindow == null)
                    continue;

                var phaseDurationSec = Math.Max(0.0, (parseWindow.EndMs - parseWindow.StartMs) / 1000.0);
                if (phaseDurationSec <= bestPhaseDurationSec)
                    continue;

                bestParse = parse;
                bestParseWindow = parseWindow;
                bestPhaseDurationSec = phaseDurationSec;
            }

            if (bestParse == null || bestParseWindow == null)
                continue;

            var phaseEntries = SlicePhaseBossEntries(bestParse.BossEntries, bestParseWindow)
                .Select(entry =>
                {
                    var clone = CloneBossTimelineEntry(entry);
                    clone.CastStartSec = Math.Round(clone.CastStartSec + cumulativeOffsetSec, 3, MidpointRounding.AwayFromZero);
                    clone.CastEndSec = Math.Round(clone.CastEndSec + cumulativeOffsetSec, 3, MidpointRounding.AwayFromZero);
                    return clone;
                });
            stitchedBossEntries.AddRange(phaseEntries);
            cumulativeOffsetSec += bestPhaseDurationSec;
        }

        return stitchedBossEntries.Count > 0
            ? stitchedBossEntries
            : sourceTimeline.BossEntries.Select(CloneBossTimelineEntry).ToList();
    }

    private static List<BossTimelineEntry> BuildSingleParseBossAttackTimeline(AggregatedTimeline sourceTimeline)
    {
        var parseBossEntries = sourceTimeline.CachedFflogsParses
            .OrderBy(parse => parse.ParseIndex)
            .Select(parse => parse.BossEntries)
            .FirstOrDefault(entries => entries.Count > 0);
        if (parseBossEntries != null)
            return parseBossEntries.Select(CloneBossTimelineEntry).ToList();

        return sourceTimeline.BossEntries
            .Select(CloneBossTimelineEntry)
            .ToList();
    }

    private int MergeGeneratedBossAttacksIntoTimeline(
        AggregatedTimeline targetTimeline,
        IReadOnlyList<BossTimelineEntry> sourceBossEntries,
        out double insertedDeadSpaceSec)
    {
        insertedDeadSpaceSec = 0.0;

        var orderedTargetBossEntries = targetTimeline.BossEntries
            .OrderBy(entry => entry.CastStartSec)
            .ThenBy(entry => entry.AbilityName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var orderedSourceBossEntries = sourceBossEntries
            .OrderBy(entry => entry.CastStartSec)
            .ThenBy(entry => entry.AbilityName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var matches = MatchBossEntriesInOrder(orderedTargetBossEntries, orderedSourceBossEntries);

        targetTimeline.DeadSpaceRanges.Clear();

        if (matches.Count > 1)
        {
            for (var matchIndex = 1; matchIndex < matches.Count; matchIndex++)
            {
                var previousTargetEntry = orderedTargetBossEntries[matches[matchIndex - 1].TargetIndex];
                var currentTargetEntry = orderedTargetBossEntries[matches[matchIndex].TargetIndex];
                var previousSourceEntry = orderedSourceBossEntries[matches[matchIndex - 1].SourceIndex];
                var currentSourceEntry = orderedSourceBossEntries[matches[matchIndex].SourceIndex];
                var existingIntervalSec = currentTargetEntry.CastStartSec - previousTargetEntry.CastStartSec;
                var sourceIntervalSec = currentSourceEntry.CastStartSec - previousSourceEntry.CastStartSec;
                var deltaSec = Math.Round(sourceIntervalSec - existingIntervalSec, 3, MidpointRounding.AwayFromZero);
                if (deltaSec <= 0.0005)
                    continue;

                var insertionBoundarySec = currentTargetEntry.CastStartSec;
                ShiftTimelineAfter(targetTimeline, insertionBoundarySec, deltaSec);
                targetTimeline.DeadSpaceRanges.Add(new DeadSpaceRange
                {
                    StartSec = insertionBoundarySec,
                    EndSec = insertionBoundarySec + deltaSec,
                });
                insertedDeadSpaceSec += deltaSec;
            }

            targetTimeline.DeadSpaceRanges = MergeDeadSpaceRanges(targetTimeline.DeadSpaceRanges);
        }

        var matchedSourceIndices = matches
            .Select(match => match.SourceIndex)
            .ToHashSet();
        var addedCount = 0;

        for (var sourceIndex = 0; sourceIndex < orderedSourceBossEntries.Count; sourceIndex++)
        {
            if (matchedSourceIndices.Contains(sourceIndex))
                continue;

            var sourceEntry = orderedSourceBossEntries[sourceIndex];
            var resolvedStartSec = ResolveInsertedBossEntryTime(orderedTargetBossEntries, orderedSourceBossEntries, matches, sourceIndex);
            var durationSec = Math.Max(0.0, sourceEntry.CastEndSec - sourceEntry.CastStartSec);
            if (targetTimeline.BossEntries.Any(existingEntry =>
                    BossEntriesMatchForStitching(existingEntry, sourceEntry) &&
                    Math.Abs(existingEntry.CastStartSec - resolvedStartSec) <= 0.05))
            {
                continue;
            }

            targetTimeline.BossEntries.Add(new BossTimelineEntry
            {
                AbilityId = sourceEntry.AbilityId,
                AbilityName = sourceEntry.AbilityName,
                SourceId = sourceEntry.SourceId,
                IsPrimaryBoss = sourceEntry.IsPrimaryBoss,
                CastStartSec = Math.Round(resolvedStartSec, 3, MidpointRounding.AwayFromZero),
                CastEndSec = Math.Round(resolvedStartSec + durationSec, 3, MidpointRounding.AwayFromZero),
            });
            addedCount++;
        }

        targetTimeline.BossEntries = targetTimeline.BossEntries
            .OrderBy(entry => entry.CastStartSec)
            .ThenBy(entry => entry.AbilityName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return addedCount;
    }

    private static List<(int TargetIndex, int SourceIndex)> MatchBossEntriesInOrder(
        IReadOnlyList<BossTimelineEntry> targetEntries,
        IReadOnlyList<BossTimelineEntry> sourceEntries)
    {
        var matches = new List<(int TargetIndex, int SourceIndex)>();
        var nextSourceIndex = 0;

        for (var targetIndex = 0; targetIndex < targetEntries.Count; targetIndex++)
        {
            for (var sourceIndex = nextSourceIndex; sourceIndex < sourceEntries.Count; sourceIndex++)
            {
                if (!BossEntriesMatchForStitching(targetEntries[targetIndex], sourceEntries[sourceIndex]))
                    continue;

                matches.Add((targetIndex, sourceIndex));
                nextSourceIndex = sourceIndex + 1;
                break;
            }
        }

        return matches;
    }

    private static bool BossEntriesMatchForStitching(BossTimelineEntry left, BossTimelineEntry right)
    {
        if (left.AbilityId > 0 && right.AbilityId > 0 && left.AbilityId == right.AbilityId)
            return true;

        return string.Equals(
            NormalizeImportedAbilityName(left.AbilityName),
            NormalizeImportedAbilityName(right.AbilityName),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void ShiftTimelineAfter(AggregatedTimeline timeline, double boundarySec, double deltaSec)
    {
        foreach (var entry in timeline.Entries.Where(entry => entry.TimeOffsetSec >= boundarySec))
            entry.TimeOffsetSec = Math.Round(entry.TimeOffsetSec + deltaSec, 3, MidpointRounding.AwayFromZero);

        foreach (var bossEntry in timeline.BossEntries.Where(entry => entry.CastStartSec >= boundarySec))
        {
            bossEntry.CastStartSec = Math.Round(bossEntry.CastStartSec + deltaSec, 3, MidpointRounding.AwayFromZero);
            bossEntry.CastEndSec = Math.Round(bossEntry.CastEndSec + deltaSec, 3, MidpointRounding.AwayFromZero);
        }
    }

    private static double ResolveInsertedBossEntryTime(
        IReadOnlyList<BossTimelineEntry> targetEntries,
        IReadOnlyList<BossTimelineEntry> sourceEntries,
        IReadOnlyList<(int TargetIndex, int SourceIndex)> matches,
        int sourceIndex)
    {
        var previousMatchIndex = -1;
        for (var matchIndex = matches.Count - 1; matchIndex >= 0; matchIndex--)
        {
            if (matches[matchIndex].SourceIndex >= sourceIndex)
                continue;

            previousMatchIndex = matchIndex;
            break;
        }

        if (previousMatchIndex >= 0)
        {
            var previousMatch = matches[previousMatchIndex];
            var previousTargetEntry = targetEntries[previousMatch.TargetIndex];
            var previousSourceEntry = sourceEntries[previousMatch.SourceIndex];
            return previousTargetEntry.CastStartSec + (sourceEntries[sourceIndex].CastStartSec - previousSourceEntry.CastStartSec);
        }

        for (var matchIndex = 0; matchIndex < matches.Count; matchIndex++)
        {
            if (matches[matchIndex].SourceIndex <= sourceIndex)
                continue;

            var nextMatch = matches[matchIndex];
            var nextTargetEntry = targetEntries[nextMatch.TargetIndex];
            var nextSourceEntry = sourceEntries[nextMatch.SourceIndex];
            return nextTargetEntry.CastStartSec - (nextSourceEntry.CastStartSec - sourceEntries[sourceIndex].CastStartSec);
        }

        return sourceEntries[sourceIndex].CastStartSec;
    }

    private static List<DeadSpaceRange> MergeDeadSpaceRanges(IEnumerable<DeadSpaceRange> ranges)
    {
        var orderedRanges = ranges
            .Where(range => range.EndSec > range.StartSec)
            .OrderBy(range => range.StartSec)
            .ToList();
        if (orderedRanges.Count == 0)
            return [];

        var mergedRanges = new List<DeadSpaceRange> { CloneDeadSpaceRange(orderedRanges[0]) };
        for (var rangeIndex = 1; rangeIndex < orderedRanges.Count; rangeIndex++)
        {
            var currentRange = orderedRanges[rangeIndex];
            var lastRange = mergedRanges[^1];
            if (currentRange.StartSec <= lastRange.EndSec + 0.0005)
            {
                lastRange.EndSec = Math.Max(lastRange.EndSec, currentRange.EndSec);
                continue;
            }

            mergedRanges.Add(CloneDeadSpaceRange(currentRange));
        }

        return mergedRanges;
    }

    private void ImportBossAttacksFromCsvClipboard(AggregatedTimeline targetTimeline)
    {
        try
        {
            var text = ImGui.GetClipboardText();
            if (string.IsNullOrWhiteSpace(text))
            {
                SetEiStatus("Clipboard is empty.", true);
                return;
            }

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                SetEiStatus("No data in clipboard.", true);
                return;
            }

            var lineIdx = 0;
            if (lines[lineIdx].TrimStart().StartsWith("# ATKTip ", StringComparison.Ordinal))
                lineIdx++;

            if (lineIdx < lines.Length &&
                lines[lineIdx].TrimStart('"').StartsWith("Time", StringComparison.OrdinalIgnoreCase))
                lineIdx++;

            var importedBossEntries = new List<BossTimelineEntry>();
            foreach (var rawLine in lines.Skip(lineIdx))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                var cols = SplitCsvLine(line);
                if (!TryParseImportedBossCsvRow(cols, out var bossEntry, allowPlainCastRows: true))
                    continue;

                importedBossEntries.Add(bossEntry);
            }

            if (importedBossEntries.Count == 0)
            {
                SetEiStatus("No valid boss attack entries found in clipboard.", true);
                return;
            }

            targetTimeline.BossEntries.AddRange(importedBossEntries);

            var maxBossTimeSec = targetTimeline.BossEntries
                .Select(entry => Math.Max(entry.CastStartSec, entry.CastEndSec))
                .DefaultIfEmpty(0.0)
                .Max();
            if (maxBossTimeSec * 1000.0 > targetTimeline.AverageDurationMs)
            {
                targetTimeline.AverageDurationMs = maxBossTimeSec * 1000.0;
                if (ReferenceEquals(targetTimeline, editingTimeline))
                    editDurationSec = (float)(targetTimeline.AverageDurationMs / 1000.0);
            }

            MarkCustomEditorModified();
            SetEiStatus($"Imported {importedBossEntries.Count} boss attack{(importedBossEntries.Count == 1 ? string.Empty : "s")} from clipboard.");
        }
        catch (Exception ex)
        {
            SetEiStatus($"Boss attack import failed: {ex.Message}", true);
        }
    }

    private void RemoveAllBossAttacks(AggregatedTimeline targetTimeline)
    {
        if (targetTimeline.BossEntries.Count == 0)
        {
            SetEiStatus("No boss attacks to remove.", true);
            return;
        }

        var removedCount = targetTimeline.BossEntries.Count;
        targetTimeline.BossEntries.Clear();
        targetTimeline.DeadSpaceRanges.Clear();
        if (editingEntryIsBoss)
            editingEntryIndex = -1;
        MarkCustomEditorModified();
        SetEiStatus($"Removed {removedCount} boss attack{(removedCount == 1 ? string.Empty : "s")} from this custom timeline.");
    }

    private void StartReportFetch()
    {
        var m = Regex.Match(riUrl, @"fflogs\.com/reports/([A-Za-z0-9]+)");
        if (!m.Success)
        {
            riStatus        = "Invalid FFLogs URL. Expected: fflogs.com/reports/REPORTCODE";
            riStatusIsError = true;
            return;
        }

        riReportCode    = m.Groups[1].Value;
        riStatus        = string.Empty;
        riStatusIsError = false;
        riFlights        = [];
        riPlayers        = [];
        riAbilityLookup  = [];
        riSelectedFight  = -1;
        riSelectedPlayer = -1;
        riSelectedPhase  = 0;
        riFetching       = true;

        riCts?.Cancel();
        riCts = new CancellationTokenSource();
        var ct   = riCts.Token;
        var code = riReportCode;

        Task.Run(async () =>
        {
            try
            {
                var (fights, players, abilities) = await plugin.FFLogsClient.GetReportInfoAsync(code, ct);
                riFlights        = fights;
                riPlayers        = players;
                riAbilityLookup  = abilities;
                riStatus         = fights.Count == 0
                    ? "No kills found in this report."
                    : $"Found {fights.Count} kill(s) and {players.Count} player(s).";
                riStatusIsError  = fights.Count == 0;
                if (fights.Count == 1) riSelectedFight = 0;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                riStatus        = $"Error: {ex.Message}";
                riStatusIsError = true;
            }
            finally { riFetching = false; }
        });
    }

    private void StartReportImport()
    {
        if (riSelectedFight  < 0 || riSelectedFight  >= riFlights.Count)  return;
        if (riSelectedPlayer < 0 || riSelectedPlayer >= riPlayers.Count)   return;

        var fight         = riFlights[riSelectedFight];
        var player        = riPlayers[riSelectedPlayer];
        var code          = riReportCode;
        var abilityLookup = riAbilityLookup;
        var jobDisplay    = SplitCamelCase(player.SubType);
        var selectedPhaseWindow = GetSelectedPhaseWindow(
            BuildEncounterPhaseWindows(CreateFightPhaseInfo(fight)),
            riSelectedPhase);

        riImporting     = true;
        riStatus        = "Fetching cast events...";
        riStatusIsError = false;

        riCts?.Cancel();
        riCts = new CancellationTokenSource();
        var ct = riCts.Token;

        Task.Run(async () =>
        {
            try
            {
                var events = await plugin.FFLogsClient.GetCastEventsForPlayerAsync(
                    code, fight.Id, fight.StartTime, fight.EndTime, player.Id, abilityLookup, ct);

                if (events.Count == 0)
                {
                    riStatus        = "No cast events found for that player.";
                    riStatusIsError = true;
                    return;
                }

                var rawBossEvents = await plugin.FFLogsClient.GetBossCastEventsAsync(
                    code,
                    fight.Id,
                    ct);
                var bossEntries = plugin.Aggregator.AggregateBossEvents(rawBossEvents, CreateFightPhaseInfo(fight));

                var timeline = BuildImportedTimeline(
                    fight,
                    jobDisplay,
                    code,
                    events,
                    bossEntries,
                    selectedPhaseWindow);
                if (timeline == null)
                {
                    riStatus = selectedPhaseWindow == null
                        ? "No cast events found for that player."
                        : $"No cast events found inside phase {GetRomanNumeral(selectedPhaseWindow.Ordinal)}.";
                    riStatusIsError = true;
                    return;
                }

                var key = BuildImportedTimelineKey(fight.EncounterId, jobDisplay, selectedPhaseWindow);
                plugin.CustomTimelineStore.SaveTimeline(plugin.Configuration, key, timeline);

                InvalidateCustomTimelineListCache();

                var phaseLabel = selectedPhaseWindow == null
                    ? string.Empty
                    : $" / {GetRomanNumeral(selectedPhaseWindow.Ordinal)}";
                riStatus        = $"Saved \"{fight.Name}{phaseLabel} / {player.Name} ({jobDisplay})\" ({timeline.Entries.Count} casts, {timeline.BossEntries.Count} boss attacks).";
                riStatusIsError = false;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                riStatus        = $"Import error: {ex.Message}";
                riStatusIsError = true;
            }
            finally { riImporting = false; }
        });
    }

    private void RebuildSkillVisibility(AggregatedTimeline tl)
    {
        var key = TimelineDatabase.MakeKey(tl.EncounterId, tl.SpecName);
        skillVisibility.Clear();
        var hidden = plugin.Configuration.HiddenAbilities.GetValueOrDefault(key);
        foreach (var (id, _) in tl.Entries.Select(e => (e.AbilityId, e.AbilityName)).Distinct())
            skillVisibility[id] = hidden == null || !hidden.Contains(id);
    }

    private void CreateAutoTimelineFromFetchedLogs(
        Encounter encounter,
        string specName,
        bool createPerPhaseTimelines,
        IReadOnlyList<EncounterPhaseWindow>? customPhaseWindows)
    {
        var sourceTimeline = plugin.TimelineStore.GetTimeline(encounter.Id, specName);
        RefreshTimelineRuntimeMetadata(sourceTimeline);
        RefreshEncounterTimelineAggregationFromCachedParses(sourceTimeline);
        if (sourceTimeline == null)
        {
            riStatus = $"No fetched logs are cached for {encounter.Name} / {specName}.";
            riStatusIsError = true;
            return;
        }

        var rawSourceEntries = ApplyAutoTimelineAbilityFilters(
            sourceTimeline,
            BuildAutoTimelineSourceFromCachedParses(sourceTimeline));
        if (rawSourceEntries.Count == 0)
        {
            riStatus = $"No usable raw FFLogs source entries remain for {encounter.Name} / {specName}. Re-enable some Auto Timeline skills or fetch logs again.";
            riStatusIsError = true;
            return;
        }

        var fullTimelineKey = BuildCustomTimelineSaveKey(encounter.Id, specName, null);
        var customTimeline = CloneTimeline(sourceTimeline);
        customTimeline.Entries = [];
        customTimeline.AutoTimelineSourceEntries = rawSourceEntries;
        customTimeline.BossEntries = BuildSingleParseBossAttackTimeline(sourceTimeline);
        customTimeline.DeadSpaceRanges.Clear();
        ApplyAutoTimeline(customTimeline);
        RemoveStoredCustomPhaseTimelines(encounter.Id, specName);

        var phaseWindows = customPhaseWindows?.Count > 0
            ? customPhaseWindows.ToList()
            : BuildEncounterPhaseWindows(sourceTimeline.PhaseInfo);
        if (!createPerPhaseTimelines)
            GenerateBossAttacksFromFetchedLogs(customTimeline, sourceTimeline);

        plugin.CustomTimelineStore.SaveTimeline(plugin.Configuration, fullTimelineKey, customTimeline);
        if (createPerPhaseTimelines)
        {
            foreach (var phaseWindow in phaseWindows)
            {
                var phaseTimeline = BuildPhaseTimelineFromBuiltTimeline(encounter.Id, customTimeline, phaseWindow);
                if (phaseTimeline == null)
                    continue;

                var phaseSourceTimeline = BuildPhaseScopedTimeline(sourceTimeline, phaseWindow);
                if (phaseSourceTimeline != null)
                {
                    phaseTimeline.BossEntries = BuildSingleParseBossAttackTimeline(phaseSourceTimeline);
                    phaseTimeline.DeadSpaceRanges.Clear();
                    GenerateBossAttacksFromFetchedLogs(phaseTimeline, phaseSourceTimeline);
                }

                var phaseKey = BuildCustomTimelineSaveKey(encounter.Id, specName, phaseWindow);
                plugin.CustomTimelineStore.SaveTimeline(plugin.Configuration, phaseKey, phaseTimeline);
            }
        }

        InvalidateCustomTimelineListCache();

        if (createPerPhaseTimelines)
        {
            var selectedPhaseWindow = phaseWindows.FirstOrDefault();
            var selectedPhaseKey = selectedPhaseWindow == null
                ? string.Empty
                : BuildCustomTimelineSaveKey(encounter.Id, specName, selectedPhaseWindow);
            if (!string.IsNullOrWhiteSpace(selectedPhaseKey) &&
                plugin.Configuration.CustomTimelines.TryGetValue(selectedPhaseKey, out var selectedPhaseTimeline))
                SelectCustomTimeline(selectedPhaseKey, selectedPhaseTimeline);
            else
                SelectCustomTimeline(fullTimelineKey, customTimeline);
        }
        else
        {
            SelectCustomTimeline(fullTimelineKey, customTimeline);
        }

        customEditorDirty = false;
        riStatus = createPerPhaseTimelines && phaseWindows.Count > 0
            ? $"Created Auto Timeline for {encounter.Name} / {specName} and split it into {phaseWindows.Count} phase timelines."
            : $"Created Auto Timeline for {encounter.Name} / {specName} from cached raw FFLogs data.";
        riStatusIsError = false;
    }

    private List<(int AbilityId, string AbilityName)> GetAutoTimelineSkillFilterOptions(AggregatedTimeline? sourceTimeline)
    {
        if (sourceTimeline == null)
            return [];

        RefreshTimelineRuntimeMetadata(sourceTimeline);

        var sourceEntries = sourceTimeline.AutoTimelineSourceEntries.Count > 0
            ? sourceTimeline.AutoTimelineSourceEntries
            : BuildAutoTimelineSourceFromCachedParses(sourceTimeline);

        return sourceEntries
            .Where(entry => entry.AbilityId > 7 && !string.IsNullOrWhiteSpace(entry.AbilityName))
            .Select(entry => (entry.AbilityId, entry.AbilityName))
            .Distinct()
            .OrderBy(entry => entry.AbilityName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<TimelineEntry> ApplyAutoTimelineAbilityFilters(
        AggregatedTimeline? timeline,
        IEnumerable<TimelineEntry> entries)
    {
        if (timeline == null)
            return entries.Select(CloneTimelineEntry).ToList();

        var timelineKey = TimelineDatabase.MakeKey(timeline.EncounterId, timeline.SpecName);
        var disabledAbilityIds = plugin.Configuration.AutoTimelineDisabledAbilities.GetValueOrDefault(timelineKey);
        if (disabledAbilityIds is not { Count: > 0 })
            return entries.Select(CloneTimelineEntry).ToList();

        return entries
            .Where(entry => !disabledAbilityIds.Contains(entry.AbilityId))
            .Select(CloneTimelineEntry)
            .ToList();
    }

    private List<(int AbilityId, string AbilityName)> GetCachedAutoTimelineSkillFilterOptions(
        string timelineKey,
        AggregatedTimeline? sourceTimeline)
    {
        if (sourceTimeline == null || string.IsNullOrWhiteSpace(timelineKey))
            return [];

        var currentSourceCount = sourceTimeline.AutoTimelineSourceEntries?.Count ?? 0;
        var currentParseCount = sourceTimeline.CachedFflogsParses.Count;
        if (!autoTimelineSkillFilterCacheDirty &&
            string.Equals(cachedAutoTimelineSkillFilterKey, timelineKey, StringComparison.OrdinalIgnoreCase) &&
            cachedAutoTimelineSkillFilterSourceCount == currentSourceCount &&
            cachedAutoTimelineSkillFilterParseCount == currentParseCount)
        {
            return cachedAutoTimelineSkillFilterOptions;
        }

        cachedAutoTimelineSkillFilterOptions.Clear();
        cachedAutoTimelineSkillFilterOptions.AddRange(GetAutoTimelineSkillFilterOptions(sourceTimeline));
        cachedAutoTimelineSkillFilterKey = timelineKey;
        cachedAutoTimelineSkillFilterSourceCount = currentSourceCount;
        cachedAutoTimelineSkillFilterParseCount = currentParseCount;
        autoTimelineSkillFilterCacheDirty = false;
        return cachedAutoTimelineSkillFilterOptions;
    }

    private void InvalidateAutoTimelineSkillFilterCache()
    {
        autoTimelineSkillFilterCacheDirty = true;
        cachedAutoTimelineSkillFilterKey = string.Empty;
        cachedAutoTimelineSkillFilterSourceCount = -1;
        cachedAutoTimelineSkillFilterParseCount = -1;
        cachedAutoTimelineSkillFilterOptions.Clear();
    }

    private sealed class EncounterPhaseWindow
    {
        public int Ordinal { get; set; }
        public int PhaseId { get; set; }
        public string SourceName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public long StartMs { get; set; }
        public long EndMs { get; set; }
    }

    private static List<BossTimelineEntry> GetAutoTimelineBossPhaseStartEntries(AggregatedTimeline? sourceTimeline)
    {
        if (sourceTimeline == null)
            return [];

        return sourceTimeline.BossEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.AbilityName))
            .OrderBy(entry => entry.CastStartSec)
            .ThenBy(entry => entry.AbilityId)
            .GroupBy(entry => $"{entry.AbilityId}:{entry.AbilityName}:{Math.Round(entry.CastStartSec, 3):F3}")
            .Select(group => group.First())
            .ToList();
    }

    private static List<string> BuildAutoTimelineBossPhaseStartOptions(IReadOnlyList<BossTimelineEntry> bossPhaseStartEntries)
    {
        return bossPhaseStartEntries
            .Select(entry => $"{FormatCsvTime(entry.CastStartSec)}  {entry.AbilityName}")
            .ToList();
    }

    private void EnsureAutoTimelineBossPhaseSelections(
        string selectionKey,
        IReadOnlyList<EncounterPhaseWindow> phaseWindows,
        IReadOnlyList<BossTimelineEntry> bossPhaseStartEntries)
    {
        if (string.Equals(riAutoPhaseStartSelectionKey, selectionKey, StringComparison.Ordinal) &&
            riAutoPhaseStartBossIndices.Count == Math.Max(0, phaseWindows.Count - 1))
            return;

        riAutoPhaseStartSelectionKey = selectionKey;
        riAutoPhaseStartBossIndices.Clear();

        if (bossPhaseStartEntries.Count == 0 || phaseWindows.Count <= 1)
            return;

        for (var phaseIndex = 1; phaseIndex < phaseWindows.Count; phaseIndex++)
        {
            var phaseWindow = phaseWindows[phaseIndex];
            var defaultIndex = -1;
            for (var bossIndex = 0; bossIndex < bossPhaseStartEntries.Count; bossIndex++)
            {
                var entryStartMs = bossPhaseStartEntries[bossIndex].CastStartSec * 1000.0;
                if (entryStartMs >= phaseWindow.StartMs && entryStartMs < phaseWindow.EndMs)
                {
                    defaultIndex = bossIndex;
                    break;
                }
            }
            if (defaultIndex < 0)
                defaultIndex = Math.Min(phaseIndex, bossPhaseStartEntries.Count - 1);
            riAutoPhaseStartBossIndices.Add(defaultIndex);
        }
    }

    private List<EncounterPhaseWindow>? BuildCustomAutoTimelinePhaseWindows(
        AggregatedTimeline? sourceTimeline,
        IReadOnlyList<EncounterPhaseWindow> fallbackPhaseWindows)
    {
        if (sourceTimeline == null || fallbackPhaseWindows.Count == 0)
            return null;

        var bossEntries = GetAutoTimelineBossPhaseStartEntries(sourceTimeline);
        if (fallbackPhaseWindows.Count == 1)
            return fallbackPhaseWindows.ToList();

        if (bossEntries.Count == 0 || riAutoPhaseStartBossIndices.Count != fallbackPhaseWindows.Count - 1)
            return null;

        var fightDurationMs = Math.Max(
            (long)Math.Round(sourceTimeline.AverageDurationMs),
            (long)Math.Ceiling((bossEntries.LastOrDefault()?.CastEndSec ?? 0.0) * 1000.0));
        var customWindows = new List<EncounterPhaseWindow>(fallbackPhaseWindows.Count);
        var previousBossIndex = -1;

        for (var phaseIndex = 0; phaseIndex < fallbackPhaseWindows.Count; phaseIndex++)
        {
            var startMs = phaseIndex == 0
                ? 0L
                : (long)Math.Round(bossEntries[riAutoPhaseStartBossIndices[phaseIndex - 1]].CastStartSec * 1000.0);
            var endMs = phaseIndex + 1 < fallbackPhaseWindows.Count
                ? (long)Math.Round(bossEntries[riAutoPhaseStartBossIndices[phaseIndex]].CastStartSec * 1000.0)
                : fightDurationMs;
            if (phaseIndex > 0)
            {
                var selectedBossIndex = riAutoPhaseStartBossIndices[phaseIndex - 1];
                if (selectedBossIndex < 0 || selectedBossIndex >= bossEntries.Count || selectedBossIndex <= previousBossIndex)
                    return null;
                previousBossIndex = selectedBossIndex;
            }

            if (endMs <= startMs)
                return null;

            var fallbackWindow = fallbackPhaseWindows[phaseIndex];
            customWindows.Add(new EncounterPhaseWindow
            {
                Ordinal = fallbackWindow.Ordinal,
                PhaseId = fallbackWindow.PhaseId,
                SourceName = fallbackWindow.SourceName,
                DisplayName = fallbackWindow.DisplayName,
                StartMs = startMs,
                EndMs = endMs,
            });

        }

        return customWindows;
    }

    private static FightPhaseInfo CreateFightPhaseInfo(ReportFight fight)
    {
        return new FightPhaseInfo
        {
            EncounterId = fight.EncounterId,
            EncounterName = fight.Name,
            FightStartTime = fight.StartTime,
            FightEndTime = fight.EndTime,
            LastPhase = fight.LastPhase,
            LastPhaseAsAbsoluteIndex = fight.LastPhaseAsAbsoluteIndex,
            LastPhaseIsIntermission = fight.LastPhaseIsIntermission,
            PhaseTransitions = fight.PhaseTransitions
                .Select(transition => new FightPhaseTransition
                {
                    Id = transition.Id,
                    StartTime = transition.StartTime,
                })
                .ToList(),
            PhaseMetadata = fight.PhaseMetadata
                .Select(phase => new EncounterPhaseMetadata
                {
                    Id = phase.Id,
                    Name = phase.Name,
                    IsIntermission = phase.IsIntermission,
                })
                .ToList(),
        };
    }

    private static List<EncounterPhaseWindow> BuildEncounterPhaseWindows(FightPhaseInfo? phaseInfo)
    {
        if (phaseInfo == null || phaseInfo.PhaseTransitions.Count == 0)
            return [];

        var metadataById = phaseInfo.PhaseMetadata
            .ToDictionary(phase => phase.Id);
        var orderedTransitions = phaseInfo.PhaseTransitions
            .OrderBy(transition => transition.StartTime)
            .ToList();
        var windows = new List<EncounterPhaseWindow>();

        for (var index = 0; index < orderedTransitions.Count; index++)
        {
            var transition = orderedTransitions[index];
            if (!metadataById.TryGetValue(transition.Id, out var metadata))
            {
                metadata = new EncounterPhaseMetadata
                {
                    Id = transition.Id,
                    Name = $"Phase {transition.Id}",
                };
            }

            var nextStartTime = index + 1 < orderedTransitions.Count
                ? orderedTransitions[index + 1].StartTime
                : phaseInfo.FightEndTime;
            var startMs = Math.Max(0L, transition.StartTime - phaseInfo.FightStartTime);
            var endMs = Math.Max(startMs, nextStartTime - phaseInfo.FightStartTime);
            if (endMs <= startMs || metadata.IsIntermission)
                continue;

            var phaseOrdinal = windows.Count + 1;
            windows.Add(new EncounterPhaseWindow
            {
                Ordinal = phaseOrdinal,
                PhaseId = transition.Id,
                SourceName = metadata.Name,
                DisplayName = GetRomanNumeral(phaseOrdinal),
                StartMs = startMs,
                EndMs = endMs,
            });
        }

        return windows;
    }

    private static List<string> BuildPhaseDropdownOptions(IReadOnlyList<EncounterPhaseWindow> phaseWindows)
    {
        var labels = new List<string> { "Full Fight" };
        labels.AddRange(phaseWindows.Select(window => window.DisplayName));
        return labels;
    }

    private static EncounterPhaseWindow? GetSelectedPhaseWindow(
        IReadOnlyList<EncounterPhaseWindow> phaseWindows,
        int selectedPhaseIndex)
    {
        if (selectedPhaseIndex <= 0)
            return null;

        var phaseIndex = selectedPhaseIndex - 1;
        return phaseIndex >= 0 && phaseIndex < phaseWindows.Count
            ? phaseWindows[phaseIndex]
            : null;
    }

    private static FightPhaseInfo? CloneFightPhaseInfo(FightPhaseInfo? phaseInfo)
    {
        if (phaseInfo == null)
            return null;

        return new FightPhaseInfo
        {
            EncounterId = phaseInfo.EncounterId,
            EncounterName = phaseInfo.EncounterName,
            FightStartTime = phaseInfo.FightStartTime,
            FightEndTime = phaseInfo.FightEndTime,
            LastPhase = phaseInfo.LastPhase,
            LastPhaseAsAbsoluteIndex = phaseInfo.LastPhaseAsAbsoluteIndex,
            LastPhaseIsIntermission = phaseInfo.LastPhaseIsIntermission,
            PhaseTransitions = phaseInfo.PhaseTransitions
                .Select(transition => new FightPhaseTransition
                {
                    Id = transition.Id,
                    StartTime = transition.StartTime,
                })
                .ToList(),
            PhaseMetadata = phaseInfo.PhaseMetadata
                .Select(phase => new EncounterPhaseMetadata
                {
                    Id = phase.Id,
                    Name = phase.Name,
                    IsIntermission = phase.IsIntermission,
                })
                .ToList(),
        };
    }

    private static AggregatedTimeline? BuildPhaseScopedTimeline(
        AggregatedTimeline? sourceTimeline,
        EncounterPhaseWindow? phaseWindow)
    {
        if (sourceTimeline == null)
            return null;

        var scopedTimeline = CloneTimeline(sourceTimeline);
        if (phaseWindow == null)
            return scopedTimeline;

        var fallbackStartSec = phaseWindow.StartMs / 1000.0;
        var fallbackEndSec = phaseWindow.EndMs / 1000.0;
        var scopedParses = sourceTimeline.CachedFflogsParses
            .Select(parse =>
            {
                var parsePhaseWindow = BuildEncounterPhaseWindows(parse.PhaseInfo ?? sourceTimeline.PhaseInfo)
                    .FirstOrDefault(window => window.Ordinal == phaseWindow.Ordinal);
                var startSec = parsePhaseWindow?.StartMs / 1000.0 ?? fallbackStartSec;
                var endSec = parsePhaseWindow?.EndMs / 1000.0 ?? fallbackEndSec;
                var scopedEntries = parse.Entries
                    .Where(entry => entry.TimeOffsetSec < endSec)
                    .Select(entry => CloneTimelineEntryWithOffset(entry, startSec))
                    .OrderBy(entry => entry.TimeOffsetSec)
                    .ThenByDescending(entry => entry.Frequency)
                    .ToList();
                var hasVisiblePhaseEntries = parse.Entries.Any(entry =>
                    entry.TimeOffsetSec >= startSec &&
                    entry.TimeOffsetSec < endSec);
                return new CachedFflogsParseTimeline
                {
                    ParseIndex = parse.ParseIndex,
                    ReportCode = parse.ReportCode,
                    FightId = parse.FightId,
                    RankingAmount = parse.RankingAmount,
                    DurationSec = Math.Max(0.0, endSec - startSec),
                    Entries = scopedEntries,
                    BossEntries = parsePhaseWindow == null
                        ? parse.BossEntries.Select(CloneBossTimelineEntry).ToList()
                        : SlicePhaseBossEntries(parse.BossEntries, parsePhaseWindow),
                    PhaseInfo = CloneFightPhaseInfo(parse.PhaseInfo ?? sourceTimeline.PhaseInfo),
                    HasVisiblePhaseEntries = hasVisiblePhaseEntries,
                };
            })
            .Where(parse => parse.HasVisiblePhaseEntries)
            .ToList();

        if (scopedParses.Count == 0)
            return null;

        scopedTimeline.EncounterName = $"{sourceTimeline.EncounterName} {phaseWindow.DisplayName}";
        scopedTimeline.AverageDurationMs = phaseWindow.EndMs - phaseWindow.StartMs;
        scopedTimeline.ParseCount = Math.Max(1, scopedParses.Count);
        scopedTimeline.CachedFflogsParses = scopedParses;
        scopedTimeline.AutoTimelineSourceEntries = [];
        scopedTimeline.Entries = [];
        scopedTimeline.BossEntries = SlicePhaseBossEntries(sourceTimeline.BossEntries, phaseWindow);
        scopedTimeline.DeadSpaceRanges = SlicePhaseDeadSpaceRanges(sourceTimeline.DeadSpaceRanges, phaseWindow);
        scopedTimeline.PhaseInfo = CloneFightPhaseInfo(sourceTimeline.PhaseInfo);
        return scopedTimeline;
    }

    private AggregatedTimeline? BuildImportedTimeline(
        ReportFight fight,
        string specName,
        string reportCode,
        List<CastEvent> events,
        List<BossTimelineEntry> bossEntries,
        EncounterPhaseWindow? phaseWindow)
    {
        var scopedEvents = phaseWindow == null
            ? events
            : SlicePhaseCasts(events, phaseWindow);
        if (scopedEvents.Count == 0)
            return null;

        var entries = BuildExactTimelineEntries(scopedEvents);
        return new AggregatedTimeline
        {
            EncounterId = fight.EncounterId,
            EncounterName = phaseWindow == null ? fight.Name : $"{fight.Name} {phaseWindow.DisplayName}",
            SpecName = specName,
            AverageDurationMs = phaseWindow == null ? fight.DurationMs : phaseWindow.EndMs - phaseWindow.StartMs,
            ParseCount = 1,
            Entries = entries,
            AutoTimelineSourceEntries = entries
                .Select(CloneTimelineEntry)
                .ToList(),
            CachedFflogsParses =
            [
                new CachedFflogsParseTimeline
                {
                    ParseIndex = 1,
                    ReportCode = reportCode,
                    FightId = fight.Id,
                    RankingAmount = 0.0,
                    DurationSec = (phaseWindow == null ? fight.DurationMs : phaseWindow.EndMs - phaseWindow.StartMs) / 1000.0,
                    Entries = entries
                        .Select(CloneTimelineEntry)
                        .ToList(),
                    BossEntries = phaseWindow == null
                        ? bossEntries.Select(CloneBossTimelineEntry).ToList()
                        : SlicePhaseBossEntries(bossEntries, phaseWindow),
                    PhaseInfo = CloneFightPhaseInfo(CreateFightPhaseInfo(fight)),
                }
            ],
            BossEntries = phaseWindow == null
                ? bossEntries.Select(CloneBossTimelineEntry).ToList()
                : SlicePhaseBossEntries(bossEntries, phaseWindow),
            DeadSpaceRanges = [],
            PhaseInfo = CloneFightPhaseInfo(CreateFightPhaseInfo(fight)),
        };
    }

    private static string BuildImportedTimelineKey(
        int encounterId,
        string specName,
        EncounterPhaseWindow? phaseWindow)
    {
        var baseKey = BuildCustomTimelineSaveKey(encounterId, specName, phaseWindow);
        return $"report_{baseKey}";
    }

    private static string BuildAutoTimelinePhaseScopedKey(int encounterId, string specName, int selectedPhaseIndex)
    {
        var baseKey = TimelineDatabase.MakeKey(encounterId, specName);
        return selectedPhaseIndex <= 0
            ? baseKey
            : $"{baseKey}_p{selectedPhaseIndex}";
    }

    private static string BuildCustomTimelineSaveKey(
        int encounterId,
        string specName,
        EncounterPhaseWindow? phaseWindow)
    {
        var baseKey = TimelineDatabase.MakeKey(encounterId, specName);
        return phaseWindow == null
            ? baseKey
            : $"{baseKey}_p{phaseWindow.Ordinal}";
    }

    private string BuildUniqueBlankCustomTimelineKey(
        int encounterId,
        string specName,
        EncounterPhaseWindow? phaseWindow)
    {
        var baseKey = $"{BuildCustomTimelineSaveKey(encounterId, specName, phaseWindow)}_blank";
        if (!plugin.Configuration.CustomTimelines.ContainsKey(baseKey))
            return baseKey;

        var suffix = 2;
        while (plugin.Configuration.CustomTimelines.ContainsKey($"{baseKey}_{suffix}"))
            suffix++;

        return $"{baseKey}_{suffix}";
    }

    private static List<CastEvent> SlicePhaseCasts(IEnumerable<CastEvent> casts, EncounterPhaseWindow window)
    {
        return casts
            .Where(cast => cast.Timestamp >= window.StartMs && cast.Timestamp < window.EndMs)
            .Select(cast => new CastEvent
            {
                Timestamp = cast.Timestamp - window.StartMs,
                AbilityGameID = cast.AbilityGameID,
                AbilityName = cast.AbilityName,
                AbilityIcon = cast.AbilityIcon,
                Type = cast.Type,
            })
            .ToList();
    }

    private static List<BossTimelineEntry> SlicePhaseBossEntries(
        IEnumerable<BossTimelineEntry> bossEntries,
        EncounterPhaseWindow window)
    {
        var startSec = window.StartMs / 1000.0;
        var endSec = window.EndMs / 1000.0;

        return bossEntries
            .Where(entry => entry.CastStartSec >= startSec && entry.CastStartSec < endSec)
            .Select(entry => new BossTimelineEntry
            {
                AbilityId = entry.AbilityId,
                AbilityName = entry.AbilityName,
                CastStartSec = Math.Max(0.0, entry.CastStartSec - startSec),
                CastEndSec = Math.Max(0.0, entry.CastEndSec - startSec),
                SourceId = entry.SourceId,
                IsPrimaryBoss = entry.IsPrimaryBoss,
            })
            .ToList();
    }

    private static List<DeadSpaceRange> SlicePhaseDeadSpaceRanges(
        IEnumerable<DeadSpaceRange> deadSpaceRanges,
        EncounterPhaseWindow window)
    {
        var startSec = window.StartMs / 1000.0;
        var endSec = window.EndMs / 1000.0;

        return deadSpaceRanges
            .Select(range => new DeadSpaceRange
            {
                StartSec = Math.Max(startSec, range.StartSec),
                EndSec = Math.Min(endSec, range.EndSec),
            })
            .Where(range => range.EndSec > range.StartSec)
            .Select(range => new DeadSpaceRange
            {
                StartSec = Math.Max(0.0, range.StartSec - startSec),
                EndSec = Math.Max(0.0, range.EndSec - startSec),
            })
            .ToList();
    }

    private static List<TimelineEntry> SlicePhaseTimelineEntries(
        IEnumerable<TimelineEntry> entries,
        EncounterPhaseWindow window)
    {
        var startSec = window.StartMs / 1000.0;
        var endSec = window.EndMs / 1000.0;

        return entries
            .Where(entry => entry.TimeOffsetSec >= startSec && entry.TimeOffsetSec < endSec)
            .Select(entry =>
            {
                var clone = CloneTimelineEntry(entry);
                clone.TimeOffsetSec -= startSec;
                return clone;
            })
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private static AggregatedTimeline? BuildPhaseTimelineFromBuiltTimeline(
        int baseEncounterId,
        AggregatedTimeline builtTimeline,
        EncounterPhaseWindow phaseWindow)
    {
        var phaseEntries = SlicePhaseTimelineEntries(builtTimeline.Entries, phaseWindow);
        if (phaseEntries.Count == 0)
            return null;

        var phaseTimeline = CloneTimeline(builtTimeline);
        phaseTimeline.EncounterId = GetEncounterPhaseId(baseEncounterId, phaseWindow.Ordinal);
        phaseTimeline.EncounterName = $"{builtTimeline.EncounterName} {phaseWindow.DisplayName}";
        phaseTimeline.AverageDurationMs = phaseWindow.EndMs - phaseWindow.StartMs;
        phaseTimeline.Entries = phaseEntries;
        phaseTimeline.AutoTimelineSourceEntries = SlicePhaseTimelineEntries(builtTimeline.AutoTimelineSourceEntries, phaseWindow);
        phaseTimeline.BossEntries = SlicePhaseBossEntries(builtTimeline.BossEntries, phaseWindow);
        phaseTimeline.DeadSpaceRanges = SlicePhaseDeadSpaceRanges(builtTimeline.DeadSpaceRanges, phaseWindow);
        phaseTimeline.PhaseInfo = CloneFightPhaseInfo(builtTimeline.PhaseInfo);
        return phaseTimeline;
    }

    private void RemoveStoredCustomPhaseTimelines(int encounterId, string specName)
    {
        var baseKey = TimelineDatabase.MakeKey(encounterId, specName);
        var keysToRemove = plugin.Configuration.CustomTimelines.Keys
            .Where(key => key.StartsWith($"{baseKey}_p", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (keysToRemove.Count == 0)
            return;

        foreach (var key in keysToRemove)
        {
            plugin.CustomTimelineStore.RemoveTimeline(plugin.Configuration, key);
            plugin.Configuration.RemoveTimelineReferences(key);
        }

        plugin.SaveTimelineUserState();
    }

    private void RemoveStoredEncounterPhaseTimelines(int baseEncounterId, string specName)
    {
        var encounterIdsToRemove = plugin.TimelineStore.GetAllTimelines()
            .Where(timeline => string.Equals(timeline.SpecName, specName, StringComparison.OrdinalIgnoreCase))
            .Where(timeline => TryGetBaseEncounterIdFromPhaseEncounterId(timeline.EncounterId, out var phaseBaseEncounterId) &&
                               phaseBaseEncounterId == baseEncounterId)
            .Select(timeline => timeline.EncounterId)
            .Distinct()
            .ToList();

        foreach (var encounterId in encounterIdsToRemove)
            plugin.TimelineStore.RemoveTimeline(encounterId, specName);
    }

    private List<TimelineEntry> BuildExactTimelineEntries(IEnumerable<CastEvent> events)
    {
        return events
            .OrderBy(e => e.Timestamp)
            .Select(e =>
            {
                var info = plugin.RecastDatabase.Lookup(e.AbilityGameID, e.AbilityName);
                return new TimelineEntry
                {
                    TimeOffsetSec = e.Timestamp / 1000.0,
                    AbilityId = info != null ? (int)info.AbilityId : e.AbilityGameID,
                    AbilityName = !string.IsNullOrWhiteSpace(info?.Name) ? info.Name : e.AbilityName,
                    AbilityIcon = e.AbilityIcon,
                    Frequency = 1.0,
                    AverageUses = 1.0,
                    IsGcd = info?.IsGcdAction ?? false,
                };
            })
            .ToList();
    }

    private List<TimelineEntry> BuildAutoTimelineSourceFromCachedParses(AggregatedTimeline tl)
    {
        if (tl.CachedFflogsParses.Count == 0)
            return [];
        var configuredGcdRecastSec = GetConfiguredAutoTimelineGcdRecastSec();
        var exactParses = new List<IReadOnlyList<TimelineEntry>>();

        foreach (var parse in tl.CachedFflogsParses
                     .Where(parse => parse.Entries.Count > 0)
                     .OrderBy(parse => parse.ParseIndex))
        {
            var orderedEntries = parse.Entries
                .OrderBy(entry => entry.TimeOffsetSec)
                .ThenByDescending(entry => entry.Frequency)
                .ToList();
            if (orderedEntries.Count == 0)
                continue;

            var parseEntries = new List<TimelineEntry>(orderedEntries.Count);

            foreach (var entry in orderedEntries)
            {
                if (entry.AbilityId <= 7)
                    continue;

                var (abilityId, abilityName, abilityIcon, isGcdAction) = NormalizeAutoTimelineSourceEntry(entry);
                parseEntries.Add(new TimelineEntry
                {
                    TimeOffsetSec = entry.TimeOffsetSec,
                    AbilityId = abilityId,
                    AbilityName = abilityName,
                    AbilityIcon = abilityIcon,
                    Frequency = 1.0,
                    AverageUses = 1.0,
                    IsGcd = isGcdAction,
                });
            }

            exactParses.Add(parseEntries);
        }

        return AutoTimelineSourceBuilder.BuildFromExactParses(exactParses, tl.CachedFflogsParses.Count, configuredGcdRecastSec);
    }

    private (int abilityId, string abilityName, string abilityIcon, bool isGcdAction)
        NormalizeAutoTimelineSourceEntry(TimelineEntry entry)
    {
        var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        return (
            info != null ? (int)info.AbilityId : entry.AbilityId,
            !string.IsNullOrWhiteSpace(info?.Name) ? info.Name : entry.AbilityName,
            !string.IsNullOrWhiteSpace(entry.AbilityIcon) ? entry.AbilityIcon : string.Empty,
            info?.IsGcdAction ?? entry.IsGcd);
    }

    private void RefreshEncounterTimelineAggregationFromCachedParses(AggregatedTimeline? tl)
    {
        if (tl == null || !NeedsEncounterTimelineAggregationRefresh(tl))
            return;

        var rebuiltSourceEntries = BuildAutoTimelineSourceFromCachedParses(tl);
        if (rebuiltSourceEntries.Count == 0)
            return;

        tl.AutoTimelineSourceEntries = rebuiltSourceEntries
            .Select(CloneTimelineEntry)
            .ToList();
        tl.Entries = TimelineEntryCondenser.Condense(
            rebuiltSourceEntries,
            Math.Max(tl.ParseCount, tl.CachedFflogsParses.Count),
            plugin.RecastDatabase);
        RefreshTimelineRuntimeMetadata(tl);
        plugin.TimelineStore.SaveTimeline(tl);
        InvalidateAutoTimelineSkillFilterCache();
    }

    private bool NeedsEncounterTimelineAggregationRefresh(AggregatedTimeline tl)
    {
        if (tl.CachedFflogsParses.Count == 0)
            return false;

        if (tl.AutoTimelineSourceEntries.Count == 0)
            return true;

        if (tl.AutoTimelineSourceEntries.Any(entry => entry.AverageUses > 1.001))
            return true;

        return !AutoTimelineSourceBuilder.UsesFixedSlotAggregation(tl.AutoTimelineSourceEntries, GetConfiguredAutoTimelineGcdRecastSec());
    }

    private float GetAbilityThreshold(int abilityId)
    {
        if (currentTimeline == null)
            return plugin.Configuration.OverlayFreqThreshold;

        var key = TimelineDatabase.MakeKey(currentTimeline.EncounterId, currentTimeline.SpecName);
        if (plugin.Configuration.AbilityFreqThresholds.TryGetValue(key, out var perAbility) &&
            perAbility.TryGetValue(abilityId, out var custom))
            return custom;

        return plugin.Configuration.OverlayFreqThreshold;
    }


    // ── Fight / job selectors ──

    private void DrawSelectors()
    {
        // If we haven't loaded selectors and have no cached DB data, show guidance
        if (!selectorsLoaded && zones.Count == 0)
        {
            var db = plugin.TimelineStore.Load();
            RebuildSelectorsFromDb(db);
        }

        if (zones.Count == 0 || allSpecNames.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(plugin.Configuration.FFLogsClientId))
            {
                ImGui.TextDisabled("Set your FFLogs API credentials in Config to get started.");
            }
            else if (selectorsError != null)
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"Error: {selectorsError}");
                if (ImGui.Button("Retry"))
                {
                    selectorsError = null;
                    isFetchingSelectors = true;
                    Task.Run(FetchSelectorsAsync);
                }
            }
            else if (!selectorsLoaded)
            {
                // Credentials may have been saved after the window was already open.
                // Kick off the fetch here if nothing is running yet.
                if (!isFetchingSelectors)
                {
                    isFetchingSelectors = true;
                    Task.Run(FetchSelectorsAsync);
                }
                ImGui.TextDisabled("Loading fight and job lists from FFLogs...");
            }
            else
            {
                ImGui.TextDisabled("No data available. Check your API credentials in Config.");
            }
            return;
        }

        // Zone selector
        var zoneNames = zones.Select(z => z.Name).ToList();
        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("Zone", ref selectedZone, zoneNames, -1))
        {
            selectedEncounter = 0;
            currentTimeline = null;
            skillVisibility.Clear();
            HideEmbeddedTimelinePreview();
        }

        ImGui.SameLine();

        // Encounter (Fight) selector
        var encounters = selectedZone < zones.Count ? zones[selectedZone].Encounters : [];
        var encounterNames = encounters.Select(e => e.Name).ToList();
        ImGui.SetNextItemWidth(200);
        if (encounterNames.Count > 0)
        {
            if (ImGui.Combo("Fight", ref selectedEncounter, encounterNames, -1))
            {
                currentTimeline = null;
                skillVisibility.Clear();
                HideEmbeddedTimelinePreview();
            }
        }
        else
        {
            var noFights = 0;
            ImGui.Combo("Fight", ref noFights, new List<string> { "(none)" }, -1);
        }

        ImGui.SameLine();

        // Job selector
        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("Job", ref selectedSpec, allSpecNames, -1))
        {
            currentTimeline = null;
            skillVisibility.Clear();
            HideEmbeddedTimelinePreview();
        }

        // Load button — loads from local cache only (no API call)
        // Determine availability and reason for disabled state
        bool canLoad;
        string loadDisabledReason;
        if (!selectorsLoaded || zones.Count == 0 || allSpecNames.Count == 0)
        {
            canLoad = false;
            loadDisabledReason = "No fight list loaded yet.\nUse \"Update from FFLogs\" to load it first.";
        }
        else if (selectedZone < zones.Count &&
                 selectedEncounter < zones[selectedZone].Encounters.Count &&
                 selectedSpec < allSpecNames.Count)
        {
            var chkEncId = zones[selectedZone].Encounters[selectedEncounter].Id;
            var chkSpec  = allSpecNames[selectedSpec];
            var chkKey   = TimelineDatabase.MakeKey(chkEncId, chkSpec);
            canLoad = plugin.TimelineStore.GetTimeline(chkEncId, chkSpec) != null;
            loadDisabledReason = canLoad ? string.Empty
                : $"No cached data for:\n  {zones[selectedZone].Encounters[selectedEncounter].Name} / {chkSpec}\n\nUse \"Update from FFLogs\" to fetch it.";
        }
        else
        {
            canLoad = false;
            loadDisabledReason = "Make a valid fight and job selection first.";
        }

        if (!canLoad) ImGui.BeginDisabled();
        if (ImGui.Button("Load Fetched Logs", default))
            LoadTimeline();
        if (!canLoad) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            if (canLoad)
            {
                ImGui.Text("Load from local cache (previously fetched data).");
                ImGui.Text("Use Update from FFLogs to fetch fresh data from FFLogs.");
            }
            else
            {
                ImGui.TextDisabled(loadDisabledReason);
            }
            ImGui.EndTooltip();
        }

        ImGui.SameLine();

        // Update from FFLogs — same row as Load Fetched Logs
        var canUpdate = !isUpdating && selectorsLoaded && zones.Count > 0 && allSpecNames.Count > 0;
        if (!canUpdate) ImGui.BeginDisabled();
        if (ImGui.Button("Update from FFLogs", default))
        {
            StartUpdate();
        }
        if (!canUpdate) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("Fetch top 100 parses for the selected fight and job.");
            ImGui.Text("This may take a minute depending on the fight.");
            ImGui.EndTooltip();
        }

        ImGui.SameLine();

        var hasTimeline = currentTimeline != null;
        if (ImGui.Button("Preview Timeline", default) && hasTimeline)
        {
            showEmbeddedTimelinePreview = !showEmbeddedTimelinePreview;
            if (!showEmbeddedTimelinePreview)
                plugin.OverlayWindow.ResetEmbeddedPreview();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            if (hasTimeline)
                ImGui.Text("Preview the timeline in-tab (starts paused).");
            else
                ImGui.Text("Load a timeline first to preview the overlay.");
            ImGui.EndTooltip();
        }

        if (showEmbeddedTimelinePreview && hasTimeline)
        {
            var previewHeight = MathF.Max(164f, plugin.Configuration.OverlayIconSize * 2.6f + 42f);
            if (ImGui.GetContentRegionAvail().X >= 320f)
                ImGui.SameLine();

            showEmbeddedTimelinePreview = plugin.OverlayWindow.DrawEmbeddedPreview(
                currentTimeline!,
                new Vector2(0f, previewHeight),
                "##EncounterTimelineEmbeddedPreview");
        }
    }

    private void ClearTimeline()
    {
        // Clear from local disk cache
        if (selectedZone < zones.Count && allSpecNames.Count > 0 && selectedSpec < allSpecNames.Count)
        {
            var encounters = zones[selectedZone].Encounters;
            if (selectedEncounter < encounters.Count)
            {
                var encounterId = encounters[selectedEncounter].Id;
                var specName = allSpecNames[selectedSpec];
                plugin.TimelineStore.RemoveTimeline(encounterId, specName);

                // Also remove from custom timelines
                var key = TimelineDatabase.MakeKey(encounterId, specName);
                plugin.CustomTimelineStore.RemoveTimeline(plugin.Configuration, key);
                plugin.Configuration.RemoveTimelineReferences(key);
                plugin.SaveTimelineUserState();
                InvalidateCustomTimelineListCache();

                updateStatus = $"Cleared cached data for {encounters[selectedEncounter].Name} / {specName}.";
            }
        }

        currentTimeline = null;
        skillVisibility.Clear();
        HideEmbeddedTimelinePreview();
    }

    private void DeleteCustomTimeline(string key)
    {
        plugin.CustomTimelineStore.RemoveTimeline(plugin.Configuration, key);
        plugin.Configuration.RemoveTimelineReferences(key);
        plugin.SaveTimelineUserState();
        InvalidateCustomTimelineListCache();

        if (string.Equals(selectedCustomKey, key, StringComparison.OrdinalIgnoreCase))
        {
            selectedCustomKey = null;
            editingTimeline = null;
            customEditorDirty = false;
            ClearCustomEditorCaches();
        }
    }

    private void RebuildSelectorsFromDb(TimelineDatabase db)
    {
        var zoneMap = new Dictionary<int, Zone>();
        var specs = new HashSet<string>();

        foreach (var (key, tl) in db.Timelines)
        {
            if (!zoneMap.ContainsKey(tl.EncounterId))
            {
                zoneMap[tl.EncounterId] = new Zone
                {
                    Id = tl.EncounterId,
                    Name = tl.EncounterName,
                    Encounters = [new Encounter { Id = tl.EncounterId, Name = tl.EncounterName }],
                };
            }
            specs.Add(tl.SpecName);
        }

        if (zones.Count == 0 && zoneMap.Count > 0)
            zones = [.. zoneMap.Values.OrderBy(z => z.Name)];
        if (allSpecNames.Count == 0 && specs.Count > 0)
            allSpecNames = [.. specs.OrderBy(s => s)];
    }

    private void MergeStoredSyntheticPhaseEncountersIntoSelectors()
    {
        if (zones.Count == 0)
            return;

        var phaseEncounters = plugin.TimelineStore.GetAllTimelines()
            .Concat(plugin.Configuration.CustomTimelines.Values)
            .Where(timeline => timeline.EncounterId > 0)
            .Select(timeline => (timeline.EncounterId, timeline.EncounterName))
            .Distinct()
            .Where(entry => TryGetBaseEncounterIdFromPhaseEncounterId(entry.EncounterId, out var baseEncounterId) &&
                            baseEncounterId != entry.EncounterId)
            .OrderBy(entry => entry.EncounterName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var (encounterId, encounterName) in phaseEncounters)
        {
            if (!TryGetBaseEncounterIdFromPhaseEncounterId(encounterId, out var baseEncounterId))
                continue;

            foreach (var zone in zones.Where(zone => zone.Encounters.Any(encounter => encounter.Id == baseEncounterId)))
            {
                if (zone.Encounters.Any(encounter => encounter.Id == encounterId))
                    continue;

                zone.Encounters.Add(new Encounter
                {
                    Id = encounterId,
                    Name = encounterName,
                });

                zone.Encounters = zone.Encounters
                    .OrderBy(encounter => encounter.Id == baseEncounterId ? 0 : 1)
                    .ThenBy(encounter => encounter.Id)
                    .ToList();
            }
        }
    }

    private static int GetEncounterPhaseId(int baseEncounterId, int phaseOrdinal)
    {
        if (phaseOrdinal <= 1)
            return baseEncounterId;

        if (LegacyGeneratedEncounterPhaseIds.TryGetValue(baseEncounterId, out var legacyPhaseIds) &&
            legacyPhaseIds.TryGetValue(phaseOrdinal, out var legacyEncounterId))
        {
            return legacyEncounterId;
        }

        return SyntheticPhaseEncounterIdBase + (baseEncounterId * 100) + phaseOrdinal;
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

    private IEnumerable<(string Key, AggregatedTimeline Timeline)> GetLinkableCustomTimelineCandidates(string sourceKey, AggregatedTimeline sourceTimeline)
    {
        if (!TryGetBaseEncounterIdFromPhaseEncounterId(sourceTimeline.EncounterId, out var sourceBaseEncounterId) ||
            sourceBaseEncounterId <= 0)
        {
            yield break;
        }

        var candidates = plugin.Configuration.CustomTimelines
            .Where(entry => !string.Equals(entry.Key, sourceKey, StringComparison.Ordinal))
            .Where(entry => string.Equals(entry.Value.SpecName, sourceTimeline.SpecName, StringComparison.OrdinalIgnoreCase))
            .Where(entry => TryGetBaseEncounterIdFromPhaseEncounterId(entry.Value.EncounterId, out var candidateBaseEncounterId) &&
                            candidateBaseEncounterId == sourceBaseEncounterId)
            .OrderBy(entry => BuildTimelineLinkLabel(entry.Key, entry.Value), StringComparer.OrdinalIgnoreCase);

        foreach (var (candidateKey, candidateTimeline) in candidates)
        {
            if (!WouldCreateTimelineLinkCycle(sourceKey, candidateKey))
                yield return (candidateKey, candidateTimeline);
        }
    }

    private void SetCustomTimelineNextLink(string sourceKey, string targetKey)
    {
        var conflictingSources = plugin.Configuration.TimelineNextLinks
            .Where(entry => !string.Equals(entry.Key, sourceKey, StringComparison.Ordinal) &&
                            string.Equals(entry.Value, targetKey, StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .ToList();

        foreach (var conflictingSource in conflictingSources)
            plugin.Configuration.TimelineNextLinks.Remove(conflictingSource);

        plugin.Configuration.TimelineNextLinks[sourceKey] = targetKey;
        plugin.SaveTimelineUserState();
    }

    private bool WouldCreateTimelineLinkCycle(string sourceKey, string targetKey)
    {
        if (string.Equals(sourceKey, targetKey, StringComparison.Ordinal))
            return true;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var currentKey = targetKey;
        while (!string.IsNullOrWhiteSpace(currentKey) && visited.Add(currentKey))
        {
            if (string.Equals(currentKey, sourceKey, StringComparison.Ordinal))
                return true;

            if (!plugin.Configuration.TimelineNextLinks.TryGetValue(currentKey, out currentKey!))
                break;
        }

        return false;
    }

    private static string BuildTimelineLinkLabel(string key, AggregatedTimeline timeline)
    {
        if (TryGetBaseEncounterIdFromPhaseEncounterId(timeline.EncounterId, out var baseEncounterId) &&
            TryGetPhaseOrdinalFromEncounterId(timeline.EncounterId, baseEncounterId, out var encounterPhaseOrdinal))
        {
            return $"{timeline.EncounterName} {GetRomanNumeral(encounterPhaseOrdinal)}";
        }

        var phaseMatch = Regex.Match(key, @"_p(?<phase>\d+)$", RegexOptions.IgnoreCase);
        if (phaseMatch.Success &&
            int.TryParse(phaseMatch.Groups["phase"].Value, out var keyPhaseOrdinal) &&
            keyPhaseOrdinal > 0)
        {
            return $"{timeline.EncounterName} {GetRomanNumeral(keyPhaseOrdinal)}";
        }

        return $"{timeline.EncounterName} [{key}]";
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

    private static string GetEncounterPhaseName(string encounterName, int phaseOrdinal)
        => phaseOrdinal <= 1
            ? encounterName
            : $"{encounterName} {GetRomanNumeral(phaseOrdinal)}";

    private static string GetRomanNumeral(int value)
    {
        return value switch
        {
            1 => "I",
            2 => "II",
            3 => "III",
            4 => "IV",
            5 => "V",
            6 => "VI",
            7 => "VII",
            8 => "VIII",
            9 => "IX",
            10 => "X",
            _ => value.ToString(),
        };
    }

    private void LoadTimeline()
    {
        if (selectedZone >= zones.Count || allSpecNames.Count == 0)
            return;

        var encounters = zones[selectedZone].Encounters;
        if (selectedEncounter >= encounters.Count)
            return;

        var encounterId = encounters[selectedEncounter].Id;
        var specName = allSpecNames[selectedSpec];

        HideEmbeddedTimelinePreview();
        currentTimeline = plugin.TimelineStore.GetTimeline(encounterId, specName);
        RefreshTimelineRuntimeMetadata(currentTimeline);
        RefreshEncounterTimelineAggregationFromCachedParses(currentTimeline);

        // Build skill visibility map — includes all abilities; threshold filtering is handled at draw time
        skillVisibility.Clear();
        if (currentTimeline != null)
        {
            var key = TimelineDatabase.MakeKey(currentTimeline.EncounterId, currentTimeline.SpecName);
            var hidden = plugin.Configuration.HiddenAbilities.GetValueOrDefault(key);
            var uniqueAbilities = currentTimeline.Entries
                .Select(e => (e.AbilityId, e.AbilityName))
                .Distinct()
                .OrderBy(a => a.AbilityName);

            foreach (var (id, _) in uniqueAbilities)
                skillVisibility[id] = hidden == null || !hidden.Contains(id);
        }
        else
        {
            updateStatus = "No cached data for this fight/job. Click Update Logs to fetch it.";
        }
    }

    // ── Skill filters ──

    private void DrawSkillFilters()
    {
        if (currentTimeline == null)
            return;

        EnsureEncounterTimelineCaches();

        if (!ImGui.TreeNode("Skill Filters"))
            return;

        var cfg = plugin.Configuration;
        var timelineKey = TimelineDatabase.MakeKey(currentTimeline.EncounterId, currentTimeline.SpecName);

        if (ImGui.Button("Show All GCDs"))
        {
            foreach (var id in cachedTimelineGcdIds) skillVisibility[id] = true;
            SaveSkillFilters();
        }
        ImGui.SameLine();
        if (ImGui.Button("Hide All GCDs"))
        {
            foreach (var id in cachedTimelineGcdIds) skillVisibility[id] = false;
            SaveSkillFilters();
        }
        ImGui.SameLine();
        if (ImGui.Button("Show All oGCDs"))
        {
            foreach (var id in cachedTimelineOgcdIds) skillVisibility[id] = true;
            SaveSkillFilters();
        }
        ImGui.SameLine();
        if (ImGui.Button("Hide All oGCDs"))
        {
            foreach (var id in cachedTimelineOgcdIds) skillVisibility[id] = false;
            SaveSkillFilters();
        }
        ImGui.SameLine();
        if (ImGui.Button("Reset Thresholds", default))
        {
            cfg.AbilityFreqThresholds.Remove(timelineKey);
            InvalidateEncounterTimelineCaches();
            plugin.OverlayWindow.InvalidateTimelineCaches();
            RequestDeferredConfigSave();
        }

        ImGui.Separator();

        cfg.AbilityFreqThresholds.TryGetValue(timelineKey, out var perAbility);

        // Fixed-height scrollable child so the list doesn't take over the screen
        var listHeight = Math.Min(cachedTimelineUniqueAbilities.Count * 22f + 8f, 220f);
        ImGui.BeginChild("##SkillFilterList", new Vector2(0, listHeight), false);

        foreach (var (id, name) in cachedTimelineUniqueAbilities)
        {
            var hasCustom = perAbility != null && perAbility.ContainsKey(id);
            var thresh    = GetAbilityThreshold(id);
            var isOpen    = expandedSkillNodes.Contains(id);

            // Visibility checkbox — auto-save on every change so state survives reloads
            var visible = skillVisibility.GetValueOrDefault(id, true);
            if (ImGui.Checkbox($"##{id}_vis", ref visible))
            {
                skillVisibility[id] = visible;
                SaveSkillFilters();
            }
            ImGui.SameLine();

            // Selectable label — activates on mouse-UP so slider drags never interfere
            var arrow     = isOpen ? "v" : ">";
            var indicator = hasCustom ? $"  [{thresh * 100:F0}%]" : $"  ({thresh * 100:F0}%)";
            if (ImGui.Selectable($"{arrow} {name}{indicator}##{id}_sel", isOpen,
                ImGuiSelectableFlags.None, default))
            {
                if (isOpen) expandedSkillNodes.Remove(id);
                else        expandedSkillNodes.Add(id);
            }

            // Slider row — only visible when expanded
            if (isOpen)
            {
                ImGui.Indent(28f);
                var threshVal = thresh;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat($"##thresh_{id}", ref threshVal, 0f, 1f, $"{threshVal * 100:F0}%%"))
                {
                    if (!cfg.AbilityFreqThresholds.ContainsKey(timelineKey))
                        cfg.AbilityFreqThresholds[timelineKey] = [];
                    cfg.AbilityFreqThresholds[timelineKey][id] = threshVal;
                    InvalidateEncounterTimelineCaches();
                    plugin.OverlayWindow.InvalidateTimelineCaches();
                    RequestDeferredConfigSave();
                }
                if (hasCustom)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Reset##{id}"))
                    {
                        perAbility!.Remove(id);
                        if (perAbility.Count == 0)
                            cfg.AbilityFreqThresholds.Remove(timelineKey);
                        InvalidateEncounterTimelineCaches();
                        plugin.OverlayWindow.InvalidateTimelineCaches();
                        RequestDeferredConfigSave();
                    }
                }
                ImGui.Unindent(28f);
            }
        }

        ImGui.EndChild();
        ImGui.TreePop();
    }

    private void SaveSkillFilters()
    {
        if (currentTimeline == null)
            return;

        var key = TimelineDatabase.MakeKey(currentTimeline.EncounterId, currentTimeline.SpecName);
        var hidden = new HashSet<int>();
        foreach (var (id, visible) in skillVisibility)
        {
            if (!visible)
                hidden.Add(id);
        }

        plugin.Configuration.HiddenAbilities[key] = hidden;
        InvalidateEncounterTimelineCaches();
        plugin.OverlayWindow.InvalidateTimelineCaches();
        RequestDeferredConfigSave();
    }

    // ── Timeline drawing ──

    // Layout constants
    private const float PixelsPerSec  = 6.0f;
    private const float LabelWidth    = 140.0f;
    private const float RulerHeight   = 22.0f;
    private const float BossRowHeight = 22.0f;

    private (int AbilityId, string AbilityName) GetTimelineDisplayAbilityIdentity(TimelineEntry entry)
    {
        var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        var abilityId = info != null ? (int)info.AbilityId : entry.AbilityId;
        var abilityName = !string.IsNullOrWhiteSpace(info?.Name)
            ? info.Name
            : entry.AbilityName;

        if (string.IsNullOrWhiteSpace(abilityName))
            abilityName = $"#{abilityId}";

        return (abilityId, abilityName);
    }

    private void DrawTimeline()
    {
        if (currentTimeline == null)
            return;

        EnsureEncounterTimelineCaches();

        var cfg      = plugin.Configuration;
        var iconSize = cfg.MainIconSize * cfg.MainIconScale;
        var rowHeight = iconSize + 4f;

        ImGui.Text($"{currentTimeline.EncounterName} - {currentTimeline.SpecName}");
        ImGui.SameLine();
        ImGui.TextDisabled($"({currentTimeline.ParseCount} parses, avg {currentTimeline.AverageDurationMs / 1000.0:F1}s)");

        if (cachedTimelineVisibleEntries.Count == 0)
        {
            ImGui.TextDisabled("No visible skills. Adjust filters above.");
            return;
        }

        var customTimelineKey = ResolveActiveCustomTimelineKey(currentTimeline);
        var isCustomTimeline  = customTimelineKey != null;
        TimelineEntry? iconCtxEntry = null;

        var durationSec = currentTimeline.AverageDurationMs / 1000.0;
        if (durationSec <= 0) durationSec = 600;

        var hasBossRows    = currentTimeline.BossEntries.Count > 0;
        var bossRowsHeight = hasBossRows ? BossRowHeight + 4 : 0;

        var timelineWidth = (float)(durationSec * PixelsPerSec);
        var totalWidth    = LabelWidth + timelineWidth + 20;
        var totalHeight   = RulerHeight + cachedTimelineAbilityRows.Count * rowHeight + bossRowsHeight + 20;

        var avail = ImGui.GetContentRegionAvail();
        if (!ImGui.BeginChild("##Timeline", avail, true, ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.EndChild();
            return;
        }

        // Reserve space for scrolling
        ImGui.Dummy(new Vector2(totalWidth, totalHeight));

        var drawList = ImGui.GetWindowDrawList();
        var origin   = ImGui.GetCursorScreenPos();
        // Adjust up past the dummy
        origin.Y -= totalHeight;

        var timeOriginX = origin.X + LabelWidth;
        var scrollY = ImGui.GetScrollY();
        var viewportHeight = ImGui.GetWindowHeight();
        var firstVisibleRow = Math.Max(0, (int)Math.Floor((scrollY - RulerHeight) / rowHeight) - 1);
        var lastVisibleRow = Math.Min(
            cachedTimelineAbilityRows.Count - 1,
            (int)Math.Ceiling((scrollY + viewportHeight - RulerHeight) / rowHeight) + 1);

        // ── Draw time ruler ──
        var rulerY     = origin.Y;
        var rulerColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        var textColor  = ImGui.GetColorU32(ImGuiCol.Text);
        var gridBottom = origin.Y + RulerHeight + cachedTimelineAbilityRows.Count * rowHeight;

        for (var t = 0.0; t < durationSec; t += 10.0)
        {
            var x       = timeOriginX + (float)(t * PixelsPerSec);
            var minutes = (int)(t / 60);
            var seconds = (int)(t % 60);

            // Vertical grid line
            drawList.AddLine(
                new Vector2(x, rulerY + RulerHeight),
                new Vector2(x, gridBottom),
                ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.3f, 0.3f)));

            // Tick + label
            drawList.AddLine(
                new Vector2(x, rulerY + RulerHeight - 6),
                new Vector2(x, rulerY + RulerHeight),
                rulerColor);
            drawList.AddText(new Vector2(x + 2, rulerY + 2), textColor, $"{minutes}:{seconds:D2}");
        }

        // ── Draw ability rows ──
        for (var rowIdx = firstVisibleRow; rowIdx <= lastVisibleRow; rowIdx++)
        {
            var row  = cachedTimelineAbilityRows[rowIdx];
            var rowY = origin.Y + RulerHeight + rowIdx * rowHeight;

            // Alternating row background
            if (rowIdx % 2 == 0)
            {
                drawList.AddRectFilled(
                    new Vector2(origin.X, rowY),
                    new Vector2(origin.X + totalWidth, rowY + rowHeight),
                    ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 0.4f)));
            }

            // Row label (ability name) — clipped to label area
            var labelText = row.AbilityName;
            if (string.IsNullOrEmpty(labelText)) labelText = $"#{row.AbilityId}";
            var labelClipMin = new Vector2(origin.X + 2, rowY);
            var labelClipMax = new Vector2(timeOriginX - 6, rowY + rowHeight);
            drawList.PushClipRect(labelClipMin, labelClipMax, true);
            drawList.AddText(new Vector2(origin.X + 4, rowY + (rowHeight - 13f) * 0.5f), textColor, labelText);
            drawList.PopClipRect();

            // Draw separator line between label and timeline
            drawList.AddLine(
                new Vector2(timeOriginX - 2, rowY),
                new Vector2(timeOriginX - 2, rowY + rowHeight),
                ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 0.5f)));

            // Draw icons along the row
            foreach (var entry in row.Entries)
            {
                var centerX = timeOriginX + (float)(entry.TimeOffsetSec * PixelsPerSec);
                var iconX   = centerX - iconSize * 0.5f;
                var iconY   = rowY + (rowHeight - iconSize) * 0.5f;
                var iconPos = new Vector2(iconX, iconY);

                var alpha = (float)Math.Clamp(entry.Frequency * cfg.MainIconOpacity, 0.1, 1.0);

                var drawn = entry.AbilityId > 0 && TryDrawActionIcon(drawList, entry.AbilityId, iconPos, iconSize, alpha);
                if (!drawn)
                {
                    // Fallback: colored rect in hue derived from ability ID
                    var hue = (entry.AbilityId % 12) / 12.0f;
                    HsvToRgb(hue, 0.5f, 0.6f, out var cr, out var cg, out var cb);
                    drawList.AddRectFilled(
                        iconPos,
                        iconPos + new Vector2(iconSize, iconSize),
                        ImGui.GetColorU32(new Vector4(cr, cg, cb, alpha * 0.8f)), 2.0f);
                }

                var hitMin  = iconPos;
                var hitMax  = iconPos + new Vector2(iconSize, iconSize);
                if (ImGui.IsMouseHoveringRect(hitMin, hitMax))
                {
                    ImGui.BeginTooltip();
                    ImGui.Text(entry.AbilityName);
                    ImGui.Separator();
                    ImGui.Text($"Time: {FormatTime(entry.TimeOffsetSec)}");
                    ImGui.Text($"Frequency: {entry.Frequency:P0} of parses");
                    ImGui.Text($"Avg Uses: {entry.AverageUses:F1}x in this window");
                    ImGui.EndTooltip();

                    if (isCustomTimeline && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                        iconCtxEntry = entry;
                }
            }
        }

        // ── Draw boss attack row ──
        if (hasBossRows)
        {
            var bossRowY = origin.Y + RulerHeight + cachedTimelineAbilityRows.Count * rowHeight + 2;

            // Row background
            drawList.AddRectFilled(
                new Vector2(origin.X, bossRowY),
                new Vector2(origin.X + totalWidth, bossRowY + BossRowHeight),
                ImGui.GetColorU32(new Vector4(0.25f, 0.05f, 0.05f, 0.5f)));

            // Row label
            drawList.AddText(new Vector2(origin.X + 4, bossRowY + 3),
                ImGui.GetColorU32(new Vector4(1f, 0.5f, 0.4f, 1f)), "Boss Attacks");

            // Separator
            drawList.AddLine(
                new Vector2(timeOriginX - 2, bossRowY),
                new Vector2(timeOriginX - 2, bossRowY + BossRowHeight),
                ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 0.5f)));

            foreach (var boss in currentTimeline.BossEntries)
            {
                var startX = timeOriginX + (float)(boss.CastStartSec * PixelsPerSec);
                var endX   = timeOriginX + (float)(boss.CastEndSec   * PixelsPerSec);
                var barW   = Math.Max(endX - startX, 4.0f);

                var barColor = BossAbilityColor(boss.AbilityId);

                var barMin = new Vector2(startX, bossRowY + 2);
                var barMax = new Vector2(startX + barW, bossRowY + BossRowHeight - 2);
                drawList.AddRectFilled(barMin, barMax, barColor, 2.0f);

                // Name label (if bar is wide enough)
                if (barW > 24)
                {
                    drawList.AddText(new Vector2(startX + 2, bossRowY + 4),
                        ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f)), boss.AbilityName);
                }

                if (ImGui.IsMouseHoveringRect(barMin, barMax))
                {
                    ImGui.BeginTooltip();
                    ImGui.Text(boss.AbilityName);
                    ImGui.Separator();
                    ImGui.Text($"Cast start: {FormatTime(boss.CastStartSec)}");
                    if (boss.CastEndSec > boss.CastStartSec)
                        ImGui.Text($"Cast time:  {(boss.CastEndSec - boss.CastStartSec):F1}s (finishes {FormatTime(boss.CastEndSec)})");
                    else
                        ImGui.Text("Instant cast");
                    ImGui.EndTooltip();
                }
            }
        }

        // Right-click removal for custom timeline icons
        if (iconCtxEntry != null)
        {
            var removedAny = RemoveDisplayedEntryFromTimeline(currentTimeline, iconCtxEntry);
            if (customTimelineKey != null &&
                plugin.Configuration.CustomTimelines.TryGetValue(customTimelineKey, out var selectedCustomTimeline) &&
                !ReferenceEquals(selectedCustomTimeline, currentTimeline))
            {
                removedAny |= RemoveDisplayedEntryFromTimeline(selectedCustomTimeline, iconCtxEntry);
            }

            if (removedAny && !currentTimeline.Entries.Any(e => e.AbilityId == iconCtxEntry.AbilityId))
                skillVisibility.Remove(iconCtxEntry.AbilityId);

            if (removedAny)
            {
                InvalidateEncounterTimelineCaches();
                InvalidateCustomEditorCaches();
                plugin.OverlayWindow.InvalidateTimelineCaches();
                if (customTimelineKey != null &&
                    plugin.Configuration.CustomTimelines.TryGetValue(customTimelineKey, out var selectedCustomTimelineForSave))
                    plugin.CustomTimelineStore.SaveTimeline(plugin.Configuration, customTimelineKey, selectedCustomTimelineForSave);

                RequestDeferredConfigSave();
            }
        }

        ImGui.EndChild();
    }

    private uint BossAbilityColor(int abilityId)
    {
        var cfg = plugin.Configuration;
        if (cfg.BossBarUseCustomColor)
            return ImGui.GetColorU32(cfg.BossBarColor with { W = 0.85f });

        float[] hues = [0.0f, 0.05f, 0.9f, 0.75f, 0.12f, 0.85f];
        var hue = hues[Math.Abs(abilityId) % hues.Length];
        HsvToRgb(hue, 0.75f, 0.9f, out var br, out var bg, out var bb);
        return ImGui.GetColorU32(new Vector4(br, bg, bb, 0.85f));
    }

    private static bool RemoveDisplayedEntryFromTimeline(AggregatedTimeline timeline, TimelineEntry displayedEntry)
    {
        var sourceEntry = ResolveDisplayedTimelineEntry(timeline.Entries, displayedEntry);
        if (sourceEntry == null)
            return false;

        return timeline.Entries.Remove(sourceEntry);
    }

    private static TimelineEntry? ResolveDisplayedTimelineEntry(
        IEnumerable<TimelineEntry> sourceEntries,
        TimelineEntry displayedEntry)
    {
        const double exactTimeToleranceSec = 0.01;

        var exactMatch = sourceEntries.FirstOrDefault(sourceEntry =>
            sourceEntry.AbilityId == displayedEntry.AbilityId &&
            string.Equals(sourceEntry.AbilityName, displayedEntry.AbilityName, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(sourceEntry.TimeOffsetSec - displayedEntry.TimeOffsetSec) <= exactTimeToleranceSec &&
            Math.Abs(sourceEntry.Frequency - displayedEntry.Frequency) <= 0.0001 &&
            Math.Abs(sourceEntry.AverageUses - displayedEntry.AverageUses) <= 0.0001);
        if (exactMatch != null)
            return exactMatch;

        return sourceEntries
            .Where(sourceEntry =>
                sourceEntry.AbilityId == displayedEntry.AbilityId &&
                string.Equals(sourceEntry.AbilityName, displayedEntry.AbilityName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(sourceEntry => Math.Abs(sourceEntry.TimeOffsetSec - displayedEntry.TimeOffsetSec))
            .ThenByDescending(sourceEntry => sourceEntry.Frequency)
            .FirstOrDefault();
    }

    private string? ResolveActiveCustomTimelineKey(AggregatedTimeline? timeline)
    {
        if (timeline == null)
            return null;

        if (selectedCustomKey != null &&
            plugin.Configuration.CustomTimelines.TryGetValue(selectedCustomKey, out var selectedTimeline) &&
            ReferenceEquals(selectedTimeline, timeline))
            return selectedCustomKey;

        foreach (var kvp in plugin.Configuration.CustomTimelines)
        {
            if (ReferenceEquals(kvp.Value, timeline))
                return kvp.Key;
        }

        return null;
    }

    private bool TryDrawActionIcon(ImDrawListPtr dl, int abilityId, Vector2 pos, float size, float alpha)
    {
        if (!iconIdCache.TryGetValue(abilityId, out var iconId))
        {
            iconId = 0;
            try
            {
                var sheet = plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
                if (sheet != null)
                {
                    var row = sheet.GetRowOrDefault((uint)abilityId);
                    if (row.HasValue) iconId = row.Value.Icon;
                }
            }
            catch { /* silently fail */ }
            iconIdCache[abilityId] = iconId;
        }

        if (iconId == 0) return false;

        try
        {
            var wrap = plugin.TextureProvider.GetFromGameIcon(
                new Dalamud.Interface.Textures.GameIconLookup(iconId)).GetWrapOrEmpty();
            if (wrap.Width <= 1) return false;

            dl.AddImage(wrap.Handle, pos, pos + new Vector2(size, size),
                Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha)));
            return true;
        }
        catch { return false; }
    }

    private static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
    {
        var i = (int)(h * 6);
        var f = h * 6 - i;
        var p = v * (1 - s);
        var q = v * (1 - f * s);
        var t = v * (1 - (1 - f) * s);
        switch (i % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
    }

    private static string FormatTime(double seconds)
    {
        var m = (int)(seconds / 60);
        var s = seconds % 60;
        return $"{m}:{s:00.0}";
    }

    // ── Fetch selectors (lightweight — just zones + classes) ──

    private async Task FetchSelectorsAsync()
    {
        try
        {
            var client = plugin.FFLogsClient;

            log.Info("Fetching zone/class lists for selectors...");
            var fetchedZones = await client.GetZonesAsync(CancellationToken.None);
            var fetchedClasses = await client.GetClassesAsync(CancellationToken.None);

            zones = fetchedZones;
            classes = fetchedClasses;
            allSpecNames = classes
                .SelectMany(c => c.Specs)
                .Select(s => s.Name)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            selectorsLoaded = true;
            log.Info("Selectors loaded: {0} zones, {1} specs.", zones.Count, allSpecNames.Count);
            log.Info("Spec names: [{0}]", string.Join("], [", allSpecNames));
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to fetch selectors.");
            selectorsError = ex.Message;
        }
        finally
        {
            isFetchingSelectors = false;
        }
    }

    // ── Update logs (single fight + job) ──

    private void StartUpdate()
    {
        if (isUpdating)
            return;

        if (string.IsNullOrWhiteSpace(plugin.Configuration.FFLogsClientId))
        {
            updateStatus = "Error: FFLogs API credentials not configured. Open Config first.";
            return;
        }

        if (zones.Count == 0 || allSpecNames.Count == 0)
        {
            updateStatus = "Error: Zone/job lists not loaded yet. Please wait or reopen the window.";
            return;
        }

        if (selectedZone >= zones.Count)
            return;

        var encounters = zones[selectedZone].Encounters;
        if (selectedEncounter >= encounters.Count || selectedSpec >= allSpecNames.Count)
            return;

        var encounter = encounters[selectedEncounter];
        var specName = allSpecNames[selectedSpec];

        isUpdating = true;
        updateProgress = 0;
        updateStatus = $"Fetching: {encounter.Name} / {specName}...";
        updateCts = new CancellationTokenSource();

        Task.Run(() => RunUpdateAsync(encounter, specName, updateCts.Token));
    }

    private async Task RunUpdateAsync(Encounter encounter, string specName, CancellationToken ct)
    {
        try
        {
            var client = plugin.FFLogsClient;
            var aggregator = plugin.Aggregator;
            var store = plugin.TimelineStore;

            log.Info("Update started for {0} / {1}.", encounter.Name, specName);

            // Clear old cached data for this fight/job before fetching
            RemoveStoredEncounterPhaseTimelines(encounter.Id, specName);

            // Step 1: Fetch top 100 rankings
            updateStatus = $"Fetching top rankings for {encounter.Name} / {specName}...";
            updateProgress = 0.05f;

            var rankings = await client.GetTopRankingsAsync(encounter.Id, specName, 100, ct);
            if (rankings.Count == 0)
            {
                updateStatus = $"Warning: No rankings found for {encounter.Name} / {specName}.";
                log.Warning("No rankings for {0} / {1}.", encounter.Name, specName);
                return;
            }

            log.Info("Got {0} rankings for {1} / {2}, fetching events...",
                rankings.Count, encounter.Name, specName);

            // Step 2: Fetch cast events in parallel with bounded concurrency
            updateStatus = $"Fetching parse events (0/{rankings.Count})...";
            updateProgress = 0.1f;

            var semaphore = new SemaphoreSlim(MaxConcurrency);
            var parseResults = new List<(CastEventsResult Result, RankingEntry Ranking, List<BossTimelineEntry> BossEntries)>();
            var parseLock = new object();
            var completedParses = 0;
            var skippedParses = 0;
            string? firstError = null;

            var tasks = rankings.Select(async ranking =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();

                    log.Debug("Fetching events for {0}#{1}...", ranking.ReportCode, ranking.FightId);
                    var parseResult = await client.GetCastEventsAsync(ranking.ReportCode, ranking.FightId, specName, ct);
                    log.Debug("Got {0} events for {1}#{2}.", parseResult.Casts.Count, ranking.ReportCode, ranking.FightId);

                    if (parseResult.Casts.Count > 0)
                    {
                        var bossRaw = await client.GetBossCastEventsAsync(ranking.ReportCode, ranking.FightId, ct);
                        var bossEntries = aggregator.AggregateBossEvents(bossRaw, parseResult.PhaseInfo);
                        lock (parseLock)
                        {
                            parseResults.Add((parseResult, ranking, bossEntries));
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    var count = Interlocked.Increment(ref skippedParses);
                    log.Warning("Skipped parse {0}#{1}: {2}", ranking.ReportCode, ranking.FightId, ex.Message);
                    if (count == 1)
                        firstError = $"{ranking.ReportCode}#{ranking.FightId}: {ex.Message}";
                }
                finally
                {
                    semaphore.Release();
                    var done = Interlocked.Increment(ref completedParses);
                    updateStatus = $"Fetching parse events ({done}/{rankings.Count})...";
                    updateProgress = 0.1f + 0.8f * done / rankings.Count;
                }
            }).ToList();

            await Task.WhenAll(tasks);

            // Step 3: Aggregate
            updateStatus = "Aggregating timeline...";
            updateProgress = 0.95f;

            if (parseResults.Count > 0)
            {
                var orderedParseResults = parseResults
                    .OrderByDescending(entry => entry.Ranking.Amount)
                    .ThenBy(entry => entry.Ranking.ReportCode, StringComparer.Ordinal)
                    .ToList();
                var samplePhaseInfo = orderedParseResults
                    .Select(entry => entry.Result.PhaseInfo)
                    .FirstOrDefault(phaseInfo => phaseInfo != null);
                var parseData = new List<(List<CastEvent> casts, long fightStartMs, long fightEndMs)>();
                var cachedParseTimelines = new List<CachedFflogsParseTimeline>();
                var parseIndex = 1;

                foreach (var (result, ranking, bossEntries) in orderedParseResults)
                {
                    if (result.Casts.Count == 0)
                        continue;

                    var fightEnd = result.Casts.Max(cast => cast.Timestamp);
                    parseData.Add((result.Casts, 0, fightEnd));
                    cachedParseTimelines.Add(new CachedFflogsParseTimeline
                    {
                        ParseIndex = parseIndex++,
                        ReportCode = ranking.ReportCode,
                        FightId = ranking.FightId,
                        RankingAmount = ranking.Amount,
                        DurationSec = fightEnd / 1000.0,
                        Entries = BuildExactTimelineEntries(result.Casts),
                        BossEntries = bossEntries
                            .Select(CloneBossTimelineEntry)
                            .ToList(),
                        PhaseInfo = CloneFightPhaseInfo(result.PhaseInfo),
                    });
                }

                var timeline = aggregator.Aggregate(
                    encounter.Id, encounter.Name, specName, parseData);
                timeline.CachedFflogsParses = cachedParseTimelines
                    .OrderBy(parse => parse.ParseIndex)
                    .ToList();
                timeline.PhaseInfo = CloneFightPhaseInfo(samplePhaseInfo);
                timeline.BossEntries = BuildGeneratedBossAttackTimeline(timeline);
                log.Info("Boss timeline: {0} entries.", timeline.BossEntries.Count);
                timeline.AverageDurationMs = Math.Max(
                    timeline.AverageDurationMs,
                    timeline.BossEntries
                        .Select(entry => Math.Max(entry.CastStartSec, entry.CastEndSec))
                        .DefaultIfEmpty(0.0)
                        .Max() * 1000.0);
                RefreshTimelineRuntimeMetadata(timeline);
                store.SaveTimeline(timeline);
                InvalidateAutoTimelineSkillFilterCache();

                // Rebuild zone → encounter mappings so the EncounterTracker picks up new timelines
                plugin.EncounterTracker.RebuildZoneMappings();

                // Auto-load the result
                currentTimeline = timeline;
                RebuildSkillVisibility();

                updateStatus = $"Done! {encounter.Name} / {specName} — " +
                    $"{parseData.Count} parses aggregated" +
                    (skippedParses > 0 ? $" ({skippedParses} skipped)" : "") + ".";
                log.Info("Saved timeline for {0} / {1} ({2} parses, {3} skipped).",
                    encounter.Name, specName, parseData.Count, skippedParses);
            }
            else
            {
                updateStatus = $"Warning: All {rankings.Count} parses failed for {encounter.Name} / {specName}." +
                    (firstError != null ? $" First error: {firstError}" : "");
                log.Warning("All parses failed for {0} / {1}. First error: {2}",
                    encounter.Name, specName, firstError ?? "unknown");
            }

            updateProgress = 1;
        }
        catch (OperationCanceledException)
        {
            updateStatus = "Update cancelled.";
            log.Info("Update cancelled by user.");
        }
        catch (Exception ex)
        {
            updateStatus = $"Update failed: {ex.Message}";
            log.Error(ex, "Update failed.");
        }
        finally
        {
            isUpdating = false;
        }
    }

    private async Task RunUpdateWithPhaseSplitsAsync(Encounter encounter, string specName, CancellationToken ct)
    {
        try
        {
            var client = plugin.FFLogsClient;
            var aggregator = plugin.Aggregator;
            var store = plugin.TimelineStore;

            log.Info("Update started for {0} / {1}.", encounter.Name, specName);

            RemoveStoredEncounterPhaseTimelines(encounter.Id, specName);

            updateStatus = $"Fetching top rankings for {encounter.Name} / {specName}...";
            updateProgress = 0.05f;

            var rankings = await client.GetTopRankingsAsync(encounter.Id, specName, 100, ct);
            if (rankings.Count == 0)
            {
                updateStatus = $"Warning: No rankings found for {encounter.Name} / {specName}.";
                log.Warning("No rankings for {0} / {1}.", encounter.Name, specName);
                return;
            }

            log.Info("Got {0} rankings for {1} / {2}, fetching events...",
                rankings.Count, encounter.Name, specName);

            updateStatus = $"Fetching parse events (0/{rankings.Count})...";
            updateProgress = 0.1f;

            var semaphore = new SemaphoreSlim(MaxConcurrency);
            var parseResults = new List<(CastEventsResult Result, RankingEntry Ranking, List<BossTimelineEntry> BossEntries)>();
            var parseLock = new object();
            var completedParses = 0;
            var skippedParses = 0;
            string? firstError = null;

            var tasks = rankings.Select(async ranking =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();

                    log.Debug("Fetching events for {0}#{1}...", ranking.ReportCode, ranking.FightId);
                    var parseResult = await client.GetCastEventsAsync(ranking.ReportCode, ranking.FightId, specName, ct);
                    log.Debug("Got {0} events for {1}#{2}.", parseResult.Casts.Count, ranking.ReportCode, ranking.FightId);

                    if (parseResult.Casts.Count > 0)
                    {
                        var bossRaw = await client.GetBossCastEventsAsync(ranking.ReportCode, ranking.FightId, ct);
                        var bossEntries = aggregator.AggregateBossEvents(bossRaw, parseResult.PhaseInfo);
                        lock (parseLock)
                        {
                            parseResults.Add((parseResult, ranking, bossEntries));
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    var count = Interlocked.Increment(ref skippedParses);
                    log.Warning("Skipped parse {0}#{1}: {2}", ranking.ReportCode, ranking.FightId, ex.Message);
                    if (count == 1)
                        firstError = $"{ranking.ReportCode}#{ranking.FightId}: {ex.Message}";
                }
                finally
                {
                    semaphore.Release();
                    var done = Interlocked.Increment(ref completedParses);
                    updateStatus = $"Fetching parse events ({done}/{rankings.Count})...";
                    updateProgress = 0.1f + 0.8f * done / rankings.Count;
                }
            }).ToList();

            await Task.WhenAll(tasks);

            updateStatus = "Aggregating timeline...";
            updateProgress = 0.95f;

            if (parseResults.Count == 0)
            {
                updateStatus = $"Warning: All {rankings.Count} parses failed for {encounter.Name} / {specName}." +
                    (firstError != null ? $" First error: {firstError}" : "");
                log.Warning("All parses failed for {0} / {1}. First error: {2}",
                    encounter.Name, specName, firstError ?? "unknown");
                updateProgress = 1;
                return;
            }

            var orderedParseResults = parseResults
                .OrderByDescending(entry => entry.Ranking.Amount)
                .ThenBy(entry => entry.Ranking.ReportCode, StringComparer.Ordinal)
                .ToList();
            var samplePhaseInfo = orderedParseResults
                .Select(entry => entry.Result.PhaseInfo)
                .FirstOrDefault(phaseInfo => phaseInfo != null);
            var phaseWindows = BuildEncounterPhaseWindows(samplePhaseInfo);

            if (phaseWindows.Count > 1)
            {
                foreach (var phaseWindow in phaseWindows)
                {
                    var phaseParseData = new List<(List<CastEvent> casts, long fightStartMs, long fightEndMs)>();
                    var phaseCachedParseTimelines = new List<CachedFflogsParseTimeline>();
                    var parseIndex = 1;

                        foreach (var (result, ranking, bossEntries) in orderedParseResults)
                        {
                            var parsePhaseWindow = BuildEncounterPhaseWindows(result.PhaseInfo)
                                .FirstOrDefault(window => window.Ordinal == phaseWindow.Ordinal);
                            if (parsePhaseWindow == null)
                                continue;

                            var phaseCasts = SlicePhaseCasts(result.Casts, parsePhaseWindow);
                            if (phaseCasts.Count == 0)
                                continue;

                            var phaseDurationMs = Math.Max(
                                parsePhaseWindow.EndMs - parsePhaseWindow.StartMs,
                                phaseCasts.Max(cast => cast.Timestamp));
                            phaseParseData.Add((phaseCasts, 0, phaseDurationMs));
                            phaseCachedParseTimelines.Add(new CachedFflogsParseTimeline
                            {
                            ParseIndex = parseIndex++,
                            ReportCode = ranking.ReportCode,
                            FightId = ranking.FightId,
                            RankingAmount = ranking.Amount,
                            DurationSec = phaseDurationMs / 1000.0,
                            Entries = BuildExactTimelineEntries(phaseCasts),
                            BossEntries = SlicePhaseBossEntries(bossEntries, parsePhaseWindow),
                            PhaseInfo = CloneFightPhaseInfo(result.PhaseInfo),
                        });
                    }

                    if (phaseParseData.Count == 0)
                        continue;

                    var phaseEncounterId = GetEncounterPhaseId(encounter.Id, phaseWindow.Ordinal);
                    var phaseEncounterName = GetEncounterPhaseName(encounter.Name, phaseWindow.Ordinal);
                    var phaseTimeline = aggregator.Aggregate(
                        phaseEncounterId,
                        phaseEncounterName,
                        specName,
                        phaseParseData);
                    phaseTimeline.CachedFflogsParses = phaseCachedParseTimelines;
                    phaseTimeline.PhaseInfo = CloneFightPhaseInfo(samplePhaseInfo);
                    phaseTimeline.BossEntries = BuildGeneratedBossAttackTimeline(phaseTimeline);
                    phaseTimeline.AverageDurationMs = Math.Max(
                        phaseTimeline.AverageDurationMs,
                        phaseTimeline.BossEntries
                            .Select(entry => Math.Max(entry.CastStartSec, entry.CastEndSec))
                            .DefaultIfEmpty(0.0)
                            .Max() * 1000.0);
                    RefreshTimelineRuntimeMetadata(phaseTimeline);
                    store.SaveTimeline(phaseTimeline);
                    InvalidateAutoTimelineSkillFilterCache();

                    if (phaseWindow.Ordinal == 1)
                        currentTimeline = phaseTimeline;
                }
            }
            else
            {
                var parseData = new List<(List<CastEvent> casts, long fightStartMs, long fightEndMs)>();
                var cachedParseTimelines = new List<CachedFflogsParseTimeline>();
                var parseIndex = 1;

                foreach (var (result, ranking, bossEntries) in orderedParseResults)
                {
                    var fightEnd = result.Casts.Max(cast => cast.Timestamp);
                    parseData.Add((result.Casts, 0, fightEnd));
                    cachedParseTimelines.Add(new CachedFflogsParseTimeline
                    {
                        ParseIndex = parseIndex++,
                        ReportCode = ranking.ReportCode,
                        FightId = ranking.FightId,
                        RankingAmount = ranking.Amount,
                        DurationSec = fightEnd / 1000.0,
                        Entries = BuildExactTimelineEntries(result.Casts),
                        BossEntries = bossEntries
                            .Select(CloneBossTimelineEntry)
                            .ToList(),
                        PhaseInfo = CloneFightPhaseInfo(result.PhaseInfo),
                    });
                }

                var timeline = aggregator.Aggregate(
                    encounter.Id,
                    encounter.Name,
                    specName,
                    parseData);
                timeline.CachedFflogsParses = cachedParseTimelines;
                timeline.PhaseInfo = CloneFightPhaseInfo(samplePhaseInfo);
                timeline.BossEntries = BuildGeneratedBossAttackTimeline(timeline);
                timeline.AverageDurationMs = Math.Max(
                    timeline.AverageDurationMs,
                    timeline.BossEntries
                        .Select(entry => Math.Max(entry.CastStartSec, entry.CastEndSec))
                        .DefaultIfEmpty(0.0)
                        .Max() * 1000.0);
                RefreshTimelineRuntimeMetadata(timeline);
                store.SaveTimeline(timeline);
                InvalidateAutoTimelineSkillFilterCache();
                currentTimeline = timeline;
            }

            plugin.EncounterTracker.RebuildZoneMappings();
            RebuildSkillVisibility();

            updateStatus = $"Done! {encounter.Name} / {specName} - " +
                $"{parseResults.Count} parses aggregated" +
                (skippedParses > 0 ? $" ({skippedParses} skipped)" : "") + ".";
            log.Info("Saved timeline for {0} / {1} ({2} parses, {3} skipped).",
                encounter.Name, specName, parseResults.Count, skippedParses);
            updateProgress = 1;
        }
        catch (OperationCanceledException)
        {
            updateStatus = "Update cancelled.";
            log.Info("Update cancelled by user.");
        }
        catch (Exception ex)
        {
            updateStatus = $"Update failed: {ex.Message}";
            log.Error(ex, "Update failed.");
        }
        finally
        {
            isUpdating = false;
        }
    }

    private void RebuildSkillVisibility()
    {
        skillVisibility.Clear();
        if (currentTimeline == null)
            return;

        InvalidateEncounterTimelineCaches();

        var key = TimelineDatabase.MakeKey(currentTimeline.EncounterId, currentTimeline.SpecName);
        var hidden = plugin.Configuration.HiddenAbilities.GetValueOrDefault(key);
        var uniqueAbilities = currentTimeline.Entries
            .Select(e => (e.AbilityId, e.AbilityName))
            .Distinct()
            .OrderBy(a => a.AbilityName);

        foreach (var (id, _) in uniqueAbilities)
            skillVisibility[id] = hidden == null || !hidden.Contains(id);
    }

    // ── Copy timeline ──

    private void CopyCurrentTimeline()
    {
        if (currentTimeline == null)
            return;

        var key = TimelineDatabase.MakeKey(currentTimeline.EncounterId, currentTimeline.SpecName);
        var isCustomTimeline = plugin.Configuration.CustomTimelines.ContainsKey(key);

        // Deep-copy via JSON, then strip entries that are currently filtered out.
        // Keep the raw Auto Timeline source list separate from the condensed display
        // list, but apply the same active skill filters to both so the copied custom
        // timeline reflects the Encounter Timeline view without losing raw timing data.
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(currentTimeline);
        var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<AggregatedTimeline>(json)!;

        copy.Entries = FilterCopiedTimelineEntries(copy.Entries);
        copy.CachedFflogsParses = isCustomTimeline
            ? currentTimeline.CachedFflogsParses
                .Select(CloneCachedFflogsParseTimeline)
                .ToList()
            : FilterCopiedCachedFflogsParses(currentTimeline.CachedFflogsParses);
        copy.AutoTimelineSourceEntries = BuildFilteredCopiedRawSource(currentTimeline, isCustomTimeline);

        plugin.CustomTimelineStore.SaveTimeline(plugin.Configuration, key, copy);
        InvalidateCustomTimelineListCache();

        // Refresh the editor if this key is already selected so it shows the new copy
        SelectCustomTimeline(key, copy);
    }

    private List<TimelineEntry> FilterCopiedTimelineEntries(IEnumerable<TimelineEntry> entries)
    {
        return entries
            .Where(e => skillVisibility.GetValueOrDefault(e.AbilityId, true))
            .Where(e => e.Frequency >= GetAbilityThreshold(e.AbilityId))
            .Select(CloneTimelineEntry)
            .ToList();
    }

    private List<CachedFflogsParseTimeline> FilterCopiedCachedFflogsParses(IEnumerable<CachedFflogsParseTimeline> parses)
    {
        return parses
            .Select(parse => new CachedFflogsParseTimeline
            {
                ParseIndex = parse.ParseIndex,
                ReportCode = parse.ReportCode,
                FightId = parse.FightId,
                RankingAmount = parse.RankingAmount,
                DurationSec = parse.DurationSec,
                Entries = FilterCopiedTimelineEntries(parse.Entries),
                BossEntries = parse.BossEntries
                    .Select(CloneBossTimelineEntry)
                    .ToList(),
            })
            .Where(parse => parse.Entries.Count > 0)
            .ToList();
    }

    private List<TimelineEntry> BuildFilteredCopiedRawSource(AggregatedTimeline timeline, bool isCustomTimeline)
    {
        var cachedSourceEntries = ApplyAutoTimelineAbilityFilters(
            timeline,
            BuildAutoTimelineSourceFromCachedParses(timeline));
        if (cachedSourceEntries.Count > 0)
            return cachedSourceEntries;

        if (isCustomTimeline)
        {
            if (timeline.AutoTimelineSourceEntries.Count > 0)
                return FilterCopiedTimelineEntries(timeline.AutoTimelineSourceEntries);

            return FilterCopiedTimelineEntries(timeline.Entries);
        }

        if (timeline.AutoTimelineSourceEntries.Count > 0)
            return FilterCopiedTimelineEntries(timeline.AutoTimelineSourceEntries);

        return FilterCopiedTimelineEntries(timeline.Entries);
    }

    private static List<TimelineEntry> FlattenCachedFflogsSourceEntries(IEnumerable<CachedFflogsParseTimeline> parses)
    {
        return parses
            .OrderBy(parse => parse.ParseIndex)
            .SelectMany(parse => parse.Entries)
            .Select(CloneTimelineEntry)
            .ToList();
    }

    private static bool IsSuspiciousFlattenedCustomSource(AggregatedTimeline tl)
    {
        if (tl.AutoTimelineSourceEntries.Count == 0 || tl.Entries.Count == 0)
            return false;

        if (tl.AutoTimelineSourceEntries.Count < Math.Max(500, tl.Entries.Count * 3))
            return false;

        var allFullFrequency = tl.AutoTimelineSourceEntries.All(entry => entry.Frequency >= 0.999);
        if (!allFullFrequency)
            return false;

        var sourceHasAnyGcd = tl.AutoTimelineSourceEntries.Any(entry => entry.IsGcd);
        var entriesHaveAnyGcd = tl.Entries.Any(entry => entry.IsGcd);
        return !sourceHasAnyGcd && entriesHaveAnyGcd;
    }

    // ── Config tab ──────────────────────────────────────────────────────

    private void DrawConfigTab()
    {
        if (!cfgInitialized)
        {
            cfgClientId     = plugin.Configuration.FFLogsClientId;
            cfgClientSecret = plugin.Configuration.FFLogsClientSecret;
            cfgInitialized  = true;
        }

        // Collapsible groups first, in the requested order
        if (ImGui.CollapsingHeader("FFLogs Credentials##cfgCreds"))
        {
            ImGui.Indent();
            DrawCfgApiCredentials();
            ImGui.Unindent();
        }
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Overlay Appearance##cfgAppearance"))
        {
            ImGui.Indent();
            DrawCfgOverlayAppearance();
            ImGui.Unindent();
        }
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Main Timeline##cfgMainTL"))
        {
            ImGui.Indent();
            DrawCfgMainTimelineSettings();
            ImGui.Unindent();
        }
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Ability Ants##cfgAnts"))
        {
            ImGui.Indent();
            DrawCfgAntsSettings();
            ImGui.Unindent();
        }
        ImGui.Spacing(); ImGui.Spacing();
        // Non-collapsed features beneath the groups
        DrawCfgOverlaySettings();
        ImGui.Spacing(); ImGui.Spacing();
        var debugEnabled = plugin.Configuration.DebugEnabled;
        if (ImGui.Checkbox("Debug Enabled", ref debugEnabled))
        {
            plugin.Configuration.DebugEnabled = debugEnabled;
            RequestDeferredUiSettingsSave();
        }
        ImGui.Spacing(); ImGui.Spacing();
        DrawCfgResetSection();
        ImGui.Spacing(); ImGui.Spacing();
        ImGui.TextDisabled("ATKTip v1.2.2");
        ImGui.TextDisabled("Timeline data sourced from FFLogs top parses.");
    }

    private void DrawCfgApiCredentials()
    {
        ImGui.TextUnformatted("FFLogs API Credentials");
        ImGui.Separator();

        ImGui.TextWrapped(
            "ATKTip needs an FFLogs API key to fetch parse data. " +
            "This is free and only takes a minute to set up.");
        ImGui.Spacing();

        if (ImGui.TreeNode("How to get your API key (step-by-step)"))
        {
            ImGui.Spacing();
            var bulletColor = new Vector4(0.6f, 0.8f, 1.0f, 1.0f);

            ImGui.TextColored(in bulletColor, "Step 1:");
            ImGui.SameLine();
            ImGui.TextWrapped("Go to https://www.fflogs.com and log in (or create a free account).");
            ImGui.Spacing();
            ImGui.TextColored(in bulletColor, "Step 2:");
            ImGui.SameLine();
            ImGui.TextWrapped("Navigate to https://www.fflogs.com/api/clients");
            ImGui.Spacing();
            ImGui.TextColored(in bulletColor, "Step 3:");
            ImGui.SameLine();
            ImGui.TextWrapped(
            "Click \"Create Client\". Give it any name (e.g. \"ATKTip\"). " +
                "For the redirect URL, enter: http://localhost");
            ImGui.Spacing();
            ImGui.TextColored(in bulletColor, "Step 4:");
            ImGui.SameLine();
            ImGui.TextWrapped(
                "After creating the client, you will see a Client ID and Client Secret. " +
                "Copy both values and paste them into the fields below.");
            ImGui.Spacing();
            ImGui.TextDisabled(
                "Note: Your credentials are stored locally and are only used to " +
                "authenticate with the FFLogs API. They are never shared.");
            ImGui.TreePop();
        }

        ImGui.Spacing();
        ImGui.Separator();

        ImGui.TextUnformatted("Client ID");
        CfgTooltip("The Client ID from your FFLogs API client.\nFound at: fflogs.com/api/clients");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##CfgClientId", ref cfgClientId, 256, ImGuiInputTextFlags.None);
        ImGui.Spacing();

        ImGui.TextUnformatted("Client Secret");
        CfgTooltip("The Client Secret from your FFLogs API client.\nThis is shown once when you create the client.\nIf you lost it, delete the old client and create a new one.");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##CfgClientSecret", ref cfgClientSecret, 256, ImGuiInputTextFlags.Password);
        ImGui.Spacing();

        if (ImGui.Button("Save Credentials", default))
        {
            plugin.Configuration.FFLogsClientId     = cfgClientId;
            plugin.Configuration.FFLogsClientSecret = cfgClientSecret;
            plugin.SaveConfig();
        }
        ImGui.SameLine();

        if (!string.IsNullOrWhiteSpace(plugin.Configuration.FFLogsClientId) &&
            !string.IsNullOrWhiteSpace(plugin.Configuration.FFLogsClientSecret))
        {
            var green = new Vector4(0.3f, 1.0f, 0.3f, 1.0f);
            ImGui.TextColored(in green, "Credentials saved.");
        }
        else
        {
            var yellow = new Vector4(1.0f, 0.8f, 0.2f, 1.0f);
            ImGui.TextColored(in yellow, "Not configured yet.");
        }
    }

    private void DrawCfgOverlaySettings()
    {
        ImGui.TextUnformatted("Overlay Settings");
        ImGui.Separator();

        var cfg = plugin.Configuration;

        var overlayEnabled = cfg.OverlayEnabled;
        if (ImGui.Checkbox("Enable Overlay", ref overlayEnabled))
        {
            cfg.OverlayEnabled = overlayEnabled;
            RequestDeferredUiSettingsSave();
        }
        CfgTooltip("Show a compact live timeline during combat.\nStarts automatically when you pull a boss.");

        var overlayLocked = cfg.OverlayLocked;
        if (ImGui.Checkbox("Lock Overlay Position", ref overlayLocked))
        {
            cfg.OverlayLocked = overlayLocked;
            RequestDeferredUiSettingsSave();
        }
        CfgTooltip("When locked, the overlay cannot be moved or resized.\nUncheck this to reposition the overlay, then re-lock it.");
    }

    private void DrawCfgOverlayAppearance()
    {
        ImGui.TextUnformatted("Overlay Appearance");
        ImGui.Separator();

        var cfg     = plugin.Configuration;
        var changed = false;

        ImGui.TextDisabled("Timing");

        var pxPerSec = cfg.OverlayPixelsPerSec;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Scroll Speed (px/sec)", ref pxPerSec, 20.0f, 200.0f, "%.0f"))
        { cfg.OverlayPixelsPerSec = pxPerSec; changed = true; }
        CfgTooltip("How many pixels represent one second of time.\nHigher = more spread out, lower = more compact.");

        var timeBehind = cfg.OverlayTimeBehind;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Look-Behind (sec)", ref timeBehind, 0.0f, 10.0f, "%.1f"))
        { cfg.OverlayTimeBehind = timeBehind; changed = true; }
        CfgTooltip("How many seconds of past abilities to keep visible.\nThis controls where the 'now' line sits.");

        ImGui.Spacing();
        ImGui.TextDisabled("Icons");

        var iconSize = cfg.OverlayIconSize;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Icon Size", ref iconSize, 16.0f, 64.0f, "%.0f"))
        { cfg.OverlayIconSize = iconSize; changed = true; }
        CfgTooltip("Size of the primary ability icon in pixels.");

        var maxStacked = cfg.OverlayMaxStackedIcons;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Max Stacked Icons", ref maxStacked, 1, 6))
        { cfg.OverlayMaxStackedIcons = maxStacked; changed = true; }
        CfgTooltip("Maximum number of abilities shown per time bucket.\nThe most frequent ability is shown largest.");

        ImGui.Spacing();
        ImGui.TextDisabled("oGCD");

        var ogcdSizeRatio = cfg.OGCDSizeRatio;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("oGCD Size", ref ogcdSizeRatio, 0.25f, 1.0f, "%.2f"))
        { cfg.OGCDSizeRatio = ogcdSizeRatio; changed = true; }
        CfgTooltip("oGCD icon size as a fraction of the GCD icon size.\nSmaller values help dense weave windows stay readable and match the ATR-style playback look.");

        var ogcdOffset = cfg.OGCDVerticalOffset;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("oGCD Vertical Offset", ref ogcdOffset, -1.0f, 1.0f, "%.2f"))
        { cfg.OGCDVerticalOffset = ogcdOffset; changed = true; }
        CfgTooltip("Move oGCDs upward from the main action lane.\n0.1 matches the ATR-style default.\nNegative values move them back down.");

        var ogcdHOffset = cfg.OGCDHorizontalOffset;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("oGCD Horizontal Offset", ref ogcdHOffset, -100f, 100f, "%.0f px"))
        { cfg.OGCDHorizontalOffset = ogcdHOffset; changed = true; }
        CfgTooltip("Nudge oGCD slot placement left (negative) or right (positive) after legal weave slots are chosen.");

        ImGui.Spacing();
        ImGui.TextDisabled("Visual");

        var bgOpacity = cfg.OverlayBgOpacity;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Background Opacity", ref bgOpacity, 0.0f, 1.0f, "%.2f"))
        { cfg.OverlayBgOpacity = bgOpacity; changed = true; }
        CfgTooltip("Opacity of the overlay background.\n0 = fully transparent, 1 = solid.");

        var pastAlpha = cfg.OverlayPastAlpha;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Past Icon Dimming", ref pastAlpha, 0.0f, 1.0f, "%.2f"))
        { cfg.OverlayPastAlpha = pastAlpha; changed = true; }
        CfgTooltip("Opacity multiplier for abilities that have already passed.\n0 = invisible, 1 = same as upcoming.");

        var freqPct = cfg.OverlayFreqThreshold * 100f;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Min Frequency", ref freqPct, 0f, 100f, "%.0f%%"))
        { cfg.OverlayFreqThreshold = freqPct / 100f; changed = true; }
        CfgTooltip("Hide abilities used in fewer than this % of parses.\nUseful to filter out rare/niche casts.");

        ImGui.Spacing();
        ImGui.TextDisabled("Elements");

        var showGrid = cfg.OverlayShowGrid;
        if (ImGui.Checkbox("Show Grid Lines", ref showGrid))
        { cfg.OverlayShowGrid = showGrid; changed = true; }
        CfgTooltip("Show vertical time grid lines on the overlay.");

        ImGui.Spacing();
        ImGui.TextDisabled("Boss Bar");

        var bossCustom = cfg.BossBarUseCustomColor;
        if (ImGui.Checkbox("Custom Boss Bar Color", ref bossCustom))
        { cfg.BossBarUseCustomColor = bossCustom; changed = true; }
        CfgTooltip("Use a single custom color for all boss cast bars\ninstead of the default per-ability coloring.");

        if (cfg.BossBarUseCustomColor)
        {
            var bossCol = cfg.BossBarColor;
            ImGui.SetNextItemWidth(200);
            if (ImGui.ColorEdit4("Boss Bar Color", ref bossCol))
            { cfg.BossBarColor = bossCol; changed = true; }
            CfgTooltip("Color applied to every boss cast bar.");
        }

        if (changed) RequestDeferredUiSettingsSave();
    }

    private void DrawCfgMainTimelineSettings()
    {
        ImGui.TextUnformatted("Main Timeline");
        ImGui.Separator();

        var cfg     = plugin.Configuration;
        var changed = false;

        var iconSize = cfg.MainIconSize;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Icon Size##Main", ref iconSize, 8.0f, 48.0f, "%.0f"))
        { cfg.MainIconSize = iconSize; changed = true; }
        CfgTooltip("Size of skill icons in the main timeline window.");

        var iconScale = cfg.MainIconScale;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Icon Scale##Main", ref iconScale, 0.5f, 2.0f, "%.2f"))
        { cfg.MainIconScale = iconScale; changed = true; }
        CfgTooltip("Scale multiplier applied on top of Icon Size.");

        var iconOpacity = cfg.MainIconOpacity;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Icon Opacity##Main", ref iconOpacity, 0.0f, 1.0f, "%.2f"))
        { cfg.MainIconOpacity = iconOpacity; changed = true; }
        CfgTooltip("Maximum opacity of skill icons.\nMultiplied by each ability's usage frequency.");

        ImGui.Spacing();
        ImGui.TextDisabled("Boss bar color is shared with the overlay — see Boss Bar above.");

        if (changed) RequestDeferredUiSettingsSave();
    }

    private void DrawCfgAntsSettings()
    {
        ImGui.TextUnformatted("Ability Ants");
        ImGui.Separator();

        var cfg     = plugin.Configuration;
        var changed = false;

        var enabled = cfg.AntsEnabled;
        if (ImGui.Checkbox("Enable Ability Ants", ref enabled))
        { cfg.AntsEnabled = enabled; changed = true; }
        CfgTooltip("Draw an animated dashed border around abilities as they cross the now-line.\nMatches FFXIV's combo/proc highlight style.");

        if (cfg.AntsEnabled)
        {
            ImGui.Spacing(); ImGui.Spacing();

            ImGui.TextDisabled("Custom ants are always enabled.");

            // ── oGCD Ants ──────────────────────────────────────────────────────
            ImGui.Spacing(); ImGui.Spacing();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "oGCD Ants");
            ImGui.Separator();

            var ogcdEnabled = cfg.OgcdAntsEnabled;
            if (ImGui.Checkbox("Enable oGCD Ants##ogcd", ref ogcdEnabled))
            { cfg.OgcdAntsEnabled = ogcdEnabled; changed = true; }
            CfgTooltip("Show ants on off-global-cooldown abilities as they approach the now-line.");

            if (cfg.OgcdAntsEnabled)
            {
                ImGui.Spacing();

                var before = cfg.AntsDurationBefore;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Before (sec)##ogcd", ref before, 0.0f, 4.0f, "%.1f"))
                { cfg.AntsDurationBefore = before; changed = true; }
                CfgTooltip("How many seconds before crossing the now-line the ants appear.");

                var after = cfg.AntsDurationAfter;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("After (sec)##ogcd", ref after, 0.0f, 4.0f, "%.1f"))
                { cfg.AntsDurationAfter = after; changed = true; }
                CfgTooltip("How many seconds after crossing the now-line the ants remain.");

                ImGui.Spacing();
                ImGui.TextDisabled($"Window: {cfg.AntsDurationBefore + cfg.AntsDurationAfter:F1}s  " +
                                   $"({cfg.AntsDurationBefore:F1}s before + {cfg.AntsDurationAfter:F1}s after)");

                ImGui.Spacing();

                var dashCol = cfg.AntsColor;
                if (ImGui.ColorEdit4("Dash colour##ogcd", ref dashCol,
                    ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
                { cfg.AntsColor = dashCol; changed = true; }

                var gapCol = cfg.AntsGapColor;
                if (ImGui.ColorEdit4("Gap colour##ogcd", ref gapCol,
                    ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
                { cfg.AntsGapColor = gapCol; changed = true; }
                CfgTooltip("Set alpha to 0 for transparent gaps.");

                ImGui.Spacing();

                var dashLen = cfg.AntsDashLength;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Dash length (px)##ogcd", ref dashLen, 1.0f, 30.0f, "%.0f"))
                { cfg.AntsDashLength = dashLen; changed = true; }

                var gapLen = cfg.AntsGapLength;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Gap length (px)##ogcd", ref gapLen, 0.0f, 20.0f, "%.0f"))
                { cfg.AntsGapLength = gapLen; changed = true; }

                var speed = cfg.AntsSpeed;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("March speed (px/s)##ogcd", ref speed, 5.0f, 200.0f, "%.0f"))
                { cfg.AntsSpeed = speed; changed = true; }
                CfgTooltip("How fast the dashes march around the border.");

                var thick = cfg.AntsThickness;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Line thickness (px)##ogcd", ref thick, 1.0f, 6.0f, "%.1f"))
                { cfg.AntsThickness = thick; changed = true; }

                var padding = cfg.AntsBorderPadding;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Border expansion (px)##ogcd", ref padding, -10.0f, 10.0f, "%.1f"))
                { cfg.AntsBorderPadding = padding; changed = true; }
                CfgTooltip("Expand (+) or shrink (-) the ants border relative to the slot edge.\n0 = flush with slot bounds.");

                var xOff = cfg.AntsXOffset;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Horizontal offset (px)##ogcd", ref xOff, -20.0f, 20.0f, "%.1f"))
                { cfg.AntsXOffset = xOff; changed = true; }
                CfgTooltip("Shift the ants border left (-) or right (+).");

                var yOff = cfg.AntsYOffset;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Vertical offset (px)##ogcd", ref yOff, -20.0f, 20.0f, "%.1f"))
                { cfg.AntsYOffset = yOff; changed = true; }
                CfgTooltip("Shift the ants border up (-) or down (+).");
            }

            // ── GCD Ants ───────────────────────────────────────────────────────
            ImGui.Spacing(); ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 1.0f, 1.0f), "GCD Ants");
            ImGui.Separator();

            var gcdEnabled = cfg.GcdAntsEnabled;
            if (ImGui.Checkbox("Enable GCD Ants##gcd", ref gcdEnabled))
            { cfg.GcdAntsEnabled = gcdEnabled; changed = true; }
            CfgTooltip("Show ants on the next upcoming GCD as it approaches the now-line.\n" +
                       "Only the single closest GCD entry glows at a time.");

            if (cfg.GcdAntsEnabled)
            {
                ImGui.Spacing();

                var before = cfg.GcdAntsDurationBefore;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Before (sec)##gcd", ref before, 0.0f, 4.0f, "%.1f"))
                { cfg.GcdAntsDurationBefore = before; changed = true; }
                CfgTooltip("How many seconds before crossing the now-line the ants appear.");

                var after = cfg.GcdAntsDurationAfter;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("After (sec)##gcd", ref after, 0.0f, 4.0f, "%.1f"))
                { cfg.GcdAntsDurationAfter = after; changed = true; }
                CfgTooltip("How many seconds after crossing the now-line the ants remain.");

                ImGui.Spacing();
                ImGui.TextDisabled($"Window: {cfg.GcdAntsDurationBefore + cfg.GcdAntsDurationAfter:F1}s  " +
                                   $"({cfg.GcdAntsDurationBefore:F1}s before + {cfg.GcdAntsDurationAfter:F1}s after)");

                ImGui.Spacing();

                var dashCol = cfg.GcdAntsColor;
                if (ImGui.ColorEdit4("Dash colour##gcd", ref dashCol,
                    ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
                { cfg.GcdAntsColor = dashCol; changed = true; }

                var gapCol = cfg.GcdAntsGapColor;
                if (ImGui.ColorEdit4("Gap colour##gcd", ref gapCol,
                    ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
                { cfg.GcdAntsGapColor = gapCol; changed = true; }
                CfgTooltip("Set alpha to 0 for transparent gaps.");

                ImGui.Spacing();

                var dashLen = cfg.GcdAntsDashLength;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Dash length (px)##gcd", ref dashLen, 1.0f, 30.0f, "%.0f"))
                { cfg.GcdAntsDashLength = dashLen; changed = true; }

                var gapLen = cfg.GcdAntsGapLength;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Gap length (px)##gcd", ref gapLen, 0.0f, 20.0f, "%.0f"))
                { cfg.GcdAntsGapLength = gapLen; changed = true; }

                var speed = cfg.GcdAntsSpeed;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("March speed (px/s)##gcd", ref speed, 5.0f, 200.0f, "%.0f"))
                { cfg.GcdAntsSpeed = speed; changed = true; }
                CfgTooltip("How fast the dashes march around the border.");

                var thick = cfg.GcdAntsThickness;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Line thickness (px)##gcd", ref thick, 1.0f, 6.0f, "%.1f"))
                { cfg.GcdAntsThickness = thick; changed = true; }

                var padding = cfg.GcdAntsBorderPadding;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Border expansion (px)##gcd", ref padding, -10.0f, 10.0f, "%.1f"))
                { cfg.GcdAntsBorderPadding = padding; changed = true; }
                CfgTooltip("Expand (+) or shrink (-) the ants border relative to the slot edge.\n0 = flush with slot bounds.");

                var xOff = cfg.GcdAntsXOffset;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Horizontal offset (px)##gcd", ref xOff, -20.0f, 20.0f, "%.1f"))
                { cfg.GcdAntsXOffset = xOff; changed = true; }
                CfgTooltip("Shift the ants border left (-) or right (+).");

                var yOff = cfg.GcdAntsYOffset;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Vertical offset (px)##gcd", ref yOff, -20.0f, 20.0f, "%.1f"))
                { cfg.GcdAntsYOffset = yOff; changed = true; }
                CfgTooltip("Shift the ants border up (-) or down (+).");
            }
        }

        if (changed) RequestDeferredUiSettingsSave();
    }

    private void DrawCfgResetSection()
    {
        ImGui.TextUnformatted("Reset");
        ImGui.Separator();

        var red = new Vector4(1.0f, 0.35f, 0.35f, 1.0f);
        ImGui.TextColored(in red, "This will reset all settings to their default values.");
        ImGui.Spacing();

        if (ImGui.Button("Reset to Defaults", default))
        {
            var cfg = plugin.Configuration;
            cfg.OverlayEnabled         = true;
            cfg.OverlayLocked          = true;
            cfg.OverlayPixelsPerSec    = 72.0f;
            cfg.OverlayIconSize        = 44.0f;
            cfg.OverlayTimeBehind      = 1.5f;
            cfg.OverlayBgOpacity       = 1.0f;
            cfg.OverlayPastAlpha       = 1.0f;
            cfg.OverlayFreqThreshold   = 0.0f;
            cfg.OverlayShowGrid        = true;
            cfg.OverlayMaxStackedIcons = 3;
            cfg.OGCDSizeRatio          = 0.75f;
            cfg.OGCDVerticalOffset     = 0.1f;
            cfg.OGCDHorizontalOffset   = 0f;
            cfg.BossBarUseCustomColor  = false;
            cfg.BossBarColor           = new Vector4(0.85f, 0.35f, 0.20f, 1.00f);
            cfg.MainIconSize           = 22.0f;
            cfg.MainIconOpacity        = 1.0f;
            cfg.MainIconScale          = 1.0f;
            cfg.AutoTimelineGcdRecastSec = 2.5f;
            cfg.AutoTimelineDotRefreshBufferSec = 6.0f;
            cfg.DebugEnabled           = false;
            cfg.AutoTimelineDisabledAbilities.Clear();
            cfg.AntsEnabled            = true;
            cfg.AntsCustomEnabled      = true;
            // oGCD ants
            cfg.OgcdAntsEnabled        = true;
            cfg.AntsDurationBefore     = 1.5f;
            cfg.AntsDurationAfter      = 1.5f;
            cfg.AntsColor              = new Vector4(1.0f, 0.85f, 0.0f, 1.0f);
            cfg.AntsGapColor           = new Vector4(0.0f, 0.0f, 0.0f, 0.5f);
            cfg.AntsDashLength         = 6.0f;
            cfg.AntsGapLength          = 4.0f;
            cfg.AntsSpeed              = 40.0f;
            cfg.AntsThickness          = 2.0f;
            cfg.AntsBorderPadding      = 0.0f;
            cfg.AntsXOffset            = -5.0f;
            cfg.AntsYOffset            = 0.0f;
            // GCD ants
            cfg.GcdAntsEnabled         = true;
            cfg.GcdAntsDurationBefore  = 0.5f;
            cfg.GcdAntsDurationAfter   = 0.5f;
            cfg.GcdAntsColor           = new Vector4(0.2f, 0.8f, 1.0f, 1.0f);
            cfg.GcdAntsGapColor        = new Vector4(0.0f, 0.0f, 0.0f, 0.5f);
            cfg.GcdAntsDashLength      = 6.0f;
            cfg.GcdAntsGapLength       = 4.0f;
            cfg.GcdAntsSpeed           = 40.0f;
            cfg.GcdAntsThickness       = 2.0f;
            cfg.GcdAntsBorderPadding   = 0.0f;
            cfg.GcdAntsXOffset         = -5.0f;
            cfg.GcdAntsYOffset         = 0.0f;
            plugin.SaveUiSettings();
        }

        if (ImGui.Button("Clear Cached Logs", default))
        {
            plugin.TimelineStore.ClearAll();
            InvalidateAutoTimelineSkillFilterCache();
            plugin.EncounterTracker.RebuildZoneMappings();

            currentTimeline = null;
            skillVisibility.Clear();
            expandedSkillNodes.Clear();
            HideEmbeddedTimelinePreview();

            cacheClearNotice = "Cleared cached logs.";
            updateProgress = 0f;
        }

        if (!string.IsNullOrWhiteSpace(cacheClearNotice))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(cacheClearNotice);
        }
    }

    private static string FormatAutoStateSnapshot(AutoTimelineState state)
    {
        var gaugeParts = state.GaugeState
            .Where(kvp => kvp.Value > 0)
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => $"{kvp.Key}={kvp.Value}")
            .ToList();
        var actionParts = state.ActionState
            .Where(kvp => kvp.Value > 0)
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => $"{kvp.Key}={kvp.Value}")
            .ToList();

        var parts = new List<string>
        {
            $"lastGcd={state.LastGcdId}",
            $"lastCombo={state.LastComboAbilityName ?? "-"}",
            $"card={state.CardDrawState ?? "-"}",
        };
        if (gaugeParts.Count > 0)
            parts.Add($"gauge[{string.Join(", ", gaugeParts)}]");
        if (actionParts.Count > 0)
            parts.Add($"action[{string.Join(", ", actionParts)}]");

        return string.Join(" | ", parts);
    }

    private AggregatedTimeline PrepareAutoTimelineSourceClone(AggregatedTimeline tl)
    {
        RefreshTimelineRuntimeMetadata(tl);
        var timelineKey = TimelineDatabase.MakeKey(tl.EncounterId, tl.SpecName);
        var isCustomTimeline = plugin.Configuration.CustomTimelines.ContainsKey(timelineKey);
        var storeTimeline = plugin.TimelineStore.GetTimeline(tl.EncounterId, tl.SpecName);
        RefreshTimelineRuntimeMetadata(storeTimeline);
        var rebuiltCachedSource = ApplyAutoTimelineAbilityFilters(
            tl,
            BuildAutoTimelineSourceFromCachedParses(tl));
        if (rebuiltCachedSource.Count > 0)
            tl.AutoTimelineSourceEntries = rebuiltCachedSource;
        else if (!isCustomTimeline &&
                 storeTimeline != null &&
                 !ReferenceEquals(storeTimeline, tl))
        {
            var storeCachedSource = ApplyAutoTimelineAbilityFilters(
                tl,
                BuildAutoTimelineSourceFromCachedParses(storeTimeline));
            if (storeCachedSource.Count > 0)
                tl.AutoTimelineSourceEntries = storeCachedSource;
        }

        if (isCustomTimeline && IsSuspiciousFlattenedCustomSource(tl))
        {
            tl.AutoTimelineSourceEntries = ApplyAutoTimelineAbilityFilters(tl, tl.Entries);
        }

        if (tl.AutoTimelineSourceEntries.Count == 0)
        {
            if (isCustomTimeline)
            {
                tl.AutoTimelineSourceEntries = ApplyAutoTimelineAbilityFilters(tl, tl.Entries);
            }
            else
            {
                tl.AutoTimelineSourceEntries = [];
            }
        }

        var effectiveSourceEntries = ApplyAutoTimelineAbilityFilters(tl, tl.AutoTimelineSourceEntries);
        if (ShouldSupplementAutoTimelineGcdShell(effectiveSourceEntries) &&
            storeTimeline != null &&
            !ReferenceEquals(storeTimeline, tl))
        {
            var donorEntries = ApplyAutoTimelineAbilityFilters(
                tl,
                BuildAutoTimelineSourceFromCachedParses(storeTimeline));
            if (donorEntries.Count > 0)
                effectiveSourceEntries = MergeAutoTimelineSourceWithGcdShell(effectiveSourceEntries, donorEntries);
        }
        effectiveSourceEntries = CollapseDuplicateAutoTimelineEntries(effectiveSourceEntries);

        var sourceTimeline = CloneTimeline(tl);
        sourceTimeline.Entries = effectiveSourceEntries;
        sourceTimeline.AutoTimelineSourceEntries = effectiveSourceEntries
            .Select(CloneTimelineEntry)
            .ToList();
        RefreshTimelineRuntimeMetadata(sourceTimeline);
        return sourceTimeline;
    }

    private bool ShouldSupplementAutoTimelineGcdShell(IReadOnlyCollection<TimelineEntry> entries)
        => entries.Count > 0 && entries.Count(IsGcdEntry) < 12;

    private List<TimelineEntry> FilterAutoTimelineResultEntries(
        IEnumerable<TimelineEntry> entries,
        ISet<int> allowedAbilityIds)
    {
        if (allowedAbilityIds.Count == 0)
            return entries
                .Select(CloneTimelineEntry)
                .ToList();

        return entries
            .Where(entry => allowedAbilityIds.Contains(entry.AbilityId))
            .Select(CloneTimelineEntry)
            .ToList();
    }

    private List<TimelineEntry> FilterVisibleAutoTimelineResultEntries(IEnumerable<TimelineEntry> entries)
        => CollapseDuplicateAutoTimelineEntries(entries
            .Where(entry => entry.TimeOffsetSec >= -0.001));

    private List<TimelineEntry> CollapseDuplicateAutoTimelineEntries(IEnumerable<TimelineEntry> entries)
    {
        return entries
            .GroupBy(
                entry => (
                    entry.AbilityId,
                    entry.IsGcd,
                    TimeSec: Math.Round(entry.TimeOffsetSec, 3)))
            .Select(group =>
            {
                var strongest = group
                    .OrderByDescending(entry => entry.Frequency)
                    .ThenByDescending(entry => entry.AverageUses)
                    .First();
                var collapsed = CloneTimelineEntry(strongest);
                collapsed.Frequency = group.Max(entry => entry.Frequency);
                collapsed.AverageUses = group.Max(entry => entry.AverageUses);
                return collapsed;
            })
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private List<TimelineEntry> MergeAutoTimelineSourceWithGcdShell(
        IEnumerable<TimelineEntry> primaryEntries,
        IEnumerable<TimelineEntry> donorEntries)
    {
        var merged = primaryEntries
            .Select(CloneTimelineEntry)
            .ToList();
        var seenKeys = merged
            .Select(GetAutoEntryIdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var donorEntry in donorEntries
                     .Where(IsGcdEntry)
                     .OrderBy(entry => entry.TimeOffsetSec)
                     .ThenByDescending(entry => entry.Frequency))
        {
            var key = GetAutoEntryIdentityKey(donorEntry);
            if (!seenKeys.Add(key))
                continue;

            merged.Add(CloneTimelineEntry(donorEntry));
        }

        return merged
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private static AggregatedTimeline CloneTimeline(AggregatedTimeline tl)
    {
        return new AggregatedTimeline
        {
            EncounterId = tl.EncounterId,
            EncounterName = tl.EncounterName,
            SpecName = tl.SpecName,
            AverageDurationMs = tl.AverageDurationMs,
            ParseCount = tl.ParseCount,
            Entries = tl.Entries
                .Select(CloneTimelineEntry)
                .ToList(),
            AutoTimelineSourceEntries = tl.AutoTimelineSourceEntries
                .Select(CloneTimelineEntry)
                .ToList(),
            CachedFflogsParses = tl.CachedFflogsParses
                .Select(CloneCachedFflogsParseTimeline)
                .ToList(),
            BossEntries = tl.BossEntries
                .Select(CloneBossTimelineEntry)
                .ToList(),
            DeadSpaceRanges = tl.DeadSpaceRanges
                .Select(CloneDeadSpaceRange)
                .ToList(),
            PhaseInfo = CloneFightPhaseInfo(tl.PhaseInfo),
        };
    }

    private static CachedFflogsParseTimeline CloneCachedFflogsParseTimeline(CachedFflogsParseTimeline parse)
    {
        return new CachedFflogsParseTimeline
        {
            ParseIndex = parse.ParseIndex,
            ReportCode = parse.ReportCode,
            FightId = parse.FightId,
            RankingAmount = parse.RankingAmount,
            DurationSec = parse.DurationSec,
            Entries = parse.Entries
                .Select(CloneTimelineEntry)
                .ToList(),
            BossEntries = parse.BossEntries
                .Select(CloneBossTimelineEntry)
                .ToList(),
            PhaseInfo = CloneFightPhaseInfo(parse.PhaseInfo),
            HasVisiblePhaseEntries = parse.HasVisiblePhaseEntries,
        };
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

    private static BossTimelineEntry CloneBossTimelineEntry(BossTimelineEntry entry)
    {
        return new BossTimelineEntry
        {
            AbilityId = entry.AbilityId,
            AbilityName = entry.AbilityName,
            CastStartSec = entry.CastStartSec,
            CastEndSec = entry.CastEndSec,
            SourceId = entry.SourceId,
            IsPrimaryBoss = entry.IsPrimaryBoss,
        };
    }

    private static DeadSpaceRange CloneDeadSpaceRange(DeadSpaceRange range)
    {
        return new DeadSpaceRange
        {
            StartSec = range.StartSec,
            EndSec = range.EndSec,
        };
    }

    private static TimelineEntry CloneTimelineEntryWithOffset(TimelineEntry entry, double offsetSec)
    {
        var clone = CloneTimelineEntry(entry);
        clone.TimeOffsetSec = Math.Max(0.0, clone.TimeOffsetSec - offsetSec);
        return clone;
    }

    private void RefreshTimelineRuntimeMetadata(AggregatedTimeline? tl)
    {
        if (tl == null)
            return;

        RefreshTimelineEntryRuntimeMetadata(tl.Entries);
        RefreshTimelineEntryRuntimeMetadata(tl.AutoTimelineSourceEntries);
        foreach (var parse in tl.CachedFflogsParses)
            RefreshTimelineEntryRuntimeMetadata(parse.Entries);
    }

    private void RefreshTimelineEntryRuntimeMetadata(IEnumerable<TimelineEntry> entries)
    {
        foreach (var entry in entries)
        {
            var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
            if (info != null)
                entry.IsGcd = info.IsGcdAction;
        }
    }

    private string GetLatestAutoTimelineDebugReport(string specName)
        => lastAutoTimelineDebugReportsBySpec.GetValueOrDefault(specName, string.Empty);

    private void SetLatestAutoTimelineDebugReport(string specName, string report)
    {
        if (string.IsNullOrWhiteSpace(specName))
            return;

        lastAutoTimelineDebugReportsBySpec[specName] = report ?? string.Empty;
    }

    private void SaveAutoTimelineDebugReport(AggregatedTimeline tl)
    {
        var report = GetLatestAutoTimelineDebugReport(tl.SpecName);
        if (string.IsNullOrWhiteSpace(report))
        {
            SetDebugStatus($"No Auto Timeline debug report has been captured yet for {tl.SpecName}. Run Auto Timeline first.", true);
            return;
        }

        SaveDebugTextArtifact(tl, report, "auto_timeline_debug");
    }

    private void SaveCachedFflogsDebugArtifacts(AggregatedTimeline tl, string? runToken = null)
    {
        if (tl.CachedFflogsParses.Count == 0)
            return;

        SaveDebugTextArtifact(tl, BuildCachedFflogsDebugReport(tl), "cached_fflogs_source", runToken);
        SaveCachedFflogsJsonArtifact(tl, runToken);
    }

    private void CopyConflictDebugReport(AggregatedTimeline tl)
    {
        lastConflictDebugReport = BuildConflictDebugReport(tl);
        ImGui.SetClipboardText(lastConflictDebugReport);
        SetDebugStatus("Copied the current conflict debug report to clipboard.");
    }

    private void SaveConflictDebugReport(AggregatedTimeline tl)
    {
        lastConflictDebugReport = BuildConflictDebugReport(tl);
        SaveDebugTextArtifact(tl, lastConflictDebugReport, "conflict_debug");
    }

    private string BuildConflictDebugReport(AggregatedTimeline tl)
    {
        RebuildConflicts(tl);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Conflict Debug | {tl.EncounterName} / {tl.SpecName}");
        sb.AppendLine($"Generated: {DateTime.UtcNow:O}");
        sb.AppendLine($"Player entries: {tl.Entries.Count} | Boss entries: {tl.BossEntries.Count}");
        sb.AppendLine($"Conflicts: {conflictedPlayerIndices.Count}");
        sb.AppendLine();

        if (conflictedPlayerIndices.Count == 0)
        {
            sb.AppendLine("No conflicts detected.");
        }
        else
        {
            sb.AppendLine("Conflicted Entries");
            foreach (var row in tl.Entries
                .Select((entry, index) => new { entry, index })
                .Where(row => conflictedPlayerIndices.Contains(row.index))
                .OrderBy(row => row.entry.TimeOffsetSec)
                .ThenBy(row => row.entry.AbilityName, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"  {FormatTime(row.entry.TimeOffsetSec)} | {row.entry.AbilityName} | {conflictReasons.GetValueOrDefault(row.index, "Unknown conflict")}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Chronological Player Entries");
        foreach (var row in tl.Entries
            .Select((entry, index) => new { entry, index })
            .OrderBy(row => row.entry.TimeOffsetSec)
            .ThenBy(row => row.entry.AbilityName, StringComparer.OrdinalIgnoreCase))
        {
            var marker = conflictedPlayerIndices.Contains(row.index) ? "!" : " ";
            var freq = $"{row.entry.Frequency * 100.0:F0}%";
            sb.AppendLine($"{marker} {FormatTime(row.entry.TimeOffsetSec)} | {row.entry.AbilityName} | freq {freq} | avg {row.entry.AverageUses:F1}");
        }

        return sb.ToString();
    }

    private string BuildCachedFflogsDebugReport(AggregatedTimeline tl)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Cached FFLogs Source | {tl.EncounterName} / {tl.SpecName}");
        sb.AppendLine($"Generated: {DateTime.UtcNow:O}");
        sb.AppendLine($"Cached parse timelines: {tl.CachedFflogsParses.Count}");
        sb.AppendLine();

        foreach (var parse in tl.CachedFflogsParses
                     .OrderBy(parse => parse.ParseIndex)
                     .ThenBy(parse => parse.ReportCode, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(parse => parse.FightId))
        {
            var gcdCount = parse.Entries.Count(IsGcdEntry);
            var ogcdCount = parse.Entries.Count - gcdCount;
            sb.AppendLine($"Parse #{parse.ParseIndex:00} | {parse.ReportCode}#{parse.FightId} | rank {parse.RankingAmount:F2} | duration {parse.DurationSec:F3}s | entries {parse.Entries.Count} | GCD {gcdCount} | oGCD {ogcdCount}");
            foreach (var entry in parse.Entries
                         .OrderBy(entry => entry.TimeOffsetSec)
                         .ThenByDescending(entry => entry.Frequency)
                         .ThenBy(entry => entry.AbilityName, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"  {FormatTime(entry.TimeOffsetSec)} | {(IsGcdEntry(entry) ? "GCD " : "oGCD")} | {entry.AbilityName} | id {entry.AbilityId}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private string GetDebugArtifactsDirectoryPath()
    {
        var path = Path.Combine(plugin.ConfigDirectory, DebugArtifactsDirectoryName);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string SanitizeDebugFileComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray());
        sanitized = Regex.Replace(sanitized, @"\s+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private string BuildDebugArtifactPath(AggregatedTimeline tl, string label, string extension, string? runToken = null)
    {
        var token = string.IsNullOrWhiteSpace(runToken)
            ? DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff")
            : runToken;
        var encounterName = SanitizeDebugFileComponent(string.IsNullOrWhiteSpace(tl.EncounterName) ? $"encounter_{tl.EncounterId}" : tl.EncounterName);
        var specName = SanitizeDebugFileComponent(tl.SpecName);
        return Path.Combine(GetDebugArtifactsDirectoryPath(), $"{token}_{encounterName}_{specName}_{label}.{extension}");
    }

    private void SaveDebugTextArtifact(AggregatedTimeline tl, string contents, string label, string? runToken = null)
    {
        try
        {
            var path = BuildDebugArtifactPath(tl, label, "txt", runToken);
            File.WriteAllText(path, contents);
            SetDebugStatus($"Saved {label.Replace('_', ' ')} to {path}");
        }
        catch (Exception ex)
        {
            SetDebugStatus($"Failed to save {label.Replace('_', ' ')}: {ex.Message}", true);
        }
    }

    private void SaveTimelineSnapshot(AggregatedTimeline tl, string label, string? runToken = null)
    {
        try
        {
            var path = BuildDebugArtifactPath(tl, label, "json", runToken);
            var json = System.Text.Json.JsonSerializer.Serialize(
                CloneTimeline(tl),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            SetDebugStatus($"Saved {label.Replace('_', ' ')} to {path}");
        }
        catch (Exception ex)
        {
            SetDebugStatus($"Failed to save {label.Replace('_', ' ')}: {ex.Message}", true);
        }
    }

    private void SaveCachedFflogsJsonArtifact(AggregatedTimeline tl, string? runToken = null)
    {
        try
        {
            var path = BuildDebugArtifactPath(tl, "cached_fflogs_source", "json", runToken);
            var json = System.Text.Json.JsonSerializer.Serialize(
                tl.CachedFflogsParses
                    .Select(CloneCachedFflogsParseTimeline)
                    .ToList(),
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            SetDebugStatus($"Saved cached fflogs source to {path}");
        }
        catch (Exception ex)
        {
            SetDebugStatus($"Failed to save cached fflogs source: {ex.Message}", true);
        }
    }

    private void OpenDebugArtifactsDirectory()
    {
        try
        {
            var path = GetDebugArtifactsDirectoryPath();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            SetDebugStatus($"Opened debug artifacts folder: {path}");
        }
        catch (Exception ex)
        {
            SetDebugStatus($"Failed to open debug artifacts folder: {ex.Message}", true);
        }
    }

    private static void CfgTooltip(string text)
    {
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text(text);
            ImGui.EndTooltip();
        }
    }
}

