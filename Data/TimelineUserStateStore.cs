using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace ATKTip.Data;

public sealed class TimelineUserStateStore
{
    private readonly string path;
    private readonly IPluginLog log;

    public TimelineUserStateStore(string path, IPluginLog log)
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
            var data = JsonConvert.DeserializeObject<TimelineUserStateData>(json);
            if (data == null)
                return;

            cfg.HiddenAbilities = data.HiddenAbilities ?? [];
            cfg.AbilityFreqThresholds = data.AbilityFreqThresholds ?? [];
            cfg.TimelineGroups = data.TimelineGroups ?? [];
            cfg.TimelineGroupAssignments = data.TimelineGroupAssignments ?? [];
            cfg.TimelineNextLinks = data.TimelineNextLinks ?? [];
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load timeline user state.");
        }
    }

    public void SaveFrom(Configuration cfg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonConvert.SerializeObject(new TimelineUserStateData
            {
                HiddenAbilities = cfg.HiddenAbilities,
                AbilityFreqThresholds = cfg.AbilityFreqThresholds,
                TimelineGroups = cfg.TimelineGroups,
                TimelineGroupAssignments = cfg.TimelineGroupAssignments,
                TimelineNextLinks = cfg.TimelineNextLinks,
            }, Formatting.Indented);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to save timeline user state.");
        }
    }

    private sealed class TimelineUserStateData
    {
        public Dictionary<string, HashSet<int>> HiddenAbilities { get; set; } = [];
        public Dictionary<string, Dictionary<int, float>> AbilityFreqThresholds { get; set; } = [];
        public List<string> TimelineGroups { get; set; } = [];
        public Dictionary<string, string> TimelineGroupAssignments { get; set; } = [];
        public Dictionary<string, string> TimelineNextLinks { get; set; } = [];
    }
}
