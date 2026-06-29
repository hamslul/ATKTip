using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ATKTip.Data;

namespace ATKTip;

internal static partial class BossModRebornExport
{
    private const int SyntheticPhaseEncounterIdBase = 1_000_000;
    private const string EncounterDataFileName = "ATKTip.BMR.encounters.json";

    private sealed record class StateAnchor(uint StateId, double TimeSec);

    private sealed class EncounterDefinition
    {
        public required string EncounterType { get; init; }
        public required int Level { get; init; }
        public required List<StateAnchor> Anchors { get; init; }
    }

    private sealed class UtilityMapping
    {
        public required string TrackName { get; init; }
        public required string OptionName { get; init; }
    }

    private sealed class JobModuleDefinition
    {
        public required string ModuleType { get; init; }
        public required IReadOnlyDictionary<string, UtilityMapping> AbilityMappings { get; init; }
    }

    private sealed class EncounterPhaseWindow
    {
        public required int Ordinal { get; init; }
        public required long StartMs { get; init; }
        public required long EndMs { get; init; }
    }

    private sealed class JobDefinition
    {
        public required string ClassCode { get; init; }
        public required IReadOnlyList<JobModuleDefinition> Modules { get; init; }
    }

    private sealed record class BossModImportResult(bool LiveImported, string LiveStatus);

    private static readonly Lazy<Dictionary<int, EncounterDefinition>> EncounterDefinitions = new(LoadEncounterDefinitions);

    private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> LegacyGeneratedEncounterPhaseIds =
        new Dictionary<int, IReadOnlyDictionary<int, int>>
        {
            [104] = new Dictionary<int, int>
            {
                [2] = 105,
            },
        };

    private static readonly IReadOnlyDictionary<string, int> EncounterNameAliases =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [NormalizeName("The Unending Coil of Bahamut")] = 1073,
            [NormalizeName("UCOB")] = 1073,
            [NormalizeName("The Weapon's Refrain")] = 1074,
            [NormalizeName("The Weapons Refrain")] = 1074,
            [NormalizeName("UWU")] = 1074,
            [NormalizeName("The Epic of Alexander")] = 1075,
            [NormalizeName("TEA")] = 1075,
            [NormalizeName("Dragonsong's Reprise")] = 1076,
            [NormalizeName("Dragonsongs Reprise")] = 1076,
            [NormalizeName("DSR")] = 1076,
            [NormalizeName("DSW")] = 1076,
            [NormalizeName("The Omega Protocol")] = 1077,
            [NormalizeName("TOP")] = 1077,
            [NormalizeName("Futures Rewritten")] = 1079,
            [NormalizeName("FRU")] = 1079,
            [NormalizeName("Valigarmanda")] = 1071,
            [NormalizeName("Worqor Lar Dor")] = 1071,
            [NormalizeName("Zoraal Ja")] = 1072,
            [NormalizeName("Everkeep")] = 1072,
            [NormalizeName("Queen Eternal")] = 1078,
            [NormalizeName("The Minstrel's Ballad: Sphene's Burden")] = 1078,
            [NormalizeName("The Minstrels Ballad Sphenes Burden")] = 1078,
            [NormalizeName("Zelenia")] = 1080,
            [NormalizeName("Recollection")] = 1080,
            [NormalizeName("Necron")] = 1081,
            [NormalizeName("The Minstrel's Ballad: Necron's Embrace")] = 1081,
            [NormalizeName("The Minstrels Ballad Necrons Embrace")] = 1081,
            [NormalizeName("Guardian Arkveld")] = 1082,
            [NormalizeName("Doomtrain")] = 1083,
            [NormalizeName("Dancing Mad")] = 1085,
            [NormalizeName("DMU")] = 1085,
            [NormalizeName("Black Cat")] = 93,
            [NormalizeName("Honey B. Lovely")] = 94,
            [NormalizeName("Honey B Lovely")] = 94,
            [NormalizeName("Brute Bomber")] = 95,
            [NormalizeName("Wicked Thunder")] = 96,
            [NormalizeName("Dancing Green")] = 97,
            [NormalizeName("Sugar Riot")] = 98,
            [NormalizeName("Brute Abombinator")] = 99,
            [NormalizeName("Howling Blade")] = 100,
            [NormalizeName("Vampiric Idol")] = 101,
            [NormalizeName("Vamp Fatale")] = 101,
            [NormalizeName("The Xtremes")] = 102,
            [NormalizeName("The Tyrant")] = 103,
        };

    private static readonly IReadOnlyDictionary<string, JobDefinition> JobDefinitions = CreateJobDefinitions();
    private static readonly IReadOnlyDictionary<string, JobDefinition> JobDefinitionLookup = CreateJobDefinitionLookup(JobDefinitions);

    public static bool TryExportUtilityPlan(AggregatedTimeline timeline, RecastDatabase recastDatabase, out string clipboardJson, out string status)
    {
        clipboardJson = string.Empty;

        var originalEncounterId = timeline.EncounterId;
        var baseEncounterId = GetBaseEncounterId(originalEncounterId);
        baseEncounterId = ResolveEncounterId(timeline, baseEncounterId);
        if (!EncounterDefinitions.Value.TryGetValue(baseEncounterId, out var encounter))
        {
            if (EncounterDefinitions.Value.Count == 0)
            {
                status = $"BMR encounter data file was not loaded: {EncounterDataFileName}.";
                return false;
            }

            status = $"No BMR encounter match: ID {timeline.EncounterId}, name \"{timeline.EncounterName}\".";
            return false;
        }

        if (!TryResolveJobDefinition(timeline.SpecName, out var job))
        {
            var normalizedSpecName = NormalizeName(timeline.SpecName);
            status = string.IsNullOrWhiteSpace(normalizedSpecName)
                ? $"BossModReborn export is not configured for {timeline.SpecName}."
                : $"BossModReborn export is not configured for {timeline.SpecName} (normalized: {normalizedSpecName}).";
            return false;
        }

        var phaseWindows = BuildEncounterPhaseWindows(timeline.PhaseInfo);
        var phaseDurationsSec = phaseWindows
            .Select(window => Math.Max(0.0, (window.EndMs - window.StartMs) / 1000.0))
            .ToList();
        var absoluteTimelineOffsetSec = 0.0;
        if (TryGetEncounterPhaseOrdinal(baseEncounterId, originalEncounterId, out var phaseOrdinal))
        {
            var phaseWindow = phaseWindows.FirstOrDefault(window => window.Ordinal == phaseOrdinal);
            if (phaseWindow == null)
            {
                status = $"BossModReborn export could not resolve phase {phaseOrdinal} for encounter \"{timeline.EncounterName}\".";
                return false;
            }

            absoluteTimelineOffsetSec = phaseWindow.StartMs / 1000.0;
        }

        var moduleTrackEntries = new Dictionary<string, Dictionary<string, JsonArray>>(StringComparer.Ordinal);
        foreach (var module in job.Modules)
            moduleTrackEntries[module.ModuleType] = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);
        var mappedCount = 0;
        var skippedCount = 0;

        foreach (var entry in timeline.Entries.OrderBy(entry => entry.TimeOffsetSec))
        {
            var normalizedAbility = NormalizeName(entry.AbilityName);
            var absoluteTimeSec = absoluteTimelineOffsetSec + entry.TimeOffsetSec;
            EncounterPhaseWindow? phaseWindow = null;
            if (phaseWindows.Count > 0 &&
                !TryGetPhaseWindowForTime(phaseWindows, absoluteTimeSec, out phaseWindow))
            {
                skippedCount++;
                continue;
            }

            var anchor = FindBestAnchor(encounter.Anchors, absoluteTimeSec, phaseWindow);
            var stateId = NormalizeAnchorStateId(anchor.StateId, phaseWindow);
            var timeSinceActivation = Math.Max(0.0, absoluteTimeSec - anchor.TimeSec);
            var windowLength = GetWindowLength(entry, recastDatabase);
            var mappedEntry = false;

            foreach (var module in job.Modules)
            {
                if (!module.AbilityMappings.TryGetValue(normalizedAbility, out var mapping))
                    continue;

                if (!moduleTrackEntries[module.ModuleType].TryGetValue(mapping.TrackName, out var trackArray))
                {
                    trackArray = [];
                    moduleTrackEntries[module.ModuleType][mapping.TrackName] = trackArray;
                }

                trackArray.Add(new JsonObject
                {
                    ["StateID"] = $"0x{stateId:X8}",
                    ["TimeSinceActivation"] = Math.Round(timeSinceActivation, 3),
                    ["WindowLength"] = Math.Round(windowLength, 3),
                    ["Option"] = mapping.OptionName,
                    ["Comment"] = $"{entry.AbilityName} @ {FormatTime(absoluteTimeSec)}",
                });

                mappedEntry = true;
                mappedCount++;
            }

            if (!mappedEntry)
                skippedCount++;
        }

        if (mappedCount == 0)
        {
            status = "No BossModReborn utility planner actions were found in the selected timeline.";
            return false;
        }

        var modulesObject = new JsonObject();
        foreach (var module in job.Modules)
        {
            var moduleObject = new JsonObject();
            foreach (var (trackName, entries) in moduleTrackEntries[module.ModuleType].OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
                moduleObject[trackName] = entries;
            moduleObject["_defaults"] = new JsonObject();
            modulesObject[module.ModuleType] = moduleObject;
        }

        var phaseDurationsArray = new JsonArray();
        foreach (var phaseDurationSec in phaseDurationsSec)
            phaseDurationsArray.Add(Math.Round(phaseDurationSec, 3));

        var payload = new JsonObject
        {
            ["Name"] = $"{timeline.EncounterName} {timeline.SpecName} Utility Import",
            ["Encounter"] = encounter.EncounterType,
            ["Class"] = job.ClassCode,
            ["Level"] = encounter.Level,
            ["PhaseDurations"] = phaseDurationsArray,
            ["Modules"] = modulesObject,
            ["Targeting"] = new JsonArray(),
        };

        var wrapped = new JsonObject
        {
            ["version"] = 1,
            ["payload"] = payload,
        };

        clipboardJson = wrapped.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var guid = Guid.NewGuid().ToString();

        try
        {
            var importResult = SavePlanToBossModReborn(guid, encounter.EncounterType, job.ClassCode, clipboardJson);
            status = $"Exported {mappedCount} planner entries to BossModReborn and selected the plan in the BMR manifest" +
                     (skippedCount > 0 ? $" ({skippedCount} timeline actions skipped)." : ".") +
                     (importResult.LiveImported
                         ? " Direct BMR import completed."
                         : $" Direct BMR import unavailable: {importResult.LiveStatus}");
        }
        catch (Exception ex)
        {
            status = $"Generated the import plan, but failed to write it into BossModReborn: {ex.Message}";
            return false;
        }

        return true;
    }

    private static JobDefinition CreateJobDefinition(string classCode, params JobModuleDefinition[] modules)
    {
        return new JobDefinition
        {
            ClassCode = classCode,
            Modules = modules,
        };
    }

    private static IReadOnlyDictionary<string, JobDefinition> CreateJobDefinitionLookup(IReadOnlyDictionary<string, JobDefinition> jobDefinitions)
    {
        var lookup = new Dictionary<string, JobDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var (jobName, jobDefinition) in jobDefinitions)
        {
            RegisterJobDefinitionAlias(lookup, jobName, jobDefinition);
            RegisterJobDefinitionAlias(lookup, jobName.Trim(), jobDefinition);
            RegisterJobDefinitionAlias(lookup, NormalizeName(jobName), jobDefinition);
            RegisterJobDefinitionAlias(lookup, jobName.Replace(" ", string.Empty, StringComparison.Ordinal), jobDefinition);
            RegisterJobDefinitionAlias(lookup, jobDefinition.ClassCode, jobDefinition);
            RegisterJobDefinitionAlias(lookup, NormalizeName(jobDefinition.ClassCode), jobDefinition);
        }

        return lookup;
    }

    private static void RegisterJobDefinitionAlias(
        IDictionary<string, JobDefinition> lookup,
        string alias,
        JobDefinition jobDefinition)
    {
        if (string.IsNullOrWhiteSpace(alias))
            return;

        lookup[alias] = jobDefinition;
    }

    private static bool TryResolveJobDefinition(string? specName, out JobDefinition jobDefinition)
    {
        jobDefinition = null!;
        if (string.IsNullOrWhiteSpace(specName))
            return false;

        if (JobDefinitionLookup.TryGetValue(specName, out var resolvedJobDefinition))
        {
            jobDefinition = resolvedJobDefinition;
            return true;
        }

        var trimmedSpecName = specName.Trim();
        if (JobDefinitionLookup.TryGetValue(trimmedSpecName, out resolvedJobDefinition))
        {
            jobDefinition = resolvedJobDefinition;
            return true;
        }

        var noWhitespaceSpecName = new string(trimmedSpecName.Where(static character => !char.IsWhiteSpace(character)).ToArray());
        if (JobDefinitionLookup.TryGetValue(noWhitespaceSpecName, out resolvedJobDefinition))
        {
            jobDefinition = resolvedJobDefinition;
            return true;
        }

        var normalizedSpecName = NormalizeName(trimmedSpecName);
        if (!string.IsNullOrWhiteSpace(normalizedSpecName) &&
            JobDefinitionLookup.TryGetValue(normalizedSpecName, out resolvedJobDefinition))
        {
            jobDefinition = resolvedJobDefinition;
            return true;
        }

        return false;
    }

    private static JobModuleDefinition CreateJobModuleDefinition(string moduleType, params (string Ability, string Track, string Option)[] mappings)
    {
        var abilityMappings = new Dictionary<string, UtilityMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in mappings)
        {
            abilityMappings[NormalizeName(mapping.Ability)] = new UtilityMapping
            {
                TrackName = mapping.Track,
                OptionName = mapping.Option,
            };
        }

        return new JobModuleDefinition
        {
            ModuleType = moduleType,
            AbilityMappings = abilityMappings,
        };
    }

    private static Dictionary<int, EncounterDefinition> LoadEncounterDefinitions()
    {
        using var stream = OpenEncounterDefinitionsStream();
        if (stream == null)
            return [];

        using var document = JsonDocument.Parse(stream);
        var result = new Dictionary<int, EncounterDefinition>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var encounterId))
                continue;

            var anchorsText = property.Value.GetProperty("anchors").GetString() ?? string.Empty;
            result[encounterId] = new EncounterDefinition
            {
                EncounterType = property.Value.GetProperty("encounterType").GetString() ?? string.Empty,
                Level = property.Value.GetProperty("level").GetInt32(),
                Anchors = ParseAnchors(anchorsText),
            };
        }

        AddEncounterDefinition(
            result,
            1085,
            "BossMod.Dawntrail.Ultimate.DMU.DMU",
            100,
            "0@0.000,1@10.100,5@15.100,1010@18.350,1011@26.050,1020@29.050,1030@32.250,1040@34.850,1041@37.150,1050@37.950,1060@42.150,1070@45.750,1080@48.450,1090@49.450,2090@53.350,2091@57.350,2100@62.350,2110@65.550,2120@67.650,2130@69.750,2131@77.050,2140@80.050,2150@87.200,2160@91.300,2161@92.000,2165@97.000,2170@97.800,2180@102.400,2190@106.400,2200@110.900,2210@114.700,2220@123.900,2221@128.900,2215@132.100,2222@134.200,2225@136.300,3230@138.400,3231@145.200,3240@150.200,3250@158.000,3260@161.000,3270@166.400,3280@172.000,3290@179.900,4000@184.900,1000000@195.900,1000010@206.200,1000011@214.200,1000020@219.200,1000021@227.200,1000030@234.200,1000040@247.400,1000050@257.400,1000055@263.100,1000060@268.100,1000070@268.500,1000080@278.500,1000085@284.200,1000090@289.200,1000100@289.600,1000110@299.600,1000115@305.300,1000120@310.300,1000130@310.700,1000140@320.700,1000145@326.100,1000150@331.100,1000151@335.200,1000160@340.200,1000161@348.400,1000170@351.400,1000171@354.500,1000180@358.500,1000190@364.200,1000200@364.800,1000210@366.300,1000220@368.300,1000221@368.900,1000222@370.900,1FF0000@375.900");
        AddEncounterDefinition(
            result,
            1094,
            "BossMod.Dawntrail.Ultimate.DMU.DMU",
            100,
            "0@0.000,1@10.100,5@15.100,1010@18.350,1011@26.050,1020@29.050,1030@32.250,1040@34.850,1041@37.150,1050@37.950,1060@42.150,1070@45.750,1080@48.450,1090@49.450,2090@53.350,2091@57.350,2100@62.350,2110@65.550,2120@67.650,2130@69.750,2131@77.050,2140@80.050,2150@87.200,2160@91.300,2161@92.000,2165@97.000,2170@97.800,2180@102.400,2190@106.400,2200@110.900,2210@114.700,2220@123.900,2221@128.900,2215@132.100,2222@134.200,2225@136.300,3230@138.400,3231@145.200,3240@150.200,3250@158.000,3260@161.000,3270@166.400,3280@172.000,3290@179.900,4000@184.900,1000000@195.900,1000010@206.200,1000011@214.200,1000020@219.200,1000021@227.200,1000030@234.200,1000040@247.400,1000050@257.400,1000055@263.100,1000060@268.100,1000070@268.500,1000080@278.500,1000085@284.200,1000090@289.200,1000100@289.600,1000110@299.600,1000115@305.300,1000120@310.300,1000130@310.700,1000140@320.700,1000145@326.100,1000150@331.100,1000151@335.200,1000160@340.200,1000161@348.400,1000170@351.400,1000171@354.500,1000180@358.500,1000190@364.200,1000200@364.800,1000210@366.300,1000220@368.300,1000221@368.900,1000222@370.900,1FF0000@375.900");

        return result;
    }

    private static void AddEncounterDefinition(
        IDictionary<int, EncounterDefinition> definitions,
        int encounterId,
        string encounterType,
        int level,
        string anchorsText)
    {
        definitions[encounterId] = new EncounterDefinition
        {
            EncounterType = encounterType,
            Level = level,
            Anchors = ParseAnchors(anchorsText),
        };
    }

    private static Stream? OpenEncounterDefinitionsStream()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(BossModRebornExport).Assembly.Location);
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, EncounterDataFileName),
            assemblyDirectory != null ? Path.Combine(assemblyDirectory, EncounterDataFileName) : string.Empty,
            Path.Combine(Environment.CurrentDirectory, EncounterDataFileName),
        };

        var path = candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        if (path != null)
            return File.OpenRead(path);

        var assembly = typeof(BossModRebornExport).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(EncounterDataFileName, StringComparison.OrdinalIgnoreCase));

        return resourceName != null ? assembly.GetManifestResourceStream(resourceName) : null;
    }

    private static List<StateAnchor> ParseAnchors(string anchorsText)
    {
        var result = new List<StateAnchor>();
        foreach (var token in anchorsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var splitIndex = token.IndexOf('@');
            if (splitIndex <= 0 || splitIndex >= token.Length - 1)
                continue;

            if (!uint.TryParse(token[..splitIndex], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var stateId))
                continue;
            if (!double.TryParse(token[(splitIndex + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var timeSec))
                continue;

            result.Add(new StateAnchor(stateId, timeSec));
        }

        result.Sort((left, right) => left.TimeSec.CompareTo(right.TimeSec));
        return result;
    }

    private static List<EncounterPhaseWindow> BuildEncounterPhaseWindows(FightPhaseInfo? phaseInfo)
    {
        if (phaseInfo == null || phaseInfo.PhaseTransitions.Count == 0)
            return [];

        var metadataById = phaseInfo.PhaseMetadata.ToDictionary(phase => phase.Id);
        var orderedTransitions = phaseInfo.PhaseTransitions
            .OrderBy(transition => transition.StartTime)
            .ToList();
        var windows = new List<EncounterPhaseWindow>();

        for (var index = 0; index < orderedTransitions.Count; index++)
        {
            var transition = orderedTransitions[index];
            if (!metadataById.TryGetValue(transition.Id, out var metadata) || metadata.IsIntermission)
                continue;

            var nextStartTime = index + 1 < orderedTransitions.Count
                ? orderedTransitions[index + 1].StartTime
                : phaseInfo.FightEndTime;
            var startMs = Math.Max(0L, transition.StartTime - phaseInfo.FightStartTime);
            var endMs = Math.Max(startMs, nextStartTime - phaseInfo.FightStartTime);
            if (endMs <= startMs)
                continue;

            windows.Add(new EncounterPhaseWindow
            {
                Ordinal = windows.Count + 1,
                StartMs = startMs,
                EndMs = endMs,
            });
        }

        return windows;
    }

    private static bool TryGetPhaseWindowForTime(
        IReadOnlyList<EncounterPhaseWindow> phaseWindows,
        double timeSec,
        out EncounterPhaseWindow? phaseWindow)
    {
        phaseWindow = phaseWindows.FirstOrDefault(window =>
        {
            var startSec = window.StartMs / 1000.0;
            var endSec = window.EndMs / 1000.0;
            return timeSec >= startSec - 0.001 && timeSec < endSec + 0.001;
        });
        return phaseWindow != null;
    }

    private static StateAnchor FindBestAnchor(
        IReadOnlyList<StateAnchor> anchors,
        double timeSec,
        EncounterPhaseWindow? phaseWindow)
    {
        if (anchors.Count == 0)
            return new StateAnchor(0, 0);

        if (phaseWindow != null)
        {
            var phaseStartSec = phaseWindow.StartMs / 1000.0;
            var phaseEndSec = phaseWindow.EndMs / 1000.0;
            var phaseAnchors = anchors
                .Where(anchor => anchor.TimeSec >= phaseStartSec - 0.001 && anchor.TimeSec <= phaseEndSec + 0.001)
                .ToList();
            if (phaseAnchors.Count > 0)
                return FindBestAnchor(phaseAnchors, timeSec, null);
        }

        var low = 0;
        var high = anchors.Count - 1;
        var best = anchors[0];
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var anchor = anchors[mid];
            if (anchor.TimeSec <= timeSec)
            {
                best = anchor;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    private static uint NormalizeAnchorStateId(uint stateId, EncounterPhaseWindow? phaseWindow)
    {
        if (phaseWindow == null || phaseWindow.Ordinal <= 1 || (stateId & 0xFF000000) != 0)
            return stateId;

        // Older bundled anchor tables were generated with phase sequence bytes stripped.
        // BossModReborn state machines use the top byte to distinguish phases.
        return stateId | ((uint)(phaseWindow.Ordinal - 1) << 24);
    }

    private static double GetWindowLength(TimelineEntry entry, RecastDatabase recastDatabase)
    {
        var recastInfo = recastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        if (recastInfo?.IsGcdAction == true)
            return Math.Max(1.5, recastInfo.CastTimeSec > 0 ? recastInfo.CastTimeSec : 2.5);

        return 0.75;
    }

    private static BossModImportResult SavePlanToBossModReborn(string guid, string encounterType, string classCode, string planJson)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher",
            "pluginConfigs",
            "BossModReborn",
            "autorot");
        var plansDirectory = Path.Combine(root, "plans");
        Directory.CreateDirectory(plansDirectory);
        var planPath = Path.Combine(plansDirectory, $"{guid}.json");
        File.WriteAllText(planPath, planJson, Encoding.UTF8);

        var manifestPath = Path.Combine(root, "plans.manifest.json");
        JsonObject manifest;
        if (File.Exists(manifestPath))
        {
            manifest = JsonNode.Parse(File.ReadAllText(manifestPath, Encoding.UTF8))?.AsObject() ?? new JsonObject();
        }
        else
        {
            manifest = new JsonObject();
        }

        manifest["version"] = 0;
        var payload = manifest["payload"] as JsonObject ?? new JsonObject();
        manifest["payload"] = payload;

        var encounterObject = payload[encounterType] as JsonObject ?? new JsonObject();
        payload[encounterType] = encounterObject;

        var classObject = encounterObject[classCode] as JsonObject ?? new JsonObject();
        encounterObject[classCode] = classObject;
        var plansArray = classObject["Plans"] as JsonArray ?? new JsonArray();
        classObject["Plans"] = plansArray;

        for (var index = plansArray.Count - 1; index >= 0; index--)
        {
            if (string.Equals(plansArray[index]?.GetValue<string>(), guid, StringComparison.Ordinal))
                plansArray.RemoveAt(index);
        }

        plansArray.Add(guid);
        classObject["SelectedIndex"] = plansArray.Count - 1;

        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        return TryImportPlanIntoLiveBossModReborn(planPath, guid);
    }

    private static BossModImportResult TryImportPlanIntoLiveBossModReborn(string planPath, string guid)
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "BossModReborn", StringComparison.OrdinalIgnoreCase));
            if (assembly == null)
                return new(false, "BossModReborn assembly is not loaded. Reload BMR or restart after export.");

            var plan = DeserializeBossModRebornPlan(assembly, planPath, guid);
            if (plan == null)
                return new(false, "BMR rejected the generated plan JSON during parse.");

            var plansDatabase = FindBossModRebornPlanDatabase(assembly);
            if (plansDatabase == null)
                return new(false, "could not locate BMR's live PlanDatabase. Reload BMR to pick up the manifest.");

            var planType = assembly.GetType("BossMod.Autorotation.Plan");
            var encounter = planType?.GetField("Encounter")?.GetValue(plan);
            var classValue = planType?.GetField("Class")?.GetValue(plan);
            if (encounter == null || classValue == null)
                return new(false, "generated BMR plan did not expose Encounter/Class fields.");

            var modifyPlan = plansDatabase.GetType().GetMethod("ModifyPlan", BindingFlags.Instance | BindingFlags.Public);
            if (modifyPlan == null)
                return new(false, "BMR PlanDatabase.ModifyPlan was not found.");
            modifyPlan?.Invoke(plansDatabase, [null, plan]);

            var getPlans = plansDatabase.GetType().GetMethod("GetPlans", BindingFlags.Instance | BindingFlags.Public);
            var planList = getPlans?.Invoke(plansDatabase, [encounter, classValue]);
            var plans = GetFieldValue(planList, "Plans") as System.Collections.IList;
            if (planList == null || plans == null)
                return new(true, "plan imported, but selection could not be verified.");

            var selectedIndexField = planList.GetType().GetField("SelectedIndex", BindingFlags.Instance | BindingFlags.Public);
            selectedIndexField?.SetValue(planList, plans.Count - 1);

            var modifyManifest = plansDatabase.GetType().GetMethod("ModifyManifest", BindingFlags.Instance | BindingFlags.Public);
            modifyManifest?.Invoke(plansDatabase, [encounter, classValue]);
            return new(true, "imported through BMR PlanDatabase.ModifyPlan.");
        }
        catch (TargetInvocationException ex)
        {
            return new(false, ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    private static object? DeserializeBossModRebornPlan(Assembly assembly, string planPath, string guid)
    {
        var converterType = assembly.GetType("BossMod.Autorotation.PlanPresetConverter");
        var schema = converterType?.GetField("PlanSchema", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        var load = schema?.GetType().GetMethod("Load", BindingFlags.Instance | BindingFlags.Public);
        var loaded = load?.Invoke(schema, [new FileInfo(planPath)]);
        if (loaded == null)
            return null;

        var payload = (JsonElement)loaded.GetType().GetField("Item2")!.GetValue(loaded)!;
        var serializationType = assembly.GetType("BossMod.Serialization");
        var options = serializationType?.GetMethod("BuildSerializationOptions", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null) as JsonSerializerOptions;
        var planType = assembly.GetType("BossMod.Autorotation.Plan");
        var plan = planType != null ? JsonSerializer.Deserialize(payload, planType, options) : null;
        planType?.GetField("Guid")?.SetValue(plan, guid);
        return plan;
    }

    private static object? FindBossModRebornPlanDatabase(Assembly assembly)
    {
        var pluginType = assembly.GetType("BossMod.Plugin");
        var plugin = pluginType != null ? FindBossModRebornPluginInstance(assembly, pluginType) : null;
        var rotationDatabase = GetFieldValue(plugin, "_rotationDB");
        var planDatabase = GetFieldValue(rotationDatabase, "Plans");
        if (planDatabase != null)
            return planDatabase;

        var rotationWindow = FindBossModRebornRotationWindow(assembly);
        var manager = GetFieldValue(rotationWindow, "_mgr");
        var database = GetFieldValue(manager, "Database");
        return GetFieldValue(database, "Plans");
    }

    private static object? FindBossModRebornPluginInstance(Assembly assembly, Type pluginType)
    {
        var serviceType = assembly.GetType("BossMod.Service");
        if (serviceType == null)
            return null;

        var roots = serviceType
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => SafeGetValue(() => field.GetValue(null)))
            .Where(value => value != null)!;

        foreach (var root in roots)
        {
            var plugin = FindObjectByType(root!, value => pluginType.IsInstanceOfType(value), 9);
            if (plugin != null)
                return plugin;
        }

        return null;
    }

    private static object? FindBossModRebornRotationWindow(Assembly assembly)
    {
        var serviceType = assembly.GetType("BossMod.Service");
        var windowSystem = serviceType?.GetField("WindowSystem", BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
        return windowSystem != null
            ? FindObjectByType(windowSystem, value => string.Equals(value.GetType().FullName, "BossMod.Autorotation.UIRotationWindow", StringComparison.Ordinal), 8)
            : null;
    }

    private static object? FindObjectByType(object root, Func<object, bool> predicate, int maxDepth)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(object Value, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0 && visited.Count < 5000)
        {
            var (value, depth) = queue.Dequeue();
            if (!visited.Add(value))
                continue;
            if (predicate(value))
                return value;
            if (depth >= maxDepth)
                continue;

            if (value is Delegate del)
            {
                foreach (var childDelegate in del.GetInvocationList())
                {
                    if (childDelegate.Target != null && ShouldReflectInto(childDelegate.Target.GetType()))
                        queue.Enqueue((childDelegate.Target, depth + 1));
                }
            }

            if (value is System.Collections.IEnumerable enumerable and not string)
            {
                foreach (var item in enumerable)
                {
                    if (item != null && ShouldReflectInto(item.GetType()))
                        queue.Enqueue((item, depth + 1));
                }
            }

            foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.IsStatic || !ShouldReflectInto(field.FieldType))
                    continue;

                var child = SafeGetValue(() => field.GetValue(value));
                if (child != null)
                    queue.Enqueue((child, depth + 1));
            }

            foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!property.CanRead ||
                    property.GetIndexParameters().Length > 0 ||
                    !ShouldReflectInto(property.PropertyType))
                    continue;

                var child = SafeGetValue(() => property.GetValue(value));
                if (child != null)
                    queue.Enqueue((child, depth + 1));
            }
        }

        return null;
    }

    private static bool ShouldReflectInto(Type type)
        => !type.IsPrimitive &&
           !type.IsEnum &&
           type != typeof(string) &&
           type != typeof(decimal) &&
           !type.FullName!.StartsWith("System.Reflection.", StringComparison.Ordinal);

    private static object? GetFieldValue(object? instance, string fieldName)
        => instance?.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);

    private static object? SafeGetValue(Func<object?> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }

    private static int GetBaseEncounterId(int encounterId)
    {
        if (encounterId == 105)
            return 104;

        if (encounterId >= SyntheticPhaseEncounterIdBase)
        {
            var baseEncounterId = (encounterId - SyntheticPhaseEncounterIdBase) / 100;
            return baseEncounterId > 0 ? baseEncounterId : encounterId;
        }

        return encounterId;
    }

    private static bool TryGetEncounterPhaseOrdinal(int baseEncounterId, int encounterId, out int phaseOrdinal)
    {
        foreach (var (legacyBaseEncounterId, legacyPhaseIds) in LegacyGeneratedEncounterPhaseIds)
        {
            if (legacyBaseEncounterId != baseEncounterId)
                continue;

            foreach (var (ordinal, legacyEncounterId) in legacyPhaseIds)
            {
                if (legacyEncounterId == encounterId)
                {
                    phaseOrdinal = ordinal;
                    return true;
                }
            }
        }

        if (encounterId < SyntheticPhaseEncounterIdBase)
        {
            phaseOrdinal = 0;
            return false;
        }

        var rawValue = encounterId - SyntheticPhaseEncounterIdBase;
        var candidateBaseEncounterId = rawValue / 100;
        var candidatePhaseOrdinal = rawValue % 100;
        if (candidateBaseEncounterId != baseEncounterId || candidatePhaseOrdinal <= 1)
        {
            phaseOrdinal = 0;
            return false;
        }

        phaseOrdinal = candidatePhaseOrdinal;
        return true;
    }

    private static int ResolveEncounterId(AggregatedTimeline timeline, int baseEncounterId)
    {
        if (EncounterDefinitions.Value.ContainsKey(baseEncounterId))
            return baseEncounterId;

        if (EncounterNameAliases.TryGetValue(NormalizeName(timeline.EncounterName), out var aliasEncounterId))
            return aliasEncounterId;

        foreach (var (alias, encounterId) in EncounterNameAliases)
        {
            if (NormalizeName(timeline.EncounterName).Contains(alias, StringComparison.OrdinalIgnoreCase))
                return encounterId;
        }

        return baseEncounterId;
    }

    private static string NormalizeName(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string FormatTime(double seconds)
        => $"{(int)(seconds / 60)}:{seconds % 60:00.0}";
}
