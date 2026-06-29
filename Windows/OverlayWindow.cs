using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using ATKTip.Data;
using GameObjectId = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId;
using static FFXIVClientStructs.FFXIV.Client.Game.Character.ActionEffectHandler;

namespace ATKTip.Windows;

/// <summary>
/// Overlay window that renders an ATR-exact two-pass timeline:
///   Pass 1 (General layer) â€” horizontal GCD/oGCD bars
///   Pass 2 (Icon layer)    â€” action icons drawn on top of bars
/// Boss casts are rendered in a strip anchored to the bottom edge.
/// </summary>
public sealed class OverlayWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ICondition condition;
    private readonly IDutyState dutyState;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Hook<ActionManager.Delegates.UseAction>? useActionHook;
    private readonly Hook<ReceiveActionEffectDelegate>? receiveActionEffectHook;
    // â”€â”€ Auto-execute (hidden feature) â”€â”€
    /// <summary>
    /// When true, each timeline entry is automatically executed via
    /// <c>ActionManager.UseAction</c> the moment it crosses the red bar during live combat.
    /// Enabled by clicking the Config tab label 7 times in a row in the main window.
    /// </summary>
    public bool AutoExecuteEnabled { get; set; }
    private readonly Dictionary<ulong, uint> observedBossCastIds = [];
    private double autoScrubLastResolvedBossTimeSec = double.NegativeInfinity;
    private double autoScrubLastObservedElapsedSec = double.NegativeInfinity;
    private double autoScrubLastSeekTimeSec = double.NaN;
    private DateTime autoScrubLastSeekAtUtc = DateTime.MinValue;

    // â”€â”€ ATR geometry constants (match ATR defaults exactly) â”€â”€
    // GCDHeightHigh / GCDHeightLow control the vertical extent of the GCD bar.
    // ATR defaults: high=0.5, low=0.8 â†’ bar spans centerY+0 to centerY+0.3*iconSize.
    private const float BaseActionAnimationLockSec = 0.50f;
    private const float AssumedAnimationLockLatencySec = 0.02f;
    private const float GCDHeightHigh  = 0.5f;
    private const float GCDHeightLow   = 0.8f;
    // GCD window duration (no live cast data available, use the standard 2.5 s).
    private const float GCDWindowSec   = 2.5f;
    // oGCD animlock window (shorter bar for oGCDs).
    private const float OGCDWindowSec  = BaseActionAnimationLockSec + AssumedAnimationLockLatencySec;
    private const double OverlayMinOgcdVisualGapSec = 0.8;
    private const double AutoRetryIntervalSec = 0.10;
    private const double AutoConflictToleranceSec = 0.099;
    private const double AutoConflictComparisonEpsilonSec = 0.000001;
    private const double AutoRebaseJumpThresholdSec = 0.50;
    private const double AutoPendingResetGraceSec = 0.20;
    private const double AutoAcceptedInstantRetrySec = 0.75;
    // Rounding radius for bars (matches ATR GCDRound default ~4).
    private const float BarRound       = 4.0f;
    // Gap between stacked icons in the same bucket.
    private const float IconGap        = 2.0f;

    // â”€â”€ ATR-exact colours â”€â”€
    // GCD bar: dark background, blue cast fill, white border.
    private static readonly Vector4 ColGCDBackground  = new(0.20f, 0.20f, 0.20f, 1.00f);
    private static readonly Vector4 ColGCDCast        = new(0.40f, 0.60f, 0.90f, 1.00f);
    private static readonly Vector4 ColGCDBorder      = new(1.00f, 1.00f, 1.00f, 0.80f);
    // oGCD bar: same dark background, green cast fill (distinguishes from GCDs).
    private static readonly Vector4 ColOGCDBackground = new(0.20f, 0.20f, 0.20f, 1.00f);
    private static readonly Vector4 ColOGCDCast       = new(0.40f, 0.85f, 0.45f, 1.00f);
    private static readonly Vector4 ColOGCDBorder     = new(1.00f, 1.00f, 1.00f, 0.70f);
    // Center guide line (ATR GridCenterLine).
    private static readonly Vector4 ColCenterLine     = new(0.80f, 0.80f, 0.80f, 0.50f);
    // Now / start line (ATR GridStartLine â€” red vertical bar at current time).
    private static readonly Vector4 ColNowLine        = new(0.80f, 0.20f, 0.20f, 1.00f);
    // Grid lines.
    private static readonly Vector4 ColGrid           = new(1.00f, 1.00f, 1.00f, 0.12f);
    private static readonly Vector4 ColGridMajor      = new(1.00f, 1.00f, 1.00f, 0.28f);
    private static readonly Vector4 ColGridLabel      = new(1.00f, 1.00f, 1.00f, 0.22f);

    // â”€â”€ Combat tracking â”€â”€
    private bool     inCombat;
    private DateTime combatStartTime;
    private double   combatElapsedSec;
    /// <summary>When true the view is frozen at <see cref="combatElapsedSec"/> so the
    /// user can study upcoming abilities without the red line advancing.</summary>
    private bool     combatViewPaused;
    private bool     awaitingManualPhaseStart;
    private bool     manualOverlayActive;
    /// <summary>Set when the user clicks Ã— to close the overlay mid-combat.
    /// Suppresses ants and auto-execute until a fresh timeline is loaded.</summary>
    private bool     overlayDismissed;
    private bool     antsArmed;
    /// <summary>
    /// True when the active timeline was loaded because the player entered a
    /// mapped encounter zone (set by <see cref="PrepareCombatPreview"/>).
    /// When false (e.g. user manually loaded from the viewer), combat events are
    /// ignored so attacking a training dummy doesn't accidentally start playback.
    /// </summary>
    private bool     isEncounterZone;

    // â”€â”€ Preview (scrub) mode â”€â”€
    private bool     isPreview;
    private bool     previewAutoplay;
    private DateTime previewStartTime;
    private double   previewManualTimeSec;
    private bool     isScrubbing;

    private AggregatedTimeline?      embeddedPreviewTimeline;
    private string                   embeddedPreviewTimelineKey = string.Empty;
    private readonly Dictionary<int, bool> embeddedSkillVisibility = [];
    private bool                     embeddedPreviewAutoplay;
    private DateTime                 embeddedPreviewStartTime;
    private double                   embeddedPreviewElapsedSec;
    private double                   embeddedPreviewManualTimeSec;
    private bool                     embeddedPreviewIsScrubbing;

    // â”€â”€ Timeline data â”€â”€
    private AggregatedTimeline?      activeTimeline;
    private string                   activeTimelineKey = string.Empty;
    private Dictionary<int, bool>    skillVisibility   = [];

    // â”€â”€ Caches â”€â”€
    private readonly Dictionary<int, uint> iconIdCache = [];
    private readonly ConcurrentDictionary<int, bool> oGcdCache = new();
    private AggregatedTimeline? cachedActiveFilteredTimeline;
    private int cachedActiveFilteredRevision = -1;
    private int activeTimelineFilterRevision;
    private readonly List<TimelineEntry> cachedActiveFilteredEntries = [];
    private readonly List<TimelineEntry> cachedActiveFilteredGcdEntries = [];
    private readonly List<TimelineEntry> cachedActiveFilteredOgcdEntries = [];
    private AggregatedTimeline? cachedEmbeddedFilteredTimeline;
    private int cachedEmbeddedFilteredRevision = -1;
    private int embeddedTimelineFilterRevision;
    private readonly List<TimelineEntry> cachedEmbeddedFilteredEntries = [];
    private readonly List<TimelineEntry> cachedEmbeddedFilteredGcdEntries = [];
    private readonly List<TimelineEntry> cachedEmbeddedFilteredOgcdEntries = [];
    private readonly List<TimelineEntry> activeVisibleEntriesScratch = [];
    private readonly List<TimelineEntry> embeddedVisibleEntriesScratch = [];
    private readonly HashSet<int> cachedAntsGcdIds = [];
    private readonly HashSet<int> cachedAntsOgcdIds = [];
    private AggregatedTimeline? cachedAntsTimeline;
    private int cachedAntsRevision = -1;
    private long cachedAntsElapsedBucket = long.MinValue;

    private Configuration Cfg => plugin.Configuration;

    private sealed class OverlayGcdBucket
    {
        public float X { get; init; }
        public bool IsPast { get; init; }
        public List<TimelineEntry> Entries { get; init; } = [];
    }

    private sealed class OverlayOgcdPlacement
    {
        public required TimelineEntry Entry { get; init; }
        public double DisplayTimeSec { get; set; }
        public float X { get; set; }
        public bool IsPast { get; set; }
    }

    private unsafe delegate void ReceiveActionEffectDelegate(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        Header* header,
        TargetEffects* effects,
        GameObjectId* targetEntityIds);

    private sealed class AutoLaneState
    {
        public List<TimelineEntry> Entries { get; } = [];
        public int NextIndex { get; set; }
        public double DisplayTimeSec { get; set; }
        public TimelineEntry? PendingEntry { get; set; }
        public DateTime LastAttemptAtUtc { get; set; } = DateTime.MinValue;
        public DateTime RequestAcceptedAtUtc { get; set; } = DateTime.MinValue;
        public bool RequestAccepted { get; set; }
        public bool RecastObserved { get; set; }
        public bool CastObserved { get; set; }
        public bool CompletionObserved { get; set; }
    }

    private readonly AutoLaneState autoGcdLane = new();
    private readonly AutoLaneState autoOgcdLane = new();
    private string autoTimelineKey = string.Empty;
    private bool autoRuntimeInitialized;
    private double autoBaseTimeSec = double.NaN;
    private double autoLastObservedBaseTimeSec = double.NaN;
    private DateTime autoLastObservedBaseAtUtc = DateTime.MinValue;
    private double autoDisplayGcdTimeSec;
    private double autoDisplayOgcdTimeSec;

    private sealed class ObservedBossCast
    {
        public uint AbilityId { get; init; }
        public ulong MaxHp { get; init; }
        public ulong CurrentHp { get; init; }
        public bool IsPrimaryBoss { get; init; }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    public unsafe OverlayWindow(Plugin plugin, ICondition condition, IDutyState dutyState, IObjectTable objectTable, IFramework framework, IGameInteropProvider gameInterop, IPluginLog log)
        : base("ATKTip##Overlay",
            ImGuiWindowFlags.NoTitleBar      |
            ImGuiWindowFlags.NoScrollbar     |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse)
    {
        this.plugin     = plugin;
        this.condition  = condition;
        this.dutyState  = dutyState;
        this.objectTable = objectTable;
        this.framework  = framework;
        this.log        = log;
        useActionHook   = gameInterop.HookFromAddress<ActionManager.Delegates.UseAction>(
            ActionManager.MemberFunctionPointers.UseAction,
            HandleUseAction);
        receiveActionEffectHook = gameInterop.HookFromAddress<ReceiveActionEffectDelegate>(
            Addresses.Receive.Value,
            ReceiveActionEffectDetour);

        condition.ConditionChange += OnConditionChange;
        dutyState.DutyWiped      += OnDutyWiped;
        dutyState.DutyCompleted  += OnDutyCompleted;
        framework.Update         += OnFrameworkUpdate;
        useActionHook.Enable();
        receiveActionEffectHook.Enable();

        RespectCloseHotkey = false;
        IsOpen = false;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 128),
            MaximumSize = new Vector2(float.MaxValue, 400),
        };
        Size = new Vector2(
            MathF.Max(1280f, plugin.Configuration.OverlayPixelsPerSec * 18f),
            MathF.Max(164f, plugin.Configuration.OverlayIconSize * 2.6f + 42f));
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    // â”€â”€ Public API â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>True when a timeline is loaded and ready to display.</summary>
    public bool HasActiveTimeline => activeTimeline != null;
    public bool IsEmbeddedPreviewScrubbing => embeddedPreviewIsScrubbing;

    public void InvalidateTimelineCaches()
    {
        unchecked
        {
            activeTimelineFilterRevision++;
        }

        cachedAntsTimeline = null;
        cachedAntsRevision = -1;
        cachedAntsElapsedBucket = long.MinValue;
    }

    public bool CanManageAbilityAnts()
        => Cfg.AntsEnabled &&
           IsOpen &&
           antsArmed &&
           !overlayDismissed &&
           activeTimeline != null &&
           (inCombat || isPreview) &&
           (!manualOverlayActive || inCombat);

    public void SetTimeline(AggregatedTimeline? timeline, string key, bool resetDismissed = true)
    {
        activeTimeline    = timeline;
        activeTimelineKey = key;
        if (resetDismissed)
            overlayDismissed = false;      // fresh load â€” re-enable ants and auto-exec
        antsArmed         = false;
        skillVisibility.Clear();
        oGcdCache.Clear();
        ResetAutoRuntimeState();
        ResetAutoScrubState();
        InvalidateTimelineCaches();

        if (timeline != null)
        {
            PopulateTimelineVisibility(timeline, key, skillVisibility);
            ClassifyTimelineEntries(timeline);
        }
    }

    private void PopulateTimelineVisibility(
        AggregatedTimeline timeline,
        string key,
        Dictionary<int, bool> target)
    {
        target.Clear();

        var hidden = plugin.Configuration.HiddenAbilities.GetValueOrDefault(key);
        foreach (var id in timeline.Entries.Select(e => e.AbilityId).Distinct())
            target[id] = hidden == null || !hidden.Contains(id);
    }

    private void ClassifyTimelineEntries(AggregatedTimeline timeline)
    {
        foreach (var e in timeline.Entries)
            e.IsGcd = !IsOGCD(e.AbilityId, e.AbilityName);
    }

    private void EnsureActiveFilteredEntriesCache()
    {
        var timeline = activeTimeline;
        if (timeline == null)
            return;

        if (ReferenceEquals(cachedActiveFilteredTimeline, timeline) &&
            cachedActiveFilteredRevision == activeTimelineFilterRevision)
            return;

        cachedActiveFilteredTimeline = timeline;
        cachedActiveFilteredRevision = activeTimelineFilterRevision;
        cachedActiveFilteredEntries.Clear();
        cachedActiveFilteredGcdEntries.Clear();
        cachedActiveFilteredOgcdEntries.Clear();

        cachedActiveFilteredEntries.AddRange(TimelineJobRules.ApplyPostSelectionRules(
            timeline.SpecName,
            timeline.Entries,
            promoteMacrocosmosToVisualGcd: true)
            .Where(e => skillVisibility.GetValueOrDefault(e.AbilityId, true))
            .Where(e => e.Frequency >= GetAbilityThreshold(timeline, e.AbilityId))
            .OrderBy(e => e.TimeOffsetSec)
            .ThenByDescending(e => e.Frequency));

        foreach (var entry in cachedActiveFilteredEntries)
        {
            if (entry.IsGcd)
                cachedActiveFilteredGcdEntries.Add(entry);
            else
                cachedActiveFilteredOgcdEntries.Add(entry);
        }
    }

    private void EnsureEmbeddedFilteredEntriesCache()
    {
        var timeline = embeddedPreviewTimeline;
        if (timeline == null)
            return;

        if (ReferenceEquals(cachedEmbeddedFilteredTimeline, timeline) &&
            cachedEmbeddedFilteredRevision == embeddedTimelineFilterRevision)
            return;

        cachedEmbeddedFilteredTimeline = timeline;
        cachedEmbeddedFilteredRevision = embeddedTimelineFilterRevision;
        cachedEmbeddedFilteredEntries.Clear();
        cachedEmbeddedFilteredGcdEntries.Clear();
        cachedEmbeddedFilteredOgcdEntries.Clear();

        cachedEmbeddedFilteredEntries.AddRange(TimelineJobRules.ApplyPostSelectionRules(
            timeline.SpecName,
            timeline.Entries,
            promoteMacrocosmosToVisualGcd: true)
            .Where(e => embeddedSkillVisibility.GetValueOrDefault(e.AbilityId, true))
            .Where(e => e.Frequency >= GetAbilityThreshold(timeline, e.AbilityId))
            .OrderBy(e => e.TimeOffsetSec)
            .ThenByDescending(e => e.Frequency));

        foreach (var entry in cachedEmbeddedFilteredEntries)
        {
            if (entry.IsGcd)
                cachedEmbeddedFilteredGcdEntries.Add(entry);
            else
                cachedEmbeddedFilteredOgcdEntries.Add(entry);
        }
    }

    private static void CollectEntriesInTimeWindow(
        IReadOnlyList<TimelineEntry> source,
        double minTimeSec,
        double maxTimeSec,
        List<TimelineEntry> target)
    {
        target.Clear();
        for (var i = 0; i < source.Count; i++)
        {
            var entry = source[i];
            if (entry.TimeOffsetSec < minTimeSec)
                continue;
            if (entry.TimeOffsetSec > maxTimeSec)
                break;
            target.Add(entry);
        }
    }

    private void EnsureAntsAbilityCache(double elapsed)
    {
        var timeline = activeTimeline;
        if (timeline == null)
            return;

        EnsureActiveFilteredEntriesCache();
        var elapsedBucket = (long)Math.Round(elapsed * 20.0);
        if (ReferenceEquals(cachedAntsTimeline, timeline) &&
            cachedAntsRevision == activeTimelineFilterRevision &&
            cachedAntsElapsedBucket == elapsedBucket)
            return;

        cachedAntsTimeline = timeline;
        cachedAntsRevision = activeTimelineFilterRevision;
        cachedAntsElapsedBucket = elapsedBucket;
        cachedAntsGcdIds.Clear();
        cachedAntsOgcdIds.Clear();

        if (Cfg.OgcdAntsEnabled)
        {
            var before = (double)Cfg.AntsDurationBefore;
            var after = (double)Cfg.AntsDurationAfter;
            var placements = BuildOverlayOgcdPlacements(
                cachedActiveFilteredOgcdEntries,
                elapsed,
                0f,
                Math.Max(Cfg.OverlayPixelsPerSec, 1f),
                Cfg.OverlayMaxStackedIcons);

            foreach (var placement in placements)
            {
                var rel = placement.DisplayTimeSec - elapsed;
                if (rel >= -after && rel <= before)
                    cachedAntsOgcdIds.Add(placement.Entry.AbilityId);
            }
        }

        if (Cfg.GcdAntsEnabled)
        {
            var before = (double)Cfg.GcdAntsDurationBefore;
            var after = (double)Cfg.GcdAntsDurationAfter;
            TimelineEntry? bestGcd = null;
            var minAbsRel = double.MaxValue;
            foreach (var entry in cachedActiveFilteredGcdEntries)
            {
                var rel = entry.TimeOffsetSec - elapsed;
                if (rel < -after || rel > before)
                    continue;

                var absRel = Math.Abs(rel);
                if (absRel < minAbsRel)
                {
                    minAbsRel = absRel;
                    bestGcd = entry;
                }
            }

            if (bestGcd != null)
                cachedAntsGcdIds.Add(bestGcd.AbilityId);
        }
    }

    public void StartPreview(AggregatedTimeline timeline)
    {
        var key = TimelineDatabase.MakeKey(timeline.EncounterId, timeline.SpecName);
        SetTimeline(timeline, key);
        antsArmed            = true;
        isPreview            = true;
        previewAutoplay      = false;   // start paused â€” user presses play to begin
        previewStartTime     = DateTime.UtcNow;
        previewManualTimeSec = 0;
        combatElapsedSec     = 0;
        isScrubbing          = false;
        awaitingManualPhaseStart = false;
        IsOpen               = true;
    }

    public void SwitchCombatTimeline(AggregatedTimeline timeline, string key)
    {
        var preserveDismissed = overlayDismissed;
        var preserveOpen = IsOpen;

        SetTimeline(timeline, key, resetDismissed: false);
        manualOverlayActive = false;
        overlayDismissed  = preserveDismissed;
        antsArmed         = !overlayDismissed;
        isEncounterZone   = true;
        isPreview         = false;
        previewAutoplay   = false;
        previewManualTimeSec = 0;
        isScrubbing       = false;
        combatViewPaused  = false;
        awaitingManualPhaseStart = false;
        inCombat          = true;
        combatStartTime   = DateTime.UtcNow;
        combatElapsedSec  = 0;
        IsOpen = preserveOpen && !overlayDismissed && Cfg.OverlayEnabled;
    }

    public void SwitchCombatTimelinePaused(AggregatedTimeline timeline, string key)
    {
        SwitchCombatTimeline(timeline, key);
        PauseCombatViewAtCurrentTime(resetToStart: true, armForManualPhaseStart: true);
    }

    public void StopPreview()
    {
        SetTimeline(null, string.Empty);
        manualOverlayActive = false;
        antsArmed            = false;
        isPreview            = false;
        previewAutoplay      = false;
        combatElapsedSec     = 0;
        previewManualTimeSec = 0;
        isScrubbing          = false;
        awaitingManualPhaseStart = false;
        isEncounterZone      = false;
        IsOpen               = false;
    }

    public void ClearForPhaseTransition()
    {
        SetTimeline(null, string.Empty, resetDismissed: false);
        manualOverlayActive = false;
        antsArmed            = false;
        isPreview            = false;
        previewAutoplay      = false;
        previewManualTimeSec = 0;
        combatElapsedSec     = 0;
        isScrubbing          = false;
        inCombat             = false;
        combatViewPaused     = false;
        awaitingManualPhaseStart = false;
        isEncounterZone      = true;
        IsOpen               = false;
    }

    public void ResetEmbeddedPreview()
    {
        embeddedPreviewTimeline      = null;
        embeddedPreviewTimelineKey   = string.Empty;
        embeddedSkillVisibility.Clear();
        embeddedPreviewAutoplay      = false;
        embeddedPreviewElapsedSec    = 0;
        embeddedPreviewManualTimeSec = 0;
        embeddedPreviewIsScrubbing   = false;
        unchecked
        {
            embeddedTimelineFilterRevision++;
        }
    }

    public bool DrawEmbeddedPreview(AggregatedTimeline timeline, Vector2 size, string childId)
    {
        EnsureEmbeddedPreviewTimeline(timeline);

        if (!ImGui.BeginChild(childId, size, true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.EndChild();
            return true;
        }

        var keepOpen = embeddedPreviewTimeline == null || DrawEmbeddedPreviewContents(embeddedPreviewTimeline);

        ImGui.EndChild();
        return keepOpen;
    }

    private void EnsureEmbeddedPreviewTimeline(AggregatedTimeline timeline)
    {
        var key = TimelineDatabase.MakeKey(timeline.EncounterId, timeline.SpecName);
        if (ReferenceEquals(embeddedPreviewTimeline, timeline) &&
            string.Equals(embeddedPreviewTimelineKey, key, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        embeddedPreviewTimeline    = timeline;
        embeddedPreviewTimelineKey = key;
        PopulateTimelineVisibility(timeline, key, embeddedSkillVisibility);
        ClassifyTimelineEntries(timeline);
        unchecked
        {
            embeddedTimelineFilterRevision++;
        }

        embeddedPreviewAutoplay      = false;
        embeddedPreviewStartTime     = DateTime.UtcNow;
        embeddedPreviewElapsedSec    = 0;
        embeddedPreviewManualTimeSec = 0;
        embeddedPreviewIsScrubbing   = false;
    }

    /// <summary>
    /// Clears the active timeline and closes the overlay when the player leaves
    /// a mapped instance. Does not affect manually-started previews.
    /// </summary>
    public void ClearForZoneChange()
    {
        SetTimeline(null, string.Empty);
        manualOverlayActive = false;
        antsArmed         = false;
        isPreview        = false;
        previewAutoplay  = false;
        combatElapsedSec = 0;
        inCombat         = false;
        isScrubbing      = false;
        awaitingManualPhaseStart = false;
        isEncounterZone  = false;
        IsOpen           = false;
    }

    /// <summary>
    /// Opens the overlay in a paused preview at t=0 so the player can study the
    /// timeline before the pull. When combat starts, OnConditionChange locks the
    /// overlay and switches to live tracking automatically.
    /// </summary>
    public void PrepareCombatPreview(AggregatedTimeline timeline, string? key = null)
    {
        var resolvedKey = string.IsNullOrWhiteSpace(key)
            ? TimelineDatabase.MakeKey(timeline.EncounterId, timeline.SpecName)
            : key;
        SetTimeline(timeline, resolvedKey);
        manualOverlayActive = false;
        // Manual/zone preview should show ability ants immediately while the user
        // studies or plays the timeline before the pull starts.
        antsArmed            = true;
        isPreview            = true;
        previewAutoplay      = false;   // paused â€” user scrubs freely
        previewStartTime     = DateTime.UtcNow;
        previewManualTimeSec = 0;
        combatElapsedSec     = 0;
        isScrubbing          = false;
        isEncounterZone      = true;    // loaded by EncounterTracker â€” respond to combat events
        IsOpen               = true;
    }

    public void LoadManualOverlay(AggregatedTimeline timeline, string? key = null)
    {
        var resolvedKey = string.IsNullOrWhiteSpace(key)
            ? TimelineDatabase.MakeKey(timeline.EncounterId, timeline.SpecName)
            : key;
        SetTimeline(timeline, resolvedKey);
        manualOverlayActive = true;
        antsArmed           = false;
        isPreview           = false;
        previewAutoplay     = false;
        previewManualTimeSec = 0;
        isScrubbing         = false;
        isEncounterZone     = false;
        inCombat            = false;
        combatViewPaused    = false;
        awaitingManualPhaseStart = false;
        combatStartTime     = DateTime.UtcNow;
        combatElapsedSec    = 0;
        IsOpen              = true;
    }

    public void StopManualOverlay()
    {
        StopPreview();
    }

    private void PauseCombatViewAtCurrentTime(bool resetToStart = false, bool armForManualPhaseStart = false)
    {
        if (resetToStart)
            combatElapsedSec = 0;
        else
            combatElapsedSec = Math.Max(0, (DateTime.UtcNow - combatStartTime).TotalSeconds);

        combatViewPaused = true;
        awaitingManualPhaseStart = armForManualPhaseStart;
    }

    private void ResumeCombatViewFromCurrentTime()
    {
        combatStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(Math.Max(0, combatElapsedSec));
        combatViewPaused = false;
        awaitingManualPhaseStart = false;
    }

    private void ResetAutoScrubState()
    {
        observedBossCastIds.Clear();
        autoScrubLastResolvedBossTimeSec = double.NegativeInfinity;
        autoScrubLastObservedElapsedSec = double.NegativeInfinity;
        autoScrubLastSeekTimeSec = double.NaN;
        autoScrubLastSeekAtUtc = DateTime.MinValue;
    }

    private void SeekTimelineToTime(double timeSec)
    {
        var fightDur = activeTimeline?.AverageDurationMs / 1000.0 ?? 0.0;
        var clampedTime = Math.Clamp(timeSec, 0.0, fightDur > 0 ? fightDur : Math.Max(0.0, timeSec));
        combatElapsedSec = clampedTime;

        if (isPreview)
        {
            previewManualTimeSec = clampedTime;
            previewStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(clampedTime);
        }

        if (inCombat)
            combatStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(clampedTime);
    }

    private void ProcessAutoScrub()
    {
        if (activeTimeline == null ||
            activeTimeline.BossEntries.Count == 0 ||
            overlayDismissed ||
            manualOverlayActive ||
            isScrubbing ||
            awaitingManualPhaseStart)
        {
            return;
        }

        if (!inCombat && !isPreview)
            return;

        if (inCombat && combatViewPaused)
            return;

        var currentElapsedSec = GetBaseTimelineElapsedSec();

        if (!double.IsNegativeInfinity(autoScrubLastObservedElapsedSec) &&
            currentElapsedSec < autoScrubLastObservedElapsedSec - 5.0)
        {
            ResetAutoScrubState();
        }

        autoScrubLastObservedElapsedSec = currentElapsedSec;

        foreach (var cast in CollectObservedBossCastStarts())
        {
            if (!TryResolveAutoScrubBossEntry((int)cast.AbilityId, currentElapsedSec, out var matchedEntry))
                continue;
            autoScrubLastSeekTimeSec = matchedEntry.CastStartSec;
            autoScrubLastSeekAtUtc = DateTime.UtcNow;
            SeekTimelineToTime(matchedEntry.CastStartSec);
            autoScrubLastResolvedBossTimeSec = Math.Max(autoScrubLastResolvedBossTimeSec, matchedEntry.CastStartSec);
            return;
        }
    }

    private List<ObservedBossCast> CollectObservedBossCastStarts()
    {
        var observedCasts = new List<ObservedBossCast>();
        var currentActorStates = new Dictionary<ulong, uint>();
        var primaryBossDataId = GetObservedPrimaryBossDataId();

        foreach (var obj in objectTable)
        {
            if (obj == null ||
                obj.ObjectKind != ObjectKind.BattleNpc ||
                !obj.IsTargetable ||
                obj.IsDead ||
                obj is not IBattleNpc battleNpc ||
                obj is not IBattleChara battleChara ||
                obj is not ICharacter character ||
                character.CurrentHp == 0 ||
                character.MaxHp == 0)
            {
                continue;
            }

            var currentCastId = battleChara.IsCasting ? battleChara.CastActionId : 0;
            currentActorStates[obj.GameObjectId] = currentCastId;
            if (currentCastId == 0)
                continue;

            if (observedBossCastIds.TryGetValue(obj.GameObjectId, out var previousCastId) &&
                previousCastId == currentCastId)
            {
                continue;
            }

            observedCasts.Add(new ObservedBossCast
            {
                AbilityId = currentCastId,
                MaxHp = character.MaxHp,
                CurrentHp = character.CurrentHp,
                IsPrimaryBoss = primaryBossDataId != 0 && battleNpc.NameId == primaryBossDataId,
            });
        }

        var staleActorIds = observedBossCastIds.Keys
            .Where(gameObjectId => !currentActorStates.ContainsKey(gameObjectId))
            .ToList();
        foreach (var gameObjectId in staleActorIds)
            observedBossCastIds.Remove(gameObjectId);

        foreach (var actorState in currentActorStates)
            observedBossCastIds[actorState.Key] = actorState.Value;

        return observedCasts
            .OrderByDescending(cast => cast.IsPrimaryBoss)
            .ThenByDescending(cast => cast.MaxHp)
            .ThenByDescending(cast => cast.CurrentHp)
            .ToList();
    }

    private uint GetObservedPrimaryBossDataId()
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

    private bool TryResolveAutoScrubBossEntry(int abilityId, double currentElapsedSec, out BossTimelineEntry matchedEntry)
    {
        matchedEntry = null!;
        if (activeTimeline == null || activeTimeline.BossEntries.Count == 0)
            return false;

        BossTimelineEntry? bestFutureMatch = null;
        BossTimelineEntry? bestNearestMatch = null;
        var bestNearestDistance = double.MaxValue;
        var initialAnchor = Math.Max(0.0, currentElapsedSec - 1.5);
        var resolvedAnchor = double.IsNegativeInfinity(autoScrubLastResolvedBossTimeSec)
            ? double.NegativeInfinity
            : autoScrubLastResolvedBossTimeSec + 0.05;

        foreach (var entry in activeTimeline.BossEntries)
        {
            if (entry.AbilityId != abilityId)
                continue;

            if (double.IsNegativeInfinity(autoScrubLastResolvedBossTimeSec) && entry.CastStartSec >= initialAnchor)
            {
                matchedEntry = entry;
                return true;
            }

            if (entry.CastStartSec >= resolvedAnchor && bestFutureMatch == null)
                bestFutureMatch = entry;

            var distance = Math.Abs(entry.CastStartSec - currentElapsedSec);
            if (distance < bestNearestDistance)
            {
                bestNearestDistance = distance;
                bestNearestMatch = entry;
            }
        }

        if (bestFutureMatch != null)
        {
            matchedEntry = bestFutureMatch;
            return true;
        }

        if (bestNearestMatch != null)
        {
            matchedEntry = bestNearestMatch;
            return true;
        }

        return false;
    }

    private unsafe bool HandleUseAction(
        ActionManager* actionManager,
        ActionType actionType,
        uint actionId,
        ulong targetId,
        uint extraParam,
        ActionManager.UseActionMode mode,
        uint comboRouteId,
        bool* outOptAreaTargeted)
    {
        var result = useActionHook!.Original(actionManager, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);

        var startedAreaTargeting = outOptAreaTargeted != null && *outOptAreaTargeted;
        if (result &&
            !startedAreaTargeting &&
            awaitingManualPhaseStart &&
            inCombat &&
            combatViewPaused &&
            actionType == ActionType.Action &&
            IsHostilePlayerAction(actionId, targetId))
        {
            ResumeCombatViewFromCurrentTime();
        }

        if (result &&
            !startedAreaTargeting &&
            actionType == ActionType.Action)
        {
            ObservePendingAutoActionRequest(actionId);
        }

        return result;
    }

    private bool IsHostilePlayerAction(uint actionId, ulong targetId)
    {
        if (actionId == 0 || targetId == 0 || targetId == 0xE0000000UL)
            return false;

        try
        {
            var sheet = plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            var row = sheet?.GetRowOrDefault(actionId);
            if (!row.HasValue)
                return false;

            var action = row.Value;
            return action.ClassJobCategory.RowId != 0 &&
                   action.CanTargetHostile &&
                   action.ActionCategory.RowId is 2 or 3 or 4;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="abilityId"/> should have native FFXIV ants
    /// this frame.  Called from the <c>IsActionHighlighted</c> hook on the game thread.
    /// Uses per-type (GCD / oGCD) timing windows and enable flags.
    /// </summary>
    public bool IsAbilityInAntsWindow(int abilityId)
    {
        if (!CanManageAbilityAnts()) return false;
        if (activeTimeline == null) return false;

        // Compute elapsed time fresh so the hook (game thread) stays in sync with
        // the red line regardless of when the last Draw() frame ran.
        var elapsed = inCombat
            ? (DateTime.UtcNow - combatStartTime).TotalSeconds
            : combatElapsedSec;   // preview uses the scrub position set on the UI thread
        EnsureAntsAbilityCache(elapsed);
        return cachedAntsOgcdIds.Contains(abilityId) || cachedAntsGcdIds.Contains(abilityId);
    }

    /// <summary>
    /// Returns the sets of ability IDs that should display custom ants this frame,
    /// split by type so <see cref="AntsController"/> can render each with its own style.
    /// </summary>
    public (HashSet<int> Gcd, HashSet<int> Ogcd) GetAntsAbilityIds()
    {
        var gcd  = new HashSet<int>();
        var ogcd = new HashSet<int>();
        if (!CanManageAbilityAnts()) return (gcd, ogcd);
        if (activeTimeline == null) return (gcd, ogcd);

        var elapsed = inCombat
            ? (DateTime.UtcNow - combatStartTime).TotalSeconds
            : combatElapsedSec;

        EnsureAntsAbilityCache(elapsed);
        gcd.UnionWith(cachedAntsGcdIds);
        ogcd.UnionWith(cachedAntsOgcdIds);
        return (gcd, ogcd);
    }

    // â”€â”€ Combat events â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat) return;

        if (value && !inCombat)
        {
            var preserveAutoPlayback = AutoExecuteEnabled && isPreview && previewAutoplay;
            var preservedElapsedSec = preserveAutoPlayback ? GetBaseTimelineElapsedSec() : 0.0;

            // Only start live tracking for timelines loaded by EncounterTracker
            // (i.e. the player is actually in a mapped encounter zone).
            // Ignoring combat from training dummies, open-world mobs, etc.
            if (!isEncounterZone && !manualOverlayActive) return;

            inCombat             = true;
            antsArmed            = true;
            isPreview            = false;
            previewAutoplay      = false;
            isScrubbing          = false;
            combatViewPaused     = false;
            awaitingManualPhaseStart = false;
            combatElapsedSec     = preserveAutoPlayback ? preservedElapsedSec : 0.0;
            combatStartTime      = DateTime.UtcNow - TimeSpan.FromSeconds(combatElapsedSec);
            ResetAutoScrubState();

            if (activeTimeline != null && Cfg.OverlayEnabled)
                IsOpen = true;
        }
        else if (!value && inCombat)
        {
            inCombat = false;
            antsArmed = false;
            awaitingManualPhaseStart = false;
            ResetAutoScrubState();

            if (manualOverlayActive)
            {
                combatViewPaused  = false;
                combatElapsedSec  = 0;
                previewManualTimeSec = 0;
                isScrubbing       = false;
                return;
            }

            // Instead of going blank, resume paused preview at the time combat ended.
            if (activeTimeline != null)
            {
                isPreview            = true;
                previewAutoplay      = false;
                previewManualTimeSec = combatElapsedSec;
                isScrubbing          = false;
            }
        }
    }

    private void OnDutyWiped(Dalamud.Game.DutyState.IDutyStateEventArgs _)
    {
        inCombat = false;
        antsArmed = false;
        awaitingManualPhaseStart = false;
        ResetAutoScrubState();

        if (manualOverlayActive)
        {
            combatViewPaused     = false;
            combatElapsedSec     = 0;
            previewManualTimeSec = 0;
            isScrubbing          = false;
            return;
        }

        // Wipe â€” snap back to t=0 in paused preview.
        if (activeTimeline != null)
        {
            isPreview            = true;
            previewAutoplay      = false;
            combatElapsedSec     = 0;
            previewManualTimeSec = 0;
            isScrubbing          = false;
        }
    }

    private void OnDutyCompleted(Dalamud.Game.DutyState.IDutyStateEventArgs _)
    {
        inCombat = false;
        antsArmed = false;
        awaitingManualPhaseStart = false;
        ResetAutoScrubState();

        if (manualOverlayActive)
        {
            combatViewPaused = true;
            return;
        }

        // Completion â€” stay paused at wherever the timeline is.
        if (activeTimeline != null)
        {
            isPreview            = true;
            previewAutoplay      = false;
            previewManualTimeSec = combatElapsedSec;
            isScrubbing          = false;
        }
    }

    // â”€â”€ Window lifecycle â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public override bool DrawConditions()
    {
        if (!IsOpen || activeTimeline == null)
            return false;

        if (isPreview)
            return true;

        return Cfg.OverlayEnabled;
    }

    public override void PreDraw()
    {
        // Lock applies in ALL modes (including preview) â€” respects config.
        if ((Cfg.OverlayLocked || inCombat) && !isScrubbing)
            Flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        else
            Flags &= ~(ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize);

        // Always allow mouse input so the lock / close / pause buttons remain
        // clickable during live combat as well as in preview mode.
        Flags &= ~ImGuiWindowFlags.NoInputs;

        ImGui.SetNextWindowBgAlpha(0.0f);
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // DRAW
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    public override void Draw()
    {
        if (activeTimeline == null) return;

        // Config shortcuts
        var pxPerSec   = Cfg.OverlayPixelsPerSec;
        var iconSize   = Cfg.OverlayIconSize;
        var pastAlpha  = Cfg.OverlayPastAlpha;
        var bgOpacity  = Cfg.OverlayBgOpacity;
        var showGrid   = Cfg.OverlayShowGrid;
        var timeBehind = Cfg.OverlayTimeBehind;
        var maxStack   = Cfg.OverlayMaxStackedIcons;
        var fightDur   = activeTimeline.AverageDurationMs / 1000.0;

        // â”€â”€ Time update â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        if (isPreview)
        {
            if (previewAutoplay)
            {
                combatElapsedSec = (DateTime.UtcNow - previewStartTime).TotalSeconds;
                if (fightDur > 0 && combatElapsedSec > fightDur + 5)
                {
                    previewStartTime = DateTime.UtcNow;
                    combatElapsedSec = 0;
                }
                previewManualTimeSec = combatElapsedSec;
            }
            else
            {
                combatElapsedSec = previewManualTimeSec;
            }
        }
        else if (inCombat)
        {
            // Only advance the view clock when not manually paused.
            if (!combatViewPaused)
                combatElapsedSec = (DateTime.UtcNow - combatStartTime).TotalSeconds;
        }
        else if (manualOverlayActive)
        {
            combatElapsedSec = 0;
        }

        var autoPlaybackActive = AutoExecuteEnabled && autoRuntimeInitialized && IsAutoTimelinePlaying();
        var gcdElapsedSec = autoPlaybackActive ? autoDisplayGcdTimeSec : combatElapsedSec;
        var ogcdElapsedSec = autoPlaybackActive ? autoDisplayOgcdTimeSec : combatElapsedSec;
        var displayTimelineElapsedSec = autoPlaybackActive ? gcdElapsedSec : combatElapsedSec;

        var isActive   = inCombat || isPreview || manualOverlayActive;
        var drawList   = ImGui.GetWindowDrawList();
        var wPos       = ImGui.GetWindowPos();
        var wSize      = ImGui.GetWindowSize();

        // â”€â”€ Background â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        drawList.AddRectFilled(wPos, wPos + wSize,
            ImGui.GetColorU32(new Vector4(0.06f, 0.06f, 0.10f, bgOpacity)), 4.0f);

        // â”€â”€ Scrub / control bar (preview AND live combat) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Row: [â–¶/â¸] [ðŸ”’] [track...........] [time] [Ã—]
        var scrubH = 0.0f;
        if ((isPreview || inCombat) && fightDur > 0)
        {
            const float btnH   = 18.0f;
            const float btnW   = 18.0f;
            const float margin = 4.0f;
            const float btnGap = 3.0f;
            const float trackH = 6.0f;

            var sTop = wPos.Y + 3f;
            var midY = sTop + btnH / 2f;

            // Button X anchors
            var playX  = wPos.X + margin;
            var lockX  = playX  + btnW + btnGap;
            var closeX = wPos.X + wSize.X - margin - btnW;

            // Track sits between the lock button and close button
            var trackL = lockX + btnW + btnGap;
            var trackR = closeX - btnGap;
            var trackW = MathF.Max(trackR - trackL, 1f);

            var frac = (float)Math.Clamp(displayTimelineElapsedSec / fightDur, 0.0, 1.0);

            // Track background
            drawList.AddRectFilled(
                new Vector2(trackL, midY - trackH / 2f),
                new Vector2(trackR, midY + trackH / 2f),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.10f)), 3f);

            // Filled portion
            if (frac > 0)
                drawList.AddRectFilled(
                    new Vector2(trackL,               midY - trackH / 2f),
                    new Vector2(trackL + trackW * frac, midY + trackH / 2f),
                    ImGui.GetColorU32(new Vector4(0.40f, 0.70f, 1.00f, 0.55f)), 3f);

            // Playhead pip
            drawList.AddCircleFilled(
                new Vector2(trackL + trackW * frac, midY),
                5.0f, ImGui.GetColorU32(new Vector4(0.65f, 0.88f, 1.00f, 0.95f)));

            // Time label below track (right-aligned to track end)
            var timeLabel = FormatTime(displayTimelineElapsedSec) + " / " + FormatTime(fightDur);
            var labelSz   = ImGui.CalcTextSize(timeLabel);
            drawList.AddText(
                new Vector2(trackR - labelSz.X, sTop + btnH + 1f),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.35f)),
                timeLabel);

            // â”€â”€ Play / Pause button â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            var playTL    = new Vector2(playX,        sTop);
            var playBR    = new Vector2(playX + btnW, sTop + btnH);
            var playHover = ImGui.IsMouseHoveringRect(playTL, playBR);

            drawList.AddRectFilled(playTL, playBR,
                ImGui.GetColorU32(playHover
                    ? new Vector4(0.30f, 0.65f, 1.00f, 0.90f)
                    : new Vector4(0.15f, 0.35f, 0.65f, 0.72f)), 3f);

            // Show pause icon when playing/live, play icon when paused/frozen.
            var showingPauseIcon = inCombat ? !combatViewPaused : previewAutoplay;
            if (showingPauseIcon)
            {
                // Pause: two vertical bars
                var bw  = 2.5f;
                var bh  = btnH * 0.55f;
                var cx2 = playX + btnW / 2f;
                var ty  = midY - bh / 2f;
                drawList.AddRectFilled(
                    new Vector2(cx2 - bw * 2f, ty), new Vector2(cx2 - bw * 0.5f, ty + bh),
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f)));
                drawList.AddRectFilled(
                    new Vector2(cx2 + bw * 0.5f, ty), new Vector2(cx2 + bw * 2f, ty + bh),
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f)));
            }
            else
            {
                // Play: right-pointing triangle
                var th  = btnH * 0.55f;
                var cx2 = playX + btnW / 2f - 1f;
                drawList.AddTriangleFilled(
                    new Vector2(cx2 - th * 0.4f, midY - th / 2f),
                    new Vector2(cx2 - th * 0.4f, midY + th / 2f),
                    new Vector2(cx2 + th * 0.6f, midY),
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f)));
            }

            if (playHover && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                if (inCombat)
                {
                    // Toggle freeze â€” pause freezes the view, play re-syncs to live.
                    if (combatViewPaused)
                        ResumeCombatViewFromCurrentTime();
                    else
                        PauseCombatViewAtCurrentTime();
                }
                else if (previewAutoplay)
                {
                    previewAutoplay      = false;
                    previewManualTimeSec = displayTimelineElapsedSec;
                }
                else
                {
                    previewAutoplay  = true;
                    previewStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(previewManualTimeSec);
                }
            }

            // â”€â”€ Lock button â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            var lockTL    = new Vector2(lockX,        sTop);
            var lockBR    = new Vector2(lockX + btnW, sTop + btnH);
            var lockHover = ImGui.IsMouseHoveringRect(lockTL, lockBR);
            var isLocked  = Cfg.OverlayLocked;

            drawList.AddRectFilled(lockTL, lockBR,
                ImGui.GetColorU32(lockHover
                    ? new Vector4(0.70f, 0.60f, 0.20f, 0.90f)
                    : isLocked
                        ? new Vector4(0.45f, 0.35f, 0.10f, 0.78f)
                        : new Vector4(0.18f, 0.22f, 0.28f, 0.68f)), 3f);

            DrawLockIcon(drawList,
                new Vector2(lockX + btnW / 2f, sTop + btnH / 2f),
                MathF.Min(btnW, btnH) * 0.80f,
                isLocked,
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.92f)));

            if (lockHover && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                Cfg.OverlayLocked = !Cfg.OverlayLocked;
                plugin.SaveUiSettings();
            }

            // â”€â”€ Close button â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            var closeTL    = new Vector2(closeX,        sTop);
            var closeBR    = new Vector2(closeX + btnW, sTop + btnH);
            var closeHover = ImGui.IsMouseHoveringRect(closeTL, closeBR);

            drawList.AddRectFilled(closeTL, closeBR,
                ImGui.GetColorU32(closeHover
                    ? new Vector4(0.85f, 0.20f, 0.20f, 0.92f)
                    : new Vector4(0.50f, 0.10f, 0.10f, 0.72f)), 3f);

            var xLbl = "x";
            var xSz  = ImGui.CalcTextSize(xLbl);
            drawList.AddText(
                new Vector2(closeX + (btnW - xSz.X) / 2f, sTop + (btnH - xSz.Y) / 2f),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f)), xLbl);

            if (closeHover && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                // Always kill auto-execute (+ its DTR bar entry) when the user
                // manually closes the overlay.
                if (AutoExecuteEnabled)
                {
                    AutoExecuteEnabled = false;
                    plugin.MainWindow.ApplyAutoExecDtr(false);
                    ResetAutoRuntimeState();
                }

                if (inCombat)
                {
                    if (manualOverlayActive)
                    {
                        StopManualOverlay();
                    }
                    else
                    {
                        antsArmed = false;
                        overlayDismissed = true;    // suppress ants + auto-exec for rest of pull
                        IsOpen = false;
                    }
                }
                else
                    StopPreview();

                return;
            }

            // â”€â”€ Scrub input (track hit area) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // Available in preview always; in combat only when the view is paused.
            var scrubAllowed = isPreview || combatViewPaused;
            var barTL = new Vector2(trackL, midY - trackH / 2f - 4f);
            var barBR = new Vector2(trackR, midY + trackH / 2f + 4f);

            if (scrubAllowed && ImGui.IsMouseHoveringRect(barTL, barBR) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                isScrubbing = true;

            if (isScrubbing)
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    var mfrac        = Math.Clamp((ImGui.GetMousePos().X - trackL) / trackW, 0.0, 1.0);
                    combatElapsedSec = mfrac * fightDur;
                    if (isPreview)
                    {
                        previewAutoplay      = false;
                        previewManualTimeSec = combatElapsedSec;
                    }
                    else
                    {
                        combatViewPaused = true;
                    }
                }
                else
                {
                    isScrubbing = false;
                }
            }

            // Scroll wheel nudge (preview always; combat only when paused)
            if (scrubAllowed && ImGui.IsWindowHovered())
            {
                var wheel = ImGui.GetIO().MouseWheel;
                if (MathF.Abs(wheel) > 0.01f)
                {
                    combatElapsedSec = Math.Clamp(combatElapsedSec - wheel * 2.0, 0.0, fightDur);
                    if (isPreview)
                    {
                        previewAutoplay      = false;
                        previewManualTimeSec = combatElapsedSec;
                    }
                    else
                    {
                        combatViewPaused = true;
                    }
                }
            }

            scrubH = btnH + labelSz.Y + 5f;
        }

        // â”€â”€ Lay out timeline and boss strip â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var hasBoss       = activeTimeline.BossEntries.Count > 0;
        var bossStripH    = hasBoss ? 26.0f : 0.0f;

        var tlTop    = wPos.Y + scrubH + 2f;
        var tlBot    = wPos.Y + wSize.Y - bossStripH - 2f;
        var tlLeft   = wPos.X + 2f;
        var tlRight  = wPos.X + wSize.X - 2f;
        var tlH      = tlBot - tlTop;
        var tlW      = tlRight - tlLeft;

        if (tlH < 16f || tlW < 50f) return;

        // "Now" X â€” timeBehind seconds from the LEFT edge, so widening the window
        // extends the look-ahead on the right rather than pushing the red line left.
        var nowX = tlLeft + timeBehind * pxPerSec;

        // Vertical center of the timeline (ATR centers all items here)
        var centerY = tlTop + tlH / 2f;

        // oGCD size and center Y â€” follow ATR's presentation more closely:
        // smaller icons that sit above the center line, while still sharing
        // the same horizontal timing space as GCDs.
        var oGcdSize    = iconSize * Cfg.OGCDSizeRatio;
        var oGcdCenterY = centerY - (Cfg.OGCDVerticalOffset * iconSize + oGcdSize / 2f);

        // ATR GCD bar top/bottom Y
        // leftTop.Y    = centerY + (GCDHeightHigh * iconSize - iconSize / 2) = centerY + 0
        // leftBottom.Y = centerY + (GCDHeightLow  * iconSize - iconSize / 2) = centerY + 0.3*iconSize
        var gcdBarTop = centerY + GCDHeightHigh * iconSize - iconSize / 2f;  // = centerY
        var gcdBarBot = centerY + GCDHeightLow  * iconSize - iconSize / 2f;  // = centerY + 0.3*iconSize

        var visStart = Math.Min(gcdElapsedSec, ogcdElapsedSec) - (nowX - tlLeft)  / pxPerSec;
        var visEnd   = Math.Max(gcdElapsedSec, ogcdElapsedSec) + (tlRight - nowX) / pxPerSec;

        // â”€â”€ ATR DrawGrid â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        if (showGrid)
        {
            for (var t = Math.Ceiling(visStart); t <= visEnd; t += 1.0)
            {
                if (t < 0) continue;
                var gx = nowX + (float)(t - gcdElapsedSec) * pxPerSec;
                if (gx < tlLeft || gx > tlRight) continue;

                var major = (int)t % 5 == 0;
                drawList.AddLine(
                    new Vector2(gx, tlTop),
                    new Vector2(gx, tlBot),
                    ImGui.GetColorU32(major ? ColGridMajor : ColGrid),
                    major ? 1.5f : 1.0f);

                if (major)
                {
                    var lbl = FormatTimeShort(t);
                    var lsz = ImGui.CalcTextSize(lbl);
                    drawList.AddText(
                        new Vector2(gx - lsz.X / 2f, tlTop + 1f),
                        ImGui.GetColorU32(ColGridLabel), lbl);
                }
            }
        }

        // â”€â”€ ATR GridCenterLine â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        drawList.AddLine(
            new Vector2(tlLeft,  centerY),
            new Vector2(tlRight, centerY),
            ImGui.GetColorU32(ColCenterLine), 1.0f);

        // â”€â”€ ATR GridStartLine (the "now" vertical bar) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        drawList.AddLine(
            new Vector2(nowX, tlTop),
            new Vector2(nowX, tlBot),
            ImGui.GetColorU32(ColNowLine), 2.0f);

        // â”€â”€ Idle message â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        if (!isActive)
        {
            const string msg = "Waiting for combat...";
            var msz = ImGui.CalcTextSize(msg);
            drawList.AddText(
                new Vector2(wPos.X + (wSize.X - msz.X) / 2f, centerY - msz.Y / 2f),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.25f)), msg);
        }
        else
        {
            // â”€â”€ Collect and bucket visible entries â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            EnsureActiveFilteredEntriesCache();
            var sourceEntries = autoPlaybackActive ? activeTimeline.Entries : cachedActiveFilteredEntries;
            CollectEntriesInTimeWindow(sourceEntries, visStart - 5, visEnd + 5, activeVisibleEntriesScratch);
            var visibleEntries = activeVisibleEntriesScratch;
            var gcdWindowSec = GCDWindowSec;
            var gcdBuckets = visibleEntries
                .Where(e => e.IsGcd)
                .GroupBy(e => e.TimeOffsetSec)
                .OrderBy(g => g.Key)
                .Select(g => new OverlayGcdBucket
                {
                    X = nowX + (float)(g.Key - gcdElapsedSec) * pxPerSec,
                    IsPast = g.Key < gcdElapsedSec,
                    Entries = g.OrderByDescending(e => e.Frequency).Take(maxStack).ToList(),
                })
                .ToList();
            var ogcdPlacements = BuildOverlayOgcdPlacements(
                visibleEntries,
                ogcdElapsedSec,
                nowX,
                pxPerSec,
                maxStack);

            // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
            // PASS 1 â€” TimelineLayer.General
            // Draw horizontal bars for every GCD and oGCD.
            // ATR draws: Background â†’ AnimLock (skipped: no data) â†’ Cast fill â†’ Border
            // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
            foreach (var bucket in gcdBuckets)
            {
                // â”€â”€ GCD bars â”€â”€
                for (var i = 0; i < bucket.Entries.Count; i++)
                {
                    var e        = bucket.Entries[i];
                    var alpha    = bucket.IsPast ? pastAlpha : 1.0f;
                    if (alpha < 0.01f) continue;
                    var barAlpha = alpha * bgOpacity;

                    // Icon center X for this stacked GCD
                    var cx   = bucket.X - i * (iconSize + IconGap);
                    // ATR: bar starts at centerPos + iconSize/2 in time direction (right)
                    var barL = MathF.Max(cx + iconSize / 2f, tlLeft);
                    var barR = MathF.Min(cx + iconSize / 2f + gcdWindowSec * pxPerSec, tlRight);
                    if (barR <= barL) continue;

                    if (barAlpha >= 0.01f)
                    {
                        // Background (dark gray)
                        drawList.AddRectFilled(
                            new Vector2(barL, gcdBarTop),
                            new Vector2(barR, gcdBarBot),
                            MulAlpha(ColGCDBackground, barAlpha), BarRound);

                        // Cast fill (blue) â€” left 45% approximates cast time
                        var castR = barL + (barR - barL) * 0.45f;
                        drawList.AddRectFilled(
                            new Vector2(barL, gcdBarTop),
                            new Vector2(castR, gcdBarBot),
                            MulAlpha(ColGCDCast, barAlpha), BarRound);

                        // Border (white)
                        drawList.AddRect(
                            new Vector2(barL, gcdBarTop),
                            new Vector2(barR, gcdBarBot),
                            MulAlpha(ColGCDBorder, barAlpha),
                            BarRound, ImDrawFlags.RoundCornersAll, 1.0f);
                    }
                }

                // Each GCD bucket now shares a single unified action lane with the
                // oGCD icons, so no special per-bucket oGCD pass lives here anymore.
            }

            foreach (var bucket in gcdBuckets)
            {
                for (var i = 0; i < bucket.Entries.Count; i++)
                {
                    var e   = bucket.Entries[i];
                    var cx  = bucket.X - i * (iconSize + IconGap);
                    var pos = new Vector2(cx - iconSize / 2f, centerY - iconSize / 2f);
                    DrawIcon(drawList, e, pos, iconSize, bucket.IsPast, pastAlpha, gcdElapsedSec);
                }
            }

            foreach (var placement in ogcdPlacements)
            {
                var pos = new Vector2(placement.X - oGcdSize / 2f, oGcdCenterY - oGcdSize / 2f);
                DrawIcon(drawList, placement.Entry, pos, oGcdSize, placement.IsPast, pastAlpha, ogcdElapsedSec);
            }
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        // BOSS CAST STRIP â€” anchored to bottom
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        if (hasBoss)
        {
            var bTop    = wPos.Y + wSize.Y - bossStripH - 2f;
            var bBot    = wPos.Y + wSize.Y - 2f;
            var bH      = bBot - bTop;
            var bThick  = bH * 0.65f;
            var bCenY   = bTop + bH / 2f;
            var bBarMinY = bCenY - bThick / 2f;
            var bBarMaxY = bCenY + bThick / 2f;

            // Strip background
            drawList.AddRectFilled(
                new Vector2(tlLeft, bTop), new Vector2(tlRight, bBot),
                ImGui.GetColorU32(new Vector4(0.15f, 0.03f, 0.03f, bgOpacity)));
            drawList.AddLine(
                new Vector2(tlLeft, bTop), new Vector2(tlRight, bTop),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.18f)));

            // "Boss" label at left
            drawList.AddText(
                new Vector2(tlLeft + 3f, bTop + (bH - ImGui.GetTextLineHeight()) / 2f),
                ImGui.GetColorU32(new Vector4(1f, 0.50f, 0.50f, 0.70f)), "Boss");

            foreach (var boss in activeTimeline.BossEntries)
            {
                var startX = nowX + (float)(boss.CastStartSec - gcdElapsedSec) * pxPerSec;
                var endX   = nowX + (float)(boss.CastEndSec   - gcdElapsedSec) * pxPerSec;
                if (endX - startX < 3f) endX = startX + 3f;  // min width for instants

                if (endX < tlLeft || startX > tlRight) continue;
                var dL = MathF.Max(startX, tlLeft);
                var dR = MathF.Min(endX,   tlRight);
                var isPastBoss = boss.CastEndSec < gcdElapsedSec;
                var bossAlpha = isPastBoss ? pastAlpha : 1.0f;
                if (bossAlpha < 0.01f) continue;

                drawList.AddRectFilled(
                    new Vector2(dL, bBarMinY), new Vector2(dR, bBarMaxY),
                    BossAbilityColor(boss.AbilityId, bossAlpha), 2.0f);

                // Name text
                var castW = dR - dL;
                if (castW > 28f)
                {
                    var lbl = TruncateTextToWidth(boss.AbilityName, castW - 6f);
                    if (!string.IsNullOrEmpty(lbl))
                    {
                        var lsz = ImGui.CalcTextSize(lbl);
                        drawList.AddText(
                            new Vector2(dL + 3f, bCenY - lsz.Y / 2f),
                            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, bossAlpha)), lbl);
                    }
                }

                // Tooltip
                if (ImGui.IsMouseHoveringRect(new Vector2(dL, bBarMinY), new Vector2(dR, bBarMaxY)))
                {
                    var castDur = boss.CastEndSec - boss.CastStartSec;
                    ImGui.BeginTooltip();
                    ImGui.Text(boss.AbilityName);
                    ImGui.Separator();
                    ImGui.Text($"Cast start:  {FormatTime(boss.CastStartSec)}");
                    if (castDur > 0.05)
                        ImGui.Text($"Cast finish: {FormatTime(boss.CastEndSec)}  ({castDur:F2}s)");
                    else
                        ImGui.Text("Instant cast");
                    ImGui.EndTooltip();
                }
            }
        }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // PASS 2 ICON HELPER
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    private bool DrawEmbeddedPreviewContents(AggregatedTimeline timeline)
    {
        var pxPerSec   = Cfg.OverlayPixelsPerSec;
        var iconSize   = Cfg.OverlayIconSize;
        var pastAlpha  = Cfg.OverlayPastAlpha;
        var bgOpacity  = Cfg.OverlayBgOpacity;
        var showGrid   = Cfg.OverlayShowGrid;
        var timeBehind = Cfg.OverlayTimeBehind;
        var maxStack   = Cfg.OverlayMaxStackedIcons;
        var fightDur   = timeline.AverageDurationMs / 1000.0;

        if (embeddedPreviewAutoplay)
        {
            embeddedPreviewElapsedSec = (DateTime.UtcNow - embeddedPreviewStartTime).TotalSeconds;
            if (fightDur > 0 && embeddedPreviewElapsedSec > fightDur + 5)
            {
                embeddedPreviewStartTime = DateTime.UtcNow;
                embeddedPreviewElapsedSec = 0;
            }

            embeddedPreviewManualTimeSec = embeddedPreviewElapsedSec;
        }
        else
        {
            embeddedPreviewElapsedSec = embeddedPreviewManualTimeSec;
        }

        var drawList = ImGui.GetWindowDrawList();
        var wPos     = ImGui.GetWindowPos();
        var wSize    = ImGui.GetWindowSize();

        drawList.AddRectFilled(wPos, wPos + wSize,
            ImGui.GetColorU32(new Vector4(0.06f, 0.06f, 0.10f, bgOpacity)), 4.0f);

        var scrubH = 0.0f;
        if (fightDur > 0)
        {
            const float btnH   = 18.0f;
            const float btnW   = 18.0f;
            const float margin = 4.0f;
            const float btnGap = 3.0f;
            const float trackH = 6.0f;

            var sTop   = wPos.Y + 3f;
            var midY   = sTop + btnH / 2f;
            var playX  = wPos.X + margin;
            var closeX = wPos.X + wSize.X - margin - btnW;
            var trackL = playX + btnW + btnGap;
            var trackR = closeX - btnGap;
            var trackW = MathF.Max(trackR - trackL, 1f);
            var frac   = (float)Math.Clamp(embeddedPreviewElapsedSec / fightDur, 0.0, 1.0);

            drawList.AddRectFilled(
                new Vector2(trackL, midY - trackH / 2f),
                new Vector2(trackR, midY + trackH / 2f),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.10f)), 3f);

            if (frac > 0)
            {
                drawList.AddRectFilled(
                    new Vector2(trackL, midY - trackH / 2f),
                    new Vector2(trackL + trackW * frac, midY + trackH / 2f),
                    ImGui.GetColorU32(new Vector4(0.40f, 0.70f, 1.00f, 0.55f)), 3f);
            }

            drawList.AddCircleFilled(
                new Vector2(trackL + trackW * frac, midY),
                5.0f,
                ImGui.GetColorU32(new Vector4(0.65f, 0.88f, 1.00f, 0.95f)));

            var timeLabel = FormatTime(embeddedPreviewElapsedSec) + " / " + FormatTime(fightDur);
            var labelSz   = ImGui.CalcTextSize(timeLabel);
            drawList.AddText(
                new Vector2(trackR - labelSz.X, sTop + btnH + 1f),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.35f)),
                timeLabel);

            var savedCursorPos = ImGui.GetCursorScreenPos();

            var playTL    = new Vector2(playX, sTop);
            var playBR    = new Vector2(playX + btnW, sTop + btnH);
            ImGui.SetCursorScreenPos(playTL);
            ImGui.InvisibleButton("##EmbeddedPreviewPlay", playBR - playTL);
            var playHover = ImGui.IsItemHovered();
            var playPressed = ImGui.IsItemClicked(ImGuiMouseButton.Left);

            drawList.AddRectFilled(playTL, playBR,
                ImGui.GetColorU32(playHover
                    ? new Vector4(0.30f, 0.65f, 1.00f, 0.90f)
                    : new Vector4(0.15f, 0.35f, 0.65f, 0.72f)), 3f);

            if (embeddedPreviewAutoplay)
            {
                var bw  = 2.5f;
                var bh  = btnH * 0.55f;
                var cx2 = playX + btnW / 2f;
                var ty  = midY - bh / 2f;
                drawList.AddRectFilled(
                    new Vector2(cx2 - bw * 2f, ty),
                    new Vector2(cx2 - bw * 0.5f, ty + bh),
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f)));
                drawList.AddRectFilled(
                    new Vector2(cx2 + bw * 0.5f, ty),
                    new Vector2(cx2 + bw * 2f, ty + bh),
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f)));
            }
            else
            {
                var th  = btnH * 0.55f;
                var cx2 = playX + btnW / 2f - 1f;
                drawList.AddTriangleFilled(
                    new Vector2(cx2 - th * 0.4f, midY - th / 2f),
                    new Vector2(cx2 - th * 0.4f, midY + th / 2f),
                    new Vector2(cx2 + th * 0.6f, midY),
                    ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f)));
            }

            if (playPressed)
            {
                if (embeddedPreviewAutoplay)
                {
                    embeddedPreviewAutoplay = false;
                    embeddedPreviewManualTimeSec = embeddedPreviewElapsedSec;
                }
                else
                {
                    embeddedPreviewAutoplay = true;
                    embeddedPreviewStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(embeddedPreviewManualTimeSec);
                }
            }

            var closeTL    = new Vector2(closeX, sTop);
            var closeBR    = new Vector2(closeX + btnW, sTop + btnH);
            ImGui.SetCursorScreenPos(closeTL);
            ImGui.InvisibleButton("##EmbeddedPreviewClose", closeBR - closeTL);
            var closeHover = ImGui.IsItemHovered();
            var closePressed = ImGui.IsItemClicked(ImGuiMouseButton.Left);

            drawList.AddRectFilled(closeTL, closeBR,
                ImGui.GetColorU32(closeHover
                    ? new Vector4(0.85f, 0.20f, 0.20f, 0.92f)
                    : new Vector4(0.50f, 0.10f, 0.10f, 0.72f)), 3f);

            var xLbl = "x";
            var xSz  = ImGui.CalcTextSize(xLbl);
            drawList.AddText(
                new Vector2(closeX + (btnW - xSz.X) / 2f, sTop + (btnH - xSz.Y) / 2f),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f)),
                xLbl);

            if (closePressed)
            {
                ResetEmbeddedPreview();
                return false;
            }

            var barTL = new Vector2(trackL, midY - trackH / 2f - 4f);
            var barBR = new Vector2(trackR, midY + trackH / 2f + 4f);
            ImGui.SetCursorScreenPos(barTL);
            ImGui.InvisibleButton("##EmbeddedPreviewScrubBar", barBR - barTL);
            var scrubActive = ImGui.IsItemActive();

            embeddedPreviewIsScrubbing = scrubActive;
            if (scrubActive)
            {
                var mfrac = Math.Clamp((ImGui.GetMousePos().X - trackL) / trackW, 0.0, 1.0);
                embeddedPreviewElapsedSec = mfrac * fightDur;
                embeddedPreviewAutoplay = false;
                embeddedPreviewManualTimeSec = embeddedPreviewElapsedSec;
            }

            ImGui.SetCursorScreenPos(savedCursorPos);

            if (ImGui.IsWindowHovered())
            {
                var wheel = ImGui.GetIO().MouseWheel;
                if (MathF.Abs(wheel) > 0.01f)
                {
                    embeddedPreviewElapsedSec = Math.Clamp(embeddedPreviewElapsedSec - wheel * 2.0, 0.0, fightDur);
                    embeddedPreviewAutoplay = false;
                    embeddedPreviewManualTimeSec = embeddedPreviewElapsedSec;
                }
            }

            scrubH = btnH + labelSz.Y + 5f;
        }

        var hasBoss    = timeline.BossEntries.Count > 0;
        var bossStripH = hasBoss ? 26.0f : 0.0f;
        var tlTop      = wPos.Y + scrubH + 2f;
        var tlBot      = wPos.Y + wSize.Y - bossStripH - 2f;
        var tlLeft     = wPos.X + 2f;
        var tlRight    = wPos.X + wSize.X - 2f;
        var tlH        = tlBot - tlTop;
        var tlW        = tlRight - tlLeft;

        if (tlH < 16f || tlW < 50f)
            return true;

        var nowX        = tlLeft + timeBehind * pxPerSec;
        var centerY     = tlTop + tlH / 2f;
        var oGcdSize    = iconSize * Cfg.OGCDSizeRatio;
        var oGcdCenterY = centerY - (Cfg.OGCDVerticalOffset * iconSize + oGcdSize / 2f);
        var gcdBarTop   = centerY + GCDHeightHigh * iconSize - iconSize / 2f;
        var gcdBarBot   = centerY + GCDHeightLow * iconSize - iconSize / 2f;
        var visStart    = embeddedPreviewElapsedSec - (nowX - tlLeft) / pxPerSec;
        var visEnd      = embeddedPreviewElapsedSec + (tlRight - nowX) / pxPerSec;

        if (showGrid)
        {
            for (var t = Math.Ceiling(visStart); t <= visEnd; t += 1.0)
            {
                if (t < 0)
                    continue;

                var gx = nowX + (float)(t - embeddedPreviewElapsedSec) * pxPerSec;
                if (gx < tlLeft || gx > tlRight)
                    continue;

                var major = (int)t % 5 == 0;
                drawList.AddLine(
                    new Vector2(gx, tlTop),
                    new Vector2(gx, tlBot),
                    ImGui.GetColorU32(major ? ColGridMajor : ColGrid),
                    major ? 1.5f : 1.0f);

                if (major)
                {
                    var lbl = FormatTimeShort(t);
                    var lsz = ImGui.CalcTextSize(lbl);
                    drawList.AddText(
                        new Vector2(gx - lsz.X / 2f, tlTop + 1f),
                        ImGui.GetColorU32(ColGridLabel),
                        lbl);
                }
            }
        }

        drawList.AddLine(
            new Vector2(tlLeft, centerY),
            new Vector2(tlRight, centerY),
            ImGui.GetColorU32(ColCenterLine),
            1.0f);

        drawList.AddLine(
            new Vector2(nowX, tlTop),
            new Vector2(nowX, tlBot),
            ImGui.GetColorU32(ColNowLine),
            2.0f);

        EnsureEmbeddedFilteredEntriesCache();
        CollectEntriesInTimeWindow(cachedEmbeddedFilteredEntries, visStart - 5, visEnd + 5, embeddedVisibleEntriesScratch);
        var visibleEntries = embeddedVisibleEntriesScratch;
        var gcdWindowSec = GCDWindowSec;
        var gcdBuckets = visibleEntries
            .Where(e => e.IsGcd)
            .GroupBy(e => e.TimeOffsetSec)
            .OrderBy(g => g.Key)
            .Select(g => new OverlayGcdBucket
            {
                X = nowX + (float)(g.Key - embeddedPreviewElapsedSec) * pxPerSec,
                IsPast = g.Key < embeddedPreviewElapsedSec,
                Entries = g.OrderByDescending(e => e.Frequency).Take(maxStack).ToList(),
            })
            .ToList();
        var ogcdPlacements = BuildOverlayOgcdPlacements(
            visibleEntries,
            embeddedPreviewElapsedSec,
            nowX,
            pxPerSec,
            maxStack);

        foreach (var bucket in gcdBuckets)
        {
            for (var i = 0; i < bucket.Entries.Count; i++)
            {
                var alpha = bucket.IsPast ? pastAlpha : 1.0f;
                if (alpha < 0.01f)
                    continue;

                var barAlpha = alpha * bgOpacity;
                var cx       = bucket.X - i * (iconSize + IconGap);
                var barL     = MathF.Max(cx + iconSize / 2f, tlLeft);
                var barR     = MathF.Min(cx + iconSize / 2f + gcdWindowSec * pxPerSec, tlRight);
                if (barR <= barL)
                    continue;

                drawList.AddRectFilled(
                    new Vector2(barL, gcdBarTop),
                    new Vector2(barR, gcdBarBot),
                    MulAlpha(ColGCDBackground, barAlpha),
                    BarRound);

                var castR = barL + (barR - barL) * 0.45f;
                drawList.AddRectFilled(
                    new Vector2(barL, gcdBarTop),
                    new Vector2(castR, gcdBarBot),
                    MulAlpha(ColGCDCast, barAlpha),
                    BarRound);

                drawList.AddRect(
                    new Vector2(barL, gcdBarTop),
                    new Vector2(barR, gcdBarBot),
                    MulAlpha(ColGCDBorder, barAlpha),
                    BarRound,
                    ImDrawFlags.RoundCornersAll,
                    1.0f);
            }
        }

        foreach (var bucket in gcdBuckets)
        {
            for (var i = 0; i < bucket.Entries.Count; i++)
            {
                var e   = bucket.Entries[i];
                var cx  = bucket.X - i * (iconSize + IconGap);
                var pos = new Vector2(cx - iconSize / 2f, centerY - iconSize / 2f);
                DrawIcon(drawList, e, pos, iconSize, bucket.IsPast, pastAlpha, embeddedPreviewElapsedSec);
            }
        }

        foreach (var placement in ogcdPlacements)
        {
            var pos = new Vector2(placement.X - oGcdSize / 2f, oGcdCenterY - oGcdSize / 2f);
            DrawIcon(drawList, placement.Entry, pos, oGcdSize, placement.IsPast, pastAlpha, embeddedPreviewElapsedSec);
        }

        if (!hasBoss)
            return true;

        var bTop     = wPos.Y + wSize.Y - bossStripH - 2f;
        var bBot     = wPos.Y + wSize.Y - 2f;
        var bH       = bBot - bTop;
        var bThick   = bH * 0.65f;
        var bCenY    = bTop + bH / 2f;
        var bBarMinY = bCenY - bThick / 2f;
        var bBarMaxY = bCenY + bThick / 2f;

        drawList.AddRectFilled(
            new Vector2(tlLeft, bTop),
            new Vector2(tlRight, bBot),
            ImGui.GetColorU32(new Vector4(0.15f, 0.03f, 0.03f, bgOpacity)));
        drawList.AddLine(
            new Vector2(tlLeft, bTop),
            new Vector2(tlRight, bTop),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.18f)));
        drawList.AddText(
            new Vector2(tlLeft + 3f, bTop + (bH - ImGui.GetTextLineHeight()) / 2f),
            ImGui.GetColorU32(new Vector4(1f, 0.50f, 0.50f, 0.70f)),
            "Boss");

        foreach (var boss in timeline.BossEntries)
        {
            var startX = nowX + (float)(boss.CastStartSec - embeddedPreviewElapsedSec) * pxPerSec;
            var endX   = nowX + (float)(boss.CastEndSec - embeddedPreviewElapsedSec) * pxPerSec;
            if (endX - startX < 3f)
                endX = startX + 3f;

            if (endX < tlLeft || startX > tlRight)
                continue;

            var dL         = MathF.Max(startX, tlLeft);
            var dR         = MathF.Min(endX, tlRight);
            var isPastBoss = boss.CastEndSec < embeddedPreviewElapsedSec;
            var bossAlpha  = isPastBoss ? pastAlpha : 1.0f;
            if (bossAlpha < 0.01f)
                continue;

            drawList.AddRectFilled(
                new Vector2(dL, bBarMinY),
                new Vector2(dR, bBarMaxY),
                BossAbilityColor(boss.AbilityId, bossAlpha),
                2.0f);

            var castW = dR - dL;
            if (castW > 28f)
            {
                var lbl = TruncateTextToWidth(boss.AbilityName, castW - 6f);
                if (!string.IsNullOrEmpty(lbl))
                {
                    var lsz = ImGui.CalcTextSize(lbl);
                    drawList.AddText(
                        new Vector2(dL + 3f, bCenY - lsz.Y / 2f),
                        ImGui.GetColorU32(new Vector4(1f, 1f, 1f, bossAlpha)),
                        lbl);
                }
            }

            if (ImGui.IsMouseHoveringRect(new Vector2(dL, bBarMinY), new Vector2(dR, bBarMaxY)))
            {
                var castDur = boss.CastEndSec - boss.CastStartSec;
                ImGui.BeginTooltip();
                ImGui.Text(boss.AbilityName);
                ImGui.Separator();
                ImGui.Text($"Cast start:  {FormatTime(boss.CastStartSec)}");
                if (castDur > 0.05)
                    ImGui.Text($"Cast finish: {FormatTime(boss.CastEndSec)}  ({castDur:F2}s)");
                else
                    ImGui.Text("Instant cast");
                ImGui.EndTooltip();
            }
        }

        return true;
    }

    private void DrawIcon(ImDrawListPtr dl, TimelineEntry entry, Vector2 pos, float size,
        bool isPast, float pastAlpha, double elapsed)
    {
        var alpha = isPast ? pastAlpha : 1.0f;

        var end = pos + new Vector2(size, size);

        if (entry.AbilityId > 0 && !TryDrawActionIcon(dl, entry.AbilityId, pos, size, alpha))
        {
            // Fallback â€” coloured rect + abbreviated name
            var hue = (entry.AbilityId % 12) / 12.0f;
            HsvToRgb(hue, 0.5f, 0.6f, out var cr, out var cg, out var cb);
            dl.AddRectFilled(pos, end,
                ImGui.GetColorU32(new Vector4(cr, cg, cb, alpha * 0.8f)), 4.0f);

            var abbr = Abbreviate(entry.AbilityName, 3);
            var asz  = ImGui.CalcTextSize(abbr);
            dl.AddText(
                new Vector2(pos.X + (size - asz.X) / 2f, pos.Y + (size - asz.Y) / 2f),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha)), abbr);
        }

        // 1 px white border (ATR draws this as part of the icon layer)
        dl.AddRect(pos, end,
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha * 0.55f)),
            4.0f, ImDrawFlags.RoundCornersAll, 1.0f);

        // Tooltip
        if (ImGui.IsMouseHoveringRect(pos, end))
        {
            var rel    = entry.TimeOffsetSec - elapsed;
            var relStr = rel >= 0 ? $"+{rel:F1}s" : $"{rel:F1}s";
            ImGui.BeginTooltip();
            ImGui.Text(entry.AbilityName);
            ImGui.Separator();
            ImGui.Text($"Time:      {FormatTime(entry.TimeOffsetSec)} ({relStr})");
            ImGui.Text($"Frequency: {entry.Frequency:P0}");
            ImGui.Text($"Avg uses:  {entry.AverageUses:F1}x");
            ImGui.EndTooltip();
        }
    }

    private List<OverlayOgcdPlacement> BuildOverlayOgcdPlacements(
        IReadOnlyList<TimelineEntry> visibleEntries,
        double elapsed,
        float nowX,
        float pxPerSec,
        int maxStack)
    {
        if (visibleEntries.Count == 0)
            return [];

        var ogcdEntries = visibleEntries
            .Where(e => !e.IsGcd)
            .GroupBy(e => e.TimeOffsetSec)
            .OrderBy(g => g.Key)
            .SelectMany(g => g.OrderByDescending(e => e.Frequency).Take(maxStack))
            .ToList();
        if (ogcdEntries.Count == 0)
            return [];

        var gcdTimes = visibleEntries
            .Where(e => e.IsGcd)
            .Select(e => e.TimeOffsetSec)
            .Distinct()
            .OrderBy(t => t)
            .ToList();
        var gcdForbiddenRanges = BuildOverlayGcdForbiddenRanges(gcdTimes);

        var placements = new List<OverlayOgcdPlacement>(ogcdEntries.Count);
        var horizontalTimeOffset = pxPerSec > 0f
            ? (double)Cfg.OGCDHorizontalOffset / pxPerSec
            : 0.0;
        var previousDisplayTime = double.NegativeInfinity;

        foreach (var entry in ogcdEntries.OrderBy(e => e.TimeOffsetSec).ThenByDescending(e => e.Frequency))
        {
            var baseDisplayTime = entry.TimeOffsetSec + horizontalTimeOffset;
            var resolvedDisplayTime = ResolveOverlayOgcdDisplayTime(baseDisplayTime, previousDisplayTime, gcdForbiddenRanges);
            placements.Add(new OverlayOgcdPlacement
            {
                Entry = entry,
                DisplayTimeSec = resolvedDisplayTime,
                X = nowX + (float)(resolvedDisplayTime - elapsed) * pxPerSec,
                IsPast = resolvedDisplayTime < elapsed,
            });
            previousDisplayTime = resolvedDisplayTime;
        }

        return placements
            .OrderBy(p => p.DisplayTimeSec)
            .ThenByDescending(p => p.Entry.Frequency)
            .ToList();
    }

    private static double ResolveOverlayOgcdDisplayTime(
        double baseDisplayTime,
        double previousDisplayTime,
        IReadOnlyList<(double Start, double End)> gcdForbiddenRanges)
    {
        var lowerBound = double.IsNegativeInfinity(previousDisplayTime)
            ? double.NegativeInfinity
            : previousDisplayTime + OverlayMinOgcdVisualGapSec;
        var displayTime = baseDisplayTime;

        var containingRangeIndex = FindOverlayContainingForbiddenRange(gcdForbiddenRanges, baseDisplayTime);
        if (containingRangeIndex >= 0)
        {
            var containingRange = gcdForbiddenRanges[containingRangeIndex];
            var leftCandidate = containingRange.Start;
            var rightCandidate = containingRange.End;
            var leftAllowed = leftCandidate >= lowerBound;

            if (leftAllowed && Math.Abs(baseDisplayTime - leftCandidate) <= Math.Abs(rightCandidate - baseDisplayTime))
                displayTime = leftCandidate;
            else
                displayTime = Math.Max(rightCandidate, lowerBound);
        }
        else
        {
            displayTime = Math.Max(baseDisplayTime, lowerBound);
        }

        foreach (var forbiddenRange in gcdForbiddenRanges)
        {
            if (displayTime > forbiddenRange.Start && displayTime < forbiddenRange.End)
            {
                displayTime = forbiddenRange.End;
            }
        }

        return displayTime;
    }

    private static List<(double Start, double End)> BuildOverlayGcdForbiddenRanges(IReadOnlyList<double> gcdTimes)
    {
        var ranges = new List<(double Start, double End)>(gcdTimes.Count);
        if (gcdTimes.Count == 0)
            return ranges;

        foreach (var gcdTime in gcdTimes)
        {
            var start = gcdTime - OverlayMinOgcdVisualGapSec;
            var end = gcdTime + OverlayMinOgcdVisualGapSec;
            if (ranges.Count == 0)
            {
                ranges.Add((start, end));
                continue;
            }

            var lastRange = ranges[^1];
            if (start <= lastRange.End)
                ranges[^1] = (lastRange.Start, Math.Max(lastRange.End, end));
            else
                ranges.Add((start, end));
        }

        return ranges;
    }

    private static int FindOverlayContainingForbiddenRange(
        IReadOnlyList<(double Start, double End)> forbiddenRanges,
        double time)
    {
        for (var index = 0; index < forbiddenRanges.Count; index++)
        {
            var range = forbiddenRanges[index];
            if (time > range.Start && time < range.End)
                return index;
        }

        return -1;
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // ICON LOADING
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

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
            var wrap = plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            if (wrap.Width <= 1) return false;

            dl.AddImage(wrap.Handle, pos, pos + new Vector2(size, size),
                Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha)));
            return true;
        }
        catch { return false; }
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // oGCD DETECTION
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    /// <summary>
    /// Returns the effective frequency threshold for a specific ability.
    /// Falls back to the global OverlayFreqThreshold if no per-ability override is set.
    /// </summary>
    private float GetAbilityThreshold(int abilityId)
    {
        if (activeTimeline == null)
            return Cfg.OverlayFreqThreshold;

        return GetAbilityThreshold(activeTimeline, abilityId);
    }

    private float GetAbilityThreshold(AggregatedTimeline timeline, int abilityId)
    {
        var key = TimelineDatabase.MakeKey(timeline.EncounterId, timeline.SpecName);
        if (Cfg.AbilityFreqThresholds.TryGetValue(key, out var perAbility) &&
            perAbility.TryGetValue(abilityId, out var custom))
            return custom;

        return Cfg.OverlayFreqThreshold;
    }

    private bool IsOGCD(int abilityId, string abilityName = "")
    {
        if (string.IsNullOrWhiteSpace(abilityName) && oGcdCache.TryGetValue(abilityId, out var cached))
            return cached;

        // A timeline GCD is any action whose real game category is Spell or Weaponskill.
        // Everything else is treated as an oGCD for ants/timeline display purposes.
        var info   = plugin.RecastDatabase.Lookup(abilityId, abilityName);
        var isOgcd = info == null || !info.IsGcdAction;

        oGcdCache[abilityId] = isOgcd;
        return isOgcd;
    }

    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
    // HELPERS
    // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

    /// <summary>Multiplies the alpha component of a Vector4 colour.</summary>
    private static uint MulAlpha(Vector4 col, float alpha)
        => ImGui.GetColorU32(col with { W = col.W * alpha });

    private static string FormatTime(double seconds)
    {
        var m = (int)(seconds / 60);
        var s = seconds % 60;
        return $"{m}:{s:00.0}";
    }

    private static string FormatTimeShort(double seconds)
        => $"{(int)(seconds / 60)}:{(int)(seconds % 60):D2}";

    private static string Abbreviate(string name, int maxChars)
    {
        if (string.IsNullOrEmpty(name)) return "?";
        if (name.Length <= maxChars) return name;

        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            var abbr = string.Concat(parts.Select(p => p.Length > 0 ? p[0].ToString() : string.Empty));
            if (abbr.Length <= maxChars + 1) return abbr;
        }
        return name[..maxChars];
    }

    private static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
    {
        var i = (int)(h * 6); var f = h * 6 - i;
        var p = v * (1 - s); var q = v * (1 - f * s); var t = v * (1 - (1 - f) * s);
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

    private uint BossAbilityColor(int abilityId, float alpha = 1.0f)
    {
        if (Cfg.BossBarUseCustomColor)
            return MulAlpha(Cfg.BossBarColor, alpha);

        float[] hues = [0.0f, 0.05f, 0.90f, 0.75f, 0.12f, 0.85f];
        var hue = hues[Math.Abs(abilityId) % hues.Length];
        HsvToRgb(hue, 0.7f, 0.75f, out var r, out var g, out var b);
        return ImGui.GetColorU32(new Vector4(r, g, b, alpha));
    }

    /// <summary>
    /// Draws a padlock icon centered at <paramref name="center"/> within a square of <paramref name="size"/>.
    /// Locked = shackle closed over body. Unlocked = shackle open (right post only).
    /// </summary>
    private static void DrawLockIcon(ImDrawListPtr dl, Vector2 center, float size, bool locked, uint col)
    {
        // Body â€” filled rounded rect occupying the lower ~55% of the icon area
        var bodyW = size * 0.62f;
        var bodyH = size * 0.50f;
        var bodyT = center.Y + size * 0.06f;
        var bodyB = bodyT + bodyH;
        var bodyL = center.X - bodyW / 2f;
        var bodyR = center.X + bodyW / 2f;
        dl.AddRectFilled(new Vector2(bodyL, bodyT), new Vector2(bodyR, bodyB), col, 2f);

        // Keyhole â€” small dark circle in center of body
        dl.AddCircleFilled(
            new Vector2(center.X, bodyT + bodyH * 0.42f),
            bodyW * 0.12f,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.45f)), 6);

        // Shackle geometry
        var sR    = bodyW * 0.28f;                              // arc radius
        var sCenX = locked ? center.X : center.X + sR * 0.55f; // shift right when open
        var sCenY = bodyT;                                       // arc center at body top

        // Top arc (upper semicircle, Ï€ â†’ 2Ï€) rendered as line segments
        const int segs = 7;
        for (var i = 0; i < segs; i++)
        {
            var a0 = MathF.PI * (1f + (float)i       / segs);
            var a1 = MathF.PI * (1f + (float)(i + 1) / segs);
            dl.AddLine(
                new Vector2(sCenX + sR * MathF.Cos(a0), sCenY + sR * MathF.Sin(a0)),
                new Vector2(sCenX + sR * MathF.Cos(a1), sCenY + sR * MathF.Sin(a1)),
                col, 1.8f);
        }

        // Vertical posts dropping into body
        var postBot = bodyT + bodyH * 0.28f;
        if (locked)
        {
            dl.AddLine(new Vector2(sCenX - sR, sCenY), new Vector2(sCenX - sR, postBot), col, 1.8f);
            dl.AddLine(new Vector2(sCenX + sR, sCenY), new Vector2(sCenX + sR, postBot), col, 1.8f);
        }
        else
        {
            // Only the right post; left post is "open"
            dl.AddLine(new Vector2(sCenX + sR, sCenY), new Vector2(sCenX + sR, postBot), col, 1.8f);
        }
    }

    private static string TruncateTextToWidth(string text, float maxWidth)
    {
        if (maxWidth <= 0) return string.Empty;
        if (ImGui.CalcTextSize(text).X <= maxWidth) return text;
        var approxChars = (int)(maxWidth / 7.0f);
        if (approxChars <= 1) return string.Empty;
        var t = text[..Math.Min(text.Length, approxChars - 1)] + "â€¦";
        return ImGui.CalcTextSize(t).X <= maxWidth ? t : string.Empty;
    }

    private void ResetAutoRuntimeState()
    {
        if (autoRuntimeInitialized ||
            autoGcdLane.PendingEntry != null ||
            autoOgcdLane.PendingEntry != null ||
            autoGcdLane.NextIndex > 0 ||
            autoOgcdLane.NextIndex > 0)
        {
        }

        autoGcdLane.Entries.Clear();
        autoGcdLane.NextIndex = 0;
        autoGcdLane.DisplayTimeSec = 0;
        autoGcdLane.PendingEntry = null;
        autoGcdLane.LastAttemptAtUtc = DateTime.MinValue;
        autoGcdLane.RequestAcceptedAtUtc = DateTime.MinValue;
        autoGcdLane.RequestAccepted = false;
        autoGcdLane.RecastObserved = false;
        autoGcdLane.CastObserved = false;
        autoGcdLane.CompletionObserved = false;

        autoOgcdLane.Entries.Clear();
        autoOgcdLane.NextIndex = 0;
        autoOgcdLane.DisplayTimeSec = 0;
        autoOgcdLane.PendingEntry = null;
        autoOgcdLane.LastAttemptAtUtc = DateTime.MinValue;
        autoOgcdLane.RequestAcceptedAtUtc = DateTime.MinValue;
        autoOgcdLane.RequestAccepted = false;
        autoOgcdLane.RecastObserved = false;
        autoOgcdLane.CastObserved = false;
        autoOgcdLane.CompletionObserved = false;

        autoTimelineKey = string.Empty;
        autoRuntimeInitialized = false;
        autoBaseTimeSec = double.NaN;
        autoLastObservedBaseTimeSec = double.NaN;
        autoLastObservedBaseAtUtc = DateTime.MinValue;
        autoDisplayGcdTimeSec = combatElapsedSec;
        autoDisplayOgcdTimeSec = combatElapsedSec;
    }

    private double GetBaseTimelineElapsedSec()
    {
        var fightDur = activeTimeline?.AverageDurationMs / 1000.0 ?? 0.0;
        if (isPreview)
        {
            if (previewAutoplay)
            {
                var elapsed = (DateTime.UtcNow - previewStartTime).TotalSeconds;
                if (fightDur > 0 && elapsed > fightDur + 5.0)
                {
                    previewStartTime = DateTime.UtcNow;
                    elapsed = 0.0;
                }

                previewManualTimeSec = elapsed;
                return elapsed;
            }

            return previewManualTimeSec;
        }

        if (inCombat)
            return combatViewPaused ? combatElapsedSec : Math.Max(0.0, (DateTime.UtcNow - combatStartTime).TotalSeconds);

        return manualOverlayActive ? 0.0 : combatElapsedSec;
    }

    private bool IsAutoTimelinePlaying()
        => (inCombat && !combatViewPaused) || (isPreview && previewAutoplay);

    private void DisableAutoAndShowConflict(string message)
    {
        AutoExecuteEnabled = false;
        plugin.MainWindow.ApplyAutoExecDtr(false);
        ResetAutoRuntimeState();
        antsArmed = false;
        overlayDismissed = true;
        IsOpen = false;
        plugin.AutoModalWindow.Show(message);
    }

    private static string BuildAutoConflictMessage()
        => "Conflicting actions detected. Ensure no 2 actions have the same timestamp before proceeding. Actions must be at least 0.099s apart from each other. Tip: use the timeline right-click Auto Space function to automatically fix same-lane conflicts.";

    private bool InitializeAutoRuntime(double baseTimeSec)
    {
        ResetAutoRuntimeState();
        if (activeTimeline == null)
            return false;

        autoTimelineKey = activeTimelineKey;
        var previousGcdTime = double.NegativeInfinity;
        var previousOgcdTime = double.NegativeInfinity;
        for (var index = 0; index < activeTimeline.Entries.Count; index++)
        {
            var entry = activeTimeline.Entries[index];
            entry.IsGcd = !IsOGCD(entry.AbilityId, entry.AbilityName);
            if (entry.IsGcd)
            {
                if (!double.IsNegativeInfinity(previousGcdTime) &&
                    entry.TimeOffsetSec + AutoConflictComparisonEpsilonSec < previousGcdTime + AutoConflictToleranceSec)
                {
                    DisableAutoAndShowConflict(BuildAutoConflictMessage());
                    return false;
                }

                previousGcdTime = entry.TimeOffsetSec;
                autoGcdLane.Entries.Add(entry);
            }
            else
            {
                if (!double.IsNegativeInfinity(previousOgcdTime) &&
                    entry.TimeOffsetSec + AutoConflictComparisonEpsilonSec < previousOgcdTime + AutoConflictToleranceSec)
                {
                    DisableAutoAndShowConflict(BuildAutoConflictMessage());
                    return false;
                }

                previousOgcdTime = entry.TimeOffsetSec;
                autoOgcdLane.Entries.Add(entry);
            }
        }

        autoGcdLane.NextIndex = autoGcdLane.Entries.FindIndex(entry => entry.TimeOffsetSec >= baseTimeSec - 0.0005);
        if (autoGcdLane.NextIndex < 0)
            autoGcdLane.NextIndex = autoGcdLane.Entries.Count;

        autoOgcdLane.NextIndex = autoOgcdLane.Entries.FindIndex(entry => entry.TimeOffsetSec >= baseTimeSec - 0.0005);
        if (autoOgcdLane.NextIndex < 0)
            autoOgcdLane.NextIndex = autoOgcdLane.Entries.Count;

        autoGcdLane.DisplayTimeSec = baseTimeSec;
        autoOgcdLane.DisplayTimeSec = baseTimeSec;
        autoDisplayGcdTimeSec = baseTimeSec;
        autoDisplayOgcdTimeSec = baseTimeSec;
        autoBaseTimeSec = baseTimeSec;
        autoLastObservedBaseTimeSec = baseTimeSec;
        autoLastObservedBaseAtUtc = DateTime.UtcNow;
        autoRuntimeInitialized = true;
        return true;
    }

    private bool ShouldRebaseAutoRuntime(double baseTimeSec)
    {
        if (!autoRuntimeInitialized)
        {
            return true;
        }

        if (!string.Equals(autoTimelineKey, activeTimelineKey, StringComparison.Ordinal))
        {
            return true;
        }

        if (!double.IsFinite(autoLastObservedBaseTimeSec) || autoLastObservedBaseAtUtc == DateTime.MinValue)
        {
            return true;
        }

        var now = DateTime.UtcNow;
        var wallAdvanceSec = Math.Max(0.0, (now - autoLastObservedBaseAtUtc).TotalSeconds);
        var observedAdvanceSec = baseTimeSec - autoLastObservedBaseTimeSec;

        if (autoScrubLastSeekAtUtc != DateTime.MinValue &&
            now <= autoScrubLastSeekAtUtc.AddSeconds(0.50) &&
            double.IsFinite(autoScrubLastSeekTimeSec) &&
            Math.Abs(baseTimeSec - autoScrubLastSeekTimeSec) <= 0.05)
        {
            return false;
        }

        if (observedAdvanceSec < -AutoRebaseJumpThresholdSec)
        {
            return true;
        }

        if (Math.Abs(observedAdvanceSec - wallAdvanceSec) > AutoRebaseJumpThresholdSec)
        {
            return true;
        }

        return false;
    }

    private void ClearLanePending(AutoLaneState lane)
    {
        if (lane.PendingEntry != null || lane.RequestAccepted || lane.RecastObserved || lane.CastObserved || lane.CompletionObserved)
        {
        }

        lane.PendingEntry = null;
        lane.RequestAccepted = false;
        lane.RecastObserved = false;
        lane.CastObserved = false;
        lane.CompletionObserved = false;
        lane.RequestAcceptedAtUtc = DateTime.MinValue;
        lane.LastAttemptAtUtc = DateTime.MinValue;
    }

    private void CompleteLaneEntry(AutoLaneState lane)
    {
        if (lane.PendingEntry != null)
        {
            lane.NextIndex++;
        }

        ClearLanePending(lane);
    }

    private unsafe double GetActionCooldownRemainingSec(uint actionId)
    {
        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return 0.0;

        var remaining = 0.0;
        if (actionManager->IsRecastTimerActive(ActionType.Action, actionId))
        {
            remaining = Math.Max(
                0.0,
                actionManager->GetRecastTime(ActionType.Action, actionId) -
                actionManager->GetRecastTimeElapsed(ActionType.Action, actionId));
        }

        var mainGroup = actionManager->GetRecastGroup((int)ActionType.Action, actionId);
        var mainDetail = actionManager->GetRecastGroupDetail(mainGroup);
        if (mainDetail != null && mainDetail->IsActive)
        {
            remaining = Math.Max(remaining, Math.Max(0.0, mainDetail->Total - mainDetail->Elapsed));
            var charges = Math.Max(1, (int)ActionManager.GetMaxCharges(actionId, 0));
            remaining = Math.Max(remaining, Math.Max(0.0, mainDetail->Total / charges - mainDetail->Elapsed));
        }

        var additionalGroup = actionManager->GetAdditionalRecastGroup(ActionType.Action, actionId);
        var additionalDetail = actionManager->GetRecastGroupDetail(additionalGroup);
        if (additionalDetail != null && additionalDetail->IsActive)
            remaining = Math.Max(remaining, Math.Max(0.0, additionalDetail->Total - additionalDetail->Elapsed));

        return remaining;
    }

    private uint ResolveAutoActionId(TimelineEntry entry)
    {
        var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        return GetAdjustedAutoActionId(info?.AbilityId ?? (uint)Math.Max(0, entry.AbilityId));
    }

    private uint ResolveAutoActionId(AutoLaneState lane)
        => lane.PendingEntry == null ? 0u : ResolveAutoActionId(lane.PendingEntry);

    private unsafe uint GetAdjustedAutoActionId(uint actionId)
    {
        if (actionId == 0)
            return 0;

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return actionId;

        var adjustedActionId = actionManager->GetAdjustedActionId(actionId);
        return adjustedActionId != 0 ? adjustedActionId : actionId;
    }

    private bool LaneMatchesActionId(AutoLaneState lane, uint actionId)
    {
        if (lane.PendingEntry == null || actionId == 0)
            return false;

        var resolvedActionId = ResolveAutoActionId(lane);
        if (resolvedActionId == 0)
            return false;

        var adjustedActionId = GetAdjustedAutoActionId(actionId);
        return actionId == resolvedActionId ||
               adjustedActionId == resolvedActionId ||
               lane.PendingEntry.AbilityId == actionId ||
               lane.PendingEntry.AbilityId == adjustedActionId;
    }

    private void ObservePendingAutoActionRequest(AutoLaneState lane, uint actionId)
    {
        if (!LaneMatchesActionId(lane, actionId))
            return;

        var now = DateTime.UtcNow;
        lane.RequestAccepted = true;
        lane.RequestAcceptedAtUtc = now;
        lane.LastAttemptAtUtc = now;
        lane.RecastObserved |= GetActionCooldownRemainingSec(ResolveAutoActionId(lane)) > 0.0;
    }

    private void ObservePendingAutoActionRequest(uint actionId)
    {
        ObservePendingAutoActionRequest(autoOgcdLane, actionId);
        ObservePendingAutoActionRequest(autoGcdLane, actionId);
    }

    private bool IsGroundTargetedAction(uint actionId)
    {
        try
        {
            var sheet = plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            var row = sheet?.GetRowOrDefault(actionId);
            return row.HasValue && row.Value.TargetArea;
        }
        catch
        {
            return false;
        }
    }

    private unsafe bool TryUseActionOnSelfGround(uint actionId)
    {
        var actionManager = ActionManager.Instance();
        var localPlayer = objectTable.LocalPlayer;
        if (actionManager == null || localPlayer == null)
            return false;

        var location = localPlayer.Position;
        return actionManager->UseActionLocation(ActionType.Action, actionId, localPlayer.GameObjectId, &location, ActionManager.GetExtraParamForSummonAction(actionId));
    }

    private unsafe ulong ResolveAutoActionTargetId(uint actionId)
    {
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return 0;

        try
        {
            var currentTarget = targetSystem->GetTargetObject();
            if (currentTarget != null && ActionManager.CanUseActionOnTarget(actionId, currentTarget))
                return currentTarget->GetGameObjectId();

            var localPlayer = objectTable.LocalPlayer;
            if (localPlayer == null)
                return 0;

            var actionSheet = plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            var actionRow = actionSheet?.GetRowOrDefault(actionId);
            if (!actionRow.HasValue)
                return 0;

            if (!actionRow.Value.CanTargetSelf || actionRow.Value.CanTargetHostile)
                return 0;

            var selfTarget = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)localPlayer.Address;
            if (selfTarget == null || !ActionManager.CanUseActionOnTarget(actionId, selfTarget))
                return 0;

            return selfTarget->GetGameObjectId();
        }
        catch
        {
            return 0;
        }
    }

    private unsafe bool TryIssueAutoAction(TimelineEntry entry)
    {
        var actionId = ResolveAutoActionId(entry);
        if (actionId == 0)
            return false;

        if (IsGroundTargetedAction(actionId))
            return TryUseActionOnSelfGround(actionId);

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return false;

        var targetId = ResolveAutoActionTargetId(actionId);
        return targetId != 0
            ? actionManager->UseAction(ActionType.Action, actionId, targetId)
            : actionManager->UseAction(ActionType.Action, actionId);
    }

    private bool HasPendingActionSelfStatus(AutoLaneState lane)
    {
        if (lane.PendingEntry == null)
            return false;

        var info = plugin.RecastDatabase.Lookup(lane.PendingEntry.AbilityId, lane.PendingEntry.AbilityName);
        if (string.IsNullOrWhiteSpace(info?.SelfStatusName))
            return false;

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
            return false;

        try
        {
            var actionSheet = plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            var resolvedActionId = ResolveAutoActionId(lane);
            var actionRow = actionSheet?.GetRowOrDefault(resolvedActionId);
            if (!actionRow.HasValue)
                return false;

            var selfStatusId = actionRow.Value.StatusGainSelf.RowId;
            if (selfStatusId == 0)
                return false;

            foreach (var status in localPlayer.StatusList)
            {
                if (status.StatusId == selfStatusId)
                {
                    return true;
                }
            }

            var selfStatusName = info.SelfStatusName.Trim();
            if (!string.IsNullOrWhiteSpace(selfStatusName))
            {
                var statusSheet = plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>();
                foreach (var status in localPlayer.StatusList)
                {
                    var statusRow = statusSheet?.GetRowOrDefault(status.StatusId);
                    if (!statusRow.HasValue)
                        continue;

                    var statusName = statusRow.Value.Name.ExtractText().Trim();
                    if (string.Equals(statusName, selfStatusName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private HashSet<string> GetLocalPlayerStatusNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
            return names;

        try
        {
            var statusSheet = plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>();
            foreach (var status in localPlayer.StatusList)
            {
                var statusRow = statusSheet?.GetRowOrDefault(status.StatusId);
                if (!statusRow.HasValue)
                    continue;

                var statusName = statusRow.Value.Name.ExtractText().Trim();
                if (!string.IsNullOrWhiteSpace(statusName))
                    names.Add(statusName);
            }
        }
        catch
        {
        }

        return names;
    }

    private static bool IsConsumableRequiredStateName(string stateName)
    {
        if (string.IsNullOrWhiteSpace(stateName))
            return false;

        if (stateName.StartsWith("Action Grant::", StringComparison.OrdinalIgnoreCase))
            return true;

        var normalized = stateName.Trim().ToLowerInvariant();
        return normalized.Contains(" ready", StringComparison.Ordinal) ||
               normalized.EndsWith("ready", StringComparison.Ordinal) ||
               normalized.Contains("dualcast", StringComparison.Ordinal) ||
               normalized.Contains("hawk's eye", StringComparison.Ordinal) ||
               normalized.Contains("flourishing ", StringComparison.Ordinal) ||
               normalized.Contains("starstruck", StringComparison.Ordinal) ||
               normalized.Contains("divine might", StringComparison.Ordinal) ||
               normalized.Contains("aetherhues", StringComparison.Ordinal) ||
               normalized.Contains("hyperphantasia", StringComparison.Ordinal) ||
               normalized.Contains("monochrome tones", StringComparison.Ordinal) ||
               normalized.Contains("subtractive palette", StringComparison.Ordinal) ||
               normalized.Contains("rainbow bright", StringComparison.Ordinal) ||
               normalized.Contains("hammer time", StringComparison.Ordinal) ||
               normalized.Contains("tempera coat", StringComparison.Ordinal) ||
               normalized.Contains("divining", StringComparison.Ordinal);
    }

    private bool HasPendingActionConsumedRequiredState(AutoLaneState lane)
    {
        if (lane.PendingEntry == null)
            return false;

        var actionRule = plugin.ActionStateDatabase.Lookup(
            ResolveAutoActionId(lane) > 0 ? (int)ResolveAutoActionId(lane) : lane.PendingEntry.AbilityId,
            lane.PendingEntry.AbilityName);
        if (actionRule == null)
            return false;

        var requiredStateNames = actionRule.Effects
            .Where(effect => effect.MinRequired > 0)
            .Select(effect => effect.StateName.Trim())
            .Where(stateName => !string.IsNullOrWhiteSpace(stateName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requiredStateNames.Count == 0)
            return false;

        var playerStatusNames = GetLocalPlayerStatusNames();
        var consumableRequiredStateNames = requiredStateNames
            .Where(IsConsumableRequiredStateName)
            .ToList();
        var completionStateNames = consumableRequiredStateNames.Count > 0
            ? consumableRequiredStateNames
            : requiredStateNames;

        if (completionStateNames.Any(playerStatusNames.Contains))
            return false;
        return true;
    }

    private bool ShouldSkipPendingOgcd(AutoLaneState lane)
    {
        if (lane.PendingEntry == null || lane.PendingEntry.IsGcd || lane.RequestAccepted)
            return false;

        var remaining = GetActionCooldownRemainingSec(ResolveAutoActionId(lane));
        if (remaining > 0.0)
        {
            return true;
        }

        return false;
    }

    private bool IsPendingActionInterrupted(AutoLaneState lane)
    {
        if (lane.PendingEntry == null || !lane.RequestAccepted || lane.CompletionObserved)
            return false;

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
            return false;

        var resolvedActionId = ResolveAutoActionId(lane);
        var info = plugin.RecastDatabase.Lookup(lane.PendingEntry.AbilityId, lane.PendingEntry.AbilityName);
        if ((info?.CastTimeSec ?? 0.0) <= 0.0)
            return false;

        var stoppedCasting = !localPlayer.IsCasting || localPlayer.CastActionId != resolvedActionId;
        if (!stoppedCasting)
            return false;

        if (GetActionCooldownRemainingSec(resolvedActionId) > 0.0)
            return false;

        var interrupted = DateTime.UtcNow >= lane.RequestAcceptedAtUtc.AddSeconds(AutoPendingResetGraceSec);
        if (interrupted)
        {
        }

        return interrupted;
    }

    private bool IsPendingActionSatisfied(AutoLaneState lane)
    {
        if (lane.PendingEntry == null || !lane.RequestAccepted)
            return false;

        if (lane.CompletionObserved)
        {
            return true;
        }

        var resolvedActionId = ResolveAutoActionId(lane);
        var cooldownRemaining = GetActionCooldownRemainingSec(resolvedActionId);
        if (!lane.RecastObserved && cooldownRemaining > 0.0)
        {
            lane.RecastObserved = true;
        }

        if (!lane.RecastObserved)
        {
            if (HasPendingActionSelfStatus(lane))
                lane.RecastObserved = true;
            else if (DateTime.UtcNow >= lane.RequestAcceptedAtUtc.AddSeconds(AutoPendingResetGraceSec) &&
                     HasPendingActionConsumedRequiredState(lane))
                lane.RecastObserved = true;
            else
                return false;
        }

        var info = plugin.RecastDatabase.Lookup(lane.PendingEntry.AbilityId, lane.PendingEntry.AbilityName);
        var hasCastTime = (info?.CastTimeSec ?? 0.0) > 0.0;
        if (!hasCastTime)
        {
            var satisfied = DateTime.UtcNow >= lane.RequestAcceptedAtUtc.AddSeconds(AutoPendingResetGraceSec);
            if (satisfied)
            {
            }

            return satisfied;
        }

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
            return false;

        if (localPlayer.IsCasting && localPlayer.CastActionId == resolvedActionId)
        {
            if (!lane.CastObserved)
            {
                lane.CastObserved = true;
            }

            return false;
        }

        if (!lane.CastObserved)
            return false;

        var castSatisfied = DateTime.UtcNow >= lane.RequestAcceptedAtUtc.AddSeconds(AutoPendingResetGraceSec);
        if (castSatisfied)
        {
        }

        return castSatisfied;
    }

    private void AdvanceAutoLane(AutoLaneState lane, double baseTimeSec)
    {
        while (true)
        {
            if (lane.PendingEntry == null && lane.NextIndex >= lane.Entries.Count)
            {
                lane.DisplayTimeSec = baseTimeSec;
                return;
            }

            if (lane.PendingEntry == null)
            {
                var nextEntry = lane.Entries[lane.NextIndex];
                if (baseTimeSec < nextEntry.TimeOffsetSec)
                {
                    lane.DisplayTimeSec = baseTimeSec;
                    return;
                }

                lane.PendingEntry = nextEntry;
                lane.DisplayTimeSec = nextEntry.TimeOffsetSec;
            }

            lane.DisplayTimeSec = lane.PendingEntry!.TimeOffsetSec;

            if (ShouldSkipPendingOgcd(lane))
            {
                CompleteLaneEntry(lane);
                continue;
            }

            if (IsPendingActionSatisfied(lane))
            {
                CompleteLaneEntry(lane);
                continue;
            }

            if (lane.RequestAccepted)
            {
                var localPlayer = objectTable.LocalPlayer;
                var resolvedActionId = ResolveAutoActionId(lane);
                if (localPlayer != null &&
                    localPlayer.IsCasting &&
                    localPlayer.CastActionId == resolvedActionId)
                {
                    if (!lane.CastObserved)
                    {
                        lane.CastObserved = true;
                    }
                }

                if (IsPendingActionInterrupted(lane))
                {
                    ClearLanePending(lane);
                    continue;
                }

                var info = plugin.RecastDatabase.Lookup(lane.PendingEntry.AbilityId, lane.PendingEntry.AbilityName);
                var hasCastTime = (info?.CastTimeSec ?? 0.0) > 0.0;
                if (!hasCastTime &&
                    DateTime.UtcNow >= lane.RequestAcceptedAtUtc.AddSeconds(AutoAcceptedInstantRetrySec) &&
                    !IsPendingActionSatisfied(lane))
                {
                    ClearLanePending(lane);
                    continue;
                }

                return;
            }

            if (DateTime.UtcNow < lane.LastAttemptAtUtc.AddSeconds(AutoRetryIntervalSec))
                return;

            lane.LastAttemptAtUtc = DateTime.UtcNow;
            if (!TryIssueAutoAction(lane.PendingEntry))
            {
                return;
            }

            lane.RequestAccepted = true;
            lane.RequestAcceptedAtUtc = DateTime.UtcNow;
            lane.RecastObserved = GetActionCooldownRemainingSec(ResolveAutoActionId(lane)) > 0.0;
            return;
        }
    }

    private unsafe void ReceiveActionEffectDetour(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        Header* header,
        TargetEffects* effects,
        GameObjectId* targetEntityIds)
    {
        receiveActionEffectHook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);

        try
        {
            var localPlayer = objectTable.LocalPlayer;
            if (localPlayer == null || casterEntityId != localPlayer.EntityId)
                return;

            var actionId = header->ActionId;
            if (LaneMatchesActionId(autoGcdLane, actionId))
            {
                autoGcdLane.CompletionObserved = true;
                autoGcdLane.RecastObserved = true;
            }

            if (LaneMatchesActionId(autoOgcdLane, actionId))
            {
                autoOgcdLane.CompletionObserved = true;
                autoOgcdLane.RecastObserved = true;
            }
        }
        catch
        {
        }
    }

    // â”€â”€ Auto-execute framework update â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Called every game frame on the framework thread.
    /// Fires <c>ActionManager.UseAction</c> for each timeline entry the moment it
    /// crosses the red bar during live combat â€” but only when
    /// <see cref="AutoExecuteEnabled"/> is true (the hidden easter egg).
    ///
    /// Queue-based design following the WrathCombo pattern:
    ///   â€¢ Phase 1 â€“ Scan all entries each frame. Enqueue each one exactly once when it
    ///               reaches its scheduled time (Â±350 ms accept window). Entries that pass
    ///               the 350 ms window before being seen are permanently skipped so a late-
    ///               joining auto-execute doesn't fire obviously stale actions.
    ///   â€¢ Phase 2 â€“ When <c>AnimationLock == 0</c> (game ready), dequeue the next pending
    ///               action and fire it â€” one per frame, matching WrathCombo's approach.
    ///               Items that have sat in the queue > 5 s (implying a hung queue) are
    ///               discarded, but normally queued oGCDs will fire within a full GCD cycle.
    ///
    /// Attempts to use the current action entry.
    /// </summary>
    private unsafe void OnFrameworkUpdate(IFramework _)
    {
        combatElapsedSec = GetBaseTimelineElapsedSec();
        ProcessAutoScrub();

        var baseTimeSec = GetBaseTimelineElapsedSec();
        autoBaseTimeSec = baseTimeSec;
        autoDisplayGcdTimeSec = baseTimeSec;
        autoDisplayOgcdTimeSec = baseTimeSec;

        if (!AutoExecuteEnabled || activeTimeline == null || overlayDismissed || (!inCombat && !isPreview))
        {
            ResetAutoRuntimeState();
            return;
        }

        if (!IsAutoTimelinePlaying())
        {
            ResetAutoRuntimeState();
            return;
        }

        if (ShouldRebaseAutoRuntime(baseTimeSec))
        {
            if (!InitializeAutoRuntime(baseTimeSec))
                return;
        }

        AdvanceAutoLane(autoOgcdLane, baseTimeSec);
        AdvanceAutoLane(autoGcdLane, baseTimeSec);

        autoLastObservedBaseTimeSec = baseTimeSec;
        autoLastObservedBaseAtUtc = DateTime.UtcNow;
        autoDisplayGcdTimeSec = autoGcdLane.DisplayTimeSec;
        autoDisplayOgcdTimeSec = autoOgcdLane.DisplayTimeSec;
    }

    public void Dispose()
    {
        framework.Update         -= OnFrameworkUpdate;
        condition.ConditionChange -= OnConditionChange;
        dutyState.DutyWiped      -= OnDutyWiped;
        dutyState.DutyCompleted  -= OnDutyCompleted;
        receiveActionEffectHook?.Disable();
        receiveActionEffectHook?.Dispose();
        useActionHook?.Disable();
        useActionHook?.Dispose();
    }
}
