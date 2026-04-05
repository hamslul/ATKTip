using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;

namespace ATKTip;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // FFLogs API credentials
    public string FFLogsClientId { get; set; } = string.Empty;
    public string FFLogsClientSecret { get; set; } = string.Empty;

    // Overlay settings
    public bool OverlayEnabled { get; set; } = true;
    public bool OverlayLocked { get; set; } = true;
    public float OverlayScale { get; set; } = 1.0f;
    public float OverlayPixelsPerSec { get; set; } = 60.0f;
    public float OverlayIconSize { get; set; } = 32.0f;
    public float OverlayTimeBehind { get; set; } = 4.5f;
    public float OverlayBgOpacity { get; set; } = 0.0f;
    public float OverlayPastAlpha { get; set; } = 1.0f;
    public float OverlayFreqThreshold { get; set; } = 0.40f;
    public bool OverlayShowGrid { get; set; } = true;
    public bool OverlayShowFreqBars { get; set; } = true;
    public bool OverlayShowTimer { get; set; } = true;
    public int OverlayMaxStackedIcons { get; set; } = 3;
    public float OGCDSizeRatio { get; set; } = 0.75f;      // oGCD icon size as fraction of GCD icon size
    public float OGCDVerticalOffset { get; set; } = 0.1f;  // fraction of GCD icon size above center
    public float OGCDHorizontalOffset { get; set; } = 45f;  // pixel shift applied to all oGCD positions

    // Boss bar color (shared by overlay and main window)
    public bool    BossBarUseCustomColor { get; set; } = false;
    public Vector4 BossBarColor          { get; set; } = new(0.85f, 0.35f, 0.20f, 1.00f);

    // Main window timeline icon settings
    public float MainIconSize    { get; set; } = 22.0f;
    public float MainIconOpacity { get; set; } = 1.0f;
    public float MainIconScale   { get; set; } = 1.0f;

    // Ability Ants — marching-ants border effect as abilities cross the now-line
    public bool    AntsEnabled         { get; set; } = true;
    public float   AntsDurationBefore  { get; set; } = 1.5f;  // seconds before crossing
    public float   AntsDurationAfter   { get; set; } = 1.5f;  // seconds after crossing

    // Custom ants — replace native FFXIV ants with a fully customizable ImGui-drawn version
    public bool    AntsCustomEnabled   { get; set; } = false;
    public Vector4 AntsColor           { get; set; } = new(1.0f, 0.85f, 0.0f, 1.0f);   // gold dash
    public Vector4 AntsGapColor        { get; set; } = new(0.0f, 0.0f, 0.0f, 0.5f);    // dark gap
    public float   AntsDashLength      { get; set; } = 6.0f;
    public float   AntsGapLength       { get; set; } = 4.0f;
    public float   AntsSpeed           { get; set; } = 40.0f;
    public float   AntsThickness       { get; set; } = 2.0f;
    public float   AntsBorderPadding   { get; set; } = 0.0f;   // px to expand (+) or inset (-) the border

    // Per-timeline skill visibility: key = "encounterId_specName", value = set of hidden ability IDs
    public Dictionary<string, HashSet<int>> HiddenAbilities { get; set; } = [];

    // Per-ability frequency thresholds: key = "encounterId_specName", value = { abilityId -> threshold (0..1) }
    // Overrides OverlayFreqThreshold for specific abilities. Falls back to global if not set.
    public Dictionary<string, Dictionary<int, float>> AbilityFreqThresholds { get; set; } = [];

    // Custom timelines the user has saved (serialized copies they can edit)
    public Dictionary<string, Data.AggregatedTimeline> CustomTimelines { get; set; } = [];
}
