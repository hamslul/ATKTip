using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using ATKTip.Data;
using LuminaAction = Lumina.Excel.Sheets.Action;
using GameObjectId = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId;
using static FFXIVClientStructs.FFXIV.Client.Game.Character.ActionEffectHandler;

namespace ATKTip;

[StructLayout(LayoutKind.Explicit)]
internal struct EnemyActorCast
{
    [FieldOffset(0)]
    public ushort ActionId;

    [FieldOffset(2)]
    public byte ActionKind;

    [FieldOffset(3)]
    public byte DisplayDelay;

    [FieldOffset(4)]
    public uint RealActionId;

    [FieldOffset(8)]
    public float CastTime;

    [FieldOffset(12)]
    public uint TargetId;

    [FieldOffset(16)]
    public ushort FacingRaw;

    [FieldOffset(18)]
    public byte CanInterrupt;

    [FieldOffset(24)]
    public ushort PosX;

    [FieldOffset(26)]
    public ushort PosY;

    [FieldOffset(28)]
    public ushort PosZ;

    public readonly float Facing => (float)((double)(int)FacingRaw * 9.587526218325454E-05 - Math.PI);

    public readonly Vector3 Pos
    {
        get
        {
            float x = (float)(int)PosX * 0.030518044f - 1000f;
            float y = (float)(int)PosY * 0.030518044f - 1000f;
            float z = (float)(int)PosZ * 0.030518044f - 1000f;
            return new Vector3(x, y, z);
        }
    }
}

public sealed unsafe class EnemyCastTracker : IDisposable
{
    public enum EventKind : byte
    {
        CastStart,
        Ability,
        CastFinish,
    }

    public sealed class TrackedEvent
    {
        public long Seq { get; init; }
        public int PullIndex { get; init; }
        public DateTime TimeUtc { get; init; }
        public double RelativeTimeSec { get; init; }
        public EventKind Kind { get; init; }
        public string SourceName { get; init; } = string.Empty;
        public uint SourceId { get; init; }
        public string TargetName { get; init; } = string.Empty;
        public uint TargetId { get; init; }
        public int AbilityId { get; init; }
        public string AbilityName { get; init; } = string.Empty;
        public uint IconId { get; init; }
        public double CastDurationSec { get; init; }
        public bool MatchedBossEntry { get; set; }
        public int MatchedBossIndex { get; set; } = -1;
        public double MatchedTimelineTimeSec { get; set; } = double.NaN;
        public bool TriggeredAlignment { get; set; }
    }

    public sealed class PullInfo
    {
        public int Index { get; init; }
        public DateTime StartUtc { get; init; }
        public DateTime EndUtc { get; set; }
        public int EventCount { get; set; }
        public string Label => $"Pull {Index}";

        public string FormatRange()
        {
            var start = StartUtc.ToLocalTime();
            var end = (EndUtc == DateTime.MinValue ? DateTime.UtcNow : EndUtc).ToLocalTime();
            return $"{start:HH:mm:ss} · {FormatDuration(end - start)}";
        }

        private static string FormatDuration(TimeSpan value)
        {
            var totalMinutes = (int)value.TotalMinutes;
            return $"{totalMinutes}:{value.Seconds:00} · {Math.Max(0, value.Milliseconds) / 10:00}";
        }
    }

    public sealed class AlignmentSnapshot
    {
        public long EventSeq { get; init; }
        public string AbilityName { get; init; } = string.Empty;
        public EventKind EventKind { get; init; }
        public int BossIndex { get; init; }
        public int BossEntryCount { get; init; }
        public double ObservedTimeSec { get; init; }
        public double TimelineTimeSec { get; init; }
        public DateTime UpdatedAtUtc { get; init; }
    }

    private sealed class ActorState
    {
        public uint LastCastId;
        public bool SeenThisFrame;
    }

    private sealed class AlignmentCandidate
    {
        public required TrackedEvent Event { get; init; }
        public required string NormalizedAbilityName { get; init; }
    }

    private sealed class BossAlignmentEntry
    {
        public int Index { get; init; }
        public required string NormalizedAbilityName { get; init; }
        public required BossTimelineEntry Entry { get; init; }
    }

    private sealed class PendingHookEvent
    {
        public required EventKind Kind { get; init; }
        public required DateTime TimeUtc { get; init; }
        public required uint SourceId { get; init; }
        public required string SourceName { get; init; }
        public required uint TargetId { get; init; }
        public required string TargetName { get; init; }
        public required int AbilityId { get; init; }
        public required string AbilityName { get; init; }
        public required uint IconId { get; init; }
        public required double CastDurationSec { get; init; }
    }

    private delegate void ActorCastDelegate(uint casterId, EnemyActorCast* data);

    private const string ActorCastSig = "40 53 57 48 81 EC ?? ?? ?? ?? 48 8B FA 8B D1";
    private const double AlignmentSeekBypassWindowSec = 7.0;
    private const double CollapsedStartcastSuppressionWindowSec = 0.75;

    private readonly Plugin plugin;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Hook<ActorCastDelegate>? actorCastHook;
    private readonly Dictionary<uint, ActorState> actorStates = [];
    private readonly HashSet<(uint SourceId, int AbilityId)> activeCastKeys = [];
    private readonly Queue<PendingHookEvent> hookQueue = new();
    private readonly object hookQueueLock = new();
    private readonly List<PullInfo> pulls = [];
    private readonly List<TrackedEvent> events = [];
    private readonly List<AlignmentCandidate> currentPullCandidates = [];
    private readonly List<BossAlignmentEntry> bossAlignmentEntries = [];
    private readonly HashSet<string> matchableAbilityNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, (string Name, uint IconId)> actionInfoCache = [];

    private AggregatedTimeline? activeTimeline;
    private string activeTimelineKey = string.Empty;
    private bool pullOpen;
    private int currentPullIndex;
    private DateTime currentPullStartUtc = DateTime.MinValue;
    private long nextSequence;

    public EnemyCastTracker(
        Plugin plugin,
        IObjectTable objectTable,
        IFramework framework,
        IGameInteropProvider interop,
        IPluginLog log)
    {
        this.plugin = plugin;
        this.objectTable = objectTable;
        this.framework = framework;
        this.log = log;

        try
        {
            actorCastHook = interop.HookFromSignature<ActorCastDelegate>(ActorCastSig, ActorCastDetour);
            actorCastHook.Enable();
        }
        catch (Exception ex)
        {
            log.Error(ex, "EnemyCastTracker: failed to install ActorCast hook.");
        }

        framework.Update += OnFrameworkUpdate;
    }

    public IReadOnlyList<PullInfo> Pulls => pulls;
    public IReadOnlyList<TrackedEvent> Events => events;
    public AlignmentSnapshot? LastAlignment { get; private set; }
    public string ActiveTimelineKey => activeTimelineKey;
    public bool HasActiveTimeline => activeTimeline != null;
    public int CurrentPullIndex => currentPullIndex;

    public void SetActiveTimeline(AggregatedTimeline? timeline, string key)
    {
        activeTimeline = timeline;
        activeTimelineKey = timeline == null ? string.Empty : key;

        bossAlignmentEntries.Clear();
        matchableAbilityNames.Clear();
        LastAlignment = null;

        if (timeline == null)
        {
            RebuildAlignmentState(shouldApplyOverlayAlignment: false);
            return;
        }

        foreach (var (entry, index) in timeline.BossEntries
                     .OrderBy(static entry => entry.CastStartSec)
                     .Select((entry, index) => (entry, index)))
        {
            var normalizedName = NormalizeAbilityName(entry.AbilityName);
            if (string.IsNullOrWhiteSpace(normalizedName))
                continue;

            bossAlignmentEntries.Add(new BossAlignmentEntry
            {
                Index = index,
                NormalizedAbilityName = normalizedName,
                Entry = entry,
            });
            matchableAbilityNames.Add(normalizedName);
        }

        RebuildAlignmentState(shouldApplyOverlayAlignment: false);
    }

    public void NotifyCombatStarted()
    {
        if (activeTimeline == null)
            return;

        if (pullOpen)
            return;

        StartNewPull(DateTime.UtcNow);
    }

    public void NotifyCombatEnded()
    {
        if (!pullOpen)
            return;

        pulls[^1].EndUtc = DateTime.UtcNow;
        pullOpen = false;
    }

    public void NotifyWipe()
    {
        NotifyCombatEnded();
        ResetTransientState();

        if (!plugin.Configuration.DebugEnabled)
            ClearAll();
    }

    public void NotifyZoneChange()
    {
        NotifyCombatEnded();
        ResetTransientState();

        if (!plugin.Configuration.DebugEnabled)
            ClearAll();
    }

    public void ClearAll()
    {
        pulls.Clear();
        events.Clear();
        currentPullIndex = 0;
        currentPullStartUtc = DateTime.MinValue;
        pullOpen = false;
        ResetTransientState();
    }

    public void ObserveActionEffect(
        uint casterEntityId,
        Header* header,
        GameObjectId* targetEntityIds)
    {
        try
        {
            if (!ShouldCapture() || header == null)
                return;

            if (!TryGetEnemyBattleChara(casterEntityId, out var caster))
                return;

            if ((ActionType)header->ActionType != ActionType.Action)
                return;

            var actionId = unchecked((int)header->ActionId);
            if (actionId <= 0)
                return;

            var (abilityName, iconId) = ResolveActionInfo(actionId);
            if (string.IsNullOrWhiteSpace(abilityName))
                return;

            var targetId = ResolveActionEffectTargetId(header, targetEntityIds);
            var targetName = ResolveObjectName(targetId);

            QueueHookEvent(new PendingHookEvent
            {
                Kind = EventKind.Ability,
                TimeUtc = DateTime.UtcNow,
                SourceId = casterEntityId,
                SourceName = ResolveObjectName(casterEntityId, caster.Name.TextValue),
                TargetId = targetId,
                TargetName = targetName,
                AbilityId = actionId,
                AbilityName = abilityName,
                IconId = iconId,
                CastDurationSec = 0.0,
            });
        }
        catch
        {
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        DrainHookQueue();

        if (!ShouldCapture())
        {
            actorStates.Clear();
            activeCastKeys.Clear();
            return;
        }

        PollEnemyCasts();
    }

    private bool ShouldCapture()
        => activeTimeline != null;

    private void StartNewPull(DateTime startTimeUtc)
    {
        currentPullIndex = pulls.Count + 1;
        currentPullStartUtc = startTimeUtc;
        pullOpen = true;
        pulls.Add(new PullInfo
        {
            Index = currentPullIndex,
            StartUtc = startTimeUtc,
            EndUtc = DateTime.MinValue,
        });
        ResetTransientState();
    }

    private void ResetTransientState()
    {
        actorStates.Clear();
        activeCastKeys.Clear();
        currentPullCandidates.Clear();
        LastAlignment = null;
    }

    private void DrainHookQueue()
    {
        while (true)
        {
            PendingHookEvent? pending = null;
            lock (hookQueueLock)
            {
                if (hookQueue.Count > 0)
                    pending = hookQueue.Dequeue();
            }

            if (pending == null)
                break;

            EmitEvent(pending);
        }
    }

    private void QueueHookEvent(PendingHookEvent pending)
    {
        lock (hookQueueLock)
        {
            hookQueue.Enqueue(pending);
        }
    }

    private void EmitEvent(PendingHookEvent pending)
    {
        if (!ShouldCapture())
            return;

        EnsurePull(pending.TimeUtc);

        var trackedEvent = new TrackedEvent
        {
            Seq = ++nextSequence,
            PullIndex = currentPullIndex,
            TimeUtc = pending.TimeUtc,
            RelativeTimeSec = ResolveTrackedTimeSec(pending.TimeUtc),
            Kind = pending.Kind,
            SourceName = pending.SourceName,
            SourceId = pending.SourceId,
            TargetName = pending.TargetName,
            TargetId = pending.TargetId,
            AbilityId = pending.AbilityId,
            AbilityName = pending.AbilityName,
            IconId = pending.IconId,
            CastDurationSec = pending.CastDurationSec,
        };

        events.Add(trackedEvent);
        pulls[^1].EventCount++;

        var castKey = (pending.SourceId, pending.AbilityId);
        var isAlignmentCandidate = false;

        switch (pending.Kind)
        {
            case EventKind.CastStart:
                activeCastKeys.Add(castKey);
                isAlignmentCandidate = true;
                break;
            case EventKind.Ability:
                break;
            case EventKind.CastFinish:
                activeCastKeys.Remove(castKey);
                break;
        }

        if (isAlignmentCandidate)
        {
            var normalizedName = NormalizeAbilityName(trackedEvent.AbilityName);
            if (matchableAbilityNames.Contains(normalizedName) &&
                !ShouldSuppressCollapsedStartcast(normalizedName, trackedEvent.RelativeTimeSec))
            {
                currentPullCandidates.Add(new AlignmentCandidate
                {
                    Event = trackedEvent,
                    NormalizedAbilityName = normalizedName,
                });
            }
        }

        RebuildAlignmentState(shouldApplyOverlayAlignment: isAlignmentCandidate);
    }

    private bool ShouldSuppressCollapsedStartcast(string normalizedAbilityName, double eventTimeSec)
    {
        for (var index = currentPullCandidates.Count - 1; index >= 0; index--)
        {
            var existing = currentPullCandidates[index];
            if (!string.Equals(existing.NormalizedAbilityName, normalizedAbilityName, StringComparison.OrdinalIgnoreCase))
                continue;

            return Math.Abs(existing.Event.RelativeTimeSec - eventTimeSec) <= CollapsedStartcastSuppressionWindowSec;
        }

        return false;
    }

    private void EnsurePull(DateTime timestampUtc)
    {
        if (currentPullIndex != 0 && pullOpen)
            return;

        StartNewPull(timestampUtc);
    }

    private void RebuildAlignmentState(bool shouldApplyOverlayAlignment)
    {
        foreach (var candidate in currentPullCandidates)
        {
            candidate.Event.MatchedBossEntry = false;
            candidate.Event.MatchedBossIndex = -1;
            candidate.Event.MatchedTimelineTimeSec = double.NaN;
            candidate.Event.TriggeredAlignment = false;
        }

        LastAlignment = null;

        if (activeTimeline == null || bossAlignmentEntries.Count == 0 || currentPullCandidates.Count == 0)
            return;

        var nextBossSearchIndex = 0;
        AlignmentCandidate? lastMatchedCandidate = null;
        BossAlignmentEntry? lastMatchedBossEntry = null;
        double lastMatchedTimeSec = double.NaN;

        foreach (var candidate in currentPullCandidates)
        {
            var matchedIndex = FindBestBossEntryIndex(candidate, nextBossSearchIndex);
            if (matchedIndex < 0)
                continue;

            var matchedBoss = bossAlignmentEntries[matchedIndex];
            var timelineTimeSec = GetTimelineTimeSec(matchedBoss.Entry);

            candidate.Event.MatchedBossEntry = true;
            candidate.Event.MatchedBossIndex = matchedBoss.Index;
            candidate.Event.MatchedTimelineTimeSec = timelineTimeSec;

            nextBossSearchIndex = matchedIndex + 1;
            lastMatchedCandidate = candidate;
            lastMatchedBossEntry = matchedBoss;
            lastMatchedTimeSec = timelineTimeSec;
        }

        if (lastMatchedCandidate == null || lastMatchedBossEntry == null || !double.IsFinite(lastMatchedTimeSec))
            return;

        var currentTimelineTimeSec = plugin.OverlayWindow.GetCurrentTimelineTimeSec();
        var shouldSeek =
            shouldApplyOverlayAlignment &&
            Math.Abs(currentTimelineTimeSec - lastMatchedTimeSec) > AlignmentSeekBypassWindowSec;

        lastMatchedCandidate.Event.TriggeredAlignment = shouldSeek;
        LastAlignment = new AlignmentSnapshot
        {
            EventSeq = lastMatchedCandidate.Event.Seq,
            AbilityName = lastMatchedCandidate.Event.AbilityName,
            EventKind = lastMatchedCandidate.Event.Kind,
            BossIndex = lastMatchedBossEntry.Index,
            BossEntryCount = bossAlignmentEntries.Count,
            ObservedTimeSec = lastMatchedCandidate.Event.RelativeTimeSec,
            TimelineTimeSec = lastMatchedTimeSec,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        if (shouldSeek)
            plugin.OverlayWindow.ApplyEnemyCastAlignment(lastMatchedTimeSec);
    }

    private double ResolveTrackedTimeSec(DateTime timestampUtc)
    {
        var overlayTimeSec = plugin.OverlayWindow.GetCurrentTimelineTimeSec();
        if (double.IsFinite(overlayTimeSec) && overlayTimeSec >= 0.0)
            return overlayTimeSec;

        if (currentPullStartUtc != DateTime.MinValue)
            return Math.Max(0.0, (timestampUtc - currentPullStartUtc).TotalSeconds);

        return 0.0;
    }

    private int FindBestBossEntryIndex(AlignmentCandidate candidate, int searchStartIndex)
    {
        for (var index = Math.Max(0, searchStartIndex); index < bossAlignmentEntries.Count; index++)
        {
            var entry = bossAlignmentEntries[index];
            if (!string.Equals(entry.NormalizedAbilityName, candidate.NormalizedAbilityName, StringComparison.OrdinalIgnoreCase))
                continue;

            // Consume the earliest future same-name entry.
            // This mirrors the intended "check it off the timeline" behavior:
            // once #1 is matched, the next identical live cast can only match #2 or later.
            return index;
        }

        return -1;
    }

    private static double GetTimelineTimeSec(BossTimelineEntry entry)
        => entry.CastStartSec;

    private void PollEnemyCasts()
    {
        foreach (var state in actorStates.Values)
            state.SeenThisFrame = false;

        foreach (var obj in objectTable)
        {
            if (!TryGetEnemyBattleChara(obj, out var battleChara))
                continue;

            var actorId = battleChara.EntityId;
            if (!actorStates.TryGetValue(actorId, out var state))
            {
                state = new ActorState();
                actorStates[actorId] = state;
            }

            state.SeenThisFrame = true;

            var currentCastId = battleChara.IsCasting ? battleChara.CastActionId : 0u;
            if (state.LastCastId == currentCastId)
                continue;

            if (state.LastCastId != 0)
            {
                var previousActionId = unchecked((int)state.LastCastId);
                var (abilityName, iconId) = ResolveActionInfo(previousActionId);
                EmitEvent(new PendingHookEvent
                {
                    Kind = EventKind.CastFinish,
                    TimeUtc = DateTime.UtcNow,
                    SourceId = actorId,
                    SourceName = ResolveObjectName(actorId, battleChara.Name.TextValue),
                    TargetId = 0,
                    TargetName = string.Empty,
                    AbilityId = previousActionId,
                    AbilityName = abilityName,
                    IconId = iconId,
                    CastDurationSec = 0.0,
                });
            }

            if (currentCastId != 0)
            {
                var castKey = (actorId, unchecked((int)currentCastId));
                if (!activeCastKeys.Contains(castKey))
                {
                    var actionId = unchecked((int)currentCastId);
                    var (abilityName, iconId) = ResolveActionInfo(actionId);
                    var targetId = NormalizeObjectId(battleChara.CastTargetObjectId);
                    EmitEvent(new PendingHookEvent
                    {
                        Kind = EventKind.CastStart,
                        TimeUtc = DateTime.UtcNow,
                        SourceId = actorId,
                        SourceName = ResolveObjectName(actorId, battleChara.Name.TextValue),
                        TargetId = targetId,
                        TargetName = ResolveObjectName(targetId),
                        AbilityId = actionId,
                        AbilityName = abilityName,
                        IconId = iconId,
                        CastDurationSec = Math.Max(0.0, battleChara.TotalCastTime),
                    });
                }
            }

            state.LastCastId = currentCastId;
        }

        var staleActorIds = actorStates
            .Where(static pair => !pair.Value.SeenThisFrame)
            .Select(static pair => pair.Key)
            .ToList();
        foreach (var actorId in staleActorIds)
            actorStates.Remove(actorId);
    }

    private unsafe void ActorCastDetour(uint casterId, EnemyActorCast* data)
    {
        actorCastHook!.Original(casterId, data);

        try
        {
            if (!ShouldCapture() || data == null || data->ActionKind != 1)
                return;

            if (!TryGetEnemyBattleChara(casterId, out var battleChara))
                return;

            var actionId = data->ActionId;
            var (abilityName, iconId) = ResolveActionInfo(actionId);
            var targetId = data->TargetId;

            QueueHookEvent(new PendingHookEvent
            {
                Kind = EventKind.CastStart,
                TimeUtc = DateTime.UtcNow,
                SourceId = casterId,
                SourceName = ResolveObjectName(casterId, battleChara.Name.TextValue),
                TargetId = targetId,
                TargetName = ResolveObjectName(targetId),
                AbilityId = actionId,
                AbilityName = abilityName,
                IconId = iconId,
                CastDurationSec = Math.Max(0.0, data->CastTime + 0.3f),
            });
        }
        catch (Exception ex)
        {
            log.Debug("EnemyCastTracker: ActorCast detour failed: {0}", ex.Message);
        }
    }

    private bool TryGetEnemyBattleChara(uint entityId, out IBattleChara battleChara)
    {
        battleChara = null!;

        foreach (var obj in objectTable)
        {
            if (obj is not IBattleChara candidate)
                continue;

            if (candidate.EntityId != entityId || !IsEnemyBattleChara(candidate))
                continue;

            battleChara = candidate;
            return true;
        }

        return false;
    }

    private static bool TryGetEnemyBattleChara(IGameObject? obj, out IBattleChara battleChara)
    {
        if (obj is IBattleChara candidate && IsEnemyBattleChara(candidate))
        {
            battleChara = candidate;
            return true;
        }

        battleChara = null!;
        return false;
    }

    private static bool IsEnemyBattleChara(IBattleChara battleChara)
    {
        if (battleChara.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)
            return false;

        if (battleChara is IBattleNpc battleNpc && IsFriendlyNpc((byte)battleNpc.BattleNpcKind))
            return false;

        return true;
    }

    // Mirrors YapYapDraw's classification:
    // BattleNpcKind Pet=2, Chocobo=3, NpcPartyMember=9 are allied summons/companions.
    private static bool IsFriendlyNpc(byte kind)
        => kind is 2 or 3 or 9;

    private (string Name, uint IconId) ResolveActionInfo(int actionId)
    {
        if (actionInfoCache.TryGetValue(actionId, out var cached))
            return cached;

        var actionName = $"#{actionId.ToString(CultureInfo.InvariantCulture)}";
        uint iconId = 0;

        try
        {
            var sheet = plugin.DataManager.GetExcelSheet<LuminaAction>();
            var row = sheet?.GetRowOrDefault((uint)Math.Max(0, actionId));
            if (row.HasValue)
            {
                var extractedName = row.Value.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(extractedName))
                    actionName = extractedName;
                iconId = row.Value.Icon;
            }
        }
        catch
        {
        }

        cached = (actionName, iconId);
        actionInfoCache[actionId] = cached;
        return cached;
    }

    private uint ResolveActionEffectTargetId(Header* header, GameObjectId* targetEntityIds)
    {
        if (header == null)
            return 0;

        try
        {
            if (targetEntityIds != null && header->NumTargets > 0)
                return NormalizeObjectId(targetEntityIds[0]);
        }
        catch
        {
        }

        try
        {
            return NormalizeObjectId(header->AnimationTargetId);
        }
        catch
        {
            return 0;
        }
    }

    private string ResolveObjectName(uint entityId, string fallback = "")
    {
        if (entityId == 0 || entityId == 0xE0000000)
            return fallback;

        foreach (var obj in objectTable)
        {
            if (obj.EntityId != entityId)
                continue;

            return obj.Name.TextValue;
        }

        return fallback;
    }

    private static string NormalizeAbilityName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static uint NormalizeObjectId(ulong objectId)
        => objectId > uint.MaxValue ? 0u : (uint)objectId;

    private static uint NormalizeObjectId(GameObjectId objectId)
        => NormalizeObjectId((ulong)objectId);

    public static string FormatRelativeTime(double valueSec)
    {
        var clamped = Math.Max(0.0, valueSec);
        var totalMinutes = (int)(clamped / 60.0);
        var seconds = clamped - totalMinutes * 60.0;
        return $"{totalMinutes:00}:{seconds:00.000}";
    }

    public static string GetEventLabel(EventKind kind)
        => kind switch
        {
            EventKind.CastStart => "startcast",
            EventKind.Ability => "use",
            EventKind.CastFinish => "endcast",
            _ => kind.ToString(),
        };

    public static string BuildDetailText(TrackedEvent trackedEvent)
    {
        var detail = trackedEvent.AbilityName;
        if (trackedEvent.Kind == EventKind.CastStart && trackedEvent.CastDurationSec > 0.05)
            detail += $" ({trackedEvent.CastDurationSec:F1}s)";

        if (!string.IsNullOrWhiteSpace(trackedEvent.TargetName))
            detail += $" -> {trackedEvent.TargetName}";

        if (trackedEvent.MatchedBossEntry)
            detail += $"  @ {FormatRelativeTime(trackedEvent.MatchedTimelineTimeSec)}";

        return detail;
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        actorCastHook?.Disable();
        actorCastHook?.Dispose();
    }
}
