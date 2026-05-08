using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using ATKTip.Data;

namespace ATKTip.Windows;

/// <summary>
/// Overlay window that renders an ATR-exact two-pass timeline:
///   Pass 1 (General layer) — horizontal GCD/oGCD bars
///   Pass 2 (Icon layer)    — action icons drawn on top of bars
/// Boss casts are rendered in a strip anchored to the bottom edge.
/// </summary>
public sealed class OverlayWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ICondition condition;
    private readonly IDutyState dutyState;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    // ── Auto-execute (hidden feature) ──
    /// <summary>
    /// When true, each timeline entry is automatically executed via
    /// <c>ActionManager.UseAction</c> the moment it crosses the red bar during live combat.
    /// Enabled by clicking the Config tab label 7 times in a row in the main window.
    /// </summary>
    public bool AutoExecuteEnabled { get; set; }
    // Queue-based auto-execute state.
    // autoExecQueue  – actions waiting to fire; one dequeued per frame when AnimationLock == 0.
    //                  Tuple includes wall-clock enqueue time so stale entries can be purged.
    // autoExecQueued – timeline keys already seen (enqueued OR window-expired); prevents re-enqueue.
    private readonly Queue<(double scheduledAt, int abilityId, long enqueuedAtMs)> autoExecQueue  = new();
    private readonly HashSet<(double timeSec, int abilityId)>                      autoExecQueued = [];
    private double autoExecLastElapsed = -1.0;   // tracks previous frame's elapsed for backward-scrub detection

    // ── ATR geometry constants (match ATR defaults exactly) ──
    // GCDHeightHigh / GCDHeightLow control the vertical extent of the GCD bar.
    // ATR defaults: high=0.5, low=0.8 → bar spans centerY+0 to centerY+0.3*iconSize.
    private const float BaseActionAnimationLockSec = 0.50f;
    private const float AssumedAnimationLockLatencySec = 0.02f;
    private const float GCDHeightHigh  = 0.5f;
    private const float GCDHeightLow   = 0.8f;
    // GCD window duration (no live cast data available, use the standard 2.5 s).
    private const float GCDWindowSec   = 2.5f;
    // oGCD animlock window (shorter bar for oGCDs).
    private const float OGCDWindowSec  = BaseActionAnimationLockSec + AssumedAnimationLockLatencySec;
    private const double OverlayMinOgcdVisualGapSec = 0.8;
    // Rounding radius for bars (matches ATR GCDRound default ~4).
    private const float BarRound       = 4.0f;
    // Gap between stacked icons in the same bucket.
    private const float IconGap        = 2.0f;

    // ── ATR-exact colours ──
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
    // Now / start line (ATR GridStartLine — red vertical bar at current time).
    private static readonly Vector4 ColNowLine        = new(0.80f, 0.20f, 0.20f, 1.00f);
    // Grid lines.
    private static readonly Vector4 ColGrid           = new(1.00f, 1.00f, 1.00f, 0.12f);
    private static readonly Vector4 ColGridMajor      = new(1.00f, 1.00f, 1.00f, 0.28f);
    private static readonly Vector4 ColGridLabel      = new(1.00f, 1.00f, 1.00f, 0.22f);

    // ── Combat tracking ──
    private bool     inCombat;
    private DateTime combatStartTime;
    private double   combatElapsedSec;
    /// <summary>When true the view is frozen at <see cref="combatElapsedSec"/> so the
    /// user can study upcoming abilities without the red line advancing.</summary>
    private bool     combatViewPaused;
    /// <summary>Set when the user clicks × to close the overlay mid-combat.
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

    // ── Preview (scrub) mode ──
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

    // ── Timeline data ──
    private AggregatedTimeline?      activeTimeline;
    private string                   activeTimelineKey = string.Empty;
    private Dictionary<int, bool>    skillVisibility   = [];

    // ── Caches ──
    private readonly Dictionary<int, uint> iconIdCache = [];
    private readonly ConcurrentDictionary<int, bool> oGcdCache = new();

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

    // ═══════════════════════════════════════════════════════════════════

    public OverlayWindow(Plugin plugin, ICondition condition, IDutyState dutyState, IFramework framework, IPluginLog log)
        : base("ATKTip##Overlay",
            ImGuiWindowFlags.NoTitleBar      |
            ImGuiWindowFlags.NoScrollbar     |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse)
    {
        this.plugin     = plugin;
        this.condition  = condition;
        this.dutyState  = dutyState;
        this.framework  = framework;
        this.log        = log;

        condition.ConditionChange += OnConditionChange;
        dutyState.DutyWiped      += OnDutyWiped;
        dutyState.DutyCompleted  += OnDutyCompleted;
        framework.Update         += OnFrameworkUpdate;

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

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>True when a timeline is loaded and ready to display.</summary>
    public bool HasActiveTimeline => activeTimeline != null;
    public bool IsEmbeddedPreviewScrubbing => embeddedPreviewIsScrubbing;

    public bool CanManageAbilityAnts()
        => Cfg.AntsEnabled &&
           IsOpen &&
           antsArmed &&
           !overlayDismissed &&
           activeTimeline != null &&
           (inCombat || isPreview);

    public void SetTimeline(AggregatedTimeline? timeline, string key)
    {
        activeTimeline    = timeline;
        activeTimelineKey = key;
        overlayDismissed  = false;      // fresh load — re-enable ants and auto-exec
        antsArmed         = false;
        skillVisibility.Clear();
        oGcdCache.Clear();
        autoExecQueue.Clear();
        autoExecQueued.Clear();
        autoExecLastElapsed = -1.0;

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

    public void StartPreview(AggregatedTimeline timeline)
    {
        var key = TimelineDatabase.MakeKey(timeline.EncounterId, timeline.SpecName);
        SetTimeline(timeline, key);
        antsArmed            = true;
        isPreview            = true;
        previewAutoplay      = false;   // start paused — user presses play to begin
        previewStartTime     = DateTime.UtcNow;
        previewManualTimeSec = 0;
        combatElapsedSec     = 0;
        isScrubbing          = false;
        autoExecQueue.Clear();
        autoExecQueued.Clear();
        autoExecLastElapsed  = -1.0;
        IsOpen               = true;
    }

    public void StopPreview()
    {
        SetTimeline(null, string.Empty);
        antsArmed            = false;
        isPreview            = false;
        previewAutoplay      = false;
        combatElapsedSec     = 0;
        previewManualTimeSec = 0;
        isScrubbing          = false;
        isEncounterZone      = false;
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
        antsArmed         = false;
        isPreview        = false;
        previewAutoplay  = false;
        combatElapsedSec = 0;
        inCombat         = false;
        isScrubbing      = false;
        isEncounterZone  = false;
        IsOpen           = false;
    }

    /// <summary>
    /// Opens the overlay in a paused preview at t=0 so the player can study the
    /// timeline before the pull. When combat starts, OnConditionChange locks the
    /// overlay and switches to live tracking automatically.
    /// </summary>
    public void PrepareCombatPreview(AggregatedTimeline timeline)
    {
        var key = TimelineDatabase.MakeKey(timeline.EncounterId, timeline.SpecName);
        SetTimeline(timeline, key);
        antsArmed            = false;
        isPreview            = true;
        previewAutoplay      = false;   // paused — user scrubs freely
        previewStartTime     = DateTime.UtcNow;
        previewManualTimeSec = 0;
        combatElapsedSec     = 0;
        isScrubbing          = false;
        isEncounterZone      = true;    // loaded by EncounterTracker — respond to combat events
        autoExecQueue.Clear();
        autoExecQueued.Clear();
        autoExecLastElapsed  = -1.0;
        IsOpen               = true;
    }

    /// <summary>
    /// Returns true when <paramref name="abilityId"/> should have native FFXIV ants
    /// this frame.  Called from the <c>IsActionHighlighted</c> hook on the game thread.
    /// Uses per-type (GCD / oGCD) timing windows and enable flags.
    /// </summary>
    public bool IsAbilityInAntsWindow(int abilityId)
    {
        if (!CanManageAbilityAnts()) return false;
        var timeline = activeTimeline;
        if (timeline == null) return false;

        // Compute elapsed time fresh so the hook (game thread) stays in sync with
        // the red line regardless of when the last Draw() frame ran.
        var elapsed = inCombat
            ? (DateTime.UtcNow - combatStartTime).TotalSeconds
            : combatElapsedSec;   // preview uses the scrub position set on the UI thread

        if (IsOGCD(abilityId))
        {
            if (!Cfg.OgcdAntsEnabled) return false;

            var before        = (double)Cfg.AntsDurationBefore;
            var after         = (double)Cfg.AntsDurationAfter;
            var visibleEntries = timeline.Entries
                .Where(e => !e.IsGcd)
                .Where(e => e.AbilityId == abilityId)
                .Where(e => skillVisibility.GetValueOrDefault(e.AbilityId, true))
                .Where(e => e.Frequency >= GetAbilityThreshold(e.AbilityId))
                .OrderBy(e => e.TimeOffsetSec)
                .ToList();
            var placements = BuildOverlayOgcdPlacements(
                visibleEntries,
                elapsed,
                0f,
                Math.Max(Cfg.OverlayPixelsPerSec, 1f),
                Cfg.OverlayMaxStackedIcons);

            foreach (var placement in placements)
            {
                var rel = placement.DisplayTimeSec - elapsed;
                if (rel >= -after && rel <= before) return true;
            }
            return false;
        }
        else
        {
            if (!Cfg.GcdAntsEnabled) return false;

            var before    = (double)Cfg.GcdAntsDurationBefore;
            var after     = (double)Cfg.GcdAntsDurationAfter;

            // GCDs: exactly one glows — the entry closest to the red line.
            Data.TimelineEntry? bestGcd = null;
            var minAbsRel = double.MaxValue;
            foreach (var e in timeline.Entries)
            {
                if (e.Frequency < GetAbilityThreshold(e.AbilityId)) continue;
                if (!skillVisibility.GetValueOrDefault(e.AbilityId, true)) continue;
                if (!e.IsGcd) continue;
                var rel = e.TimeOffsetSec - elapsed;
                if (rel < -after || rel > before) continue;
                var absRel = Math.Abs(rel);
                if (absRel < minAbsRel) { minAbsRel = absRel; bestGcd = e; }
            }
            return bestGcd != null && bestGcd.AbilityId == abilityId;
        }
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
        var timeline = activeTimeline;
        if (timeline == null) return (gcd, ogcd);

        var elapsed = inCombat
            ? (DateTime.UtcNow - combatStartTime).TotalSeconds
            : combatElapsedSec;

        // ── oGCDs ─────────────────────────────────────────────────────────
        if (Cfg.OgcdAntsEnabled)
        {
            var before        = (double)Cfg.AntsDurationBefore;
            var after         = (double)Cfg.AntsDurationAfter;
            var visibleEntries = timeline.Entries
                .Where(e => !e.IsGcd)
                .Where(e => skillVisibility.GetValueOrDefault(e.AbilityId, true))
                .Where(e => e.Frequency >= GetAbilityThreshold(e.AbilityId))
                .OrderBy(e => e.TimeOffsetSec)
                .ToList();
            var placements = BuildOverlayOgcdPlacements(
                visibleEntries,
                elapsed,
                0f,
                Math.Max(Cfg.OverlayPixelsPerSec, 1f),
                Cfg.OverlayMaxStackedIcons);

            foreach (var placement in placements)
            {
                var rel = placement.DisplayTimeSec - elapsed;
                if (rel >= -after && rel <= before) ogcd.Add(placement.Entry.AbilityId);
            }
        }

        // ── GCDs — exactly one (the entry closest to the red line) ─────────
        if (Cfg.GcdAntsEnabled)
        {
            var before    = (double)Cfg.GcdAntsDurationBefore;
            var after     = (double)Cfg.GcdAntsDurationAfter;

            Data.TimelineEntry? bestGcd = null;
            var minAbsRel = double.MaxValue;
            foreach (var e in timeline.Entries)
            {
                if (e.Frequency < GetAbilityThreshold(e.AbilityId)) continue;
                if (!skillVisibility.GetValueOrDefault(e.AbilityId, true)) continue;
                if (!e.IsGcd) continue;
                var rel = e.TimeOffsetSec - elapsed;
                if (rel < -after || rel > before) continue;
                var absRel = Math.Abs(rel);
                if (absRel < minAbsRel) { minAbsRel = absRel; bestGcd = e; }
            }
            if (bestGcd != null) gcd.Add(bestGcd.AbilityId);
        }

        return (gcd, ogcd);
    }

    // ── Combat events ───────────────────────────────────────────────────

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat) return;

        if (value && !inCombat)
        {
            // Only start live tracking for timelines loaded by EncounterTracker
            // (i.e. the player is actually in a mapped encounter zone).
            // Ignoring combat from training dummies, open-world mobs, etc.
            if (!isEncounterZone) return;

            inCombat             = true;
            isPreview            = false;
            previewAutoplay      = false;
            isScrubbing          = false;
            combatViewPaused     = false;
            combatStartTime      = DateTime.UtcNow;
            combatElapsedSec     = 0;
            autoExecQueue.Clear();
            autoExecQueued.Clear();

            // Lock overlay when the fight starts so it stays in place
            Cfg.OverlayLocked = true;
            plugin.SaveConfig();

            if (activeTimeline != null && Cfg.OverlayEnabled)
                IsOpen = true;
        }
        else if (!value && inCombat)
        {
            inCombat = false;
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
        autoExecQueue.Clear();
        autoExecQueued.Clear();
        // Wipe — snap back to t=0 in paused preview.
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
        // Completion — stay paused at wherever the timeline is.
        if (activeTimeline != null)
        {
            isPreview            = true;
            previewAutoplay      = false;
            previewManualTimeSec = combatElapsedSec;
            isScrubbing          = false;
        }
    }

    // ── Window lifecycle ────────────────────────────────────────────────

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
        // Lock applies in ALL modes (including preview) — respects config.
        if (Cfg.OverlayLocked && !isScrubbing)
            Flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        else
            Flags &= ~(ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize);

        // Always allow mouse input so the lock / close / pause buttons remain
        // clickable during live combat as well as in preview mode.
        Flags &= ~ImGuiWindowFlags.NoInputs;

        ImGui.SetNextWindowBgAlpha(0.0f);
    }

    // ═══════════════════════════════════════════════════════════════════
    // DRAW
    // ═══════════════════════════════════════════════════════════════════

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

        // ── Time update ──────────────────────────────────────────────
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

        var isActive   = inCombat || isPreview;
        var drawList   = ImGui.GetWindowDrawList();
        var wPos       = ImGui.GetWindowPos();
        var wSize      = ImGui.GetWindowSize();

        // ── Background ───────────────────────────────────────────────
        drawList.AddRectFilled(wPos, wPos + wSize,
            ImGui.GetColorU32(new Vector4(0.06f, 0.06f, 0.10f, bgOpacity)), 4.0f);

        // ── Scrub / control bar (preview AND live combat) ────────────
        // Row: [▶/⏸] [🔒] [track...........] [time] [×]
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

            // Track sits between lock button and close button
            var trackL = lockX + btnW + btnGap;
            var trackR = closeX - btnGap;
            var trackW = MathF.Max(trackR - trackL, 1f);

            var frac = (float)Math.Clamp(combatElapsedSec / fightDur, 0.0, 1.0);

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
            var timeLabel = FormatTime(combatElapsedSec) + " / " + FormatTime(fightDur);
            var labelSz   = ImGui.CalcTextSize(timeLabel);
            drawList.AddText(
                new Vector2(trackR - labelSz.X, sTop + btnH + 1f),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.35f)),
                timeLabel);

            // ── Play / Pause button ───────────────────────────────────
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
                    // Toggle freeze — pause freezes the view, play re-syncs to live.
                    combatViewPaused = !combatViewPaused;
                    if (!combatViewPaused)
                        combatElapsedSec = (DateTime.UtcNow - combatStartTime).TotalSeconds;
                }
                else if (previewAutoplay)
                {
                    previewAutoplay      = false;
                    previewManualTimeSec = combatElapsedSec;
                }
                else
                {
                    previewAutoplay  = true;
                    previewStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(previewManualTimeSec);
                }
            }

            // ── Lock button ───────────────────────────────────────────
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
                plugin.SaveConfig();
            }

            // ── Close button ──────────────────────────────────────────
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
                }

                if (inCombat)
                {
                    antsArmed = false;
                    overlayDismissed = true;    // suppress ants + auto-exec for rest of pull
                    IsOpen = false;
                }
                else
                    StopPreview();

                return;
            }

            // ── Scrub input (track hit area) ──────────────────────────
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

        // ── Lay out timeline and boss strip ─────────────────────────
        var hasBoss       = activeTimeline.BossEntries.Count > 0;
        var bossStripH    = hasBoss ? 26.0f : 0.0f;

        var tlTop    = wPos.Y + scrubH + 2f;
        var tlBot    = wPos.Y + wSize.Y - bossStripH - 2f;
        var tlLeft   = wPos.X + 2f;
        var tlRight  = wPos.X + wSize.X - 2f;
        var tlH      = tlBot - tlTop;
        var tlW      = tlRight - tlLeft;

        if (tlH < 16f || tlW < 50f) return;

        // "Now" X — timeBehind seconds from the LEFT edge, so widening the window
        // extends the look-ahead on the right rather than pushing the red line left.
        var nowX = tlLeft + timeBehind * pxPerSec;

        // Vertical center of the timeline (ATR centers all items here)
        var centerY = tlTop + tlH / 2f;

        // oGCD size and center Y — follow ATR's presentation more closely:
        // smaller icons that sit above the center line, while still sharing
        // the same horizontal timing space as GCDs.
        var oGcdSize    = iconSize * Cfg.OGCDSizeRatio;
        var oGcdCenterY = centerY - (Cfg.OGCDVerticalOffset * iconSize + oGcdSize / 2f);

        // ATR GCD bar top/bottom Y
        // leftTop.Y    = centerY + (GCDHeightHigh * iconSize - iconSize / 2) = centerY + 0
        // leftBottom.Y = centerY + (GCDHeightLow  * iconSize - iconSize / 2) = centerY + 0.3*iconSize
        var gcdBarTop = centerY + GCDHeightHigh * iconSize - iconSize / 2f;  // = centerY
        var gcdBarBot = centerY + GCDHeightLow  * iconSize - iconSize / 2f;  // = centerY + 0.3*iconSize

        var visStart = combatElapsedSec - (nowX - tlLeft)  / pxPerSec;
        var visEnd   = combatElapsedSec + (tlRight - nowX) / pxPerSec;

        // ── ATR DrawGrid ──────────────────────────────────────────────
        if (showGrid)
        {
            for (var t = Math.Ceiling(visStart); t <= visEnd; t += 1.0)
            {
                if (t < 0) continue;
                var gx = nowX + (float)(t - combatElapsedSec) * pxPerSec;
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

        // ── ATR GridCenterLine ────────────────────────────────────────
        drawList.AddLine(
            new Vector2(tlLeft,  centerY),
            new Vector2(tlRight, centerY),
            ImGui.GetColorU32(ColCenterLine), 1.0f);

        // ── ATR GridStartLine (the "now" vertical bar) ────────────────
        drawList.AddLine(
            new Vector2(nowX, tlTop),
            new Vector2(nowX, tlBot),
            ImGui.GetColorU32(ColNowLine), 2.0f);

        // ── Idle message ──────────────────────────────────────────────
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
            // ── Collect and bucket visible entries ────────────────────
            var visibleEntries = TimelineJobRules.ApplyPostSelectionRules(
                    activeTimeline.SpecName,
                    activeTimeline.Entries,
                    promoteMacrocosmosToVisualGcd: true)
                .Where(e => e.TimeOffsetSec >= visStart - 5 && e.TimeOffsetSec <= visEnd + 5)
                .Where(e => skillVisibility.GetValueOrDefault(e.AbilityId, true))
                .Where(e => e.Frequency >= GetAbilityThreshold(e.AbilityId))
                .OrderBy(e => e.TimeOffsetSec)
                .ThenByDescending(e => e.Frequency)
                .ToList();
            var gcdWindowSec = GCDWindowSec;
            var gcdBuckets = visibleEntries
                .Where(e => e.IsGcd)
                .GroupBy(e => e.TimeOffsetSec)
                .OrderBy(g => g.Key)
                .Select(g => new OverlayGcdBucket
                {
                    X = nowX + (float)(g.Key - combatElapsedSec) * pxPerSec,
                    IsPast = g.Key < combatElapsedSec,
                    Entries = g.OrderByDescending(e => e.Frequency).Take(maxStack).ToList(),
                })
                .ToList();
            var ogcdPlacements = BuildOverlayOgcdPlacements(
                visibleEntries,
                combatElapsedSec,
                nowX,
                pxPerSec,
                maxStack);

            // ══════════════════════════════════════════════════════════
            // PASS 1 — TimelineLayer.General
            // Draw horizontal bars for every GCD and oGCD.
            // ATR draws: Background → AnimLock (skipped: no data) → Cast fill → Border
            // ══════════════════════════════════════════════════════════
            foreach (var bucket in gcdBuckets)
            {
                // ── GCD bars ──
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

                        // Cast fill (blue) — left 45% approximates cast time
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
                    DrawIcon(drawList, e, pos, iconSize, bucket.IsPast, pastAlpha, combatElapsedSec);
                }
            }

            foreach (var placement in ogcdPlacements)
            {
                var pos = new Vector2(placement.X - oGcdSize / 2f, oGcdCenterY - oGcdSize / 2f);
                DrawIcon(drawList, placement.Entry, pos, oGcdSize, placement.IsPast, pastAlpha, combatElapsedSec);
            }
        }

        // ══════════════════════════════════════════════════════════════
        // BOSS CAST STRIP — anchored to bottom
        // ══════════════════════════════════════════════════════════════
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
                var startX = nowX + (float)(boss.CastStartSec - combatElapsedSec) * pxPerSec;
                var endX   = nowX + (float)(boss.CastEndSec   - combatElapsedSec) * pxPerSec;
                if (endX - startX < 3f) endX = startX + 3f;  // min width for instants

                if (endX < tlLeft || startX > tlRight) continue;
                var dL = MathF.Max(startX, tlLeft);
                var dR = MathF.Min(endX,   tlRight);
                var isPastBoss = boss.CastEndSec < combatElapsedSec;
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

    // ═══════════════════════════════════════════════════════════════════
    // PASS 2 ICON HELPER
    // ═══════════════════════════════════════════════════════════════════

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

        var visibleEntries = TimelineJobRules.ApplyPostSelectionRules(
                timeline.SpecName,
                timeline.Entries,
                promoteMacrocosmosToVisualGcd: true)
            .Where(e => e.TimeOffsetSec >= visStart - 5 && e.TimeOffsetSec <= visEnd + 5)
            .Where(e => embeddedSkillVisibility.GetValueOrDefault(e.AbilityId, true))
            .Where(e => e.Frequency >= GetAbilityThreshold(timeline, e.AbilityId))
            .OrderBy(e => e.TimeOffsetSec)
            .ThenByDescending(e => e.Frequency)
            .ToList();
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
            // Fallback — coloured rect + abbreviated name
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

    // ═══════════════════════════════════════════════════════════════════
    // ICON LOADING
    // ═══════════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════════
    // oGCD DETECTION
    // ═══════════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

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
        // Body — filled rounded rect occupying the lower ~55% of the icon area
        var bodyW = size * 0.62f;
        var bodyH = size * 0.50f;
        var bodyT = center.Y + size * 0.06f;
        var bodyB = bodyT + bodyH;
        var bodyL = center.X - bodyW / 2f;
        var bodyR = center.X + bodyW / 2f;
        dl.AddRectFilled(new Vector2(bodyL, bodyT), new Vector2(bodyR, bodyB), col, 2f);

        // Keyhole — small dark circle in center of body
        dl.AddCircleFilled(
            new Vector2(center.X, bodyT + bodyH * 0.42f),
            bodyW * 0.12f,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.45f)), 6);

        // Shackle geometry
        var sR    = bodyW * 0.28f;                              // arc radius
        var sCenX = locked ? center.X : center.X + sR * 0.55f; // shift right when open
        var sCenY = bodyT;                                       // arc center at body top

        // Top arc (upper semicircle, π → 2π) rendered as line segments
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
        var t = text[..Math.Min(text.Length, approxChars - 1)] + "…";
        return ImGui.CalcTextSize(t).X <= maxWidth ? t : string.Empty;
    }

    // ── Auto-execute framework update ───────────────────────────────────

    /// <summary>
    /// Called every game frame on the framework thread.
    /// Fires <c>ActionManager.UseAction</c> for each timeline entry the moment it
    /// crosses the red bar during live combat — but only when
    /// <see cref="AutoExecuteEnabled"/> is true (the hidden easter egg).
    ///
    /// Queue-based design following the WrathCombo pattern:
    ///   • Phase 1 – Scan all entries each frame. Enqueue each one exactly once when it
    ///               reaches its scheduled time (±350 ms accept window). Entries that pass
    ///               the 350 ms window before being seen are permanently skipped so a late-
    ///               joining auto-execute doesn't fire obviously stale actions.
    ///   • Phase 2 – When <c>AnimationLock == 0</c> (game ready), dequeue the next pending
    ///               action and fire it — one per frame, matching WrathCombo's approach.
    ///               Items that have sat in the queue > 5 s (implying a hung queue) are
    ///               discarded, but normally queued oGCDs will fire within a full GCD cycle.
    ///
    /// Key fix vs. previous 350 ms staleness check: oGCDs enqueued simultaneously (e.g.
    /// two oGCDs between the same pair of GCDs) now wait patiently in the queue while
    /// AnimationLock drains (~600 ms per oGCD) before each fires.
    /// </summary>
    private unsafe void OnFrameworkUpdate(IFramework fw)
    {
        if (!AutoExecuteEnabled) return;
        if (activeTimeline == null) return;
        if (overlayDismissed) return;

        // Active means either live combat or any preview mode (autoplay or manual scrub).
        if (!inCombat && !isPreview) return;

        // combatElapsedSec is the canonical "current time on the timeline" for all modes:
        //   combat        → (UtcNow - combatStartTime).TotalSeconds   (set each Draw frame)
        //   preview play  → (UtcNow - previewStartTime).TotalSeconds  (set each Draw frame)
        //   preview scrub → previewManualTimeSec                       (set by scrub bar)
        var elapsed = combatElapsedSec;

        // ── Backward-scrub detection ──────────────────────────────────────────────────
        // If time moved back by > 1 second (wipe, scrub), flush everything so entries
        // can be re-enqueued from scratch.
        if (elapsed < autoExecLastElapsed - 1.0)
        {
            autoExecQueue.Clear();
            autoExecQueued.Clear();
        }
        autoExecLastElapsed = elapsed;

        // ── Phase 1: Enqueue entries entering their window ────────────────────────────
        // Accept: scheduled time has passed AND we're within 350 ms of it.
        // Reject (permanently skip): > 350 ms late and never enqueued — this prevents
        //   firing obviously stale actions if auto-execute is toggled on mid-fight.
        const double AcceptWindowSec = 0.350;

        foreach (var e in activeTimeline.Entries)
        {
            // Respect the same visibility and frequency gates as the visual overlay.
            if (!skillVisibility.GetValueOrDefault(e.AbilityId, true)) continue;
            if (e.Frequency < GetAbilityThreshold(e.AbilityId)) continue;

            var key   = (e.TimeOffsetSec, e.AbilityId);
            var delta = elapsed - e.TimeOffsetSec;   // positive = we've passed the scheduled mark

            if (delta < 0.0) continue;   // not yet time

            if (delta > AcceptWindowSec)
            {
                // Past the accept window — add to the seen set so we never enqueue it
                // in a future frame either.  (Add is a no-op if already present.)
                autoExecQueued.Add(key);
                continue;
            }

            // Within [0, AcceptWindowSec]: enqueue exactly once, stamped with wall-clock time.
            if (autoExecQueued.Add(key))
                autoExecQueue.Enqueue((e.TimeOffsetSec, e.AbilityId, Environment.TickCount64));
        }

        // ── Phase 2: Fire one action per frame when the game is ready ─────────────────
        // WrathCombo rule: require AnimationLock == 0 before sending any ability.
        // This prevents UseAction from overwriting the game's single native queue slot
        // while a prior action is still in its 600 ms animation lock.
        var animLock = ActionManager.Instance()->AnimationLock;
        if (animLock > 0f) return;

        // Drain any entries that are implausibly old (> 5 s in queue = fight ended, lag spike,
        // or other anomaly), then fire the first healthy one and stop for this frame.
        const long MaxQueueAgeMs = 5_000L;
        var nowMs = Environment.TickCount64;

        while (autoExecQueue.Count > 0)
        {
            var (_, abilityId, enqueuedAtMs) = autoExecQueue.Peek();

            if (nowMs - enqueuedAtMs > MaxQueueAgeMs)
            {
                autoExecQueue.Dequeue();   // stale — silently discard
                continue;
            }

            // Fire this action and stop for this frame.
            autoExecQueue.Dequeue();
            ActionManager.Instance()->UseAction(ActionType.Action, (uint)abilityId);
            break;
        }
    }

    public void Dispose()
    {
        framework.Update         -= OnFrameworkUpdate;
        condition.ConditionChange -= OnConditionChange;
        dutyState.DutyWiped      -= OnDutyWiped;
        dutyState.DutyCompleted  -= OnDutyCompleted;
    }
}
