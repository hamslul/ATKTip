using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
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
        public uint   AbilityId     { get; init; }
        public string Name          { get; init; } = string.Empty;
        public double RecastSec     { get; init; }
        public byte   CooldownGroup { get; init; }
        /// <summary>Maximum charges (normalised: 0 in game data treated as 1).</summary>
        public int    MaxCharges    { get; init; }
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

                var info = new RecastInfo
                {
                    AbilityId     = action.RowId,
                    Name          = name,
                    RecastSec     = action.Recast100ms / 10.0,
                    CooldownGroup = action.CooldownGroup,
                    MaxCharges    = Math.Max(1, (int)action.MaxCharges),
                };

                byId[action.RowId] = info;
                byName.TryAdd(name, info);
            }

            log.Info("[RecastDB] Loaded {0} actions.", byId.Count);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[RecastDB] Failed to load action data from Lumina.");
        }
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
