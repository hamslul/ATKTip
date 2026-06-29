using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace ATKTip.Data;

public sealed class CustomTimelineStore
{
    private readonly string rootDir;
    private readonly string manifestPath;
    private readonly IPluginLog log;

    private CustomTimelineManifest manifest = new();

    public CustomTimelineStore(string dataDir, IPluginLog log)
    {
        rootDir = Path.Combine(dataDir, "custom-timelines");
        manifestPath = Path.Combine(rootDir, "manifest.json");
        this.log = log;
    }

    public void LoadInto(Configuration cfg)
    {
        Directory.CreateDirectory(rootDir);
        var manifestExists = File.Exists(manifestPath);

        if (manifestExists)
        {
            try
            {
                var json = File.ReadAllText(manifestPath);
                manifest = JsonConvert.DeserializeObject<CustomTimelineManifest>(json) ?? new CustomTimelineManifest();
            }
            catch (Exception ex)
            {
                log.Error(ex, "Failed to load custom timeline manifest.");
                manifest = new CustomTimelineManifest();
            }
        }

        var loaded = new Dictionary<string, AggregatedTimeline>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            var path = Path.Combine(rootDir, entry.FileName);
            if (!File.Exists(path))
                continue;

            try
            {
                var json = File.ReadAllText(path);
                var timeline = JsonConvert.DeserializeObject<AggregatedTimeline>(json);
                if (timeline != null)
                    loaded[entry.Key] = timeline;
            }
            catch (Exception ex)
            {
                log.Error(ex, "Failed to load custom timeline {0}.", entry.Key);
            }
        }

        if (!manifestExists && loaded.Count == 0 && cfg.CustomTimelines.Count > 0)
        {
            manifest = new CustomTimelineManifest();
            foreach (var (key, timeline) in cfg.CustomTimelines)
            {
                loaded[key] = timeline;
                var fileName = GetOrCreateFileName(key);
                WriteTimelineFile(fileName, timeline);
            }
            SaveManifest();
        }

        cfg.CustomTimelines = loaded;
    }

    public void SaveTimeline(Configuration cfg, string key, AggregatedTimeline timeline)
    {
        Directory.CreateDirectory(rootDir);
        cfg.CustomTimelines[key] = timeline;
        WriteTimelineFile(GetOrCreateFileName(key), timeline);
        SaveManifest();
    }

    public bool RemoveTimeline(Configuration cfg, string key)
    {
        var removed = cfg.CustomTimelines.Remove(key);
        var fileName = manifest.Entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.Ordinal))?.FileName;
        manifest.Entries.RemoveAll(e => string.Equals(e.Key, key, StringComparison.Ordinal));

        if (fileName != null)
        {
            try
            {
                var path = Path.Combine(rootDir, fileName);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                log.Error(ex, "Failed to delete custom timeline file for {0}.", key);
            }
        }

        if (removed || fileName != null)
            SaveManifest();

        return removed;
    }

    public void SaveOrder(IEnumerable<string> orderedKeys)
    {
        var fileByKey = manifest.Entries.ToDictionary(e => e.Key, e => e.FileName, StringComparer.Ordinal);
        manifest.Entries = orderedKeys
            .Select(key => new CustomTimelineManifestEntry
            {
                Key = key,
                FileName = fileByKey.TryGetValue(key, out var fileName) ? fileName : CreateFileName(key),
            })
            .ToList();
        SaveManifest();
    }

    private string GetOrCreateFileName(string key)
    {
        var existing = manifest.Entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.Ordinal));
        if (existing != null)
            return existing.FileName;

        var fileName = CreateFileName(key);
        manifest.Entries.Add(new CustomTimelineManifestEntry
        {
            Key = key,
            FileName = fileName,
        });
        return fileName;
    }

    private void WriteTimelineFile(string fileName, AggregatedTimeline timeline)
    {
        try
        {
            var json = JsonConvert.SerializeObject(timeline, Formatting.Indented);
            File.WriteAllText(Path.Combine(rootDir, fileName), json);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to save custom timeline {0}.", timeline.SpecName);
        }
    }

    private void SaveManifest()
    {
        try
        {
            Directory.CreateDirectory(rootDir);
            var json = JsonConvert.SerializeObject(manifest, Formatting.Indented);
            File.WriteAllText(manifestPath, json);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to save custom timeline manifest.");
        }
    }

    private static string CreateFileName(string key)
    {
        var safe = new string(key.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
        if (safe.Length == 0)
            safe = "timeline";
        if (safe.Length > 48)
            safe = safe[..48];

        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return $"{safe}_{hash[..12]}.json";
    }

    private sealed class CustomTimelineManifest
    {
        public List<CustomTimelineManifestEntry> Entries { get; set; } = [];
    }

    private sealed class CustomTimelineManifestEntry
    {
        public string Key { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
    }
}
