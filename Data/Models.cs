using System;
using System.Collections.Generic;

namespace ATKTip.Data;

// ── FFLogs API response models ──

public sealed class FFLogsTokenResponse
{
    [Newtonsoft.Json.JsonProperty("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [Newtonsoft.Json.JsonProperty("token_type")]
    public string TokenType { get; set; } = string.Empty;

    [Newtonsoft.Json.JsonProperty("expires_in")]
    public int ExpiresIn { get; set; }
}

public sealed class GraphQLResponse<T>
{
    public T? Data { get; set; }
    public List<GraphQLError>? Errors { get; set; }
}

public sealed class GraphQLError
{
    public string Message { get; set; } = string.Empty;
}

// ── Game data models ──

public sealed class Zone
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<Encounter> Encounters { get; set; } = [];
}

public sealed class Encounter
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class GameClass
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<GameSpec> Specs { get; set; } = [];
}

public sealed class GameSpec
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

// ── Ranking / parse models ──

public sealed class RankingEntry
{
    public string ReportCode { get; set; } = string.Empty;
    public int FightId { get; set; }
    public double Duration { get; set; }
    public double Amount { get; set; }
}

// ── Raw event from a single parse ──

public sealed class CastEvent
{
    public long Timestamp { get; set; }
    public int AbilityGameID { get; set; }
    public string AbilityName { get; set; } = string.Empty;
    public string AbilityIcon { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

/// <summary>Raw cast event from an enemy/boss in a single parse.</summary>
public sealed class RawBossCastEvent
{
    public long Timestamp { get; set; }       // ms from fight start
    public int AbilityGameId { get; set; }
    public string AbilityName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;   // "begincast" or "cast"
    public int SourceId { get; set; }
}

/// <summary>One aggregated boss ability entry — a cast bar on the timeline.</summary>
public sealed class BossTimelineEntry
{
    public double CastStartSec { get; set; }   // fight-relative
    public double CastEndSec { get; set; }     // == CastStartSec when instant
    public int AbilityId { get; set; }
    public string AbilityName { get; set; } = string.Empty;
}

// ── Aggregated timeline models (what we persist) ──

public sealed class TimelineDatabase
{
    public DateTime GeneratedAt { get; set; }
    public string Version { get; set; } = "1";
    public Dictionary<string, AggregatedTimeline> Timelines { get; set; } = [];

    public static string MakeKey(int encounterId, string specName)
        => $"{encounterId}_{specName}";
}

public sealed class AggregatedTimeline
{
    public int EncounterId { get; set; }
    public string EncounterName { get; set; } = string.Empty;
    public string SpecName { get; set; } = string.Empty;
    public double AverageDurationMs { get; set; }
    public int ParseCount { get; set; }
    public List<TimelineEntry> Entries { get; set; } = [];
    public List<BossTimelineEntry> BossEntries { get; set; } = [];
    /// <summary>Original source entries used by Auto Timeline so repeated runs are stable.</summary>
    public List<TimelineEntry> AutoTimelineSourceEntries { get; set; } = [];
    /// <summary>Per-parse cached FFLogs timelines used for debug inspection of the top source parses.</summary>
    public List<CachedFflogsParseTimeline> CachedFflogsParses { get; set; } = [];
    /// <summary>Optional FFLogs phase metadata for fights that can be split into phase-scoped custom or auto timelines.</summary>
    public FightPhaseInfo? PhaseInfo { get; set; }
}

public sealed class CachedFflogsParseTimeline
{
    public int ParseIndex { get; set; }
    public string ReportCode { get; set; } = string.Empty;
    public int FightId { get; set; }
    public double RankingAmount { get; set; }
    public double DurationSec { get; set; }
    public List<TimelineEntry> Entries { get; set; } = [];
    public FightPhaseInfo? PhaseInfo { get; set; }
    public bool HasVisiblePhaseEntries { get; set; }
}

public sealed class TimelineEntry
{
    public double TimeOffsetSec { get; set; }
    public int AbilityId { get; set; }
    public string AbilityName { get; set; } = string.Empty;
    public string AbilityIcon { get; set; } = string.Empty;

    /// <summary>Fraction of parses (0..1) that used this skill in this window.</summary>
    public double Frequency { get; set; }

    /// <summary>Average number of times this skill was used in this window across parses that used it.</summary>
    public double AverageUses { get; set; }

    /// <summary>
    /// True when this ability is a real game GCD action (Spell or Weaponskill).
    /// Populated at load time from <see cref="RecastDatabase"/>; not persisted to JSON.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsGcd { get; set; }
}

// ── Job role helpers ──

public static class JobRoles
{
    private static readonly HashSet<string> HealerSpecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "White Mage",
        "Scholar",
        "Astrologian",
        "Sage",
    };

    private static readonly HashSet<string> TankSpecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Paladin",
        "Warrior",
        "Dark Knight",
        "Gunbreaker",
    };

    /// <summary>Returns true if the given spec name is a healer job.</summary>
    public static bool IsHealer(string specName) => HealerSpecs.Contains(specName);

    /// <summary>Returns true if the given spec name is a tank job.</summary>
    public static bool IsTank(string specName) => TankSpecs.Contains(specName);

    /// <summary>
    /// Returns the appropriate FFLogs metric for the given spec.
    /// Valid CharacterRankingMetricType values: default, dps, hps, rdps, bossdps, bossrdps, etc.
    /// Using "default" lets the API choose the most appropriate metric.
    /// </summary>
    public static string GetMetric(string specName) => "default";
}

// ── Report importer models ──

public sealed class ReportFight
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int EncounterId { get; set; }
    public long StartTime { get; set; }   // ms from report start
    public long EndTime { get; set; }     // ms from report start
    public int LastPhase { get; set; }
    public int LastPhaseAsAbsoluteIndex { get; set; }
    public bool LastPhaseIsIntermission { get; set; }
    public List<FightPhaseTransition> PhaseTransitions { get; set; } = [];
    public List<EncounterPhaseMetadata> PhaseMetadata { get; set; } = [];
    public long DurationMs => EndTime - StartTime;
}

public sealed class ReportPlayer
{
    public int Id { get; set; }           // sourceID used in events queries
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;     // e.g. "Player"
    public string SubType { get; set; } = string.Empty;  // job CamelCase, e.g. "BlackMage"
}

public sealed class EncounterPhaseMetadata
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsIntermission { get; set; }
}

public sealed class EncounterPhasesInfo
{
    public int EncounterId { get; set; }
    public bool SeparatesWipes { get; set; }
    public List<EncounterPhaseMetadata> Phases { get; set; } = [];
}

public sealed class FightPhaseTransition
{
    public int Id { get; set; }
    public long StartTime { get; set; }
}

public sealed class FightPhaseInfo
{
    public int EncounterId { get; set; }
    public string EncounterName { get; set; } = string.Empty;
    public long FightStartTime { get; set; }
    public long FightEndTime { get; set; }
    public int LastPhase { get; set; }
    public int LastPhaseAsAbsoluteIndex { get; set; }
    public bool LastPhaseIsIntermission { get; set; }
    public List<FightPhaseTransition> PhaseTransitions { get; set; } = [];
    public List<EncounterPhaseMetadata> PhaseMetadata { get; set; } = [];
}

public sealed class CastEventsResult
{
    public List<CastEvent> Casts { get; set; } = [];
    public FightPhaseInfo? PhaseInfo { get; set; }
}

// ── UI state helpers ──

public sealed class FightSelection
{
    public int ZoneIndex { get; set; }
    public int EncounterIndex { get; set; }
    public int SpecIndex { get; set; }
}

public sealed class SkillFilter
{
    public int AbilityId { get; set; }
    public string AbilityName { get; set; } = string.Empty;
    public bool Visible { get; set; } = true;
}
