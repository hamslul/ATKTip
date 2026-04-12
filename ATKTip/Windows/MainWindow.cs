using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ATKTip.Data;

namespace ATKTip.Windows;

public sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private readonly IPluginLog log;

    /// <summary>Max concurrent FFLogs API requests when fetching parse events.</summary>
    private const int MaxConcurrency = 10;

    // Zone/encounter/spec selection
    private List<Zone> zones = [];
    private List<GameClass> classes = [];
    private List<string> allSpecNames = [];
    private int selectedZone;
    private int selectedEncounter;
    private int selectedSpec;
    private bool selectorsLoaded;
    private bool isFetchingSelectors;
    private string? selectorsError;

    // Current timeline
    private AggregatedTimeline? currentTimeline;
    private Dictionary<int, bool> skillVisibility = [];
    private readonly Dictionary<int, uint> iconIdCache = [];

    // Update state
    private bool isUpdating;
    private string updateStatus = string.Empty;
    private float updateProgress;
    private CancellationTokenSource? updateCts;

    // Skill filter expanded state — tracks which ability IDs have their threshold slider open
    private readonly HashSet<int> expandedSkillNodes = [];

    // Custom timeline editor state
    private string? selectedCustomKey;
    private AggregatedTimeline? editingTimeline;
    private string editEncounterName = string.Empty;
    private string editSpecName = string.Empty;
    private int    editEncounterIdx  = 0;
    private int    editSpecIdx       = 0;
    private float editDurationSec;
    private int editParseCount;
    private int editDeltaMs;
    private int editingEntryIndex = -1;   // index into player Entries; -1 = none
    private bool editingEntryIsBoss;      // true when the popup is editing a boss entry
    private float editEntryTime;
    private float editEntryEndTime;       // boss only
    private string editEntryName = string.Empty;
    private float editEntryFrequency;
    private int   editEntryAbilityIdx = 0;
    private List<string> editEntryAbilityOptions = [];
    private bool customEditorDirty;

    // Conflict detection cache (rebuilt each frame from editingTimeline)
    private readonly HashSet<int>            conflictedPlayerIndices = [];
    private readonly Dictionary<int, string> conflictReasons         = [];

    // Export/import status banner
    private string  eiStatus     = string.Empty;
    private bool    eiIsError;
    private DateTime eiStatusUntil = DateTime.MinValue;

    // Report importer state (right panel "From FFLogs Report" section)
    private string  riUrl            = string.Empty;
    private string  riReportCode     = string.Empty;
    private string  riStatus         = string.Empty;
    private bool    riStatusIsError;
    private List<ReportFight>  riFlights        = [];
    private List<ReportPlayer> riPlayers        = [];
    private Dictionary<int, (string name, string icon)> riAbilityLookup = [];
    private int     riSelectedFight   = -1;   // index into riFlights
    private int     riSelectedPlayer  = -1;   // index into riPlayers
    private bool    riFetching;
    private bool    riImporting;
    private CancellationTokenSource? riCts;

    // Config tab state (mirrors former ConfigWindow fields)
    private string cfgClientId     = string.Empty;
    private string cfgClientSecret = string.Empty;
    private bool   cfgInitialized;
    private bool   focusConfigTab;

    // Hidden easter-egg: click "Config" tab 7 times rapidly to toggle auto-execute
    private int               secretClickCount;
    private DateTime          secretLastClick    = DateTime.MinValue;
    private string            secretBanner       = string.Empty;
    private DateTime          secretBannerUntil  = DateTime.MinValue;
    private IDtrBarEntry?     autoExecDtrEntry;

    /// <summary>Opens the main window and auto-selects the Config tab on the next frame.</summary>
    public void FocusConfigTab() { IsOpen = true; focusConfigTab = true; }

    /// <summary>Creates or removes the DTR bar entry to reflect auto-execute state.</summary>
    public void ApplyAutoExecDtr(bool enabled)
    {
        if (enabled)
        {
            autoExecDtrEntry ??= plugin.DtrBar.Get("ATKTip");
            autoExecDtrEntry.Text  = new SeString(new TextPayload("ATK ▶ AUTO"));
            autoExecDtrEntry.Shown = true;
        }
        else
        {
            autoExecDtrEntry?.Remove();
            autoExecDtrEntry = null;
        }
    }

    public MainWindow(Plugin plugin, IPluginLog log)
        : base("ATKTip - Timeline##ATKTipMain")
    {
        this.plugin = plugin;
        this.log = log;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900, 600),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(1100, 700);
    }

    public override void OnOpen()
    {
        // Load latest credentials into the Config tab fields
        cfgClientId     = plugin.Configuration.FFLogsClientId;
        cfgClientSecret = plugin.Configuration.FFLogsClientSecret;
        cfgInitialized  = true;

        // Auto-populate dropdowns on first open if credentials are set
        if (!selectorsLoaded && !isFetchingSelectors &&
            !string.IsNullOrWhiteSpace(plugin.Configuration.FFLogsClientId))
        {
            selectorsError = null;
            isFetchingSelectors = true;
            Task.Run(FetchSelectorsAsync);
        }
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##MainTabs"))
            return;

        if (ImGui.BeginTabItem("Encounter Timeline"))
        {
            DrawTimelineTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Custom Timelines"))
        {
            DrawCustomTimelinesTab();
            ImGui.EndTabItem();
        }

        var configFlags = focusConfigTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        focusConfigTab = false;
        var configTabOpen    = ImGui.BeginTabItem("Config", configFlags);
        var configTabClicked = ImGui.IsItemClicked();   // captured immediately — last item is the tab label
        if (configTabOpen)
        {
            DrawConfigTab();
            ImGui.EndTabItem();
        }

        // Hidden easter-egg: click "Config" tab label 7 times rapidly to toggle auto-execute
        if (configTabClicked)
        {
            var now = DateTime.UtcNow;
            if ((now - secretLastClick).TotalSeconds <= 2.5)
                secretClickCount++;
            else
                secretClickCount = 1;
            secretLastClick = now;

            if (secretClickCount >= 7)
            {
                secretClickCount = 0;
                plugin.OverlayWindow.AutoExecuteEnabled = !plugin.OverlayWindow.AutoExecuteEnabled;
                ApplyAutoExecDtr(plugin.OverlayWindow.AutoExecuteEnabled);
                var state = plugin.OverlayWindow.AutoExecuteEnabled ? "ENABLED" : "DISABLED";
                secretBanner      = $"Auto-Execute: {state}";
                secretBannerUntil = DateTime.UtcNow.AddSeconds(4);
            }
        }

        ImGui.EndTabBar();

        // Secret banner — shown briefly after the toggle
        if (!string.IsNullOrEmpty(secretBanner) && DateTime.UtcNow < secretBannerUntil)
        {
            var on  = plugin.OverlayWindow.AutoExecuteEnabled;
            var col = on ? new Vector4(0.2f, 0.9f, 0.3f, 1f)
                         : new Vector4(0.9f, 0.3f, 0.2f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Text, col);
            ImGui.TextUnformatted(secretBanner);
            ImGui.PopStyleColor();
        }
    }

    private void DrawTimelineTab()
    {
        DrawSelectors();
        ImGui.Separator();

        // Show update status bar (while updating or after completion/error)
        if (isUpdating)
        {
            ImGui.TextUnformatted(updateStatus);
            ImGui.ProgressBar(updateProgress, new Vector2(-1, 0), string.Empty);
            if (ImGui.Button("Cancel", default))
            {
                updateCts?.Cancel();
            }
            return;
        }

        if (!string.IsNullOrEmpty(updateStatus))
        {
            var isError = updateStatus.StartsWith("Error") || updateStatus.StartsWith("Update failed");
            var isWarning = updateStatus.StartsWith("Warning") || updateStatus.StartsWith("Update cancelled");
            if (isError)
            {
                var red = new Vector4(1f, 0.3f, 0.3f, 1f);
                ImGui.TextColored(in red, updateStatus);
            }
            else if (isWarning)
            {
                var yellow = new Vector4(1f, 0.8f, 0.2f, 1f);
                ImGui.TextColored(in yellow, updateStatus);
            }
            else
            {
                var green = new Vector4(0.3f, 1f, 0.3f, 1f);
                ImGui.TextColored(in green, updateStatus);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Dismiss"))
            {
                updateStatus = string.Empty;
            }
            ImGui.Separator();
        }

        if (currentTimeline == null)
        {
            ImGui.TextUnformatted("Select a fight and job, then click Update Logs to fetch and view the timeline.");
            return;
        }

        DrawSkillFilters();
        ImGui.Separator();
        DrawTimeline();
    }

    // ── Custom timeline export / import ──

    private void SetEiStatus(string msg, bool isError = false)
    {
        eiStatus      = msg;
        eiIsError     = isError;
        eiStatusUntil = DateTime.UtcNow.AddSeconds(4);
    }

    /// <summary>
    /// Export the selected custom timeline to clipboard.
    /// Format: optional ATKTip metadata comment, then FFLogs Events CSV rows.
    /// <para>
    ///   Line 1 (ATKTip only): <c># ATKTip {"encounterId":N,"encounterName":"...","specName":"..."}</c>
    ///   Line 2: <c>"Time","Event",""</c>  (standard FFLogs CSV header)
    ///   Line N: <c>"MM:SS.mmm","PlayerName casts  AbilityName",""</c>
    /// </para>
    /// Plain FFLogs CSVs (no metadata line) can still be imported unchanged.
    /// </summary>
    private void ExportSelectedTimelineToCsv()
    {
        if (editingTimeline == null || selectedCustomKey == null)
        {
            SetEiStatus("Select a timeline on the left first.", true);
            return;
        }
        try
        {
            // Derive a display name for the source player.
            var source = editingTimeline.SpecName;
            if (selectedCustomKey.StartsWith("report_", StringComparison.OrdinalIgnoreCase))
            {
                var parts = selectedCustomKey.Split('_');
                if (parts.Length >= 4)
                    source = string.Join("_", parts[3..]);   // everything after "report_{code}_{fightId}_"
            }

            var sb = new System.Text.StringBuilder();

            // ── ATKTip metadata header (ignored by FFLogs CSV parsers) ────────
            var meta = System.Text.Json.JsonSerializer.Serialize(new
            {
                encounterId   = editingTimeline.EncounterId,
                encounterName = editingTimeline.EncounterName,
                specName      = editingTimeline.SpecName,
            });
            sb.AppendLine($"# ATKTip {meta}");

            // ── Standard FFLogs CSV ───────────────────────────────────────────
            sb.AppendLine("\"Time\",\"Event\",\"\"");

            foreach (var entry in editingTimeline.Entries.OrderBy(e => e.TimeOffsetSec))
            {
                var timeCsv = FormatCsvTime(entry.TimeOffsetSec);
                var evtCsv  = $"{source} casts  {entry.AbilityName}";
                sb.AppendLine($"\"{timeCsv}\",\"{evtCsv}\",\"\"");
            }

            ImGui.SetClipboardText(sb.ToString().TrimEnd());
            SetEiStatus($"Exported \"{editingTimeline.EncounterName} / {editingTimeline.SpecName}\" to clipboard (CSV).");
        }
        catch (Exception ex) { SetEiStatus($"Export failed: {ex.Message}", true); }
    }

    /// <summary>Format seconds as [−]MM:SS.mmm matching the FFLogs CSV time column.</summary>
    private static string FormatCsvTime(double seconds)
    {
        var neg = seconds < 0;
        var abs = Math.Abs(seconds);
        var mm  = (int)(abs / 60);
        var ss  = abs - mm * 60.0;
        return $"{(neg ? "-" : "")}{mm:D2}:{ss:00.000}";
    }

    /// <summary>
    /// Import a timeline from clipboard.
    /// Accepts either a plain FFLogs Events CSV or an ATKTip export (which begins
    /// with a <c># ATKTip {...}</c> metadata line).  The metadata line is parsed when
    /// present to restore encounter identity; it is silently ignored otherwise.
    /// </summary>
    private void ImportTimelineFromCsvClipboard()
    {
        try
        {
            var text = ImGui.GetClipboardText();
            if (string.IsNullOrWhiteSpace(text)) { SetEiStatus("Clipboard is empty.", true); return; }

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) { SetEiStatus("No data in clipboard.", true); return; }

            // ── ATKTip metadata line (optional) ──────────────────────────────
            // Format: # ATKTip {"encounterId":N,"encounterName":"...","specName":"..."}
            int    metaEncounterId   = 0;
            string metaEncounterName = string.Empty;
            string metaSpecName      = string.Empty;

            int lineIdx = 0;
            if (lines[lineIdx].TrimStart().StartsWith("# ATKTip ", StringComparison.Ordinal))
            {
                try
                {
                    var json = lines[lineIdx].Trim()["# ATKTip ".Length..];
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("encounterId",   out var eid)) metaEncounterId   = eid.GetInt32();
                    if (root.TryGetProperty("encounterName", out var en))  metaEncounterName = en.GetString() ?? string.Empty;
                    if (root.TryGetProperty("specName",      out var sn))  metaSpecName      = sn.GetString() ?? string.Empty;
                }
                catch { /* malformed metadata — ignore and continue */ }
                lineIdx++;
            }

            // ── Skip the CSV header row ("Time","Event","") ──────────────────
            if (lineIdx < lines.Length &&
                lines[lineIdx].TrimStart('"').StartsWith("Time", StringComparison.OrdinalIgnoreCase))
                lineIdx++;

            // ── Parse cast rows ───────────────────────────────────────────────
            var entries = new List<TimelineEntry>();
            foreach (var rawLine in lines.Skip(lineIdx))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                var cols = SplitCsvLine(line);
                if (cols.Count < 2) continue;

                var timeSec = ParseCsvTime(cols[0]);
                if (double.IsNaN(timeSec)) continue;

                // Extract ability name: "{source} casts  {ability}[ on {target}]"
                var evt      = cols[1];
                var castsIdx = evt.IndexOf(" casts  ", StringComparison.OrdinalIgnoreCase);
                string abilityName;
                if (castsIdx >= 0)
                {
                    var afterCasts = evt[(castsIdx + 8)..];
                    var onIdx      = afterCasts.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
                    abilityName    = onIdx >= 0 ? afterCasts[..onIdx].Trim() : afterCasts.Trim();
                }
                else
                {
                    abilityName = evt.Trim();
                }

                if (string.IsNullOrEmpty(abilityName)) continue;

                entries.Add(new TimelineEntry
                {
                    AbilityId     = 0,
                    AbilityName   = abilityName,
                    AbilityIcon   = string.Empty,
                    TimeOffsetSec = timeSec,
                    Frequency     = 1.0,
                    AverageUses   = 1.0,
                });
            }

            if (entries.Count == 0) { SetEiStatus("No valid cast entries found in clipboard.", true); return; }

            // ── Resolve encounter / spec names ────────────────────────────────
            // Prefer ATKTip metadata; fall back to inferring from the first cast row.
            var sourceName = metaSpecName;
            var encName    = metaEncounterName;

            if (string.IsNullOrEmpty(sourceName))
            {
                // Infer from first data line: "{source} casts  {ability}"
                var firstDataLine = lines.Skip(lineIdx).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? string.Empty;
                var firstCols     = SplitCsvLine(firstDataLine);
                sourceName = "Imported";
                if (firstCols.Count >= 2)
                {
                    var ci = firstCols[1].IndexOf(" casts  ", StringComparison.OrdinalIgnoreCase);
                    if (ci > 0) sourceName = firstCols[1][..ci].Trim();
                }
            }

            if (string.IsNullOrEmpty(encName))
                encName = "Imported Timeline";

            var durationSec = entries.Max(e => e.TimeOffsetSec);
            var key         = $"csv_import_{DateTime.UtcNow:yyyyMMddHHmmss}";

            var timeline = new AggregatedTimeline
            {
                EncounterId       = metaEncounterId,
                EncounterName     = encName,
                SpecName          = sourceName,
                AverageDurationMs = durationSec * 1000.0,
                ParseCount        = 1,
                Entries           = entries,
                BossEntries       = [],
            };

            plugin.Configuration.CustomTimelines[key] = timeline;
            plugin.SaveConfig();

            var encounterHint = metaEncounterId != 0 ? $" (encounter {encName})" : string.Empty;
            SetEiStatus($"Imported {entries.Count} casts from clipboard{encounterHint}.");
        }
        catch (Exception ex) { SetEiStatus($"Import failed: {ex.Message}", true); }
    }

    /// <summary>Parse an FFLogs CSV time string ("[−]MM:SS.mmm") to seconds.</summary>
    private static double ParseCsvTime(string s)
    {
        s = s.Trim().Trim('"');
        var neg = s.StartsWith('-');
        if (neg) s = s[1..];
        var colon = s.IndexOf(':');
        if (colon < 0) return double.NaN;
        if (!int.TryParse(s[..colon], out var mm)) return double.NaN;
        if (!double.TryParse(s[(colon + 1)..], System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var ss)) return double.NaN;
        var t = mm * 60.0 + ss;
        return neg ? -t : t;
    }

    /// <summary>Split a single CSV line respecting quoted fields.</summary>
    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var inQ    = false;
        var cur    = new System.Text.StringBuilder();
        foreach (var ch in line)
        {
            if (ch == '"') { inQ = !inQ; continue; }
            if (ch == ',' && !inQ) { result.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(ch);
        }
        result.Add(cur.ToString());
        return result;
    }

    // ── Custom Timelines tab ──

    private void DrawCustomTimelinesTab()
    {
        var customs = plugin.Configuration.CustomTimelines;

        // ── Left panel: list ──
        var listWidth = 220f;
        ImGui.BeginChild("##CustomList", new Vector2(listWidth, 0), true);

        ImGui.TextDisabled("Custom Timelines");
        ImGui.Separator();

        if (customs.Count == 0)
        {
            ImGui.TextDisabled("No custom timelines yet.");
            ImGui.TextDisabled("Use \"Copy Timeline\" in the");
            ImGui.TextDisabled("Timeline tab menu bar.");
        }
        else
        {
            foreach (var (key, tl) in customs)
            {
                var label = $"{tl.EncounterName}\n{tl.SpecName}";
                var isSelected = key == selectedCustomKey;
                if (ImGui.Selectable($"{tl.EncounterName} / {tl.SpecName}##{key}", isSelected,
                    ImGuiSelectableFlags.None, default))
                {
                    if (selectedCustomKey != key)
                    {
                        SelectCustomTimeline(key, tl);
                    }
                }
            }
        }

        // ── Export / Import buttons ──────────────────────────────────────
        ImGui.Separator();
        if (ImGui.Button("Export to Clipboard", new Vector2(-1, 0)))
            ExportSelectedTimelineToCsv();
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("Copy the selected custom timeline to clipboard as CSV.");
            ImGui.Text("Format matches the FFLogs Events CSV export.");
            ImGui.EndTooltip();
        }
        if (ImGui.Button("Import from Clipboard", new Vector2(-1, 0)))
            ImportTimelineFromCsvClipboard();
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("Paste a timeline from clipboard (FFLogs Events CSV format).");
            ImGui.Text("Paste a CSV exported from an FFLogs report Events view.");
            ImGui.EndTooltip();
        }

        // Status banner
        if (!string.IsNullOrEmpty(eiStatus) && DateTime.UtcNow < eiStatusUntil)
        {
            ImGui.Spacing();
            if (eiIsError)
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), eiStatus);
            else
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.6f, 1f), eiStatus);
        }

        ImGui.EndChild();
        ImGui.SameLine();

        // ── Right panel: editor ──
        ImGui.BeginChild("##CustomEditor", new Vector2(0, 0), false);

        DrawReportImporter();

        if (editingTimeline == null)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Select a custom timeline on the left to edit it,");
            ImGui.TextDisabled("or use \"From FFLogs Report\" above to import one.");
            ImGui.EndChild();
            return;
        }

        ImGui.Separator();

        // Header actions
        if (ImGui.Button("Load into Viewer"))
        {
            currentTimeline = editingTimeline;
            RebuildSkillVisibility(editingTimeline);
        }
        ImGui.SameLine();
        if (customEditorDirty)
        {
            if (ImGui.Button("Save Changes"))
                SaveEditingTimeline();
            ImGui.SameLine();
        }
        var deleteLabel = $"Delete##delCustom";
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.1f, 0.1f, 1f));
        if (ImGui.Button(deleteLabel))
        {
            plugin.Configuration.CustomTimelines.Remove(selectedCustomKey!);
            plugin.SaveConfig();
            selectedCustomKey = null;
            editingTimeline = null;
            customEditorDirty = false;
        }
        ImGui.PopStyleColor();

        // Guard: if Delete was just pressed editingTimeline is now null — bail out cleanly
        if (editingTimeline == null)
        {
            ImGui.EndChild();
            return;
        }

        ImGui.Separator();

        // Metadata fields
        var flatEncounters = zones.SelectMany(z => z.Encounters).ToList();
        var flatEncounterNames = flatEncounters.Select(e => e.Name).ToList();
        if (flatEncounterNames.Count > 0)
        {
            ImGui.SetNextItemWidth(280);
            if (ImGui.Combo("Encounter Name", ref editEncounterIdx, flatEncounterNames, -1))
            {
                editEncounterName             = flatEncounterNames[editEncounterIdx];
                editingTimeline.EncounterName = editEncounterName;
                editingTimeline.EncounterId   = flatEncounters[editEncounterIdx].Id;
                customEditorDirty = true;
            }
        }
        else
        {
            ImGui.SetNextItemWidth(280);
            if (ImGui.InputText("Encounter Name", ref editEncounterName, 128))
            {
                editingTimeline.EncounterName = editEncounterName;
                customEditorDirty = true;
            }
        }
        if (allSpecNames.Count > 0)
        {
            ImGui.SetNextItemWidth(280);
            if (ImGui.Combo("Spec / Job", ref editSpecIdx, allSpecNames, -1))
            {
                editSpecName = allSpecNames[editSpecIdx];
                editingTimeline.SpecName = editSpecName;
                customEditorDirty = true;
            }
        }
        else
        {
            ImGui.SetNextItemWidth(280);
            if (ImGui.InputText("Spec / Job", ref editSpecName, 64))
            {
                editingTimeline.SpecName = editSpecName;
                customEditorDirty = true;
            }
        }
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputFloat("Duration (s)", ref editDurationSec, 0.1f, 1f, "%.1f"))
        {
            editDurationSec = Math.Max(0f, editDurationSec);
            editingTimeline.AverageDurationMs = editDurationSec * 1000.0;
            customEditorDirty = true;
        }
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Parse Count", ref editParseCount, 1, 10))
        {
            editParseCount = Math.Max(0, editParseCount);
            editingTimeline.ParseCount = editParseCount;
            customEditorDirty = true;
        }
        ImGui.SetNextItemWidth(120);
        var prevDelta = editDeltaMs;
        if (ImGui.InputInt("Delta (ms)", ref editDeltaMs, 50, 200))
        {
            var shift = (editDeltaMs - prevDelta) / 1000.0;
            foreach (var entry in editingTimeline.Entries)
                entry.TimeOffsetSec -= shift;
            editingTimeline.DeltaMs = editDeltaMs;
            customEditorDirty = true;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted("Single log fetching can have inconsistent timing compared to FFLogs.\nUse this to align the timeline manually.\n\nTo find your Delta:\n  1. Open the report on FFLogs\n  2. Go to the Casts tab  (&type=casts)\n  3. Switch to Events view  (&view=events)\n  4. Filter to your character\n     (Summary \u2192 All Friendlies \u2192 [Character Name])\n     The URL should contain &type=casts&view=events&source=\n  5. Compare the first event timestamp to this timeline\n     and adjust Delta until they match.");
            ImGui.EndTooltip();
        }

        ImGui.Separator();

        // Rebuild conflict data each frame so it stays current as entries are edited
        RebuildConflicts(editingTimeline);

        ImGui.Text($"Player: {editingTimeline.Entries.Count}  |  Boss: {editingTimeline.BossEntries.Count}");
        ImGui.SameLine();
        if (ImGui.SmallButton("+ Player"))
        {
            editingTimeline.Entries.Add(new TimelineEntry
            {
                AbilityId     = 0,
                AbilityName   = "New Ability",
                TimeOffsetSec = 0.0,
                Frequency     = 1.0,
                AverageUses   = 1.0,
            });
            customEditorDirty = true;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("+ Boss"))
        {
            editingTimeline.BossEntries.Add(new BossTimelineEntry
            {
                AbilityId    = 0,
                AbilityName  = "New Boss Cast",
                CastStartSec = 0.0,
                CastEndSec   = 0.0,
            });
            customEditorDirty = true;
        }

        if (conflictedPlayerIndices.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(
                new Vector4(1f, 0.4f, 0.4f, 1f),
                $"\u26a0 {conflictedPlayerIndices.Count} recast conflict(s) — hover time cells for details");
        }

        ImGui.Separator();

        // Merge player entries + boss entries, sorted by time, for display
        var playerEntries = editingTimeline.Entries;
        var bossEntries   = editingTimeline.BossEntries;

        // Build a unified sorted row list: (isBoss, originalIndex, displayTime)
        var mergedRows = playerEntries
            .Select((e, i) => (IsBoss: false, Idx: i, Time: e.TimeOffsetSec))
            .Concat(bossEntries.Select((b, i) => (IsBoss: true, Idx: i, Time: b.CastStartSec)))
            .OrderBy(r => r.Time)
            .ToList();

        if (ImGui.BeginTable("##entries", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, -1)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Time (s)",   ImGuiTableColumnFlags.WidthFixed, 78);
            ImGui.TableSetupColumn("Ability",    ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Freq",       ImGuiTableColumnFlags.WidthFixed, 48);
            ImGui.TableSetupColumn("##edit",     ImGuiTableColumnFlags.WidthFixed, 42);
            ImGui.TableSetupColumn("##del",      ImGuiTableColumnFlags.WidthFixed, 42);
            ImGui.TableHeadersRow();

            int toDeletePlayer = -1;
            int toDeleteBoss   = -1;
            var bossColor      = new Vector4(1f, 0.45f, 0.35f, 1f);   // warm red for boss rows

            foreach (var row in mergedRows)
            {
                ImGui.TableNextRow();

                if (row.IsBoss)
                {
                    var b = bossEntries[row.Idx];

                    ImGui.TableSetColumnIndex(0);
                    var timeStr = b.CastEndSec > b.CastStartSec
                        ? $"{b.CastStartSec:F1}-{b.CastEndSec:F1}"
                        : $"{b.CastStartSec:F1}";
                    ImGui.TextColored(bossColor, timeStr);

                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextColored(bossColor, $"\u2694 {b.AbilityName}");  // ⚔ prefix

                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextDisabled("--");

                    ImGui.TableSetColumnIndex(3);
                    if (ImGui.SmallButton($"Edit##b{row.Idx}"))
                    {
                        editingEntryIndex  = row.Idx;
                        editingEntryIsBoss = true;
                        editEntryTime      = (float)b.CastStartSec;
                        editEntryEndTime   = (float)b.CastEndSec;
                        editEntryName      = b.AbilityName;
                        ImGui.OpenPopup("##EditEntry");
                    }

                    ImGui.TableSetColumnIndex(4);
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.1f, 0.1f, 1f));
                    if (ImGui.SmallButton($"Del##b{row.Idx}"))
                        toDeleteBoss = row.Idx;
                    ImGui.PopStyleColor();
                }
                else
                {
                    var e = playerEntries[row.Idx];

                    // Highlight rows with recast conflicts
                    if (conflictedPlayerIndices.Contains(row.Idx))
                    {
                        var conflictBg = ImGui.GetColorU32(new Vector4(0.8f, 0.15f, 0.15f, 0.35f));
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, conflictBg);
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, conflictBg);
                    }

                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(FormatCsvTime(e.TimeOffsetSec));
                    if (conflictedPlayerIndices.Contains(row.Idx) &&
                        conflictReasons.TryGetValue(row.Idx, out var conflictTip))
                    {
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(conflictTip);
                    }

                    ImGui.TableSetColumnIndex(1);
                    ImGui.Text(e.AbilityName);

                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text($"{e.Frequency * 100:F0}%");

                    ImGui.TableSetColumnIndex(3);
                    if (ImGui.SmallButton($"Edit##p{row.Idx}"))
                    {
                        editingEntryIndex  = row.Idx;
                        editingEntryIsBoss = false;
                        editEntryTime      = (float)e.TimeOffsetSec;
                        editEntryName      = e.AbilityName;
                        editEntryFrequency = (float)e.Frequency;

                        // Build ability options from all timelines for the same spec:
                        // search both the API-fetched TimelineStore and saved CustomTimelines.
                        var specKey = editSpecName;
                        var storeAbilities = plugin.TimelineStore.GetAllTimelines()
                            .Where(t => t.SpecName == specKey)
                            .SelectMany(t => t.Entries)
                            .Select(t => t.AbilityName);
                        var customAbilities = plugin.Configuration.CustomTimelines.Values
                            .Where(t => t.SpecName == specKey)
                            .SelectMany(t => t.Entries)
                            .Select(t => t.AbilityName);
                        // Always include entries from the timeline currently being edited.
                        var thisTimelineAbilities = editingTimeline?.Entries
                            .Select(t => t.AbilityName) ?? [];
                        editEntryAbilityOptions = storeAbilities
                            .Concat(customAbilities)
                            .Concat(thisTimelineAbilities)
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Distinct()
                            .OrderBy(n => n)
                            .ToList();
                        if (editEntryAbilityOptions.Count == 0)
                            editEntryAbilityOptions = [editEntryName];
                        editEntryAbilityIdx = Math.Max(0, editEntryAbilityOptions.IndexOf(editEntryName));
                        ImGui.OpenPopup("##EditEntry");
                    }

                    ImGui.TableSetColumnIndex(4);
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.1f, 0.1f, 1f));
                    if (ImGui.SmallButton($"Del##p{row.Idx}"))
                        toDeletePlayer = row.Idx;
                    ImGui.PopStyleColor();
                }
            }

            // Deletions (after iteration)
            if (toDeletePlayer >= 0)
            {
                playerEntries.RemoveAt(toDeletePlayer);
                if (!editingEntryIsBoss && editingEntryIndex == toDeletePlayer)
                    editingEntryIndex = -1;
                customEditorDirty = true;
            }
            if (toDeleteBoss >= 0)
            {
                bossEntries.RemoveAt(toDeleteBoss);
                if (editingEntryIsBoss && editingEntryIndex == toDeleteBoss)
                    editingEntryIndex = -1;
                customEditorDirty = true;
            }

            // Shared edit popup — fields adapt based on editingEntryIsBoss
            if (ImGui.BeginPopup("##EditEntry") && editingEntryIndex >= 0)
            {
                if (editingEntryIsBoss && editingEntryIndex < bossEntries.Count)
                {
                    ImGui.Text("Edit Boss Cast");
                    ImGui.Separator();
                    ImGui.SetNextItemWidth(160);
                    ImGui.InputText("Name##bname", ref editEntryName, 128);
                    ImGui.SetNextItemWidth(110);
                    ImGui.InputFloat("Start (s)##bstart", ref editEntryTime, 0.1f, 1f, "%.2f");
                    ImGui.SetNextItemWidth(110);
                    ImGui.InputFloat("End (s)##bend",   ref editEntryEndTime, 0.1f, 1f, "%.2f");
                    if (ImGui.Button("Apply##bapply"))
                    {
                        var b = bossEntries[editingEntryIndex];
                        b.AbilityName  = editEntryName;
                        b.CastStartSec = editEntryTime;
                        b.CastEndSec   = editEntryEndTime;
                        customEditorDirty = true;
                        ImGui.CloseCurrentPopup();
                    }
                }
                else if (!editingEntryIsBoss && editingEntryIndex < playerEntries.Count)
                {
                    ImGui.Text("Edit Player Entry");
                    ImGui.Separator();
                    if (editEntryAbilityOptions.Count > 1)
                    {
                        ImGui.SetNextItemWidth(220);
                        if (ImGui.Combo("Name##pname", ref editEntryAbilityIdx, editEntryAbilityOptions, -1))
                            editEntryName = editEntryAbilityOptions[editEntryAbilityIdx];
                    }
                    else
                    {
                        ImGui.SetNextItemWidth(220);
                        ImGui.InputText("Name##pname", ref editEntryName, 128);
                    }
                    ImGui.SetNextItemWidth(110);
                    ImGui.InputFloat("Time (s)##ptime", ref editEntryTime, 1.0f, 5f, "%.1f");
                    ImGui.SetNextItemWidth(110);
                    ImGui.SliderFloat("Frequency##pfreq", ref editEntryFrequency, 0f, 1f, "%.2f");
                    if (ImGui.Button("Apply##papply"))
                    {
                        var e = playerEntries[editingEntryIndex];
                        e.AbilityName    = editEntryName;
                        e.TimeOffsetSec  = editEntryTime;
                        e.Frequency      = editEntryFrequency;
                        customEditorDirty = true;
                        ImGui.CloseCurrentPopup();
                    }
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel##cancel"))
                    ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }

            ImGui.EndTable();
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// Rebuilds conflictedPlayerIndices and conflictReasons for the given timeline.
    /// Uses a per-cooldown-group charge queue: each use spends one charge; a charge
    /// returns after RecastSec seconds. Conflict = no charges available at use time.
    /// Only abilities with recast >= 5 s are checked (excludes GCDs).
    /// </summary>
    private void RebuildConflicts(AggregatedTimeline tl)
    {
        conflictedPlayerIndices.Clear();
        conflictReasons.Clear();

        // group → (sorted list of times when each spent charge recharges, last-use display name, last-use time)
        var groupState = new Dictionary<byte, (List<double> RechargeQueue, string Name, double Time)>();

        var sorted = tl.Entries
            .Select((e, i) => (Index: i, Entry: e))
            .OrderBy(x => x.Entry.TimeOffsetSec)
            .ToList();

        foreach (var (idx, entry) in sorted)
        {
            var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
            if (info == null || info.RecastSec < 5.0) continue;  // skip GCDs / unmapped abilities

            var group      = info.CooldownGroup;
            var maxCharges = info.MaxCharges;
            var now        = entry.TimeOffsetSec;

            if (!groupState.TryGetValue(group, out var state))
                state = ([], string.Empty, 0);

            // Remove charges that have recharged by now
            state.RechargeQueue.RemoveAll(t => t <= now);

            if (state.RechargeQueue.Count >= maxCharges)
            {
                // All charges still on cooldown — this use is a conflict
                conflictedPlayerIndices.Add(idx);
                var earliestAvail = state.RechargeQueue.Min();
                var overlapSec    = earliestAvail - now;
                var triggerLabel  = string.IsNullOrWhiteSpace(state.Name) || state.Name == entry.AbilityName
                    ? $"previous use at {FormatCsvTime(state.Time)}"
                    : $"'{state.Name}' at {FormatCsvTime(state.Time)} (shared recast group)";
                conflictReasons[idx] =
                    $"Recast conflict: {overlapSec:F1}s too early — {triggerLabel}" +
                    $" (recast: {info.RecastSec:F0}s, charges: {maxCharges})";
            }

            // Spend a charge: schedule it to return after RecastSec
            state.RechargeQueue.Add(now + info.RecastSec);
            state.RechargeQueue.Sort();

            var displayName = string.IsNullOrWhiteSpace(entry.AbilityName) ? info.Name : entry.AbilityName;
            groupState[group] = (state.RechargeQueue, displayName, now);
        }
    }

    private void SelectCustomTimeline(string key, AggregatedTimeline tl)
    {
        selectedCustomKey  = key;
        editingTimeline    = tl;
        editEncounterName  = tl.EncounterName;
        editSpecName       = tl.SpecName;
        editDurationSec    = (float)(tl.AverageDurationMs / 1000.0);
        editParseCount     = tl.ParseCount;
        editDeltaMs        = tl.DeltaMs;
        editingEntryIndex  = -1;
        customEditorDirty  = false;

        // Sync dropdown indices for encounter and spec
        var flatEncounters = zones.SelectMany(z => z.Encounters).ToList();
        editEncounterIdx = Math.Max(0, flatEncounters.FindIndex(e => e.Name == tl.EncounterName));
        editSpecIdx      = Math.Max(0, allSpecNames.IndexOf(tl.SpecName));

        // Backfill EncounterId for old timelines that were saved before this field was set.
        // We don't mark dirty — the fix is silent and will persist on the next explicit Save.
        if (tl.EncounterId == 0 &&
            editEncounterIdx >= 0 && editEncounterIdx < flatEncounters.Count)
        {
            tl.EncounterId = flatEncounters[editEncounterIdx].Id;
            // Rebuild zone map so the overlay can pick up this timeline immediately
            plugin.EncounterTracker.RebuildZoneMappings();
        }
    }

    private void SaveEditingTimeline()
    {
        if (editingTimeline == null || selectedCustomKey == null)
            return;

        // Re-key if name changed (key stays same — keys are immutable after copy)
        plugin.Configuration.CustomTimelines[selectedCustomKey] = editingTimeline;
        plugin.SaveConfig();
        customEditorDirty = false;

        // Refresh zone → encounter mappings so the overlay activates immediately
        plugin.EncounterTracker.RebuildZoneMappings();
    }

    // ── Report Importer ──

    /// <summary>Split CamelCase job name into display name: "BlackMage" → "Black Mage".</summary>
    private static string SplitCamelCase(string s)
        => string.IsNullOrEmpty(s) ? s : Regex.Replace(s, "(?<=[a-z])(?=[A-Z])", " ");

    private void DrawReportImporter()
    {
        if (!ImGui.CollapsingHeader("From FFLogs Report##riHeader"))
            return;

        ImGui.SetNextItemWidth(300);
        ImGui.InputText("Report URL##riUrl", ref riUrl, 256);
        ImGui.SameLine();

        var canFetch = !riFetching && !riImporting && !string.IsNullOrWhiteSpace(riUrl);
        if (!canFetch) ImGui.BeginDisabled();
        if (ImGui.Button("Load Report##riFetch") && canFetch)
            StartReportFetch();
        if (!canFetch) ImGui.EndDisabled();

        if (riFetching)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Fetching...");
        }

        if (!string.IsNullOrEmpty(riStatus))
        {
            if (riStatusIsError)
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), riStatus);
            else
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.6f, 1f), riStatus);
        }

        if (riFlights.Count == 0) return;

        // Fight selector
        ImGui.SetNextItemWidth(320);
        var fightPreview = riSelectedFight >= 0 && riSelectedFight < riFlights.Count
            ? riFlights[riSelectedFight].Name
            : "Select fight...";
        if (ImGui.BeginCombo("Fight##riCombo", fightPreview))
        {
            for (var i = 0; i < riFlights.Count; i++)
            {
                var f   = riFlights[i];
                var dur = TimeSpan.FromMilliseconds(f.DurationMs);
                // Show fight ID so the user can cross-reference with the URL's ?fight=N
                if (ImGui.Selectable($"[{f.Id}]  {f.Name}  ({dur:m\\:ss})##{i}", i == riSelectedFight))
                    riSelectedFight = i;
            }
            ImGui.EndCombo();
        }

        if (riPlayers.Count == 0) return;

        // Player list — show actor ID so user can cross-reference with URL's source=N
        ImGui.TextUnformatted("Players  (ID matches ?source=N in the FFLogs URL):");
        ImGui.BeginChild("##riPlayerList", new Vector2(0, 120), true);
        for (var i = 0; i < riPlayers.Count; i++)
        {
            var p          = riPlayers[i];
            var jobDisplay = SplitCamelCase(p.SubType);
            if (ImGui.Selectable($"[{p.Id}]  {p.Name}  ({jobDisplay})##{i}", i == riSelectedPlayer))
                riSelectedPlayer = i;
        }
        ImGui.EndChild();

        var canCreate = !riImporting && !riFetching
            && riSelectedFight  >= 0 && riSelectedFight  < riFlights.Count
            && riSelectedPlayer >= 0 && riSelectedPlayer < riPlayers.Count;
        if (!canCreate) ImGui.BeginDisabled();
        if (ImGui.Button("Create Timeline##riCreate") && canCreate)
            StartReportImport();
        if (!canCreate) ImGui.EndDisabled();

        if (riImporting)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Importing...");
        }
    }

    private void StartReportFetch()
    {
        var m = Regex.Match(riUrl, @"fflogs\.com/reports/([A-Za-z0-9]+)");
        if (!m.Success)
        {
            riStatus        = "Invalid FFLogs URL. Expected: fflogs.com/reports/REPORTCODE";
            riStatusIsError = true;
            return;
        }

        riReportCode    = m.Groups[1].Value;
        riStatus        = string.Empty;
        riStatusIsError = false;
        riFlights        = [];
        riPlayers        = [];
        riAbilityLookup  = [];
        riSelectedFight  = -1;
        riSelectedPlayer = -1;
        riFetching       = true;

        riCts?.Cancel();
        riCts = new CancellationTokenSource();
        var ct   = riCts.Token;
        var code = riReportCode;

        Task.Run(async () =>
        {
            try
            {
                var (fights, players, abilities) = await plugin.FFLogsClient.GetReportInfoAsync(code, ct);
                riFlights        = fights;
                riPlayers        = players;
                riAbilityLookup  = abilities;
                riStatus         = fights.Count == 0
                    ? "No kills found in this report."
                    : $"Found {fights.Count} kill(s) and {players.Count} player(s).";
                riStatusIsError  = fights.Count == 0;
                if (fights.Count == 1) riSelectedFight = 0;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                riStatus        = $"Error: {ex.Message}";
                riStatusIsError = true;
            }
            finally { riFetching = false; }
        });
    }

    private void StartReportImport()
    {
        if (riSelectedFight  < 0 || riSelectedFight  >= riFlights.Count)  return;
        if (riSelectedPlayer < 0 || riSelectedPlayer >= riPlayers.Count)   return;

        var fight         = riFlights[riSelectedFight];
        var player        = riPlayers[riSelectedPlayer];
        var code          = riReportCode;
        var abilityLookup = riAbilityLookup;
        var jobDisplay    = SplitCamelCase(player.SubType);

        riImporting     = true;
        riStatus        = "Fetching cast events...";
        riStatusIsError = false;

        riCts?.Cancel();
        riCts = new CancellationTokenSource();
        var ct = riCts.Token;

        Task.Run(async () =>
        {
            try
            {
                var events = await plugin.FFLogsClient.GetCastEventsForPlayerAsync(
                    code, fight.Id, fight.StartTime, fight.EndTime, player.Id, abilityLookup, ct);

                if (events.Count == 0)
                {
                    riStatus        = "No cast events found for that player.";
                    riStatusIsError = true;
                    return;
                }

                // Build timeline entries directly from the exact cast timestamps.
                // We intentionally bypass the 5-second bucket aggregator here:
                // that system is designed for multi-parse averaging and would
                // merge two casts within the same 5s window into one averaged entry,
                // producing timestamps that don't match the actual log.
                var entries = events
                    .OrderBy(e => e.Timestamp)
                    .Select(e => new TimelineEntry
                    {
                        TimeOffsetSec = e.Timestamp / 1000.0,
                        AbilityId     = e.AbilityGameID,
                        AbilityName   = e.AbilityName,
                        AbilityIcon   = e.AbilityIcon,
                        Frequency     = 1.0,
                        AverageUses   = 1.0,
                    })
                    .ToList();

                var timeline = new AggregatedTimeline
                {
                    EncounterId       = 0,
                    EncounterName     = fight.Name,
                    SpecName          = jobDisplay,
                    AverageDurationMs = fight.DurationMs,
                    ParseCount        = 1,
                    Entries           = entries,
                    BossEntries       = [],
                };

                var key = $"report_{code}_{fight.Id}_{player.Name}";
                plugin.Configuration.CustomTimelines[key] = timeline;
                plugin.SaveConfig();

                riStatus        = $"Saved \"{fight.Name} / {player.Name} ({jobDisplay})\" ({events.Count} casts).";
                riStatusIsError = false;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                riStatus        = $"Import error: {ex.Message}";
                riStatusIsError = true;
            }
            finally { riImporting = false; }
        });
    }

    private void RebuildSkillVisibility(AggregatedTimeline tl)
    {
        var key = TimelineDatabase.MakeKey(tl.EncounterId, tl.SpecName);
        skillVisibility.Clear();
        var hidden = plugin.Configuration.HiddenAbilities.GetValueOrDefault(key);
        foreach (var (id, _) in tl.Entries.Select(e => (e.AbilityId, e.AbilityName)).Distinct())
            skillVisibility[id] = hidden == null || !hidden.Contains(id);
    }

    private float GetAbilityThreshold(int abilityId)
    {
        if (currentTimeline == null)
            return plugin.Configuration.OverlayFreqThreshold;

        var key = TimelineDatabase.MakeKey(currentTimeline.EncounterId, currentTimeline.SpecName);
        if (plugin.Configuration.AbilityFreqThresholds.TryGetValue(key, out var perAbility) &&
            perAbility.TryGetValue(abilityId, out var custom))
            return custom;

        return plugin.Configuration.OverlayFreqThreshold;
    }


    // ── Fight / job selectors ──

    private void DrawSelectors()
    {
        // If we haven't loaded selectors and have no cached DB data, show guidance
        if (!selectorsLoaded && zones.Count == 0)
        {
            var db = plugin.TimelineStore.Load();
            RebuildSelectorsFromDb(db);
        }

        if (zones.Count == 0 || allSpecNames.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(plugin.Configuration.FFLogsClientId))
            {
                ImGui.TextDisabled("Set your FFLogs API credentials in Config to get started.");
            }
            else if (selectorsError != null)
            {
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"Error: {selectorsError}");
                if (ImGui.Button("Retry"))
                {
                    selectorsError = null;
                    isFetchingSelectors = true;
                    Task.Run(FetchSelectorsAsync);
                }
            }
            else if (!selectorsLoaded)
            {
                // Credentials may have been saved after the window was already open.
                // Kick off the fetch here if nothing is running yet.
                if (!isFetchingSelectors)
                {
                    isFetchingSelectors = true;
                    Task.Run(FetchSelectorsAsync);
                }
                ImGui.TextDisabled("Loading fight and job lists from FFLogs...");
            }
            else
            {
                ImGui.TextDisabled("No data available. Check your API credentials in Config.");
            }
            return;
        }

        // Zone selector
        var zoneNames = zones.Select(z => z.Name).ToList();
        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("Zone", ref selectedZone, zoneNames, -1))
        {
            selectedEncounter = 0;
        }

        ImGui.SameLine();

        // Encounter (Fight) selector
        var encounters = selectedZone < zones.Count ? zones[selectedZone].Encounters : [];
        var encounterNames = encounters.Select(e => e.Name).ToList();
        ImGui.SetNextItemWidth(200);
        if (encounterNames.Count > 0)
        {
            ImGui.Combo("Fight", ref selectedEncounter, encounterNames, -1);
        }
        else
        {
            var noFights = 0;
            ImGui.Combo("Fight", ref noFights, new List<string> { "(none)" }, -1);
        }

        ImGui.SameLine();

        // Job selector
        ImGui.SetNextItemWidth(200);
        ImGui.Combo("Job", ref selectedSpec, allSpecNames, -1);

        // Load button — loads from local cache only (no API call)
        // Determine availability and reason for disabled state
        bool canLoad;
        string loadDisabledReason;
        if (!selectorsLoaded || zones.Count == 0 || allSpecNames.Count == 0)
        {
            canLoad = false;
            loadDisabledReason = "No fight list loaded yet.\nUse \"Update from FFLogs\" to load it first.";
        }
        else if (selectedZone < zones.Count &&
                 selectedEncounter < zones[selectedZone].Encounters.Count &&
                 selectedSpec < allSpecNames.Count)
        {
            var chkEncId = zones[selectedZone].Encounters[selectedEncounter].Id;
            var chkSpec  = allSpecNames[selectedSpec];
            var chkKey   = TimelineDatabase.MakeKey(chkEncId, chkSpec);
            canLoad = plugin.TimelineStore.GetTimeline(chkEncId, chkSpec) != null
                   || plugin.Configuration.CustomTimelines.ContainsKey(chkKey);
            loadDisabledReason = canLoad ? string.Empty
                : $"No cached data for:\n  {zones[selectedZone].Encounters[selectedEncounter].Name} / {chkSpec}\n\nUse \"Update from FFLogs\" to fetch it.";
        }
        else
        {
            canLoad = false;
            loadDisabledReason = "Make a valid fight and job selection first.";
        }

        if (!canLoad) ImGui.BeginDisabled();
        if (ImGui.Button("Load Fetched Logs", default))
            LoadTimeline();
        if (!canLoad) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            if (canLoad)
            {
                ImGui.Text("Load from local cache (previously fetched data).");
                ImGui.Text("Use Update from FFLogs to fetch fresh data from FFLogs.");
            }
            else
            {
                ImGui.TextDisabled(loadDisabledReason);
            }
            ImGui.EndTooltip();
        }

        ImGui.SameLine();

        // Update from FFLogs — same row as Load Fetched Logs
        var canUpdate = !isUpdating && selectorsLoaded && zones.Count > 0 && allSpecNames.Count > 0;
        if (!canUpdate) ImGui.BeginDisabled();
        if (ImGui.Button("Update from FFLogs", default))
        {
            StartUpdate();
        }
        if (!canUpdate) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("Fetch top 100 parses for the selected fight and job.");
            ImGui.Text("This may take a minute depending on the fight.");
            ImGui.EndTooltip();
        }

        ImGui.SameLine();

        var hasTimeline = currentTimeline != null;
        if (ImGui.Button("Preview Timeline", default) && hasTimeline)
        {
            plugin.OverlayWindow.StartPreview(currentTimeline!);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            if (hasTimeline)
                ImGui.Text("Preview the timeline overlay (starts paused).");
            else
                ImGui.Text("Load a timeline first to preview the overlay.");
            ImGui.EndTooltip();
        }
    }

    private void ClearTimeline()
    {
        // Clear from local disk cache
        if (selectedZone < zones.Count && allSpecNames.Count > 0 && selectedSpec < allSpecNames.Count)
        {
            var encounters = zones[selectedZone].Encounters;
            if (selectedEncounter < encounters.Count)
            {
                var encounterId = encounters[selectedEncounter].Id;
                var specName = allSpecNames[selectedSpec];
                plugin.TimelineStore.RemoveTimeline(encounterId, specName);

                // Also remove from custom timelines
                var key = TimelineDatabase.MakeKey(encounterId, specName);
                plugin.Configuration.CustomTimelines.Remove(key);
                plugin.SaveConfig();

                updateStatus = $"Cleared cached data for {encounters[selectedEncounter].Name} / {specName}.";
            }
        }

        currentTimeline = null;
        skillVisibility.Clear();
    }

    private void RebuildSelectorsFromDb(TimelineDatabase db)
    {
        var zoneMap = new Dictionary<int, Zone>();
        var specs = new HashSet<string>();

        foreach (var (key, tl) in db.Timelines)
        {
            if (!zoneMap.ContainsKey(tl.EncounterId))
            {
                zoneMap[tl.EncounterId] = new Zone
                {
                    Id = tl.EncounterId,
                    Name = tl.EncounterName,
                    Encounters = [new Encounter { Id = tl.EncounterId, Name = tl.EncounterName }],
                };
            }
            specs.Add(tl.SpecName);
        }

        if (zones.Count == 0 && zoneMap.Count > 0)
            zones = [.. zoneMap.Values.OrderBy(z => z.Name)];

        if (allSpecNames.Count == 0 && specs.Count > 0)
            allSpecNames = [.. specs.OrderBy(s => s)];
    }

    private void LoadTimeline()
    {
        if (selectedZone >= zones.Count || allSpecNames.Count == 0)
            return;

        var encounters = zones[selectedZone].Encounters;
        if (selectedEncounter >= encounters.Count)
            return;

        var encounterId = encounters[selectedEncounter].Id;
        var specName = allSpecNames[selectedSpec];

        currentTimeline = plugin.TimelineStore.GetTimeline(encounterId, specName);

        // Also check custom timelines
        var key = TimelineDatabase.MakeKey(encounterId, specName);
        if (currentTimeline == null && plugin.Configuration.CustomTimelines.TryGetValue(key, out var custom))
            currentTimeline = custom;

        // Build skill visibility map — includes all abilities; threshold filtering is handled at draw time
        skillVisibility.Clear();
        if (currentTimeline != null)
        {
            var hidden = plugin.Configuration.HiddenAbilities.GetValueOrDefault(key);
            var uniqueAbilities = currentTimeline.Entries
                .Select(e => (e.AbilityId, e.AbilityName))
                .Distinct()
                .OrderBy(a => a.AbilityName);

            foreach (var (id, _) in uniqueAbilities)
                skillVisibility[id] = hidden == null || !hidden.Contains(id);
        }
        else
        {
            updateStatus = "No cached data for this fight/job. Click Update Logs to fetch it.";
        }
    }

    // ── Skill filters ──

    private void DrawSkillFilters()
    {
        if (currentTimeline == null)
            return;

        if (!ImGui.TreeNode("Skill Filters"))
            return;

        var cfg = plugin.Configuration;
        var timelineKey = TimelineDatabase.MakeKey(currentTimeline.EncounterId, currentTimeline.SpecName);

        // All abilities, unfiltered — user needs to reach any ability to set its threshold
        var uniqueAbilities = currentTimeline.Entries
            .Select(e => (e.AbilityId, e.AbilityName))
            .Distinct()
            .OrderBy(a => a.AbilityName)
            .ToList();

        // Bulk visibility buttons
        if (ImGui.Button("Show All", default))
        {
            foreach (var (id, _) in uniqueAbilities)
                skillVisibility[id] = true;
            SaveSkillFilters();
        }
        ImGui.SameLine();
        if (ImGui.Button("Hide All", default))
        {
            foreach (var (id, _) in uniqueAbilities)
                skillVisibility[id] = false;
            SaveSkillFilters();
        }
        ImGui.SameLine();
        if (ImGui.Button("Reset Thresholds", default))
        {
            cfg.AbilityFreqThresholds.Remove(timelineKey);
            plugin.SaveConfig();
        }

        ImGui.Separator();

        cfg.AbilityFreqThresholds.TryGetValue(timelineKey, out var perAbility);

        // Fixed-height scrollable child so the list doesn't take over the screen
        var listHeight = Math.Min(uniqueAbilities.Count * 22f + 8f, 220f);
        ImGui.BeginChild("##SkillFilterList", new Vector2(0, listHeight), false);

        foreach (var (id, name) in uniqueAbilities)
        {
            var hasCustom = perAbility != null && perAbility.ContainsKey(id);
            var thresh    = GetAbilityThreshold(id);
            var isOpen    = expandedSkillNodes.Contains(id);

            // Visibility checkbox — auto-save on every change so state survives reloads
            var visible = skillVisibility.GetValueOrDefault(id, true);
            if (ImGui.Checkbox($"##{id}_vis", ref visible))
            {
                skillVisibility[id] = visible;
                SaveSkillFilters();
            }
            ImGui.SameLine();

            // Selectable label — activates on mouse-UP so slider drags never interfere
            var arrow     = isOpen ? "v" : ">";
            var indicator = hasCustom ? $"  [{thresh * 100:F0}%]" : $"  ({thresh * 100:F0}%)";
            if (ImGui.Selectable($"{arrow} {name}{indicator}##{id}_sel", isOpen,
                ImGuiSelectableFlags.None, default))
            {
                if (isOpen) expandedSkillNodes.Remove(id);
                else        expandedSkillNodes.Add(id);
            }

            // Slider row — only visible when expanded
            if (isOpen)
            {
                ImGui.Indent(28f);
                var threshVal = thresh;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat($"##thresh_{id}", ref threshVal, 0f, 1f, $"{threshVal * 100:F0}%%"))
                {
                    if (!cfg.AbilityFreqThresholds.ContainsKey(timelineKey))
                        cfg.AbilityFreqThresholds[timelineKey] = [];
                    cfg.AbilityFreqThresholds[timelineKey][id] = threshVal;
                    plugin.SaveConfig();
                }
                if (hasCustom)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Reset##{id}"))
                    {
                        perAbility!.Remove(id);
                        if (perAbility.Count == 0)
                            cfg.AbilityFreqThresholds.Remove(timelineKey);
                        plugin.SaveConfig();
                    }
                }
                ImGui.Unindent(28f);
            }
        }

        ImGui.EndChild();
        ImGui.TreePop();
    }

    private void SaveSkillFilters()
    {
        if (currentTimeline == null)
            return;

        var key = TimelineDatabase.MakeKey(currentTimeline.EncounterId, currentTimeline.SpecName);
        var hidden = new HashSet<int>();
        foreach (var (id, visible) in skillVisibility)
        {
            if (!visible)
                hidden.Add(id);
        }

        plugin.Configuration.HiddenAbilities[key] = hidden;
        plugin.SaveConfig();
    }

    // ── Timeline drawing ──

    // Layout constants
    private const float PixelsPerSec  = 6.0f;
    private const float LabelWidth    = 140.0f;
    private const float RulerHeight   = 22.0f;
    private const float BossRowHeight = 22.0f;

    private void DrawTimeline()
    {
        if (currentTimeline == null)
            return;

        var cfg      = plugin.Configuration;
        var iconSize = cfg.MainIconSize * cfg.MainIconScale;
        var rowHeight = iconSize + 4f;

        if (ImGui.SmallButton("Copy"))
            CopyCurrentTimeline();
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("Copy this timeline to Custom Timelines for manual editing.");
            ImGui.EndTooltip();
        }
        ImGui.SameLine();
        ImGui.Text($"{currentTimeline.EncounterName} - {currentTimeline.SpecName}");
        ImGui.SameLine();
        ImGui.TextDisabled($"({currentTimeline.ParseCount} parses, avg {currentTimeline.AverageDurationMs / 1000.0:F1}s)");

        // Gather visible abilities — per-ability threshold takes priority over global
        var visibleEntries = currentTimeline.Entries
            .Where(e => e.Frequency >= GetAbilityThreshold(e.AbilityId))
            .Where(e => skillVisibility.GetValueOrDefault(e.AbilityId, true))
            .ToList();

        if (visibleEntries.Count == 0)
        {
            ImGui.TextDisabled("No visible skills. Adjust filters above.");
            return;
        }

        // Build row list: one row per unique ability, sorted by earliest appearance
        var abilityRows = visibleEntries
            .GroupBy(e => e.AbilityId)
            .Select(g => new
            {
                AbilityId   = g.Key,
                AbilityName = g.First().AbilityName,
                FirstTime   = g.Min(e => e.TimeOffsetSec),
                Entries     = g.ToList(),
            })
            .OrderBy(a => a.FirstTime)
            .ToList();

        var durationSec = currentTimeline.AverageDurationMs / 1000.0;
        if (durationSec <= 0) durationSec = 600;

        var hasBossRows    = currentTimeline.BossEntries.Count > 0;
        var bossRowsHeight = hasBossRows ? BossRowHeight + 4 : 0;

        var timelineWidth = (float)(durationSec * PixelsPerSec);
        var totalWidth    = LabelWidth + timelineWidth + 20;
        var totalHeight   = RulerHeight + abilityRows.Count * rowHeight + bossRowsHeight + 20;

        var avail = ImGui.GetContentRegionAvail();
        if (!ImGui.BeginChild("##Timeline", avail, true, ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.EndChild();
            return;
        }

        // Reserve space for scrolling
        ImGui.Dummy(new Vector2(totalWidth, totalHeight));

        var drawList = ImGui.GetWindowDrawList();
        var origin   = ImGui.GetCursorScreenPos();
        // Adjust up past the dummy
        origin.Y -= totalHeight;

        var timeOriginX = origin.X + LabelWidth;

        // ── Draw time ruler ──
        var rulerY     = origin.Y;
        var rulerColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        var textColor  = ImGui.GetColorU32(ImGuiCol.Text);
        var gridBottom = origin.Y + RulerHeight + abilityRows.Count * rowHeight;

        for (var t = 0.0; t < durationSec; t += 10.0)
        {
            var x       = timeOriginX + (float)(t * PixelsPerSec);
            var minutes = (int)(t / 60);
            var seconds = (int)(t % 60);

            // Vertical grid line
            drawList.AddLine(
                new Vector2(x, rulerY + RulerHeight),
                new Vector2(x, gridBottom),
                ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.3f, 0.3f)));

            // Tick + label
            drawList.AddLine(
                new Vector2(x, rulerY + RulerHeight - 6),
                new Vector2(x, rulerY + RulerHeight),
                rulerColor);
            drawList.AddText(new Vector2(x + 2, rulerY + 2), textColor, $"{minutes}:{seconds:D2}");
        }

        // ── Draw ability rows ──
        for (var rowIdx = 0; rowIdx < abilityRows.Count; rowIdx++)
        {
            var row  = abilityRows[rowIdx];
            var rowY = origin.Y + RulerHeight + rowIdx * rowHeight;

            // Alternating row background
            if (rowIdx % 2 == 0)
            {
                drawList.AddRectFilled(
                    new Vector2(origin.X, rowY),
                    new Vector2(origin.X + totalWidth, rowY + rowHeight),
                    ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 0.4f)));
            }

            // Row label (ability name) — clipped to label area
            var labelText = row.AbilityName;
            if (string.IsNullOrEmpty(labelText)) labelText = $"#{row.AbilityId}";
            drawList.AddText(new Vector2(origin.X + 4, rowY + (rowHeight - 13f) * 0.5f), textColor, labelText);

            // Draw separator line between label and timeline
            drawList.AddLine(
                new Vector2(timeOriginX - 2, rowY),
                new Vector2(timeOriginX - 2, rowY + rowHeight),
                ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 0.5f)));

            // Draw icons along the row
            foreach (var entry in row.Entries)
            {
                var centerX = timeOriginX + (float)(entry.TimeOffsetSec * PixelsPerSec);
                var iconX   = centerX - iconSize * 0.5f;
                var iconY   = rowY + (rowHeight - iconSize) * 0.5f;
                var iconPos = new Vector2(iconX, iconY);

                var alpha = (float)Math.Clamp(entry.Frequency * cfg.MainIconOpacity, 0.1, 1.0);

                var drawn = entry.AbilityId > 0 && TryDrawActionIcon(drawList, entry.AbilityId, iconPos, iconSize, alpha);
                if (!drawn)
                {
                    // Fallback: colored rect in hue derived from ability ID
                    var hue = (entry.AbilityId % 12) / 12.0f;
                    HsvToRgb(hue, 0.5f, 0.6f, out var cr, out var cg, out var cb);
                    drawList.AddRectFilled(
                        iconPos,
                        iconPos + new Vector2(iconSize, iconSize),
                        ImGui.GetColorU32(new Vector4(cr, cg, cb, alpha * 0.8f)), 2.0f);
                }

                var hitMin = iconPos;
                var hitMax = iconPos + new Vector2(iconSize, iconSize);
                if (ImGui.IsMouseHoveringRect(hitMin, hitMax))
                {
                    ImGui.BeginTooltip();
                    ImGui.Text(entry.AbilityName);
                    ImGui.Separator();
                    ImGui.Text($"Time: {FormatTime(entry.TimeOffsetSec)}");
                    ImGui.Text($"Frequency: {entry.Frequency:P0} of parses");
                    ImGui.Text($"Avg Uses: {entry.AverageUses:F1}x in this window");
                    ImGui.EndTooltip();
                }
            }
        }

        // ── Draw boss attack row ──
        if (hasBossRows)
        {
            var bossRowY = origin.Y + RulerHeight + abilityRows.Count * rowHeight + 2;

            // Row background
            drawList.AddRectFilled(
                new Vector2(origin.X, bossRowY),
                new Vector2(origin.X + totalWidth, bossRowY + BossRowHeight),
                ImGui.GetColorU32(new Vector4(0.25f, 0.05f, 0.05f, 0.5f)));

            // Row label
            drawList.AddText(new Vector2(origin.X + 4, bossRowY + 3),
                ImGui.GetColorU32(new Vector4(1f, 0.5f, 0.4f, 1f)), "Boss Attacks");

            // Separator
            drawList.AddLine(
                new Vector2(timeOriginX - 2, bossRowY),
                new Vector2(timeOriginX - 2, bossRowY + BossRowHeight),
                ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 0.5f)));

            foreach (var boss in currentTimeline.BossEntries)
            {
                var startX = timeOriginX + (float)(boss.CastStartSec * PixelsPerSec);
                var endX   = timeOriginX + (float)(boss.CastEndSec   * PixelsPerSec);
                var barW   = Math.Max(endX - startX, 4.0f);

                var barColor = BossAbilityColor(boss.AbilityId);

                var barMin = new Vector2(startX, bossRowY + 2);
                var barMax = new Vector2(startX + barW, bossRowY + BossRowHeight - 2);
                drawList.AddRectFilled(barMin, barMax, barColor, 2.0f);

                // Name label (if bar is wide enough)
                if (barW > 24)
                {
                    drawList.AddText(new Vector2(startX + 2, bossRowY + 4),
                        ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f)), boss.AbilityName);
                }

                if (ImGui.IsMouseHoveringRect(barMin, barMax))
                {
                    ImGui.BeginTooltip();
                    ImGui.Text(boss.AbilityName);
                    ImGui.Separator();
                    ImGui.Text($"Cast start: {FormatTime(boss.CastStartSec)}");
                    if (boss.CastEndSec > boss.CastStartSec)
                        ImGui.Text($"Cast time:  {(boss.CastEndSec - boss.CastStartSec):F1}s (finishes {FormatTime(boss.CastEndSec)})");
                    else
                        ImGui.Text("Instant cast");
                    ImGui.EndTooltip();
                }
            }
        }

        ImGui.EndChild();
    }

    private uint BossAbilityColor(int abilityId)
    {
        var cfg = plugin.Configuration;
        if (cfg.BossBarUseCustomColor)
            return ImGui.GetColorU32(cfg.BossBarColor with { W = 0.85f });

        float[] hues = [0.0f, 0.05f, 0.9f, 0.75f, 0.12f, 0.85f];
        var hue = hues[Math.Abs(abilityId) % hues.Length];
        HsvToRgb(hue, 0.75f, 0.9f, out var br, out var bg, out var bb);
        return ImGui.GetColorU32(new Vector4(br, bg, bb, 0.85f));
    }

    private bool TryDrawActionIcon(ImDrawListPtr dl, int abilityId, Vector2 pos, float size, float alpha)
    {
        if (!iconIdCache.TryGetValue(abilityId, out var iconId))
        {
            iconId = 0;
            try
            {
                var sheet = plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
                if (sheet != null)
                {
                    var row = sheet.GetRowOrDefault((uint)abilityId);
                    if (row.HasValue) iconId = row.Value.Icon;
                }
            }
            catch { /* silently fail */ }
            iconIdCache[abilityId] = iconId;
        }

        if (iconId == 0) return false;

        try
        {
            var wrap = plugin.TextureProvider.GetFromGameIcon(
                new Dalamud.Interface.Textures.GameIconLookup(iconId)).GetWrapOrEmpty();
            if (wrap.Width <= 1) return false;

            dl.AddImage(wrap.Handle, pos, pos + new Vector2(size, size),
                Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha)));
            return true;
        }
        catch { return false; }
    }

    private static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
    {
        var i = (int)(h * 6);
        var f = h * 6 - i;
        var p = v * (1 - s);
        var q = v * (1 - f * s);
        var t = v * (1 - (1 - f) * s);
        switch (i % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
    }

    private static string FormatTime(double seconds)
    {
        var m = (int)(seconds / 60);
        var s = seconds % 60;
        return $"{m}:{s:00.0}";
    }

    // ── Fetch selectors (lightweight — just zones + classes) ──

    private async Task FetchSelectorsAsync()
    {
        try
        {
            var client = plugin.FFLogsClient;

            log.Info("Fetching zone/class lists for selectors...");
            var fetchedZones = await client.GetZonesAsync(CancellationToken.None);
            var fetchedClasses = await client.GetClassesAsync(CancellationToken.None);

            zones = fetchedZones;
            classes = fetchedClasses;
            allSpecNames = classes
                .SelectMany(c => c.Specs)
                .Select(s => s.Name)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            selectorsLoaded = true;
            log.Info("Selectors loaded: {0} zones, {1} specs.", zones.Count, allSpecNames.Count);
            log.Info("Spec names: [{0}]", string.Join("], [", allSpecNames));
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to fetch selectors.");
            selectorsError = ex.Message;
        }
        finally
        {
            isFetchingSelectors = false;
        }
    }

    // ── Update logs (single fight + job) ──

    private void StartUpdate()
    {
        if (isUpdating)
            return;

        if (string.IsNullOrWhiteSpace(plugin.Configuration.FFLogsClientId))
        {
            updateStatus = "Error: FFLogs API credentials not configured. Open Config first.";
            return;
        }

        if (zones.Count == 0 || allSpecNames.Count == 0)
        {
            updateStatus = "Error: Zone/job lists not loaded yet. Please wait or reopen the window.";
            return;
        }

        if (selectedZone >= zones.Count)
            return;

        var encounters = zones[selectedZone].Encounters;
        if (selectedEncounter >= encounters.Count || selectedSpec >= allSpecNames.Count)
            return;

        var encounter = encounters[selectedEncounter];
        var specName = allSpecNames[selectedSpec];

        isUpdating = true;
        updateProgress = 0;
        updateStatus = $"Fetching: {encounter.Name} / {specName}...";
        updateCts = new CancellationTokenSource();

        Task.Run(() => RunUpdateAsync(encounter, specName, updateCts.Token));
    }

    private async Task RunUpdateAsync(Encounter encounter, string specName, CancellationToken ct)
    {
        try
        {
            var client = plugin.FFLogsClient;
            var aggregator = plugin.Aggregator;
            var store = plugin.TimelineStore;

            log.Info("Update started for {0} / {1}.", encounter.Name, specName);

            // Clear old cached data for this fight/job before fetching
            store.RemoveTimeline(encounter.Id, specName);

            // Step 1: Fetch top 100 rankings
            updateStatus = $"Fetching top rankings for {encounter.Name} / {specName}...";
            updateProgress = 0.05f;

            var rankings = await client.GetTopRankingsAsync(encounter.Id, specName, 100, ct);
            if (rankings.Count == 0)
            {
                updateStatus = $"Warning: No rankings found for {encounter.Name} / {specName}.";
                log.Warning("No rankings for {0} / {1}.", encounter.Name, specName);
                return;
            }

            log.Info("Got {0} rankings for {1} / {2}, fetching events...",
                rankings.Count, encounter.Name, specName);

            // Step 2: Fetch cast events in parallel with bounded concurrency
            updateStatus = $"Fetching parse events (0/{rankings.Count})...";
            updateProgress = 0.1f;

            var semaphore = new SemaphoreSlim(MaxConcurrency);
            var parseData = new List<(List<CastEvent> casts, long fightStartMs, long fightEndMs)>();
            var successfulRankings = new List<RankingEntry>();
            var parseLock = new object();
            var completedParses = 0;
            var skippedParses = 0;
            string? firstError = null;

            var tasks = rankings.Select(async ranking =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();

                    log.Debug("Fetching events for {0}#{1}...", ranking.ReportCode, ranking.FightId);
                    var events = await client.GetCastEventsAsync(ranking.ReportCode, ranking.FightId, specName, ct);
                    log.Debug("Got {0} events for {1}#{2}.", events.Count, ranking.ReportCode, ranking.FightId);

                    if (events.Count > 0)
                    {
                        // Timestamps are already fight-relative (0 = fight start).
                        // fightStartMs=0, fightEndMs=max timestamp.
                        var fightEnd = events.Max(e => e.Timestamp);
                        lock (parseLock)
                        {
                            parseData.Add((events, 0, fightEnd));
                            successfulRankings.Add(ranking);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    var count = Interlocked.Increment(ref skippedParses);
                    log.Warning("Skipped parse {0}#{1}: {2}", ranking.ReportCode, ranking.FightId, ex.Message);
                    if (count == 1)
                        firstError = $"{ranking.ReportCode}#{ranking.FightId}: {ex.Message}";
                }
                finally
                {
                    semaphore.Release();
                    var done = Interlocked.Increment(ref completedParses);
                    updateStatus = $"Fetching parse events ({done}/{rankings.Count})...";
                    updateProgress = 0.1f + 0.8f * done / rankings.Count;
                }
            }).ToList();

            await Task.WhenAll(tasks);

            // Step 3: Aggregate
            updateStatus = "Aggregating timeline...";
            updateProgress = 0.95f;

            if (parseData.Count > 0)
            {
                // Fetch boss events from the first successful parse (boss mechanics are deterministic)
                var bossEntries = new List<BossTimelineEntry>();
                if (successfulRankings.Count > 0)
                {
                    try
                    {
                        updateStatus = "Fetching boss attack timeline...";
                        var bossRaw = await client.GetBossCastEventsAsync(
                            successfulRankings[0].ReportCode, successfulRankings[0].FightId, ct);
                        bossEntries = aggregator.AggregateBossEvents(bossRaw);
                        log.Info("Boss timeline: {0} entries.", bossEntries.Count);
                    }
                    catch (Exception ex)
                    {
                        log.Warning("Boss events fetch failed (non-fatal): {0}", ex.Message);
                    }
                }

                var timeline = aggregator.Aggregate(
                    encounter.Id, encounter.Name, specName, parseData);
                timeline.BossEntries = bossEntries;
                store.SaveTimeline(timeline);

                // Rebuild zone → encounter mappings so the EncounterTracker picks up new timelines
                plugin.EncounterTracker.RebuildZoneMappings();

                // Auto-load the result
                currentTimeline = timeline;
                RebuildSkillVisibility();

                updateStatus = $"Done! {encounter.Name} / {specName} — " +
                    $"{parseData.Count} parses aggregated" +
                    (skippedParses > 0 ? $" ({skippedParses} skipped)" : "") + ".";
                log.Info("Saved timeline for {0} / {1} ({2} parses, {3} skipped).",
                    encounter.Name, specName, parseData.Count, skippedParses);
            }
            else
            {
                updateStatus = $"Warning: All {rankings.Count} parses failed for {encounter.Name} / {specName}." +
                    (firstError != null ? $" First error: {firstError}" : "");
                log.Warning("All parses failed for {0} / {1}. First error: {2}",
                    encounter.Name, specName, firstError ?? "unknown");
            }

            updateProgress = 1;
        }
        catch (OperationCanceledException)
        {
            updateStatus = "Update cancelled.";
            log.Info("Update cancelled by user.");
        }
        catch (Exception ex)
        {
            updateStatus = $"Update failed: {ex.Message}";
            log.Error(ex, "Update failed.");
        }
        finally
        {
            isUpdating = false;
        }
    }

    private void RebuildSkillVisibility()
    {
        skillVisibility.Clear();
        if (currentTimeline == null)
            return;

        var key = TimelineDatabase.MakeKey(currentTimeline.EncounterId, currentTimeline.SpecName);
        var hidden = plugin.Configuration.HiddenAbilities.GetValueOrDefault(key);
        var uniqueAbilities = currentTimeline.Entries
            .Select(e => (e.AbilityId, e.AbilityName))
            .Distinct()
            .OrderBy(a => a.AbilityName);

        foreach (var (id, _) in uniqueAbilities)
            skillVisibility[id] = hidden == null || !hidden.Contains(id);
    }

    // ── Copy timeline ──

    private void CopyCurrentTimeline()
    {
        if (currentTimeline == null)
            return;

        var key = TimelineDatabase.MakeKey(currentTimeline.EncounterId, currentTimeline.SpecName);

        // Deep-copy via JSON, then strip entries that are currently filtered out
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(currentTimeline);
        var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<AggregatedTimeline>(json)!;

        // Only keep what is currently visible (respects per-ability thresholds + skill visibility)
        copy.Entries = copy.Entries
            .Where(e => skillVisibility.GetValueOrDefault(e.AbilityId, true))
            .Where(e => e.Frequency >= GetAbilityThreshold(e.AbilityId))
            .ToList();

        plugin.Configuration.CustomTimelines[key] = copy;
        plugin.SaveConfig();
    }

    // ── Config tab ──────────────────────────────────────────────────────

    private void DrawConfigTab()
    {
        if (!cfgInitialized)
        {
            cfgClientId     = plugin.Configuration.FFLogsClientId;
            cfgClientSecret = plugin.Configuration.FFLogsClientSecret;
            cfgInitialized  = true;
        }

        // Collapsible groups first, in the requested order
        if (ImGui.CollapsingHeader("FFLogs Credentials##cfgCreds"))
        {
            ImGui.Indent();
            DrawCfgApiCredentials();
            ImGui.Unindent();
        }
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Overlay Appearance##cfgAppearance"))
        {
            ImGui.Indent();
            DrawCfgOverlayAppearance();
            ImGui.Unindent();
        }
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Main Timeline##cfgMainTL"))
        {
            ImGui.Indent();
            DrawCfgMainTimelineSettings();
            ImGui.Unindent();
        }
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Ability Ants##cfgAnts"))
        {
            ImGui.Indent();
            DrawCfgAntsSettings();
            ImGui.Unindent();
        }
        ImGui.Spacing(); ImGui.Spacing();
        // Non-collapsed features beneath the groups
        DrawCfgOverlaySettings();
        ImGui.Spacing(); ImGui.Spacing();
        DrawCfgResetSection();
        ImGui.Spacing(); ImGui.Spacing();
        ImGui.TextDisabled("ATKTip v0.1.0");
        ImGui.TextDisabled("Timeline data sourced from FFLogs top parses.");
    }

    private void DrawCfgApiCredentials()
    {
        ImGui.TextUnformatted("FFLogs API Credentials");
        ImGui.Separator();

        ImGui.TextWrapped(
            "ATKTip needs an FFLogs API key to fetch parse data. " +
            "This is free and only takes a minute to set up.");
        ImGui.Spacing();

        if (ImGui.TreeNode("How to get your API key (step-by-step)"))
        {
            ImGui.Spacing();
            var bulletColor = new Vector4(0.6f, 0.8f, 1.0f, 1.0f);

            ImGui.TextColored(in bulletColor, "Step 1:");
            ImGui.SameLine();
            ImGui.TextWrapped("Go to https://www.fflogs.com and log in (or create a free account).");
            ImGui.Spacing();
            ImGui.TextColored(in bulletColor, "Step 2:");
            ImGui.SameLine();
            ImGui.TextWrapped("Navigate to https://www.fflogs.com/api/clients");
            ImGui.Spacing();
            ImGui.TextColored(in bulletColor, "Step 3:");
            ImGui.SameLine();
            ImGui.TextWrapped(
                "Click \"Create Client\". Give it any name (e.g. \"ATKTip\"). " +
                "For the redirect URL, enter: http://localhost");
            ImGui.Spacing();
            ImGui.TextColored(in bulletColor, "Step 4:");
            ImGui.SameLine();
            ImGui.TextWrapped(
                "After creating the client, you will see a Client ID and Client Secret. " +
                "Copy both values and paste them into the fields below.");
            ImGui.Spacing();
            ImGui.TextDisabled(
                "Note: Your credentials are stored locally and are only used to " +
                "authenticate with the FFLogs API. They are never shared.");
            ImGui.TreePop();
        }

        ImGui.Spacing();
        ImGui.Separator();

        ImGui.TextUnformatted("Client ID");
        CfgTooltip("The Client ID from your FFLogs API client.\nFound at: fflogs.com/api/clients");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##CfgClientId", ref cfgClientId, 256, ImGuiInputTextFlags.None);
        ImGui.Spacing();

        ImGui.TextUnformatted("Client Secret");
        CfgTooltip("The Client Secret from your FFLogs API client.\nThis is shown once when you create the client.\nIf you lost it, delete the old client and create a new one.");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##CfgClientSecret", ref cfgClientSecret, 256, ImGuiInputTextFlags.Password);
        ImGui.Spacing();

        if (ImGui.Button("Save Credentials", default))
        {
            plugin.Configuration.FFLogsClientId     = cfgClientId;
            plugin.Configuration.FFLogsClientSecret = cfgClientSecret;
            plugin.SaveConfig();
        }
        ImGui.SameLine();

        if (!string.IsNullOrWhiteSpace(plugin.Configuration.FFLogsClientId) &&
            !string.IsNullOrWhiteSpace(plugin.Configuration.FFLogsClientSecret))
        {
            var green = new Vector4(0.3f, 1.0f, 0.3f, 1.0f);
            ImGui.TextColored(in green, "Credentials saved.");
        }
        else
        {
            var yellow = new Vector4(1.0f, 0.8f, 0.2f, 1.0f);
            ImGui.TextColored(in yellow, "Not configured yet.");
        }
    }

    private void DrawCfgOverlaySettings()
    {
        ImGui.TextUnformatted("Overlay Settings");
        ImGui.Separator();

        var cfg = plugin.Configuration;

        var overlayEnabled = cfg.OverlayEnabled;
        if (ImGui.Checkbox("Enable Overlay", ref overlayEnabled))
        {
            cfg.OverlayEnabled = overlayEnabled;
            plugin.SaveConfig();
        }
        CfgTooltip("Show a compact live timeline during combat.\nStarts automatically when you pull a boss.");

        var overlayLocked = cfg.OverlayLocked;
        if (ImGui.Checkbox("Lock Overlay Position", ref overlayLocked))
        {
            cfg.OverlayLocked = overlayLocked;
            plugin.SaveConfig();
        }
        CfgTooltip("When locked, the overlay cannot be moved or resized.\nUncheck this to reposition the overlay, then re-lock it.");
    }

    private void DrawCfgOverlayAppearance()
    {
        ImGui.TextUnformatted("Overlay Appearance");
        ImGui.Separator();

        var cfg     = plugin.Configuration;
        var changed = false;

        ImGui.TextDisabled("Timing");

        var pxPerSec = cfg.OverlayPixelsPerSec;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Scroll Speed (px/sec)", ref pxPerSec, 20.0f, 200.0f, "%.0f"))
        { cfg.OverlayPixelsPerSec = pxPerSec; changed = true; }
        CfgTooltip("How many pixels represent one second of time.\nHigher = more spread out, lower = more compact.");

        var timeBehind = cfg.OverlayTimeBehind;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Look-Behind (sec)", ref timeBehind, 0.0f, 10.0f, "%.1f"))
        { cfg.OverlayTimeBehind = timeBehind; changed = true; }
        CfgTooltip("How many seconds of past abilities to keep visible.\nThis controls where the 'now' line sits.");

        ImGui.Spacing();
        ImGui.TextDisabled("Icons");

        var iconSize = cfg.OverlayIconSize;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Icon Size", ref iconSize, 16.0f, 64.0f, "%.0f"))
        { cfg.OverlayIconSize = iconSize; changed = true; }
        CfgTooltip("Size of the primary ability icon in pixels.");

        var maxStacked = cfg.OverlayMaxStackedIcons;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Max Stacked Icons", ref maxStacked, 1, 6))
        { cfg.OverlayMaxStackedIcons = maxStacked; changed = true; }
        CfgTooltip("Maximum number of abilities shown per time bucket.\nThe most frequent ability is shown largest.");

        ImGui.Spacing();
        ImGui.TextDisabled("oGCD");

        var ogcdSizeRatio = cfg.OGCDSizeRatio;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("oGCD Size", ref ogcdSizeRatio, 0.25f, 1.0f, "%.2f"))
        { cfg.OGCDSizeRatio = ogcdSizeRatio; changed = true; }
        CfgTooltip("oGCD icon size as a fraction of the GCD icon size.\n0.75 = 75% of GCD size (ATR default).\n1.0 = same size as GCDs.");

        var ogcdOffset = cfg.OGCDVerticalOffset;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("oGCD Vertical Offset", ref ogcdOffset, -1.0f, 1.0f, "%.2f"))
        { cfg.OGCDVerticalOffset = ogcdOffset; changed = true; }
        CfgTooltip("How far above center oGCDs are positioned, as a fraction of GCD icon size.\n0.1 = slightly above center (ATR default).\n0 = same center as GCDs.\nNegative = below center.");

        var ogcdHOffset = cfg.OGCDHorizontalOffset;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("oGCD Horizontal Offset", ref ogcdHOffset, -100f, 100f, "%.0f px"))
        { cfg.OGCDHorizontalOffset = ogcdHOffset; changed = true; }
        CfgTooltip("Shift oGCD icons left (negative) or right (positive) on the timeline.\nUseful to visually separate oGCDs from GCDs at the same timestamp.");

        ImGui.Spacing();
        ImGui.TextDisabled("Visual");

        var bgOpacity = cfg.OverlayBgOpacity;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Background Opacity", ref bgOpacity, 0.0f, 1.0f, "%.2f"))
        { cfg.OverlayBgOpacity = bgOpacity; changed = true; }
        CfgTooltip("Opacity of the overlay background.\n0 = fully transparent, 1 = solid.");

        var pastAlpha = cfg.OverlayPastAlpha;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Past Icon Dimming", ref pastAlpha, 0.0f, 1.0f, "%.2f"))
        { cfg.OverlayPastAlpha = pastAlpha; changed = true; }
        CfgTooltip("Opacity multiplier for abilities that have already passed.\n0 = invisible, 1 = same as upcoming.");

        var freqPct = cfg.OverlayFreqThreshold * 100f;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Min Frequency", ref freqPct, 0f, 100f, "%.0f%%"))
        { cfg.OverlayFreqThreshold = freqPct / 100f; changed = true; }
        CfgTooltip("Hide abilities used in fewer than this % of parses.\nUseful to filter out rare/niche casts.");

        ImGui.Spacing();
        ImGui.TextDisabled("Elements");

        var showGrid = cfg.OverlayShowGrid;
        if (ImGui.Checkbox("Show Grid Lines", ref showGrid))
        { cfg.OverlayShowGrid = showGrid; changed = true; }
        CfgTooltip("Show vertical time grid lines on the overlay.");

        ImGui.Spacing();
        ImGui.TextDisabled("Boss Bar");

        var bossCustom = cfg.BossBarUseCustomColor;
        if (ImGui.Checkbox("Custom Boss Bar Color", ref bossCustom))
        { cfg.BossBarUseCustomColor = bossCustom; changed = true; }
        CfgTooltip("Use a single custom color for all boss cast bars\ninstead of the default per-ability coloring.");

        if (cfg.BossBarUseCustomColor)
        {
            var bossCol = cfg.BossBarColor;
            ImGui.SetNextItemWidth(200);
            if (ImGui.ColorEdit4("Boss Bar Color", ref bossCol))
            { cfg.BossBarColor = bossCol; changed = true; }
            CfgTooltip("Color applied to every boss cast bar.");
        }

        if (changed) plugin.SaveConfig();
    }

    private void DrawCfgMainTimelineSettings()
    {
        ImGui.TextUnformatted("Main Timeline");
        ImGui.Separator();

        var cfg     = plugin.Configuration;
        var changed = false;

        var iconSize = cfg.MainIconSize;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Icon Size##Main", ref iconSize, 8.0f, 48.0f, "%.0f"))
        { cfg.MainIconSize = iconSize; changed = true; }
        CfgTooltip("Size of skill icons in the main timeline window.");

        var iconScale = cfg.MainIconScale;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Icon Scale##Main", ref iconScale, 0.5f, 2.0f, "%.2f"))
        { cfg.MainIconScale = iconScale; changed = true; }
        CfgTooltip("Scale multiplier applied on top of Icon Size.");

        var iconOpacity = cfg.MainIconOpacity;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Icon Opacity##Main", ref iconOpacity, 0.0f, 1.0f, "%.2f"))
        { cfg.MainIconOpacity = iconOpacity; changed = true; }
        CfgTooltip("Maximum opacity of skill icons.\nMultiplied by each ability's usage frequency.");

        ImGui.Spacing();
        ImGui.TextDisabled("Boss bar color is shared with the overlay — see Boss Bar above.");

        if (changed) plugin.SaveConfig();
    }

    private void DrawCfgAntsSettings()
    {
        ImGui.TextUnformatted("Ability Ants");
        ImGui.Separator();

        var cfg     = plugin.Configuration;
        var changed = false;

        var enabled = cfg.AntsEnabled;
        if (ImGui.Checkbox("Enable Ability Ants", ref enabled))
        { cfg.AntsEnabled = enabled; changed = true; }
        CfgTooltip("Draw an animated dashed border around abilities as they cross the now-line.\nMatches FFXIV's combo/proc highlight style.");

        if (cfg.AntsEnabled)
        {
            ImGui.Spacing(); ImGui.Spacing();

            var customEnabled = cfg.AntsCustomEnabled;
            if (ImGui.Checkbox("Custom Ants (replace native highlight)", ref customEnabled))
            { cfg.AntsCustomEnabled = customEnabled; changed = true; }
            CfgTooltip("Replaces FFXIV's built-in marching-ants highlight with a fully\n" +
                       "customisable ImGui-drawn dashed border. Colour, speed and size\n" +
                       "are all adjustable below.");

            // ── oGCD Ants ──────────────────────────────────────────────────────
            ImGui.Spacing(); ImGui.Spacing();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.3f, 1.0f), "oGCD Ants");
            ImGui.Separator();

            var ogcdEnabled = cfg.OgcdAntsEnabled;
            if (ImGui.Checkbox("Enable oGCD Ants##ogcd", ref ogcdEnabled))
            { cfg.OgcdAntsEnabled = ogcdEnabled; changed = true; }
            CfgTooltip("Show ants on off-global-cooldown abilities as they approach the now-line.");

            if (cfg.OgcdAntsEnabled)
            {
                ImGui.Spacing();

                var before = cfg.AntsDurationBefore;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Before (sec)##ogcd", ref before, 0.0f, 4.0f, "%.1f"))
                { cfg.AntsDurationBefore = before; changed = true; }
                CfgTooltip("How many seconds before crossing the now-line the ants appear.");

                var after = cfg.AntsDurationAfter;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("After (sec)##ogcd", ref after, 0.0f, 4.0f, "%.1f"))
                { cfg.AntsDurationAfter = after; changed = true; }
                CfgTooltip("How many seconds after crossing the now-line the ants remain.");

                ImGui.Spacing();
                ImGui.TextDisabled($"Window: {cfg.AntsDurationBefore + cfg.AntsDurationAfter:F1}s  " +
                                   $"({cfg.AntsDurationBefore:F1}s before + {cfg.AntsDurationAfter:F1}s after)");

                if (cfg.AntsCustomEnabled)
                {
                    ImGui.Spacing();

                    var dashCol = cfg.AntsColor;
                    if (ImGui.ColorEdit4("Dash colour##ogcd", ref dashCol,
                        ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
                    { cfg.AntsColor = dashCol; changed = true; }

                    var gapCol = cfg.AntsGapColor;
                    if (ImGui.ColorEdit4("Gap colour##ogcd", ref gapCol,
                        ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
                    { cfg.AntsGapColor = gapCol; changed = true; }
                    CfgTooltip("Set alpha to 0 for transparent gaps.");

                    ImGui.Spacing();

                    var dashLen = cfg.AntsDashLength;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Dash length (px)##ogcd", ref dashLen, 1.0f, 30.0f, "%.0f"))
                    { cfg.AntsDashLength = dashLen; changed = true; }

                    var gapLen = cfg.AntsGapLength;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Gap length (px)##ogcd", ref gapLen, 0.0f, 20.0f, "%.0f"))
                    { cfg.AntsGapLength = gapLen; changed = true; }

                    var speed = cfg.AntsSpeed;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("March speed (px/s)##ogcd", ref speed, 5.0f, 200.0f, "%.0f"))
                    { cfg.AntsSpeed = speed; changed = true; }
                    CfgTooltip("How fast the dashes march around the border.");

                    var thick = cfg.AntsThickness;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Line thickness (px)##ogcd", ref thick, 1.0f, 6.0f, "%.1f"))
                    { cfg.AntsThickness = thick; changed = true; }

                    var padding = cfg.AntsBorderPadding;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Border expansion (px)##ogcd", ref padding, -10.0f, 10.0f, "%.1f"))
                    { cfg.AntsBorderPadding = padding; changed = true; }
                    CfgTooltip("Expand (+) or shrink (-) the ants border relative to the slot edge.\n0 = flush with slot bounds.");

                    var xOff = cfg.AntsXOffset;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Horizontal offset (px)##ogcd", ref xOff, -20.0f, 20.0f, "%.1f"))
                    { cfg.AntsXOffset = xOff; changed = true; }
                    CfgTooltip("Shift the ants border left (-) or right (+).");
                }
            }

            // ── GCD Ants ───────────────────────────────────────────────────────
            ImGui.Spacing(); ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.2f, 0.8f, 1.0f, 1.0f), "GCD Ants");
            ImGui.Separator();

            var gcdEnabled = cfg.GcdAntsEnabled;
            if (ImGui.Checkbox("Enable GCD Ants##gcd", ref gcdEnabled))
            { cfg.GcdAntsEnabled = gcdEnabled; changed = true; }
            CfgTooltip("Show ants on the next upcoming GCD as it approaches the now-line.\n" +
                       "Only the single closest GCD entry glows at a time.");

            if (cfg.GcdAntsEnabled)
            {
                ImGui.Spacing();

                var before = cfg.GcdAntsDurationBefore;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Before (sec)##gcd", ref before, 0.0f, 4.0f, "%.1f"))
                { cfg.GcdAntsDurationBefore = before; changed = true; }
                CfgTooltip("How many seconds before crossing the now-line the ants appear.");

                var after = cfg.GcdAntsDurationAfter;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("After (sec)##gcd", ref after, 0.0f, 4.0f, "%.1f"))
                { cfg.GcdAntsDurationAfter = after; changed = true; }
                CfgTooltip("How many seconds after crossing the now-line the ants remain.");

                ImGui.Spacing();
                ImGui.TextDisabled($"Window: {cfg.GcdAntsDurationBefore + cfg.GcdAntsDurationAfter:F1}s  " +
                                   $"({cfg.GcdAntsDurationBefore:F1}s before + {cfg.GcdAntsDurationAfter:F1}s after)");

                if (cfg.AntsCustomEnabled)
                {
                    ImGui.Spacing();

                    var dashCol = cfg.GcdAntsColor;
                    if (ImGui.ColorEdit4("Dash colour##gcd", ref dashCol,
                        ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
                    { cfg.GcdAntsColor = dashCol; changed = true; }

                    var gapCol = cfg.GcdAntsGapColor;
                    if (ImGui.ColorEdit4("Gap colour##gcd", ref gapCol,
                        ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
                    { cfg.GcdAntsGapColor = gapCol; changed = true; }
                    CfgTooltip("Set alpha to 0 for transparent gaps.");

                    ImGui.Spacing();

                    var dashLen = cfg.GcdAntsDashLength;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Dash length (px)##gcd", ref dashLen, 1.0f, 30.0f, "%.0f"))
                    { cfg.GcdAntsDashLength = dashLen; changed = true; }

                    var gapLen = cfg.GcdAntsGapLength;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Gap length (px)##gcd", ref gapLen, 0.0f, 20.0f, "%.0f"))
                    { cfg.GcdAntsGapLength = gapLen; changed = true; }

                    var speed = cfg.GcdAntsSpeed;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("March speed (px/s)##gcd", ref speed, 5.0f, 200.0f, "%.0f"))
                    { cfg.GcdAntsSpeed = speed; changed = true; }
                    CfgTooltip("How fast the dashes march around the border.");

                    var thick = cfg.GcdAntsThickness;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Line thickness (px)##gcd", ref thick, 1.0f, 6.0f, "%.1f"))
                    { cfg.GcdAntsThickness = thick; changed = true; }

                    var padding = cfg.GcdAntsBorderPadding;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Border expansion (px)##gcd", ref padding, -10.0f, 10.0f, "%.1f"))
                    { cfg.GcdAntsBorderPadding = padding; changed = true; }
                    CfgTooltip("Expand (+) or shrink (-) the ants border relative to the slot edge.\n0 = flush with slot bounds.");

                    var xOff = cfg.GcdAntsXOffset;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Horizontal offset (px)##gcd", ref xOff, -20.0f, 20.0f, "%.1f"))
                    { cfg.GcdAntsXOffset = xOff; changed = true; }
                    CfgTooltip("Shift the ants border left (-) or right (+).");
                }
            }
        }

        if (changed) plugin.SaveConfig();
    }

    private void DrawCfgResetSection()
    {
        ImGui.TextUnformatted("Reset");
        ImGui.Separator();

        var red = new Vector4(1.0f, 0.35f, 0.35f, 1.0f);
        ImGui.TextColored(in red, "This will reset all settings to their default values.");
        ImGui.Spacing();

        if (ImGui.Button("Reset to Defaults", default))
        {
            var cfg = plugin.Configuration;
            cfg.OverlayEnabled         = true;
            cfg.OverlayLocked          = true;
            cfg.OverlayPixelsPerSec    = 60.0f;
            cfg.OverlayIconSize        = 46.0f;
            cfg.OverlayTimeBehind      = 1.5f;
            cfg.OverlayBgOpacity       = 1.0f;
            cfg.OverlayPastAlpha       = 1.0f;
            cfg.OverlayFreqThreshold   = 0.0f;
            cfg.OverlayShowGrid        = true;
            cfg.OverlayMaxStackedIcons = 3;
            cfg.OGCDSizeRatio          = 1.0f;
            cfg.OGCDVerticalOffset     = 0.1f;
            cfg.OGCDHorizontalOffset   = 45f;
            cfg.BossBarUseCustomColor  = false;
            cfg.BossBarColor           = new Vector4(0.85f, 0.35f, 0.20f, 1.00f);
            cfg.MainIconSize           = 22.0f;
            cfg.MainIconOpacity        = 1.0f;
            cfg.MainIconScale          = 1.0f;
            cfg.AntsEnabled            = true;
            cfg.AntsCustomEnabled      = false;
            // oGCD ants
            cfg.OgcdAntsEnabled        = true;
            cfg.AntsDurationBefore     = 1.5f;
            cfg.AntsDurationAfter      = 1.5f;
            cfg.AntsColor              = new Vector4(1.0f, 0.85f, 0.0f, 1.0f);
            cfg.AntsGapColor           = new Vector4(0.0f, 0.0f, 0.0f, 0.5f);
            cfg.AntsDashLength         = 6.0f;
            cfg.AntsGapLength          = 4.0f;
            cfg.AntsSpeed              = 40.0f;
            cfg.AntsThickness          = 2.0f;
            cfg.AntsBorderPadding      = 0.0f;
            cfg.AntsXOffset            = -5.0f;
            // GCD ants
            cfg.GcdAntsEnabled         = true;
            cfg.GcdAntsDurationBefore  = 0.5f;
            cfg.GcdAntsDurationAfter   = 0.5f;
            cfg.GcdAntsColor           = new Vector4(0.2f, 0.8f, 1.0f, 1.0f);
            cfg.GcdAntsGapColor        = new Vector4(0.0f, 0.0f, 0.0f, 0.5f);
            cfg.GcdAntsDashLength      = 6.0f;
            cfg.GcdAntsGapLength       = 4.0f;
            cfg.GcdAntsSpeed           = 40.0f;
            cfg.GcdAntsThickness       = 2.0f;
            cfg.GcdAntsBorderPadding   = 0.0f;
            cfg.GcdAntsXOffset         = -5.0f;
            plugin.SaveConfig();
        }
    }

    private static void CfgTooltip(string text)
    {
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text(text);
            ImGui.EndTooltip();
        }
    }
}
