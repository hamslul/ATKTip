using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaActionTransient = Lumina.Excel.Sheets.ActionTransient;

namespace ATKTip.Data;

/// <summary>
/// Loads FFXIV action recast times and cooldown groups from Lumina game data.
/// Used to detect when custom timeline entries use an ability before its recast is ready.
/// </summary>
public sealed class RecastDatabase
{
    private sealed class ActionOverride
    {
        public double? RecastSec { get; init; }
        public int? MaxCharges { get; init; }
        public byte? ActionCategory { get; init; }
    }

    private static readonly IReadOnlyDictionary<string, ActionOverride> KnownOverrides =
        new Dictionary<string, ActionOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["Life Surge"] = new() { MaxCharges = 2 },
            ["Macrocosmos"] = new() { ActionCategory = 4 },
            ["Bloodspiller"] = new() { ActionCategory = 3 },
            ["Forbidden Meditation"] = new() { ActionCategory = 3 },
            ["Enlightened Meditation"] = new() { ActionCategory = 3 },
            ["Shield Samba"] = new() { RecastSec = 90.0 },
        };

    public sealed class RecastInfo
    {
        public uint   AbilityId      { get; init; }
        public string Name           { get; init; } = string.Empty;
        public string Description    { get; init; } = string.Empty;
        public string SelfStatusName { get; init; } = string.Empty;
        public int SelfStatusMaxStacks { get; init; } = 1;
        public string ProcStatusName { get; init; } = string.Empty;
        public int ProcStatusMaxStacks { get; init; } = 1;
        public double RecastSec      { get; init; }
        public double CastTimeSec    { get; init; }
        public byte   CooldownGroup  { get; init; }
        /// <summary>Maximum charges (normalised: 0 in game data treated as 1).</summary>
        public int    MaxCharges     { get; init; }
        /// <summary>True when this action belongs to a real player job/class.</summary>
        public bool   IsPlayerAction { get; init; }
        /// <summary>
        /// ActionCategory RowId: 2 = Spell (GCD), 3 = Weaponskill (GCD), 4 = Ability (oGCD).
        /// Used to distinguish GCDs from oGCDs for timeline aggregation.
        /// </summary>
        public byte   ActionCategory { get; init; }
        /// <summary>RowId of the action that must precede this one in a combo (0 = no requirement).</summary>
        public uint   ComboActionId  { get; init; }

        /// <summary>True when this action is a GCD (Spell or Weaponskill category).</summary>
        public bool IsGcdAction => ActionCategory is 2 or 3;
        /// <summary>True when this action uses the standard 2.5s base recast the timeline treats as a GCD anchor.</summary>
        public bool IsStandardTimelineGcd => Math.Abs(RecastSec - 2.5) < 0.05;
    }

    private readonly Dictionary<uint, RecastInfo>   byId   = [];
    private readonly Dictionary<string, RecastInfo> byName = new(StringComparer.OrdinalIgnoreCase);

    public RecastDatabase(IDataManager dataManager, IPluginLog log)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<LuminaAction>();
            var transientSheet = dataManager.GetExcelSheet<LuminaActionTransient>();
            if (sheet == null)
            {
                log.Warning("[RecastDB] Action sheet unavailable.");
                return;
            }

            foreach (var action in sheet)
            {
                if (action.RowId == 0) continue;
                if (action.IsPvP) continue;
                if (action.Recast100ms == 0) continue;

                var name = action.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name)) continue;

                // ClassJobCategory.RowId != 0 means the action is restricted to one or
                // more player jobs/classes — i.e. it is a real player-facing ability.
                // NPC/enemy re-uses of the same name will have RowId == 0 here.
                var isPlayerAction = action.ClassJobCategory.RowId != 0;
                var description = transientSheet?.GetRow(action.RowId).Description.ExtractText() ?? string.Empty;
                var selfStatus = action.StatusGainSelf.ValueNullable;
                var procStatus = action.ActionProcStatus.ValueNullable?.Status.ValueNullable;

                var info = new RecastInfo
                {
                    AbilityId      = action.RowId,
                    Name           = name,
                    Description    = description,
                    SelfStatusName = selfStatus?.Name.ExtractText() ?? string.Empty,
                    SelfStatusMaxStacks = Math.Max(1, (int?)(selfStatus?.MaxStacks) ?? 1),
                    ProcStatusName = procStatus?.Name.ExtractText() ?? string.Empty,
                    ProcStatusMaxStacks = Math.Max(1, (int?)(procStatus?.MaxStacks) ?? 1),
                    RecastSec      = action.Recast100ms / 10.0,
                    CastTimeSec    = action.Cast100ms / 10.0,
                    CooldownGroup  = action.CooldownGroup,
                    MaxCharges     = Math.Max(1, (int)action.MaxCharges),
                    IsPlayerAction = isPlayerAction,
                    ActionCategory = (byte)action.ActionCategory.RowId,
                    ComboActionId  = action.ActionCombo.RowId,
                };

                info = ApplyKnownOverride(info);

                byId[action.RowId] = info;

                // byName priority (highest wins):
                //   1. IsPlayerAction — real job ability beats NPC variant
                //   2. MaxCharges    — keep the version with the most charges
                //   3. AbilityId     — higher RowId = more current patch version
                if (!byName.TryGetValue(name, out var existing) || IsBetter(info, existing))
                    byName[name] = info;
            }

            log.Info("[RecastDB] Loaded {0} actions.", byId.Count);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[RecastDB] Failed to load action data from Lumina.");
        }
    }

    /// <summary>Returns true if <paramref name="candidate"/> should replace <paramref name="current"/> in byName.</summary>
    private static bool IsBetter(RecastInfo candidate, RecastInfo current)
    {
        // Rule 1: player action always beats NPC/enemy variant
        if (candidate.IsPlayerAction != current.IsPlayerAction)
            return candidate.IsPlayerAction;

        // Rule 2: more charges beats fewer charges
        if (candidate.MaxCharges != current.MaxCharges)
            return candidate.MaxCharges > current.MaxCharges;

        // Rule 3: higher RowId = more current version of the same ability
        return candidate.AbilityId > current.AbilityId;
    }

    /// <summary>
    /// Overrides Lumina base recast times with live values from <c>ActionManager.GetAdjustedRecastTime</c>,
    /// which applies all trait-based cooldown reductions (e.g. Deployment Tactics 120 s → 90 s at lv 88).
    /// Must be called on the Framework thread while a player is logged in.
    /// </summary>
    public unsafe void RefreshFromLive(IPluginLog log)
    {
        var am = ActionManager.Instance();
        if (am == null) return;

        int updated = 0;

        foreach (var (id, info) in byId)
        {
            // GetAdjustedRecastTime returns the actual recast in seconds including traits.
            // The third parameter (false) skips the haste/spell-speed modifier so we get
            // the base trait-adjusted value, not the current in-combat adjusted value.
            var liveRecast = ActionManager.GetAdjustedRecastTime(ActionType.Action, id, false) / 1000f;
            var liveCast = ActionManager.GetAdjustedCastTime(ActionType.Action, id, false) / 1000f;
            if (liveRecast <= 0f) continue;
            if (Math.Abs(liveRecast - info.RecastSec) < 0.05) continue;  // already correct

            var updated_info = new RecastInfo
            {
                AbilityId      = info.AbilityId,
                Name           = info.Name,
                Description    = info.Description,
                SelfStatusName = info.SelfStatusName,
                SelfStatusMaxStacks = info.SelfStatusMaxStacks,
                ProcStatusName = info.ProcStatusName,
                ProcStatusMaxStacks = info.ProcStatusMaxStacks,
                RecastSec      = liveRecast,
                CastTimeSec    = liveCast > 0f ? liveCast : info.CastTimeSec,
                CooldownGroup  = info.CooldownGroup,
                MaxCharges     = info.MaxCharges,
                IsPlayerAction = info.IsPlayerAction,
                ActionCategory = info.ActionCategory,
                ComboActionId  = info.ComboActionId,
            };

            updated_info = ApplyKnownOverride(updated_info);

            byId[id] = updated_info;
            if (byName.TryGetValue(info.Name, out var nameEntry) && nameEntry.AbilityId == id)
                byName[info.Name] = updated_info;

            updated++;
        }

        if (updated > 0)
            log.Info("[RecastDB] Refreshed {0} recast time(s) from live ActionManager data.", updated);
    }

    /// <summary>
    /// Looks up recast info by ability ID first, then by name as fallback.
    /// Returns null if the ability is not found in game data.
    /// </summary>
    public RecastInfo? Lookup(int id, string name)
    {
        byId.TryGetValue((uint)Math.Max(0, id), out var byIdResult);
        var byNameResult = string.IsNullOrWhiteSpace(name)
            ? null
            : byName.GetValueOrDefault(name);

        if (byIdResult == null && byNameResult == null && IsLikelyPotionName(name))
        {
            return new RecastInfo
            {
                AbilityId = (uint)Math.Max(0, id),
                Name = name,
                RecastSec = 270.0,
                CastTimeSec = 0.0,
                CooldownGroup = 0,
                MaxCharges = 1,
                IsPlayerAction = true,
                ActionCategory = 4,
            };
        }

        if (byIdResult == null)
            return byNameResult;
        if (byNameResult == null)
            return byIdResult;

        // When we have a concrete player-action ID, trust it over the shared-name
        // fallback. This avoids cross-job collisions such as Scholar Energy Drain
        // inheriting Summoner's recast data.
        if (byIdResult.IsPlayerAction)
            return byIdResult;
        if (byNameResult.IsPlayerAction)
            return byNameResult;

        return IsBetter(byNameResult, byIdResult)
            ? byNameResult
            : byIdResult;
    }

    private static bool IsLikelyPotionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (name.Contains("Gemdraught", StringComparison.OrdinalIgnoreCase))
            return true;

        return name.StartsWith("Grade ", StringComparison.OrdinalIgnoreCase) &&
               name.Contains(" of ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a dictionary mapping each ability ID to the ID of the action that must
    /// immediately precede it in a combo chain. Only abilities with a combo prerequisite
    /// (ComboActionId > 0) are included.
    /// </summary>
    public Dictionary<uint, uint> BuildComboChains()
    {
        var result = new Dictionary<uint, uint>();
        foreach (var info in byId.Values)
        {
            if (info.ComboActionId > 0)
                result[info.AbilityId] = info.ComboActionId;
        }
        return result;
    }

    public IEnumerable<RecastInfo> GetAllActions()
        => byId.Values;

    private static RecastInfo ApplyKnownOverride(RecastInfo info)
    {
        if (!KnownOverrides.TryGetValue(info.Name, out var actionOverride))
            return info;

        return new RecastInfo
        {
            AbilityId = info.AbilityId,
            Name = info.Name,
            Description = info.Description,
            SelfStatusName = info.SelfStatusName,
            SelfStatusMaxStacks = info.SelfStatusMaxStacks,
            ProcStatusName = info.ProcStatusName,
            ProcStatusMaxStacks = info.ProcStatusMaxStacks,
            RecastSec = actionOverride.RecastSec ?? info.RecastSec,
            CastTimeSec = info.CastTimeSec,
            CooldownGroup = info.CooldownGroup,
            MaxCharges = actionOverride.MaxCharges ?? info.MaxCharges,
            IsPlayerAction = info.IsPlayerAction,
            ActionCategory = actionOverride.ActionCategory ?? info.ActionCategory,
            ComboActionId = info.ComboActionId,
        };
    }
}
