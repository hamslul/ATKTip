using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ATKTip.Data;

public sealed class FFLogsClient : IDisposable
{
    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private string? accessToken;
    private DateTime tokenExpiry;

    public FFLogsClient(Configuration config, IPluginLog log)
    {
        this.config = config;
        this.log = log;
    }

    // ── Helpers ──

    private static string SafeTruncate(string? s, int maxLen)
    {
        if (s == null) return "(null)";
        return s.Length <= maxLen ? s : s[..maxLen] + "…";
    }

    // ── Authentication ──

    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (accessToken != null && DateTime.UtcNow < tokenExpiry)
            return;

        var clientId = config.FFLogsClientId?.Trim() ?? string.Empty;
        var clientSecret = config.FFLogsClientSecret?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("FFLogs API credentials are not configured.");

        log.Info("[FFLogs] Authenticating (client_id: {0}…)", SafeTruncate(clientId, 8));

        var request = new HttpRequestMessage(HttpMethod.Post, "https://www.fflogs.com/oauth/token");
        request.Content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
        ]);

        var response = await http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            log.Error("[FFLogs] Auth failed: {0} - {1}", (int)response.StatusCode, SafeTruncate(json, 300));
            throw new Exception($"FFLogs auth failed ({(int)response.StatusCode}). Check credentials.");
        }

        var token = JsonConvert.DeserializeObject<FFLogsTokenResponse>(json)
            ?? throw new Exception("Failed to parse FFLogs token response.");

        if (string.IsNullOrWhiteSpace(token.AccessToken))
            throw new Exception("FFLogs returned empty access token.");

        accessToken = token.AccessToken;
        tokenExpiry = DateTime.UtcNow.AddSeconds(Math.Max(token.ExpiresIn - 60, 60));
        log.Info("[FFLogs] Authenticated. Token expires in {0}s.", token.ExpiresIn);
    }

    // ── Raw GraphQL query (returns raw JSON string for diagnostics) ──

    private async Task<string> QueryRawAsync(string query, CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);

        var request = new HttpRequestMessage(HttpMethod.Post, "https://www.fflogs.com/api/v2/client");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(
            JsonConvert.SerializeObject(new { query }),
            Encoding.UTF8,
            "application/json");

        var response = await http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            log.Error("[FFLogs] HTTP {0}: {1}", (int)response.StatusCode, SafeTruncate(json, 500));
            throw new Exception($"FFLogs HTTP {(int)response.StatusCode}: {SafeTruncate(json, 200)}");
        }

        return json;
    }

    private async Task<JObject> QueryAsync(string query, CancellationToken ct)
    {
        var json = await QueryRawAsync(query, ct);
        var result = JObject.Parse(json);

        if (result["errors"] is JArray errors && errors.Count > 0)
        {
            var msg = errors[0]?["message"]?.ToString() ?? "Unknown GraphQL error";
            log.Error("[FFLogs] GraphQL error: {0}", msg);
            throw new Exception($"FFLogs GraphQL error: {msg}");
        }

        return result;
    }

    // ── Public API methods ──

    /// <summary>Fetch all current FFXIV zones and their encounters.</summary>
    public async Task<List<Zone>> GetZonesAsync(CancellationToken ct)
    {
        const string query = """
            {
              worldData {
                expansions {
                  id
                  name
                  zones {
                    id
                    name
                    encounters {
                      id
                      name
                    }
                  }
                }
              }
            }
            """;

        var result = await QueryAsync(query, ct);
        var expansions = result["data"]?["worldData"]?["expansions"] as JArray;
        if (expansions == null)
            return [];

        var zones = new List<Zone>();
        foreach (var expansion in expansions)
        {
            var expZones = expansion["zones"] as JArray;
            if (expZones == null) continue;

            foreach (var z in expZones)
            {
                var zone = z.ToObject<Zone>();
                if (zone != null && zone.Encounters.Count > 0)
                    zones.Add(zone);
            }
        }

        return zones;
    }

    /// <summary>Fetch all FFXIV job specs.</summary>
    public async Task<List<GameClass>> GetClassesAsync(CancellationToken ct)
    {
        const string query = """
            {
              gameData {
                class(id: 1) {
                  id
                  name
                  specs {
                    id
                    name
                  }
                }
              }
            }
            """;

        var result = await QueryAsync(query, ct);
        var classData = result["data"]?["gameData"]?["class"];
        if (classData == null) return [];

        var gameClass = classData.ToObject<GameClass>();
        return gameClass != null ? [gameClass] : [];
    }

    /// <summary>
    /// Fetch top rankings for a given encounter and spec (single page, up to 100).
    /// </summary>
    public async Task<List<RankingEntry>> GetTopRankingsAsync(
        int encounterId, string specName, int count, CancellationToken ct)
    {
        // FFLogs API expects specName without spaces: "BlackMage", "DarkKnight", etc.
        var apiSpecName = specName.Replace(" ", "");
        log.Info("[FFLogs] Fetching rankings: encounter={0} spec=\"{1}\" (api: \"{2}\")", encounterId, specName, apiSpecName);
        specName = apiSpecName;

        // Build and run query — omit metric entirely to let API choose appropriate default
        var query = $$"""
            {
              worldData {
                encounter(id: {{encounterId}}) {
                  characterRankings(
                    specName: "{{specName}}"
                    page: 1
                  )
                }
              }
            }
            """;

        // Use raw query so we can log the full response
        var rawJson = await QueryRawAsync(query, ct);
        log.Info("[FFLogs] Rankings raw response (first 1000): {0}", SafeTruncate(rawJson, 1000));

        var result = JObject.Parse(rawJson);

        // Check for errors
        if (result["errors"] is JArray errors && errors.Count > 0)
        {
            var msg = errors[0]?["message"]?.ToString() ?? "Unknown error";
            throw new Exception($"FFLogs GraphQL error: {msg}");
        }

        var rankingsToken = result["data"]?["worldData"]?["encounter"]?["characterRankings"];
        if (rankingsToken == null || rankingsToken.Type == JTokenType.Null)
        {
            log.Warning("[FFLogs] characterRankings is null. Response: {0}", SafeTruncate(rawJson, 500));
            return [];
        }

        // characterRankings is a JSON scalar. Newtonsoft may deserialize it as a JObject directly.
        log.Info("[FFLogs] characterRankings token type: {0}", rankingsToken.Type);

        JObject? rankings;
        if (rankingsToken is JObject jo)
        {
            rankings = jo;
        }
        else
        {
            // It might be a string (JValue) that needs parsing
            var str = rankingsToken.ToString();
            log.Info("[FFLogs] characterRankings as string (first 500): {0}", SafeTruncate(str, 500));
            try { rankings = JObject.Parse(str); }
            catch (Exception ex)
            {
                log.Error("[FFLogs] Failed to parse characterRankings: {0}", ex.Message);
                return [];
            }
        }

        var keys = string.Join(", ", rankings.Properties().Select(p => p.Name));
        log.Info("[FFLogs] Rankings object keys: {0}", keys);

        var rankingArray = rankings["rankings"] as JArray;
        if (rankingArray == null || rankingArray.Count == 0)
        {
            log.Warning("[FFLogs] No 'rankings' array. Keys: {0}", keys);
            return [];
        }

        log.Info("[FFLogs] Found {0} ranking entries. First: {1}",
            rankingArray.Count, SafeTruncate(rankingArray[0]?.ToString(Formatting.None), 500));

        var entries = new List<RankingEntry>();
        foreach (var r in rankingArray)
        {
            if (entries.Count >= count) break;

            var reportCode = r["report"]?["code"]?.ToString();
            var fightId = r["report"]?["fightID"]?.Value<int>();

            if (string.IsNullOrEmpty(reportCode) || fightId == null || fightId == 0)
            {
                if (entries.Count == 0)
                    log.Warning("[FFLogs] Cannot extract report from entry: {0}",
                        SafeTruncate(r.ToString(Formatting.None), 500));
                continue;
            }

            entries.Add(new RankingEntry
            {
                ReportCode = reportCode,
                FightId = fightId.Value,
                Duration = r["duration"]?.Value<double>() ?? 0,
                Amount = r["amount"]?.Value<double>() ?? 0,
            });
        }

        log.Info("[FFLogs] Parsed {0} ranking entries for {1}/{2}.", entries.Count, encounterId, specName);
        return entries;
    }

    /// <summary>
    /// Fetch cast events for a specific fight from a report.
    /// Matches the proven working approach from reference implementations:
    /// 1. Get fight times (relative to report start)
    /// 2. Fetch all events for the fight using fightIDs filter
    /// </summary>
    public async Task<CastEventsResult> GetCastEventsAsync(
        string reportCode, int fightId, string specName, CancellationToken ct)
    {
        // FFLogs API expects sourceClass without spaces: "BlackMage", "DarkKnight", etc.
        specName = specName.Replace(" ", "");

        // ── Step 1: Get fight times AND ability name lookup from masterData ──
        var fightQuery = $$"""
            {
              reportData {
                report(code: "{{reportCode}}") {
                  fights(fightIDs: [{{fightId}}]) {
                    encounterID
                    name
                    startTime
                    endTime
                    lastPhase
                    lastPhaseAsAbsoluteIndex
                    lastPhaseIsIntermission
                    phaseTransitions {
                      id
                      startTime
                    }
                  }
                  phases {
                    encounterID
                    phases {
                      id
                      name
                      isIntermission
                    }
                  }
                  masterData {
                    abilities {
                      gameID
                      name
                      icon
                    }
                  }
                }
              }
            }
            """;

        var fightRaw = await QueryRawAsync(fightQuery, ct);
        log.Debug("[FFLogs] Fight query raw (first 500): {0}", SafeTruncate(fightRaw, 500));

        var fightResult = JObject.Parse(fightRaw);
        if (fightResult["errors"] is JArray fightErrors && fightErrors.Count > 0)
        {
            var msg = fightErrors[0]?["message"]?.ToString() ?? "Unknown error";
            throw new Exception($"Fight query error: {msg}");
        }

        var reportToken = fightResult["data"]?["reportData"]?["report"];
        if (reportToken == null || reportToken.Type == JTokenType.Null)
        {
            log.Warning("[FFLogs] Report {0} is null. Raw: {1}", reportCode, SafeTruncate(fightRaw, 300));
            return new CastEventsResult();
        }

        var fights = reportToken["fights"] as JArray;
        if (fights == null || fights.Count == 0)
        {
            log.Warning("[FFLogs] Report {0} fight {1}: no fights found. Raw: {2}",
                reportCode, fightId, SafeTruncate(fightRaw, 300));
            return new CastEventsResult();
        }

        var fightStartTime = fights[0]?["startTime"]?.Value<long>() ?? 0;
        var fightEndTime = fights[0]?["endTime"]?.Value<long>() ?? 0;

        if (fightEndTime <= fightStartTime)
        {
            log.Warning("[FFLogs] Report {0} fight {1}: invalid times start={2} end={3}",
                reportCode, fightId, fightStartTime, fightEndTime);
            return new CastEventsResult();
        }

        var durationMs = fightEndTime - fightStartTime;
        log.Debug("[FFLogs] Fight times: start={0} end={1} duration={2}ms", fightStartTime, fightEndTime, durationMs);

        var encounterId = fights[0]?["encounterID"]?.Value<int>() ?? 0;
        var encounterName = fights[0]?["name"]?.ToString() ?? string.Empty;
        var lastPhase = fights[0]?["lastPhase"]?.Value<int>() ?? 0;
        var lastPhaseAsAbsoluteIndex = fights[0]?["lastPhaseAsAbsoluteIndex"]?.Value<int>() ?? 0;
        var lastPhaseIsIntermission = fights[0]?["lastPhaseIsIntermission"]?.Value<bool>() ?? false;
        var encounterPhaseMap = ParseEncounterPhases(reportToken["phases"] as JArray);
        var phaseInfo = new FightPhaseInfo
        {
            EncounterId = encounterId,
            EncounterName = encounterName,
            FightStartTime = fightStartTime,
            FightEndTime = fightEndTime,
            LastPhase = lastPhase,
            LastPhaseAsAbsoluteIndex = lastPhaseAsAbsoluteIndex,
            LastPhaseIsIntermission = lastPhaseIsIntermission,
            PhaseTransitions = ParseFightPhaseTransitions(fights[0]?["phaseTransitions"] as JArray),
            PhaseMetadata = encounterPhaseMap.TryGetValue(encounterId, out var metadata)
                ? CloneEncounterPhaseMetadataList(metadata)
                : [],
        };

        // Build ability name/icon lookup from masterData
        var abilityLookup = new Dictionary<int, (string name, string icon)>();
        var abilities = reportToken["masterData"]?["abilities"] as JArray;
        if (abilities != null)
        {
            foreach (var a in abilities)
            {
                var gameId = a["gameID"]?.Value<int>() ?? 0;
                if (gameId != 0 && !abilityLookup.ContainsKey(gameId))
                {
                    abilityLookup[gameId] = (
                        a["name"]?.ToString() ?? string.Empty,
                        a["icon"]?.ToString() ?? string.Empty
                    );
                }
            }
            log.Debug("[FFLogs] Loaded {0} ability names from masterData.", abilityLookup.Count);
        }
        else
        {
            log.Warning("[FFLogs] No masterData abilities found for report {0}.", reportCode);
        }

        // ── Step 2: Fetch events ──
        // Use the exact pattern from working reference code:
        // events(fightIDs: [X], endTime: BIG_NUMBER, limit: 10000)
        // startTime/endTime in events are relative to report start.
        var allEvents = new List<CastEvent>();
        long? nextStart = null;
        var pageNum = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            pageNum++;

            // Build events query - use dataType: Casts + sourceClass to only get cast events
            // from the specific job. On first page, don't specify startTime.
            string eventsQuery;
            if (nextStart == null)
            {
                eventsQuery = $$"""
                    {
                      reportData {
                        report(code: "{{reportCode}}") {
                          events(
                            fightIDs: [{{fightId}}]
                            dataType: Casts
                            sourceClass: "{{specName}}"
                            endTime: {{fightEndTime}}
                            limit: 10000
                          ) {
                            data
                            nextPageTimestamp
                          }
                        }
                      }
                    }
                    """;
            }
            else
            {
                eventsQuery = $$"""
                    {
                      reportData {
                        report(code: "{{reportCode}}") {
                          events(
                            fightIDs: [{{fightId}}]
                            dataType: Casts
                            sourceClass: "{{specName}}"
                            startTime: {{nextStart}}
                            endTime: {{fightEndTime}}
                            limit: 10000
                          ) {
                            data
                            nextPageTimestamp
                          }
                        }
                      }
                    }
                    """;
            }

            var eventsRaw = await QueryRawAsync(eventsQuery, ct);

            // On first page, log the raw response
            if (pageNum == 1)
                log.Info("[FFLogs] Events page 1 raw (first 1000): {0}", SafeTruncate(eventsRaw, 1000));

            var eventsResult = JObject.Parse(eventsRaw);
            if (eventsResult["errors"] is JArray evErrors && evErrors.Count > 0)
            {
                var msg = evErrors[0]?["message"]?.ToString() ?? "Unknown error";
                throw new Exception($"Events query error (page {pageNum}): {msg}");
            }

            var eventsToken = eventsResult["data"]?["reportData"]?["report"]?["events"];
            if (eventsToken == null || eventsToken.Type == JTokenType.Null)
            {
                log.Warning("[FFLogs] Events is null on page {0}. Raw: {1}",
                    pageNum, SafeTruncate(eventsRaw, 500));
                break;
            }

            log.Debug("[FFLogs] Events token type: {0}", eventsToken.Type);

            // The events field returns a ReportEventPaginator object with { data, nextPageTimestamp }
            // "data" is a JSON scalar (type JSON) - may be JArray or needs parsing
            JToken? dataToken;
            JToken? nextPageToken;

            if (eventsToken is JObject evObj)
            {
                dataToken = evObj["data"];
                nextPageToken = evObj["nextPageTimestamp"];
            }
            else
            {
                log.Warning("[FFLogs] Events is not JObject, it's {0}. Trying to parse as string...",
                    eventsToken.Type);
                try
                {
                    var evParsed = JObject.Parse(eventsToken.ToString());
                    dataToken = evParsed["data"];
                    nextPageToken = evParsed["nextPageTimestamp"];
                }
                catch
                {
                    log.Error("[FFLogs] Cannot parse events token: {0}", SafeTruncate(eventsToken.ToString(), 300));
                    break;
                }
            }

            // Parse the data field
            JArray? data = null;
            if (dataToken is JArray ja)
            {
                data = ja;
            }
            else if (dataToken != null && dataToken.Type != JTokenType.Null)
            {
                var dataStr = dataToken.ToString();
                if (pageNum == 1)
                    log.Info("[FFLogs] events.data type={0}, first 300: {1}", dataToken.Type, SafeTruncate(dataStr, 300));
                try { data = JArray.Parse(dataStr); }
                catch (Exception ex)
                {
                    log.Error("[FFLogs] Failed to parse events.data as array: {0}. Data: {1}",
                        ex.Message, SafeTruncate(dataStr, 300));
                    break;
                }
            }

            if (data == null || data.Count == 0)
            {
                log.Debug("[FFLogs] No event data on page {0}.", pageNum);
                break;
            }

            if (pageNum == 1)
                log.Info("[FFLogs] First event: {0}", SafeTruncate(data[0]?.ToString(Formatting.None), 400));

            // Parse events - flat structure: { timestamp, type, sourceID, targetID, abilityGameID, fight }
            foreach (var e in data)
            {
                var type = e["type"]?.ToString() ?? string.Empty;
                // With dataType: Casts, we get "cast" and "begincast" events.
                // Skip anything else (e.g. combatantinfo if it leaks through).
                if (type != "cast" && type != "begincast")
                    continue;

                // Events use flat abilityGameID field, NOT a nested ability object
                var abilityId = e["abilityGameID"]?.Value<int>() ?? 0;
                if (abilityId == 0) continue;

                // Look up ability name/icon from masterData
                var (abilityName, abilityIcon) = abilityLookup.GetValueOrDefault(abilityId, (string.Empty, string.Empty));

                // Event timestamps are relative to the report start.
                // Convert to fight-relative.
                var rawTs = e["timestamp"]?.Value<long>() ?? 0;

                allEvents.Add(new CastEvent
                {
                    Timestamp = rawTs - fightStartTime,
                    AbilityGameID = abilityId,
                    AbilityName = abilityName,
                    AbilityIcon = abilityIcon,
                    Type = type,
                });
            }

            log.Debug("[FFLogs] Page {0}: {1} raw events, {2} casts total so far.",
                pageNum, data.Count, allEvents.Count);

            // Check pagination
            if (nextPageToken == null || nextPageToken.Type == JTokenType.Null)
                break;

            var nextVal = nextPageToken.Value<long?>();
            if (nextVal == null || (nextStart != null && nextVal <= nextStart))
                break;

            nextStart = nextVal;
        }

        log.Info("[FFLogs] Report {0} fight {1}: {2} cast events across {3} page(s).",
            reportCode, fightId, allEvents.Count, pageNum);
        return new CastEventsResult
        {
            Casts = allEvents,
            PhaseInfo = phaseInfo,
        };
    }

    /// <summary>
    /// Fetch cast events from enemy/boss sources for a single fight.
    /// Returns raw events with pairing info for begincast → cast.
    /// </summary>
    public async Task<List<RawBossCastEvent>> GetBossCastEventsAsync(
        string reportCode, int fightId, CancellationToken ct)
    {
        // Step 1: Get fight times + masterData (reuses the same structure as player events)
        var fightQuery = $$"""
            {
              reportData {
                report(code: "{{reportCode}}") {
                  fights(fightIDs: [{{fightId}}]) {
                    startTime
                    endTime
                  }
                  masterData {
                    abilities {
                      gameID
                      name
                    }
                  }
                }
              }
            }
            """;

        var fightRaw = await QueryRawAsync(fightQuery, ct);
        var fightResult = JObject.Parse(fightRaw);

        if (fightResult["errors"] is JArray fe && fe.Count > 0)
            throw new Exception($"Boss fight query error: {fe[0]?["message"]}");

        var reportToken = fightResult["data"]?["reportData"]?["report"];
        if (reportToken == null || reportToken.Type == JTokenType.Null)
        {
            log.Warning("[FFLogs/Boss] Report {0} not found.", reportCode);
            return [];
        }

        var fights = reportToken["fights"] as JArray;
        if (fights == null || fights.Count == 0) return [];

        var fightStartTime = fights[0]?["startTime"]?.Value<long>() ?? 0;
        var fightEndTime   = fights[0]?["endTime"]?.Value<long>()   ?? 0;
        if (fightEndTime <= fightStartTime) return [];

        var abilityLookup = new Dictionary<int, string>();
        if (reportToken["masterData"]?["abilities"] is JArray abilities)
        {
            foreach (var a in abilities)
            {
                var id = a["gameID"]?.Value<int>() ?? 0;
                if (id != 0 && !abilityLookup.ContainsKey(id))
                    abilityLookup[id] = a["name"]?.ToString() ?? string.Empty;
            }
        }

        // Step 2: Fetch enemy cast events
        var allEvents = new List<RawBossCastEvent>();
        long? nextStart = null;
        var pageNum = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            pageNum++;

            var startClause = nextStart == null ? string.Empty : $"startTime: {nextStart}";
            var eventsQuery = $$"""
                {
                  reportData {
                    report(code: "{{reportCode}}") {
                      events(
                        fightIDs: [{{fightId}}]
                        dataType: Casts
                        hostilityType: Enemies
                        {{startClause}}
                        endTime: {{fightEndTime}}
                        limit: 10000
                      ) {
                        data
                        nextPageTimestamp
                      }
                    }
                  }
                }
                """;

            var eventsRaw   = await QueryRawAsync(eventsQuery, ct);
            var eventsResult = JObject.Parse(eventsRaw);

            if (eventsResult["errors"] is JArray evErr && evErr.Count > 0)
                throw new Exception($"Boss events query error: {evErr[0]?["message"]}");

            var eventsToken = eventsResult["data"]?["reportData"]?["report"]?["events"];
            if (eventsToken == null || eventsToken.Type == JTokenType.Null) break;

            JToken? dataToken = null;
            JToken? nextPageToken = null;

            if (eventsToken is JObject eo)
            {
                dataToken     = eo["data"];
                nextPageToken = eo["nextPageTimestamp"];
            }
            else
            {
                try
                {
                    var parsed    = JObject.Parse(eventsToken.ToString());
                    dataToken     = parsed["data"];
                    nextPageToken = parsed["nextPageTimestamp"];
                }
                catch { break; }
            }

            JArray? data = dataToken is JArray dja ? dja : null;
            if (data == null && dataToken != null && dataToken.Type != JTokenType.Null)
            {
                try { data = JArray.Parse(dataToken.ToString()); } catch { break; }
            }

            if (data == null || data.Count == 0) break;

            foreach (var e in data)
            {
                var type = e["type"]?.ToString() ?? string.Empty;
                if (type != "cast" && type != "begincast") continue;

                var abilityId = e["abilityGameID"]?.Value<int>() ?? 0;
                if (abilityId == 0) continue;

                var rawTs    = e["timestamp"]?.Value<long>() ?? 0;
                var sourceId = e["sourceID"]?.Value<int>()  ?? 0;

                allEvents.Add(new RawBossCastEvent
                {
                    Timestamp   = rawTs - fightStartTime,
                    AbilityGameId = abilityId,
                    AbilityName = abilityLookup.GetValueOrDefault(abilityId, string.Empty),
                    Type        = type,
                    SourceId    = sourceId,
                });
            }

            if (nextPageToken == null || nextPageToken.Type == JTokenType.Null) break;
            var nextVal = nextPageToken.Value<long?>();
            if (nextVal == null || (nextStart != null && nextVal <= nextStart)) break;
            nextStart = nextVal;
        }

        log.Info("[FFLogs/Boss] {0} fight {1}: {2} enemy events across {3} page(s).",
            reportCode, fightId, allEvents.Count, pageNum);
        return allEvents;
    }

    private static Dictionary<int, List<EncounterPhaseMetadata>> ParseEncounterPhases(JArray? phasesArray)
    {
        var result = new Dictionary<int, List<EncounterPhaseMetadata>>();
        if (phasesArray == null)
            return result;

        foreach (var encounterPhasesToken in phasesArray)
        {
            var encounterId = encounterPhasesToken["encounterID"]?.Value<int>() ?? 0;
            if (encounterId == 0)
                continue;

            var phases = new List<EncounterPhaseMetadata>();
            if (encounterPhasesToken["phases"] is JArray phaseArray)
            {
                foreach (var phaseToken in phaseArray)
                {
                    var phaseId = phaseToken["id"]?.Value<int>() ?? 0;
                    if (phaseId == 0)
                        continue;

                    phases.Add(new EncounterPhaseMetadata
                    {
                        Id = phaseId,
                        Name = phaseToken["name"]?.ToString() ?? $"Phase {phaseId}",
                        IsIntermission = phaseToken["isIntermission"]?.Value<bool>() ?? false,
                    });
                }
            }

            result[encounterId] = phases;
        }

        return result;
    }

    private static List<FightPhaseTransition> ParseFightPhaseTransitions(JArray? transitionsArray)
    {
        var transitions = new List<FightPhaseTransition>();
        if (transitionsArray == null)
            return transitions;

        foreach (var transitionToken in transitionsArray)
        {
            var phaseId = transitionToken["id"]?.Value<int>() ?? 0;
            if (phaseId == 0)
                continue;

            transitions.Add(new FightPhaseTransition
            {
                Id = phaseId,
                StartTime = transitionToken["startTime"]?.Value<long>() ?? 0,
            });
        }

        return transitions;
    }

    private static List<EncounterPhaseMetadata> CloneEncounterPhaseMetadataList(
        IEnumerable<EncounterPhaseMetadata> metadata)
        => metadata
            .Select(phase => new EncounterPhaseMetadata
            {
                Id = phase.Id,
                Name = phase.Name,
                IsIntermission = phase.IsIntermission,
            })
            .ToList();

    // ── Report importer API ──

    /// <summary>
    /// Fetch fight list and player roster for a specific report.
    /// Only returns fights where killType = Kills.
    /// </summary>
    public async Task<(List<ReportFight> fights, List<ReportPlayer> players, Dictionary<int, (string name, string icon)> abilities)>
        GetReportInfoAsync(string reportCode, CancellationToken ct)
    {
        var query = $$"""
            {
              reportData {
                report(code: "{{reportCode}}") {
                  fights(killType: Kills) {
                    id
                    name
                    encounterID
                    startTime
                    endTime
                    lastPhase
                    lastPhaseAsAbsoluteIndex
                    lastPhaseIsIntermission
                    phaseTransitions {
                      id
                      startTime
                    }
                  }
                  phases {
                    encounterID
                    phases {
                      id
                      name
                      isIntermission
                    }
                  }
                  masterData {
                    actors(type: "Player") {
                      id
                      name
                      type
                      subType
                    }
                    abilities {
                      gameID
                      name
                      icon
                    }
                  }
                }
              }
            }
            """;

        var result = await QueryAsync(query, ct);
        var reportToken = result["data"]?["reportData"]?["report"];
        if (reportToken == null || reportToken.Type == JTokenType.Null)
        {
            log.Warning("[FFLogs/RI] Report {0} not found.", reportCode);
            return ([], [], []);
        }

        var encounterPhaseMap = ParseEncounterPhases(reportToken["phases"] as JArray);

        // Parse fights
        var fights = new List<ReportFight>();
        if (reportToken["fights"] is JArray fightArray)
        {
            foreach (var f in fightArray)
            {
                var id = f["id"]?.Value<int>() ?? 0;
                if (id == 0) continue;
                var encounterId = f["encounterID"]?.Value<int>() ?? 0;
                fights.Add(new ReportFight
                {
                    Id        = id,
                    Name      = f["name"]?.ToString() ?? $"Fight {id}",
                    EncounterId = encounterId,
                    StartTime = f["startTime"]?.Value<long>() ?? 0,
                    EndTime   = f["endTime"]?.Value<long>() ?? 0,
                    LastPhase = f["lastPhase"]?.Value<int>() ?? 0,
                    LastPhaseAsAbsoluteIndex = f["lastPhaseAsAbsoluteIndex"]?.Value<int>() ?? 0,
                    LastPhaseIsIntermission = f["lastPhaseIsIntermission"]?.Value<bool>() ?? false,
                    PhaseTransitions = ParseFightPhaseTransitions(f["phaseTransitions"] as JArray),
                    PhaseMetadata = encounterPhaseMap.TryGetValue(encounterId, out var metadata)
                        ? CloneEncounterPhaseMetadataList(metadata)
                        : [],
                });
            }
        }

        // Parse players from masterData.actors
        var players = new List<ReportPlayer>();
        if (reportToken["masterData"]?["actors"] is JArray actorsArray)
        {
            foreach (var a in actorsArray)
            {
                var id = a["id"]?.Value<int>() ?? 0;
                if (id == 0) continue;
                players.Add(new ReportPlayer
                {
                    Id      = id,
                    Name    = a["name"]?.ToString() ?? string.Empty,
                    Type    = a["type"]?.ToString() ?? string.Empty,
                    SubType = a["subType"]?.ToString() ?? string.Empty,
                });
            }
        }

        // Parse abilities
        var abilities = new Dictionary<int, (string name, string icon)>();
        if (reportToken["masterData"]?["abilities"] is JArray abilitiesArray)
        {
            foreach (var a in abilitiesArray)
            {
                var id = a["gameID"]?.Value<int>() ?? 0;
                if (id != 0 && !abilities.ContainsKey(id))
                    abilities[id] = (a["name"]?.ToString() ?? string.Empty, a["icon"]?.ToString() ?? string.Empty);
            }
        }

        log.Info("[FFLogs/RI] Report {0}: {1} kill(s), {2} player(s), {3} abilities.",
            reportCode, fights.Count, players.Count, abilities.Count);
        return (fights, players, abilities);
    }

    /// <summary>
    /// Fetch cast events for a specific player (by sourceID) in a single fight.
    /// Matches the FFLogs Events CSV for a filtered player view by using the
    /// fight display start (typically the first limit break timeline update /
    /// encounter pull marker) and emitting only "cast" events.
    /// </summary>
    public async Task<List<CastEvent>> GetCastEventsForPlayerAsync(
        string reportCode, int fightId, long fightStartTime, long fightEndTime,
        int sourceId, Dictionary<int, (string name, string icon)> abilityLookup, CancellationToken ct)
    {
        var allEvents = new List<CastEvent>();
        long? nextStart = null;
        var pageNum = 0;
        var displayStartTime = await GetFightDisplayStartTimeAsync(reportCode, fightId, fightStartTime, fightEndTime, ct);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            pageNum++;

            var startClause = nextStart == null ? string.Empty : $"startTime: {nextStart}";
            var eventsQuery = $$"""
                {
                  reportData {
                    report(code: "{{reportCode}}") {
                      events(
                        fightIDs: [{{fightId}}]
                        dataType: Casts
                        sourceID: {{sourceId}}
                        {{startClause}}
                        endTime: {{fightEndTime}}
                        limit: 10000
                      ) {
                        data
                        nextPageTimestamp
                      }
                    }
                  }
                }
                """;

            var eventsRaw    = await QueryRawAsync(eventsQuery, ct);
            var eventsResult = JObject.Parse(eventsRaw);

            if (eventsResult["errors"] is JArray evErrors && evErrors.Count > 0)
                throw new Exception($"Events query error (page {pageNum}): {evErrors[0]?["message"]}");

            var eventsToken = eventsResult["data"]?["reportData"]?["report"]?["events"];
            if (eventsToken == null || eventsToken.Type == JTokenType.Null) break;

            JToken? dataToken     = null;
            JToken? nextPageToken = null;

            if (eventsToken is JObject eo)
            {
                dataToken     = eo["data"];
                nextPageToken = eo["nextPageTimestamp"];
            }
            else
            {
                try
                {
                    var parsed    = JObject.Parse(eventsToken.ToString());
                    dataToken     = parsed["data"];
                    nextPageToken = parsed["nextPageTimestamp"];
                }
                catch { break; }
            }

            JArray? data = dataToken is JArray dja ? dja : null;
            if (data == null && dataToken != null && dataToken.Type != JTokenType.Null)
            {
                try { data = JArray.Parse(dataToken.ToString()); } catch { break; }
            }

            if (data == null || data.Count == 0) break;

            // The downloadable FFLogs Events CSV uses cast timestamps, not
            // begincast timestamps. To mirror it exactly, keep only "cast".
            foreach (var e in data)
            {
                var type = e["type"]?.ToString() ?? string.Empty;
                if (type != "cast") continue;

                var abilityId = e["abilityGameID"]?.Value<int>() ?? 0;
                if (abilityId == 0) continue;

                var rawTs = e["timestamp"]?.Value<long>() ?? 0;

                var (abilityName, abilityIcon) = abilityLookup.GetValueOrDefault(abilityId, (string.Empty, string.Empty));

                allEvents.Add(new CastEvent
                {
                    Timestamp     = rawTs - displayStartTime,
                    AbilityGameID = abilityId,
                    AbilityName   = abilityName,
                    AbilityIcon   = abilityIcon,
                    Type          = type,
                });
            }

            if (nextPageToken == null || nextPageToken.Type == JTokenType.Null) break;
            var nextVal = nextPageToken.Value<long?>();
            if (nextVal == null || (nextStart != null && nextVal <= nextStart)) break;
            nextStart = nextVal;
        }

        log.Info("[FFLogs/RI] Report {0} fight {1} sourceID {2}: {3} cast events across {4} page(s).",
            reportCode, fightId, sourceId, allEvents.Count, pageNum);
        return allEvents;
    }

    private async Task<long> GetFightDisplayStartTimeAsync(
        string reportCode, int fightId, long fightStartTime, long fightEndTime, CancellationToken ct)
    {
        var previewEndTime = Math.Min(fightEndTime, fightStartTime + 30000);
        var query = $$"""
            {
              reportData {
                report(code: "{{reportCode}}") {
                  events(
                    fightIDs: [{{fightId}}]
                    endTime: {{previewEndTime}}
                    limit: 1000
                  ) {
                    data
                  }
                }
              }
            }
            """;

        var raw = await QueryRawAsync(query, ct);
        var result = JObject.Parse(raw);

        if (result["errors"] is JArray errors && errors.Count > 0)
            throw new Exception($"Display start query error: {errors[0]?["message"]}");

        var eventsToken = result["data"]?["reportData"]?["report"]?["events"];
        if (eventsToken == null || eventsToken.Type == JTokenType.Null)
            return fightStartTime;

        JToken? dataToken = null;
        if (eventsToken is JObject eo)
            dataToken = eo["data"];
        else
        {
            try
            {
                var parsed = JObject.Parse(eventsToken.ToString());
                dataToken = parsed["data"];
            }
            catch
            {
                return fightStartTime;
            }
        }

        var data = dataToken as JArray;
        if (data == null && dataToken != null && dataToken.Type != JTokenType.Null)
        {
            try { data = JArray.Parse(dataToken.ToString()); }
            catch { return fightStartTime; }
        }

        if (data == null || data.Count == 0)
            return fightStartTime;

        var explicitStart = data
            .Where(e =>
            {
                var type = e["type"]?.ToString();
                return type == "encounterstart"
                    || type == "dungeonencounterstart"
                    || type == "towerstart"
                    || type == "limitbreakupdate";
            })
            .Select(e => e["timestamp"]?.Value<long>() ?? long.MaxValue)
            .DefaultIfEmpty(long.MaxValue)
            .Min();
        if (explicitStart != long.MaxValue)
            return explicitStart;

        return fightStartTime;
    }

    // ── Rate limit ──

    public async Task<(int spent, int limit, int resetIn)> GetRateLimitAsync(CancellationToken ct)
    {
        const string query = "{ rateLimitData { pointsSpentThisHour limitPerHour pointsResetIn } }";
        var result = await QueryAsync(query, ct);
        var rl = result["data"]?["rateLimitData"];
        return (
            rl?["pointsSpentThisHour"]?.Value<int>() ?? 0,
            rl?["limitPerHour"]?.Value<int>() ?? 0,
            rl?["pointsResetIn"]?.Value<int>() ?? 0
        );
    }

    public void Dispose()
    {
        http.Dispose();
    }
}
