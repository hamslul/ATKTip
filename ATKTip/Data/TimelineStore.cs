using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace ATKTip.Data;

/// <summary>
/// Persists the aggregated timeline database to disk as JSON.
/// </summary>
public sealed class TimelineStore
{
    private readonly string dataDir;
    private readonly string dbPath;
    private readonly IPluginLog log;

    private TimelineDatabase? cached;

    public TimelineStore(string dataDir, IPluginLog log)
    {
        this.dataDir = dataDir;
        this.dbPath = Path.Combine(dataDir, "timelines.json");
        this.log = log;
    }

    public TimelineDatabase Load()
    {
        if (cached != null)
            return cached;

        if (!File.Exists(dbPath))
        {
            cached = new TimelineDatabase { GeneratedAt = DateTime.MinValue };
            return cached;
        }

        try
        {
            var json = File.ReadAllText(dbPath);
            cached = JsonConvert.DeserializeObject<TimelineDatabase>(json) ?? new TimelineDatabase();
            log.Info("Loaded timeline database ({0} timelines, generated {1}).",
                cached.Timelines.Count, cached.GeneratedAt);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load timeline database, starting fresh.");
            cached = new TimelineDatabase();
        }

        return cached;
    }

    public void Save(TimelineDatabase db)
    {
        db.GeneratedAt = DateTime.UtcNow;
        cached = db;

        var json = JsonConvert.SerializeObject(db, Formatting.Indented);
        File.WriteAllText(dbPath, json);
        log.Info("Saved timeline database ({0} timelines).", db.Timelines.Count);
    }

    /// <summary>Upsert a single timeline into the database and persist.</summary>
    public void SaveTimeline(AggregatedTimeline timeline)
    {
        var db = Load();
        var key = TimelineDatabase.MakeKey(timeline.EncounterId, timeline.SpecName);
        db.Timelines[key] = timeline;
        Save(db);
    }

    public AggregatedTimeline? GetTimeline(int encounterId, string specName)
    {
        var db = Load();
        return db.Timelines.GetValueOrDefault(TimelineDatabase.MakeKey(encounterId, specName));
    }

    /// <summary>Return all timelines stored in the database.</summary>
    public IEnumerable<AggregatedTimeline> GetAllTimelines()
    {
        return Load().Timelines.Values;
    }

    /// <summary>Remove a specific timeline from the database and persist.</summary>
    public bool RemoveTimeline(int encounterId, string specName)
    {
        var db = Load();
        var key = TimelineDatabase.MakeKey(encounterId, specName);
        if (db.Timelines.Remove(key))
        {
            Save(db);
            log.Info("Removed timeline for {0}.", key);
            return true;
        }
        return false;
    }

    /// <summary>Clear all cached timelines from disk.</summary>
    public void ClearAll()
    {
        cached = new TimelineDatabase();
        Save(cached);
        log.Info("Cleared all cached timelines.");
    }
}
