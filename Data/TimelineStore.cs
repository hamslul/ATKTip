using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace ATKTip.Data;

/// <summary>
/// Persists encounter timelines as individual JSON files plus a lightweight index.
/// </summary>
public sealed class TimelineStore
{
    private readonly string dataDir;
    private readonly string legacyDbPath;
    private readonly string timelineDir;
    private readonly string indexPath;
    private readonly IPluginLog log;

    private TimelineDatabase? cachedIndexDatabase;
    private Dictionary<string, TimelineIndexEntry>? cachedIndexEntries;
    private readonly Dictionary<string, AggregatedTimeline> fullTimelineCache = new(StringComparer.Ordinal);

    public TimelineStore(string dataDir, IPluginLog log)
    {
        this.dataDir = dataDir;
        legacyDbPath = Path.Combine(dataDir, "timelines.json");
        timelineDir = Path.Combine(dataDir, "timelines");
        indexPath = Path.Combine(timelineDir, "index.json");
        this.log = log;
    }

    public TimelineDatabase Load()
    {
        EnsureLoaded();
        return cachedIndexDatabase ?? new TimelineDatabase();
    }

    public void Save(TimelineDatabase db)
    {
        EnsureLoaded();

        foreach (var timeline in db.Timelines.Values)
            SaveTimelineInternal(timeline);

        var keys = new HashSet<string>(db.Timelines.Keys, StringComparer.Ordinal);
        foreach (var staleKey in cachedIndexEntries!.Keys.Where(k => !keys.Contains(k)).ToList())
            RemoveTimelineInternal(staleKey);
    }

    public void SaveTimeline(AggregatedTimeline timeline)
    {
        EnsureLoaded();
        SaveTimelineInternal(timeline);
    }

    public AggregatedTimeline? GetTimeline(int encounterId, string specName)
    {
        EnsureLoaded();

        var key = TimelineDatabase.MakeKey(encounterId, specName);
        if (fullTimelineCache.TryGetValue(key, out var cached))
            return cached;

        if (!cachedIndexEntries!.TryGetValue(key, out var entry))
            return null;

        var path = Path.Combine(timelineDir, entry.FileName);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var timeline = JsonConvert.DeserializeObject<AggregatedTimeline>(json);
            if (timeline == null)
                return null;

            fullTimelineCache[key] = timeline;
            return timeline;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load timeline {0}.", key);
            return null;
        }
    }

    public IEnumerable<AggregatedTimeline> GetAllTimelines()
    {
        EnsureLoaded();
        foreach (var entry in cachedIndexEntries!.Values)
        {
            var timeline = GetTimeline(entry.EncounterId, entry.SpecName);
            if (timeline != null)
                yield return timeline;
        }
    }

    public bool RemoveTimeline(int encounterId, string specName)
    {
        EnsureLoaded();
        return RemoveTimelineInternal(TimelineDatabase.MakeKey(encounterId, specName));
    }

    public void ClearAll()
    {
        EnsureLoaded();

        foreach (var entry in cachedIndexEntries!.Values.ToList())
        {
            var path = Path.Combine(timelineDir, entry.FileName);
            if (File.Exists(path))
                File.Delete(path);
        }

        fullTimelineCache.Clear();
        cachedIndexEntries.Clear();
        cachedIndexDatabase = new TimelineDatabase { GeneratedAt = DateTime.UtcNow };
        SaveIndex();
        log.Info("Cleared all cached timelines.");
    }

    private void EnsureLoaded()
    {
        if (cachedIndexEntries != null)
            return;

        Directory.CreateDirectory(timelineDir);

        if (!File.Exists(indexPath) && File.Exists(legacyDbPath))
            MigrateLegacyDatabase();

        if (!File.Exists(indexPath))
        {
            cachedIndexEntries = new Dictionary<string, TimelineIndexEntry>(StringComparer.Ordinal);
            cachedIndexDatabase = new TimelineDatabase { GeneratedAt = DateTime.MinValue };
            return;
        }

        try
        {
            var json = File.ReadAllText(indexPath);
            var index = JsonConvert.DeserializeObject<TimelineIndexData>(json) ?? new TimelineIndexData();
            cachedIndexEntries = index.Entries.ToDictionary(e => e.Key, e => e, StringComparer.Ordinal);
            cachedIndexDatabase = new TimelineDatabase
            {
                GeneratedAt = index.GeneratedAt,
                Version = index.Version,
                Timelines = index.Entries.ToDictionary(
                    e => e.Key,
                    e => new AggregatedTimeline
                    {
                        EncounterId = e.EncounterId,
                        EncounterName = e.EncounterName,
                        SpecName = e.SpecName,
                        AverageDurationMs = e.AverageDurationMs,
                        ParseCount = e.ParseCount,
                    },
                    StringComparer.Ordinal),
            };
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load timeline index, starting fresh.");
            cachedIndexEntries = new Dictionary<string, TimelineIndexEntry>(StringComparer.Ordinal);
            cachedIndexDatabase = new TimelineDatabase();
        }
    }

    private void MigrateLegacyDatabase()
    {
        try
        {
            var json = File.ReadAllText(legacyDbPath);
            var db = JsonConvert.DeserializeObject<TimelineDatabase>(json) ?? new TimelineDatabase();

            cachedIndexEntries = new Dictionary<string, TimelineIndexEntry>(StringComparer.Ordinal);
            cachedIndexDatabase = new TimelineDatabase
            {
                GeneratedAt = db.GeneratedAt,
                Version = db.Version,
                Timelines = new Dictionary<string, AggregatedTimeline>(StringComparer.Ordinal),
            };

            foreach (var timeline in db.Timelines.Values)
                SaveTimelineInternal(timeline);

            var backupPath = Path.Combine(dataDir, $"timelines_legacy_backup_{DateTime.UtcNow:yyyyMMddHHmmss}.json");
            File.Move(legacyDbPath, backupPath, true);
            log.Info("Migrated legacy timeline database to split-store format.");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to migrate legacy timeline database.");
            cachedIndexEntries = new Dictionary<string, TimelineIndexEntry>(StringComparer.Ordinal);
            cachedIndexDatabase = new TimelineDatabase();
        }
    }

    private void SaveTimelineInternal(AggregatedTimeline timeline)
    {
        var key = TimelineDatabase.MakeKey(timeline.EncounterId, timeline.SpecName);
        var fileName = cachedIndexEntries!.TryGetValue(key, out var existing)
            ? existing.FileName
            : CreateFileName(key);

        var json = JsonConvert.SerializeObject(timeline, Formatting.Indented);
        File.WriteAllText(Path.Combine(timelineDir, fileName), json);

        var indexEntry = new TimelineIndexEntry
        {
            Key = key,
            FileName = fileName,
            EncounterId = timeline.EncounterId,
            EncounterName = timeline.EncounterName,
            SpecName = timeline.SpecName,
            AverageDurationMs = timeline.AverageDurationMs,
            ParseCount = timeline.ParseCount,
        };

        cachedIndexEntries[key] = indexEntry;
        fullTimelineCache[key] = timeline;
        cachedIndexDatabase!.Timelines[key] = new AggregatedTimeline
        {
            EncounterId = timeline.EncounterId,
            EncounterName = timeline.EncounterName,
            SpecName = timeline.SpecName,
            AverageDurationMs = timeline.AverageDurationMs,
            ParseCount = timeline.ParseCount,
        };
        cachedIndexDatabase.GeneratedAt = DateTime.UtcNow;
        SaveIndex();
    }

    private bool RemoveTimelineInternal(string key)
    {
        var removed = cachedIndexEntries!.Remove(key, out var entry);
        cachedIndexDatabase!.Timelines.Remove(key);
        fullTimelineCache.Remove(key);

        if (entry != null)
        {
            var path = Path.Combine(timelineDir, entry.FileName);
            if (File.Exists(path))
                File.Delete(path);
        }

        if (removed)
        {
            cachedIndexDatabase.GeneratedAt = DateTime.UtcNow;
            SaveIndex();
            log.Info("Removed timeline for {0}.", key);
        }

        return removed;
    }

    private void SaveIndex()
    {
        var index = new TimelineIndexData
        {
            GeneratedAt = cachedIndexDatabase?.GeneratedAt ?? DateTime.UtcNow,
            Version = cachedIndexDatabase?.Version ?? "1",
            Entries = cachedIndexEntries!.Values.OrderBy(e => e.Key, StringComparer.Ordinal).ToList(),
        };

        var json = JsonConvert.SerializeObject(index, Formatting.Indented);
        File.WriteAllText(indexPath, json);
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

    private sealed class TimelineIndexData
    {
        public DateTime GeneratedAt { get; set; }
        public string Version { get; set; } = "1";
        public List<TimelineIndexEntry> Entries { get; set; } = [];
    }

    private sealed class TimelineIndexEntry
    {
        public string Key { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public int EncounterId { get; set; }
        public string EncounterName { get; set; } = string.Empty;
        public string SpecName { get; set; } = string.Empty;
        public double AverageDurationMs { get; set; }
        public int ParseCount { get; set; }
    }
}
