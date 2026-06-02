using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace ATKTip.Data;

public sealed class UiSettingsStore
{
    private readonly string path;
    private readonly IPluginLog log;

    public UiSettingsStore(string path, IPluginLog log)
    {
        this.path = path;
        this.log = log;
    }

    public void LoadInto(Configuration cfg)
    {
        if (!File.Exists(path))
            return;

        try
        {
            var json = File.ReadAllText(path);
            var data = JsonConvert.DeserializeObject<UiSettingsData>(json);
            if (data == null)
                return;

            Apply(cfg, data);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load UI settings.");
        }
    }

    public void SaveFrom(Configuration cfg)
    {
        try
        {
            var json = JsonConvert.SerializeObject(Capture(cfg), Formatting.Indented);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to save UI settings.");
        }
    }

    private static UiSettingsData Capture(Configuration cfg) => new()
    {
        OverlayEnabled = cfg.OverlayEnabled,
        OverlayLocked = cfg.OverlayLocked,
        OverlayPixelsPerSec = cfg.OverlayPixelsPerSec,
        OverlayIconSize = cfg.OverlayIconSize,
        OverlayTimeBehind = cfg.OverlayTimeBehind,
        OverlayBgOpacity = cfg.OverlayBgOpacity,
        OverlayPastAlpha = cfg.OverlayPastAlpha,
        OverlayFreqThreshold = cfg.OverlayFreqThreshold,
        OverlayShowGrid = cfg.OverlayShowGrid,
        OverlayMaxStackedIcons = cfg.OverlayMaxStackedIcons,
        OGCDSizeRatio = cfg.OGCDSizeRatio,
        OGCDVerticalOffset = cfg.OGCDVerticalOffset,
        OGCDHorizontalOffset = cfg.OGCDHorizontalOffset,
        BossBarUseCustomColor = cfg.BossBarUseCustomColor,
        BossBarColor = cfg.BossBarColor,
        MainIconSize = cfg.MainIconSize,
        MainIconOpacity = cfg.MainIconOpacity,
        MainIconScale = cfg.MainIconScale,
        AutoTimelineGcdRecastSec = cfg.AutoTimelineGcdRecastSec,
        AutoTimelineDotRefreshBufferSec = cfg.AutoTimelineDotRefreshBufferSec,
        AntsEnabled = cfg.AntsEnabled,
        AntsCustomEnabled = cfg.AntsCustomEnabled,
        OgcdAntsEnabled = cfg.OgcdAntsEnabled,
        AntsDurationBefore = cfg.AntsDurationBefore,
        AntsDurationAfter = cfg.AntsDurationAfter,
        AntsColor = cfg.AntsColor,
        AntsGapColor = cfg.AntsGapColor,
        AntsDashLength = cfg.AntsDashLength,
        AntsGapLength = cfg.AntsGapLength,
        AntsSpeed = cfg.AntsSpeed,
        AntsThickness = cfg.AntsThickness,
        AntsBorderPadding = cfg.AntsBorderPadding,
        AntsXOffset = cfg.AntsXOffset,
        AntsYOffset = cfg.AntsYOffset,
        GcdAntsEnabled = cfg.GcdAntsEnabled,
        GcdAntsDurationBefore = cfg.GcdAntsDurationBefore,
        GcdAntsDurationAfter = cfg.GcdAntsDurationAfter,
        GcdAntsColor = cfg.GcdAntsColor,
        GcdAntsGapColor = cfg.GcdAntsGapColor,
        GcdAntsDashLength = cfg.GcdAntsDashLength,
        GcdAntsGapLength = cfg.GcdAntsGapLength,
        GcdAntsSpeed = cfg.GcdAntsSpeed,
        GcdAntsThickness = cfg.GcdAntsThickness,
        GcdAntsBorderPadding = cfg.GcdAntsBorderPadding,
        GcdAntsXOffset = cfg.GcdAntsXOffset,
        GcdAntsYOffset = cfg.GcdAntsYOffset,
        AutoTimelineDisabledAbilities = cfg.AutoTimelineDisabledAbilities.ToDictionary(
            static pair => pair.Key,
            static pair => new HashSet<int>(pair.Value)),
    };

    private static void Apply(Configuration cfg, UiSettingsData data)
    {
        cfg.OverlayEnabled = data.OverlayEnabled;
        cfg.OverlayLocked = data.OverlayLocked;
        cfg.OverlayPixelsPerSec = data.OverlayPixelsPerSec;
        cfg.OverlayIconSize = data.OverlayIconSize;
        cfg.OverlayTimeBehind = data.OverlayTimeBehind;
        cfg.OverlayBgOpacity = data.OverlayBgOpacity;
        cfg.OverlayPastAlpha = data.OverlayPastAlpha;
        cfg.OverlayFreqThreshold = data.OverlayFreqThreshold;
        cfg.OverlayShowGrid = data.OverlayShowGrid;
        cfg.OverlayMaxStackedIcons = data.OverlayMaxStackedIcons;
        cfg.OGCDSizeRatio = data.OGCDSizeRatio;
        cfg.OGCDVerticalOffset = data.OGCDVerticalOffset;
        cfg.OGCDHorizontalOffset = data.OGCDHorizontalOffset;
        cfg.BossBarUseCustomColor = data.BossBarUseCustomColor;
        cfg.BossBarColor = data.BossBarColor;
        cfg.MainIconSize = data.MainIconSize;
        cfg.MainIconOpacity = data.MainIconOpacity;
        cfg.MainIconScale = data.MainIconScale;
        cfg.AutoTimelineGcdRecastSec = Math.Clamp(data.AutoTimelineGcdRecastSec, 2.0f, 2.5f);
        cfg.AutoTimelineDotRefreshBufferSec = Math.Clamp(data.AutoTimelineDotRefreshBufferSec, 0.0f, 15.0f);
        cfg.AntsEnabled = data.AntsEnabled;
        cfg.AntsCustomEnabled = true;
        cfg.OgcdAntsEnabled = data.OgcdAntsEnabled;
        cfg.AntsDurationBefore = data.AntsDurationBefore;
        cfg.AntsDurationAfter = data.AntsDurationAfter;
        cfg.AntsColor = data.AntsColor;
        cfg.AntsGapColor = data.AntsGapColor;
        cfg.AntsDashLength = data.AntsDashLength;
        cfg.AntsGapLength = data.AntsGapLength;
        cfg.AntsSpeed = data.AntsSpeed;
        cfg.AntsThickness = data.AntsThickness;
        cfg.AntsBorderPadding = data.AntsBorderPadding;
        cfg.AntsXOffset = data.AntsXOffset;
        cfg.AntsYOffset = data.AntsYOffset;
        cfg.GcdAntsEnabled = data.GcdAntsEnabled;
        cfg.GcdAntsDurationBefore = data.GcdAntsDurationBefore;
        cfg.GcdAntsDurationAfter = data.GcdAntsDurationAfter;
        cfg.GcdAntsColor = data.GcdAntsColor;
        cfg.GcdAntsGapColor = data.GcdAntsGapColor;
        cfg.GcdAntsDashLength = data.GcdAntsDashLength;
        cfg.GcdAntsGapLength = data.GcdAntsGapLength;
        cfg.GcdAntsSpeed = data.GcdAntsSpeed;
        cfg.GcdAntsThickness = data.GcdAntsThickness;
        cfg.GcdAntsBorderPadding = data.GcdAntsBorderPadding;
        cfg.GcdAntsXOffset = data.GcdAntsXOffset;
        cfg.GcdAntsYOffset = data.GcdAntsYOffset;
        cfg.AutoTimelineDisabledAbilities = data.AutoTimelineDisabledAbilities?.ToDictionary(
            static pair => pair.Key,
            static pair => new HashSet<int>(pair.Value)) ?? [];
    }

    private sealed class UiSettingsData
    {
        public bool OverlayEnabled { get; set; }
        public bool OverlayLocked { get; set; }
        public float OverlayPixelsPerSec { get; set; }
        public float OverlayIconSize { get; set; }
        public float OverlayTimeBehind { get; set; }
        public float OverlayBgOpacity { get; set; }
        public float OverlayPastAlpha { get; set; }
        public float OverlayFreqThreshold { get; set; }
        public bool OverlayShowGrid { get; set; }
        public int OverlayMaxStackedIcons { get; set; }
        public float OGCDSizeRatio { get; set; }
        public float OGCDVerticalOffset { get; set; }
        public float OGCDHorizontalOffset { get; set; }
        public bool BossBarUseCustomColor { get; set; }
        public System.Numerics.Vector4 BossBarColor { get; set; }
        public float MainIconSize { get; set; }
        public float MainIconOpacity { get; set; }
        public float MainIconScale { get; set; }
        public float AutoTimelineGcdRecastSec { get; set; } = 2.5f;
        public float AutoTimelineDotRefreshBufferSec { get; set; } = 6.0f;
        public bool AntsEnabled { get; set; }
        public bool AntsCustomEnabled { get; set; }
        public bool OgcdAntsEnabled { get; set; }
        public float AntsDurationBefore { get; set; }
        public float AntsDurationAfter { get; set; }
        public System.Numerics.Vector4 AntsColor { get; set; }
        public System.Numerics.Vector4 AntsGapColor { get; set; }
        public float AntsDashLength { get; set; }
        public float AntsGapLength { get; set; }
        public float AntsSpeed { get; set; }
        public float AntsThickness { get; set; }
        public float AntsBorderPadding { get; set; }
        public float AntsXOffset { get; set; }
        public float AntsYOffset { get; set; }
        public bool GcdAntsEnabled { get; set; }
        public float GcdAntsDurationBefore { get; set; }
        public float GcdAntsDurationAfter { get; set; }
        public System.Numerics.Vector4 GcdAntsColor { get; set; }
        public System.Numerics.Vector4 GcdAntsGapColor { get; set; }
        public float GcdAntsDashLength { get; set; }
        public float GcdAntsGapLength { get; set; }
        public float GcdAntsSpeed { get; set; }
        public float GcdAntsThickness { get; set; }
        public float GcdAntsBorderPadding { get; set; }
        public float GcdAntsXOffset { get; set; }
        public float GcdAntsYOffset { get; set; }
        public Dictionary<string, HashSet<int>> AutoTimelineDisabledAbilities { get; set; } = [];
    }
}
