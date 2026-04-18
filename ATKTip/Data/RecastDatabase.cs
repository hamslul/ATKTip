using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace ATKTip.Data;

/// <summary>
/// Loads FFXIV action recast times and cooldown groups from Lumina game data.
/// Used to detect when custom timeline entries use an ability before its recast is ready.
/// </summary>
public sealed class RecastDatabase
{
    public sealed class RecastInfo
    {
        public uint   AbilityId      { get; init; }
        public string Name           { get; init; } = string.Empty;
        public double RecastSec      { get; init; }
        public byte   CooldownGroup  { get; init; }
        /// <summary>Maximum charges (normalised: 0 in game data treated as 1).</summary>
        public int    MaxCharges     { get; init; }
        /// <summary>True when this action belongs to a real player job/class.</summary>
        public bool   IsPlayerAction { get; init; }
    }

    private readonly Dictionary<uint, RecastInfo>   byId   = [];
    private readonly Dictionary<string, RecastInfo> byName = new(StringComparer.OrdinalIgnoreCase);

    public RecastDatabase(IDataManager dataManager, IPluginLog log)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<LuminaAction>();
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

                var info = new RecastInfo
                {
                    AbilityId      = action.RowId,
                    Name           = name,
                    RecastSec      = action.Recast100ms / 10.0,
                    CooldownGroup  = action.CooldownGroup,
                    MaxCharges     = Math.Max(1, (int)action.MaxCharges),
                    IsPlayerAction = isPlayerAction,
                };

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
            if (liveRecast <= 0f) continue;
            if (Math.Abs(liveRecast - info.RecastSec) < 0.05) continue;  // already correct

            var updated_info = new RecastInfo
            {
                AbilityId      = info.AbilityId,
                Name           = info.Name,
                RecastSec      = liveRecast,
                CooldownGroup  = info.CooldownGroup,
                MaxCharges     = info.MaxCharges,
                IsPlayerAction = info.IsPlayerAction,
            };

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
        if (id > 0 && byId.TryGetValue((uint)id, out var byIdResult))
            return byIdResult;
        return byName.GetValueOrDefault(name);
    }
}
