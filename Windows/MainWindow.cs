using System;
using System.Collections.Generic;
using System.IO;
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
    private static readonly IReadOnlyDictionary<string, HashSet<string>> ConflictIgnoredRequirementNamesBySpec =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Astrologian"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "The Balance Ready",
                "The Arrow Ready",
                "The Spire Ready",
                "Lord of Crowns Ready",
                "The Spear Ready",
                "The Ewer Ready",
                "The Bole Ready",
                "Lady of Crowns Ready",
                "Horoscope Ready",
            },
            ["Bard"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Hawk's Eye",
                "Refulgent Arrow Ready",
                "Coda",
            },
            ["Dancer"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Reverse Cascade Ready",
                "Fountainfall Ready",
                "Last Dance Ready",
                "Technical Finish Ready",
            },
            ["Dark Knight"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Blood",
            },
            ["Dragoon"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Draconian Fire",
            },
            ["Gunbreaker"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Cartridge",
            },
            ["Machinist"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Heat",
            },
            ["Monk"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Chakra",
            },
            ["Ninja"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Ninki",
                "Phantom Kamaitachi Ready",
            },
            ["Pictomancer"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Palette",
            },
            ["Reaper"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Soul",
            },
            ["Red Mage"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "BlackMana",
                "WhiteMana",
                "Verfire Ready",
                "Verstone Ready",
                "Grand Impact Ready",
            },
            ["Scholar"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Aetherflow",
            },
            ["Sage"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Eukrasia",
                "Addersgall",
            },
            ["Samurai"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Kenki",
            },
            ["Viper"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "RattlingCoil",
                "SerpentOfferings",
            },
            ["Warrior"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Beast",
            },
            ["White Mage"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Divine Grace",
            },
        };
    private static readonly IReadOnlyDictionary<string, HashSet<string>> ConflictIgnoredCooldownNamesBySpec =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Machinist"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Reassemble",
                "Double Check",
                "Checkmate",
                "Tactician",
            },
            ["Ninja"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Ten",
                "Chi",
                "Jin",
            },
            ["Red Mage"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Contre Sixte",
                "Acceleration",
                "Swiftcast",
            },
            ["Samurai"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Meikyo Shisui",
                "Hissatsu: Senei",
                "Hissatsu: Gyoten",
            },
            ["Scholar"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Energy Drain",
                "Recitation",
            },
            ["Astrologian"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Essential Dignity",
                "Celestial Intersection",
                "Lightspeed",
                "Swiftcast",
            },
            ["Black Mage"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Transpose",
                "Manafont",
                "Swiftcast",
                "Retrace",
            },
            ["Bard"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Heartbreak Shot",
                "Troubadour",
            },
            ["Gunbreaker"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Aurora",
            },
            ["Warrior"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Onslaught",
                "Infuriate",
            },
            ["Pictomancer"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Striking Muse",
                "Swiftcast",
                "Tempera Coat",
            },
            ["White Mage"] = new(StringComparer.OrdinalIgnoreCase)
            {
                "Divine Benison",
                "Tetragrammaton",
                "Swiftcast",
            },
        };
    private static readonly IReadOnlySet<string> ConflictCastLockIgnoredSpecs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Astrologian",
            "Black Mage",
            "Pictomancer",
            "Red Mage",
            "Samurai",
            "Scholar",
            "Sage",
            "Summoner",
            "White Mage",
        };

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
    private bool showEmbeddedTimelinePreview;
    private readonly Dictionary<int, uint> iconIdCache = [];

    // Update state
    private bool isUpdating;
    private string updateStatus = string.Empty;
    private float updateProgress;
    private CancellationTokenSource? updateCts;

    // Skill filter expanded state — tracks which ability IDs have their threshold slider open
    private readonly HashSet<int> expandedSkillNodes = [];
    private string cacheClearNotice = string.Empty;

    // Custom timeline editor state
    private string? selectedCustomKey;
    private AggregatedTimeline? editingTimeline;
    private string editEncounterName = string.Empty;
    private string editSpecName = string.Empty;
    private int    editEncounterIdx  = 0;
    private int    editSpecIdx       = 0;
    private float editDurationSec;
    private int editingEntryIndex = -1;   // index into player Entries; -1 = none
    private bool editingEntryIsBoss;      // true when the popup is editing a boss entry
    private float editEntryTime;
    private float editEntryEndTime;       // boss only
    private string editEntryName = string.Empty;
    private float editEntryFrequency;
    private int   editEntryAbilityIdx = 0;
    private List<string> editEntryAbilityOptions = [];
    private bool customEditorDirty;

    // Group management state
    private string newGroupNameBuf = string.Empty;

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
    private int     riAutoSelectedZone = -1;
    private int     riAutoSelectedEncounter = -1;
    private int     riAutoSelectedSpec = -1;
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
    private bool              pendingDeferredConfigSave;
    private DateTime          pendingDeferredConfigSaveAt = DateTime.MinValue;
    private static readonly TimeSpan DeferredConfigSaveDelay = TimeSpan.FromMilliseconds(350);

    /// <summary>Opens the main window and auto-selects the Config tab on the next frame.</summary>
    public void FocusConfigTab() { IsOpen = true; focusConfigTab = true; }

    /// <summary>Creates or removes the DTR bar entry to reflect auto-execute state.</summary>
    public void ApplyAutoExecDtr(bool enabled)
    {
        if (enabled)
        {
            autoExecDtrEntry ??= plugin.DtrBar.Get("ATKTip");
            autoExecDtrEntry.Text  = new SeString(new TextPayload("ATK AUTO"));
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
        Size          = new Vector2(1100, 700);
        SizeCondition = ImGuiCond.FirstUseEver;
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
        ProcessDeferredConfigSave();

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

    public override void PreDraw()
    {
        if (plugin.OverlayWindow.IsEmbeddedPreviewScrubbing)
            Flags |= ImGuiWindowFlags.NoMove;
        else
            Flags &= ~ImGuiWindowFlags.NoMove;
    }

    private void RequestDeferredConfigSave()
    {
        pendingDeferredConfigSave = true;
        pendingDeferredConfigSaveAt = DateTime.UtcNow + DeferredConfigSaveDelay;
    }

    private void ProcessDeferredConfigSave()
    {
        if (!pendingDeferredConfigSave || DateTime.UtcNow < pendingDeferredConfigSaveAt)
            return;

        if (ImGui.IsAnyItemActive() ||
            ImGui.IsMouseDown(ImGuiMouseButton.Left) ||
            ImGui.IsMouseDown(ImGuiMouseButton.Right) ||
            ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            pendingDeferredConfigSaveAt = DateTime.UtcNow + DeferredConfigSaveDelay;
            return;
        }

        plugin.SaveConfig();
        pendingDeferredConfigSave = false;
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

    private void HideEmbeddedTimelinePreview()
    {
        showEmbeddedTimelinePreview = false;
        plugin.OverlayWindow.ResetEmbeddedPreview();
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

                // Resolve ability ID from game data so icons and recast detection work.
                var recastInfo = plugin.RecastDatabase.Lookup(0, abilityName);

                entries.Add(new TimelineEntry
                {
                    AbilityId     = recastInfo != null ? (int)recastInfo.AbilityId : 0,
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
            plugin.EncounterTracker.RebuildZoneMappings();

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
        ImGui.SameLine();
        if (ImGui.SmallButton("Add Group"))
            ImGui.OpenPopup("##AddGroupPopup");

        if (ImGui.BeginPopup("##AddGroupPopup"))
        {
            ImGui.Text("Group name:");
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputText("##newgroup", ref newGroupNameBuf, 64,
                ImGuiInputTextFlags.EnterReturnsTrue))
            {
                var trimmed = newGroupNameBuf.Trim();
                if (trimmed.Length > 0 && !plugin.Configuration.TimelineGroups.Contains(trimmed))
                {
                    plugin.Configuration.TimelineGroups.Add(trimmed);
                    plugin.SaveConfig();
                }
                newGroupNameBuf = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("OK"))
            {
                var trimmed = newGroupNameBuf.Trim();
                if (trimmed.Length > 0 && !plugin.Configuration.TimelineGroups.Contains(trimmed))
                {
                    plugin.Configuration.TimelineGroups.Add(trimmed);
                    plugin.SaveConfig();
                }
                newGroupNameBuf = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        ImGui.Separator();

        if (customs.Count == 0)
        {
            ImGui.TextDisabled("No custom timelines yet.");
            ImGui.TextDisabled("Use \"Copy Timeline\" in the");
            ImGui.TextDisabled("Timeline tab menu bar.");
        }
        else
        {
            var cfg = plugin.Configuration;
            var keys = customs.Keys.ToList();
            var globalIndexByKey = new Dictionary<string, int>(keys.Count, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < keys.Count; index++)
                globalIndexByKey[keys[index]] = index;

            var groupedKeysByGroup = new Dictionary<string, List<string>>(cfg.TimelineGroups.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var groupName in cfg.TimelineGroups)
                groupedKeysByGroup[groupName] = new List<string>();

            var ungroupedKeys = new List<string>();
            foreach (var key in keys)
            {
                if (cfg.TimelineGroupAssignments.TryGetValue(key, out var groupName) &&
                    groupedKeysByGroup.TryGetValue(groupName, out var groupedKeys))
                {
                    groupedKeys.Add(key);
                }
                else
                {
                    ungroupedKeys.Add(key);
                }
            }

            void DrawTimelineRow(string key, List<string> scopedKeys, int scopedIdx)
            {
                var tl         = customs[key];
                var isSelected = key == selectedCustomKey;
                var globalIdx  = globalIndexByKey[key];

                ImGui.PushID(key);

                if (ImGui.Selectable($"{tl.EncounterName} / {tl.SpecName}##{key}", isSelected,
                    ImGuiSelectableFlags.None, default))
                {
                    if (selectedCustomKey != key)
                        SelectCustomTimeline(key, tl);
                }

                // Right-click context menu
                if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    ImGui.OpenPopup("##tlctx");

                if (ImGui.BeginPopup("##tlctx"))
                {
                    ImGui.BeginDisabled(scopedIdx == 0);
                    if (ImGui.MenuItem("Move Up"))
                        ReorderCustomTimeline(keys, globalIdx, globalIndexByKey[scopedKeys[scopedIdx - 1]]);
                    ImGui.EndDisabled();

                    ImGui.BeginDisabled(scopedIdx == scopedKeys.Count - 1);
                    if (ImGui.MenuItem("Move Down"))
                        ReorderCustomTimeline(keys, globalIdx, globalIndexByKey[scopedKeys[scopedIdx + 1]]);
                    ImGui.EndDisabled();

                    ImGui.Separator();

                    if (ImGui.BeginMenu("Assign to Group"))
                    {
                        if (ImGui.MenuItem("— None —"))
                        {
                            cfg.TimelineGroupAssignments.Remove(key);
                            plugin.SaveConfig();
                        }
                        foreach (var g in cfg.TimelineGroups)
                        {
                            var isCurrent = cfg.TimelineGroupAssignments.TryGetValue(key, out var cur) && cur == g;
                            if (ImGui.MenuItem(g, string.Empty, isCurrent))
                            {
                                cfg.TimelineGroupAssignments[key] = g;
                                plugin.SaveConfig();
                            }
                        }
                        ImGui.EndMenu();
                    }

                    ImGui.EndPopup();
                }

                ImGui.PopID();
            }

            // Grouped sections — deferred mutations to avoid modifying list during iteration
            string? groupToDelete = null;
            int     groupMoveFrom = -1;
            int     groupMoveTo   = -1;

            for (var gi = 0; gi < cfg.TimelineGroups.Count; gi++)
            {
                var group     = cfg.TimelineGroups[gi];
                var groupKeys = groupedKeysByGroup.GetValueOrDefault(group) ?? [];

                var nodeOpen = ImGui.TreeNodeEx($"{group}##grpnode{gi}",
                    ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth);

                // Right-click context menu on group header
                if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    ImGui.OpenPopup($"##grpctx{gi}");

                if (ImGui.BeginPopup($"##grpctx{gi}"))
                {
                    ImGui.BeginDisabled(gi == 0);
                    if (ImGui.MenuItem("Move Up"))
                    { groupMoveFrom = gi; groupMoveTo = gi - 1; }
                    ImGui.EndDisabled();

                    ImGui.BeginDisabled(gi == cfg.TimelineGroups.Count - 1);
                    if (ImGui.MenuItem("Move Down"))
                    { groupMoveFrom = gi; groupMoveTo = gi + 1; }
                    ImGui.EndDisabled();

                    ImGui.Separator();

                    if (ImGui.MenuItem("Delete Group"))
                        groupToDelete = group;

                    ImGui.EndPopup();
                }

                if (nodeOpen)
                {
                    if (groupKeys.Count == 0)
                        ImGui.TextDisabled("  (empty)");
                    else
                        for (var groupKeyIndex = 0; groupKeyIndex < groupKeys.Count; groupKeyIndex++)
                            DrawTimelineRow(groupKeys[groupKeyIndex], groupKeys, groupKeyIndex);

                    ImGui.TreePop();
                }
            }

            // Apply deferred group mutations
            if (groupToDelete != null)
            {
                foreach (var k in groupedKeysByGroup.GetValueOrDefault(groupToDelete) ?? [])
                    cfg.TimelineGroupAssignments.Remove(k);
                cfg.TimelineGroups.Remove(groupToDelete);
                plugin.SaveConfig();
            }
            if (groupMoveFrom >= 0 && groupMoveTo >= 0 &&
                groupMoveFrom < cfg.TimelineGroups.Count && groupMoveTo < cfg.TimelineGroups.Count)
            {
                (cfg.TimelineGroups[groupMoveFrom], cfg.TimelineGroups[groupMoveTo]) =
                    (cfg.TimelineGroups[groupMoveTo], cfg.TimelineGroups[groupMoveFrom]);
                plugin.SaveConfig();
            }

            if (ungroupedKeys.Count > 0)
            {
                if (cfg.TimelineGroups.Count > 0)
                {
                    ImGui.Spacing();
                    ImGui.TextDisabled("Ungrouped");
                }
                for (var ungroupedIndex = 0; ungroupedIndex < ungroupedKeys.Count; ungroupedIndex++)
                    DrawTimelineRow(ungroupedKeys[ungroupedIndex], ungroupedKeys, ungroupedIndex);
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
        DrawAutoTimelineFromFetchedLogsSection();

        // Guard: if Delete was just pressed editingTimeline is now null, bail out cleanly.
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
        ImGui.SameLine();
        if (ImGui.Button("Auto Timeline"))
        {
            ApplyAutoTimeline(editingTimeline!);
            customEditorDirty = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("Build the best legal action order from the current source timeline.");
            ImGui.EndTooltip();
        }

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
                $"\u26a0 {conflictedPlayerIndices.Count} conflict(s) — hover time cells for details");
        }

        ImGui.Separator();

        // Merge player entries + boss entries, sorted by time, for display
        var activeTimeline = editingTimeline;
        if (activeTimeline == null)
            return;

        var playerEntries = activeTimeline.Entries;
        var bossEntries   = activeTimeline.BossEntries;

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
    /// Uses a per-ability charge queue: each use spends one charge; a charge returns
    /// after RecastSec seconds. Conflict = no charges available at use time.
    /// Keyed by ability ID (not cooldown group) so distinct abilities with coincidentally
    /// shared Lumina cooldown groups don't produce false positives.
    /// Only abilities with recast >= 5 s are checked (excludes GCDs).
    /// </summary>
    private void RebuildConflicts(AggregatedTimeline tl)
    {
        conflictedPlayerIndices.Clear();
        conflictReasons.Clear();

        // ── Cooldown (recast) tracking ──────────────────────────────────────
        // abilityId → (sorted list of times when each spent charge recharges, last-use time)
        var abilityState = new Dictionary<string, (List<double> RechargeQueue, double Time, string AbilityName)>(
            StringComparer.OrdinalIgnoreCase);
        var timelineAbilityNames = tl.Entries
            .Select(e => e.AbilityName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── Combo chain tracking ────────────────────────────────────────────
        uint lastGcdId = 0;
        string? lastComboAbilityName = null;
        double lastGcdTime = double.NegativeInfinity;
        var comboSeenIds = new HashSet<uint>();

        // ── Gauge tracking ──────────────────────────────────────────────────
        var gaugeRules = GaugeSimulator.GetRules(tl.SpecName);
        var grantedRules = GrantedActionDatabase.GetRules(tl.SpecName);
        var gaugeState = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var grantedState = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var actionState = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var passiveGaugeProgress = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (gaugeRules != null)
        {
            foreach (var res in gaugeRules.Resources)
            {
                gaugeState[res.Name] = res.InitialValue;
                passiveGaugeProgress[res.Name] = 0.0;
            }
        }
        if (grantedRules != null)
        {
            foreach (var res in grantedRules.Resources)
                grantedState[res.Name] = res.InitialValue;
        }

        // ── AST card draw state ─────────────────────────────────────────────
        // null = no draw active yet, "Astral" = after Astral Draw, "Umbral" = after Umbral Draw
        string? cardDrawState = null;
        var timedWindowEndByAbility = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var timedLockoutEndByAbility = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double castLockUntil = 0.0;

        double lastEntryTime = 0.0;

        var sorted = tl.Entries
            .Select((e, i) => (Index: i, Entry: e))
            .OrderBy(x => x.Entry.TimeOffsetSec)
            .ToList();

        // Appends a reason string; marks the index as conflicted.
        void AddReason(int i, string reason)
        {
            if (conflictReasons.TryGetValue(i, out var existing))
                conflictReasons[i] = existing + "\n" + reason;
            else
                conflictReasons[i] = reason;
            conflictedPlayerIndices.Add(i);
        }

        foreach (var (idx, entry) in sorted)
        {
            var info  = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
            bool isGcd = info?.IsGcdAction ?? false;
            var now   = entry.TimeOffsetSec;
            var effectiveCastTimeSec = GetEffectiveCastTimeSec(
                grantedState, grantedRules, info, entry.AbilityName, out var instantCastRule);

            // ── Passive gauge generation ──────────────────────────────────────
            // Advance time-based gauges (e.g. Lily, Addersgall, Polyglot) by the
            // elapsed time since the previous entry was processed.
            if (gaugeRules != null)
            {
                var elapsed = now - lastEntryTime;
                foreach (var res in gaugeRules.Resources)
                {
                    if (res.PassiveGenerationIntervalSec <= 0) continue;
                    var progress = passiveGaugeProgress.GetValueOrDefault(res.Name) + elapsed;
                    var ticks = (int)(progress / res.PassiveGenerationIntervalSec);
                    if (ticks > 0)
                    {
                        gaugeState[res.Name] = Math.Clamp(
                            gaugeState.GetValueOrDefault(res.Name) + ticks, 0, res.MaxValue);
                        progress -= ticks * res.PassiveGenerationIntervalSec;
                    }
                    passiveGaugeProgress[res.Name] = progress;
                }
            }
            lastEntryTime = now;

            if (!isGcd &&
                !ConflictCastLockIgnoredSpecs.Contains(tl.SpecName) &&
                now + ConflictCastLockToleranceSec < castLockUntil)
                AddReason(idx,
                    $"Cast lock conflict: {entry.AbilityName} lands during a prior cast window (free at {FormatCsvTime(castLockUntil)})");

            var timedWindowReason = GetTimedUsageWindowReason(
                tl.SpecName,
                timedWindowEndByAbility,
                entry.AbilityName,
                now);
            if (timedWindowReason != null)
                AddReason(idx, timedWindowReason);

            var timedLockoutReason = GetTimedUsageLockoutReason(
                tl.SpecName,
                timedLockoutEndByAbility,
                entry.AbilityName,
                now);
            if (timedLockoutReason != null)
                AddReason(idx, timedLockoutReason);

            var scholarBurstReservationReason = GetScholarBurstAetherflowReservationReason(
                tl.SpecName,
                timedWindowEndByAbility,
                gaugeState,
                entry.AbilityName,
                now);
            if (scholarBurstReservationReason != null)
                AddReason(idx, scholarBurstReservationReason);

            // ── Cooldown conflict (oGCDs / long-recast abilities, recast ≥ 5 s) ──
            if (ShouldTrackPersonalCooldown(info) &&
                !ShouldIgnoreConflictCooldown(tl.SpecName, entry.AbilityName) &&
                !ShouldBypassCooldown(grantedState, grantedRules, entry.AbilityName))
            {
                var cooldownKey = GetCooldownBucketKey(info, entry.AbilityId, entry.AbilityName);
                var maxCharges = Math.Max(1, info?.MaxCharges ?? 1);

                if (!abilityState.TryGetValue(cooldownKey, out var state))
                    state = ([], 0, string.Empty);

                state.RechargeQueue.RemoveAll(t => t <= now + AutoCooldownToleranceSec);

                if (state.RechargeQueue.Count >= maxCharges)
                {
                    var earliestAvail = state.RechargeQueue.Min();
                    var overlapSec    = earliestAvail - now;
                    if (overlapSec > AutoCooldownToleranceSec)
                    {
                        AddReason(idx,
                            $"Recast conflict: {overlapSec:F1}s too early — previous use at {FormatCsvTime(state.Time)}" +
                            $" (recast: {info!.RecastSec:F0}s, charges: {maxCharges})");
                    }
                }

                state.RechargeQueue.Add(now + info!.RecastSec);
                state.RechargeQueue.Sort();
                abilityState[cooldownKey] = (state.RechargeQueue, now, entry.AbilityName);
            }

            // ── Combo chain check (GCDs with a required predecessor) ─────────
            if (isGcd && lastGcdId != 0 && now - lastGcdTime >= ComboResetGapSec)
            {
                lastGcdId = 0;
                lastComboAbilityName = null;
                comboSeenIds.Clear();
            }

            if (isGcd && info != null && ShouldValidateComboRequirement(info, entry.AbilityName) && info.ComboActionId > 0)
            {
                var predecessorNames = GetComboPredecessorNames(info);
                var predecessorSatisfiedByName = !string.IsNullOrWhiteSpace(lastComboAbilityName) &&
                                                predecessorNames.Contains(lastComboAbilityName);
                if (lastGcdId != info.ComboActionId &&
                    !predecessorSatisfiedByName &&
                    comboSeenIds.Contains(info.ComboActionId))
                {
                    var prevInfo = plugin.RecastDatabase.Lookup((int)info.ComboActionId, string.Empty);
                    var prevName = prevInfo?.Name ?? $"#{info.ComboActionId}";
                    AddReason(idx, $"Broken combo: {entry.AbilityName} requires {prevName} in the active combo chain");
                }
            }

            // ── Gauge check ───────────────────────────────────────────────────
            Dictionary<string, int>? preActionGaugeState = null;

            var bypassGaugeSpendChecks = HasRepeatableGrantedActionCharge(grantedState, grantedRules, entry.AbilityName);

            if (gaugeRules != null &&
                gaugeRules.EffectByName.TryGetValue(entry.AbilityName, out var effects))
            {
                preActionGaugeState = new Dictionary<string, int>(gaugeState, StringComparer.OrdinalIgnoreCase);

                // Check all requirements before applying any deltas
                foreach (var effect in effects)
                {
                    if (bypassGaugeSpendChecks && (effect.MinRequired > 0 || effect.Delta < 0))
                        continue;

                    if (ShouldIgnoreConflictRequirement(tl.SpecName, effect.GaugeName))
                        continue;

                    if (effect.MinRequired > 0 &&
                        gaugeState.GetValueOrDefault(effect.GaugeName) < effect.MinRequired)
                    {
                        AddReason(idx,
                            $"Not enough gauge: {effect.GaugeName} = {gaugeState.GetValueOrDefault(effect.GaugeName)}" +
                            $" (need \u2265 {effect.MinRequired})");
                    }

                    if (effect.MaxAllowedBeforeUse < int.MaxValue)
                    {
                        var have = gaugeState.GetValueOrDefault(effect.GaugeName);
                        if (have > effect.MaxAllowedBeforeUse)
                        {
                            AddReason(idx,
                                $"Gauge refresh conflict: {entry.AbilityName} requires {effect.GaugeName} \u2264 {effect.MaxAllowedBeforeUse}" +
                                $" before use, but {have} remains");
                        }
                    }
                }

                // Apply all deltas regardless of conflict so later entries see correct state
                foreach (var effect in effects)
                {
                    if (bypassGaugeSpendChecks && effect.Delta < 0)
                        continue;

                    ApplyGaugeEffectToState(gaugeState, gaugeRules, effect);
                }
            }

            // ── AST card draw validation ──────────────────────────────────────
            var actionRule = plugin.ActionStateDatabase.Lookup(entry.AbilityId, entry.AbilityName);
            if (actionRule != null)
            {
                var effectiveGaugeState = preActionGaugeState ?? gaugeState;
                foreach (var effect in actionRule.Effects)
                {
                    var currentState = GetEffectiveStateValue(effect.StateName, actionState, effectiveGaugeState, grantedState);
                    if (effect.MinRequired > 0 &&
                        !ShouldIgnoreConflictRequirement(tl.SpecName, effect.StateName) &&
                        !ShouldIgnoreActionStateRequirement(actionRule, effect, entry.AbilityName, gaugeRules, grantedRules) &&
                        ShouldEnforceActionStateRequirement(effect.StateName, timelineAbilityNames, gaugeRules, grantedRules) &&
                        currentState < effect.MinRequired)
                    {
                        AddReason(idx,
                            $"Missing action state: {effect.StateName} = {currentState}" +
                            $" (need \u2265 {effect.MinRequired})");
                    }
                }

                ApplyActionStateEffects(actionState, actionRule);
            }

            var repeatRule = FindRepeatableGrantedActionRule(grantedRules, entry.AbilityName);
            if (repeatRule != null)
            {
                var current = grantedState.GetValueOrDefault(repeatRule.ResourceName);
                var maxValue = grantedRules?.Resources.FirstOrDefault(r =>
                    string.Equals(r.Name, repeatRule.ResourceName, StringComparison.OrdinalIgnoreCase))
                    ?.MaxValue ?? int.MaxValue;

                if (UsesRepeatableGrantedActionCharge(grantedState, repeatRule, entry.AbilityName))
                {
                    if (current < repeatRule.ConsumeCount)
                    {
                        AddReason(idx,
                            $"Not enough gauge: {repeatRule.ResourceName} = {current}" +
                            $" (need \u2265 {repeatRule.ConsumeCount})");
                    }
                    else
                    {
                        grantedState[repeatRule.ResourceName] = Math.Clamp(
                            current - repeatRule.ConsumeCount,
                            0,
                            maxValue);
                    }
                }
                else if (string.Equals(repeatRule.TriggerName, entry.AbilityName, StringComparison.OrdinalIgnoreCase))
                {
                    grantedState[repeatRule.ResourceName] = Math.Clamp(
                        current + repeatRule.GrantCount,
                        0,
                        maxValue);
                }
            }

            if (grantedRules?.CardDraw is { } cardDraw &&
                !string.Equals(tl.SpecName, "Astrologian", StringComparison.OrdinalIgnoreCase))
            {
                var name = entry.AbilityName;

                if (cardDraw.AstralCards.Contains(name))
                {
                    if (cardDrawState != "Astral")
                        AddReason(idx,
                            $"Card draw conflict: {name} requires Astral Draw" +
                            (cardDrawState == "Umbral" ? " (Umbral Draw active)" : " (no draw active)"));
                    cardDrawState = null;
                }
                else if (cardDraw.UmbralCards.Contains(name))
                {
                    if (cardDrawState != "Umbral")
                        AddReason(idx,
                            $"Card draw conflict: {name} requires Umbral Draw" +
                            (cardDrawState == "Astral" ? " (Astral Draw active)" : " (no draw active)"));
                    cardDrawState = null;
                }
                else if (string.Equals(name, cardDraw.AstralDrawName, StringComparison.OrdinalIgnoreCase))
                {
                    cardDrawState = "Astral";
                }
                else if (string.Equals(name, cardDraw.UmbralDrawName, StringComparison.OrdinalIgnoreCase))
                {
                    cardDrawState = "Umbral";
                }
            }

            // ── Advance combo state ───────────────────────────────────────────
            ApplyTimedUsageWindowState(tl.SpecName, timedWindowEndByAbility, entry.AbilityName, now);
            ApplyTimedUsageLockoutState(tl.SpecName, timedLockoutEndByAbility, entry.AbilityName, now);

            ApplyCastStateTransitions(
                grantedState, grantedRules, info, entry.AbilityName, effectiveCastTimeSec, instantCastRule);

            if (isGcd && info != null)
            {
                if (IsComboTrackedAction(info, entry.AbilityName))
                {
                    lastGcdId = info.AbilityId;
                    lastComboAbilityName = entry.AbilityName;
                    lastGcdTime = now;
                    comboSeenIds.Add(info.AbilityId);
                }
                castLockUntil = effectiveCastTimeSec > AutoCastLockToleranceSec
                    ? now + effectiveCastTimeSec
                    : now;
            }
        }
    }

    /// <summary>
    /// Swaps the custom timeline at <paramref name="fromIdx"/> with the one at
    /// <paramref name="toIdx"/> by rebuilding the dictionary in the new order.
    /// </summary>
    private void ReorderCustomTimeline(List<string> orderedKeys, int fromIdx, int toIdx)
    {
        if (fromIdx < 0 || toIdx < 0 || fromIdx >= orderedKeys.Count || toIdx >= orderedKeys.Count)
            return;

        var customs = plugin.Configuration.CustomTimelines;
        // Swap in the key list
        (orderedKeys[fromIdx], orderedKeys[toIdx]) = (orderedKeys[toIdx], orderedKeys[fromIdx]);

        // Rebuild dict in the new order (Dictionary preserves insertion order in .NET 5+)
        var reordered = new Dictionary<string, AggregatedTimeline>(orderedKeys.Count);
        foreach (var k in orderedKeys)
            reordered[k] = customs[k];

        plugin.Configuration.CustomTimelines = reordered;
        plugin.SaveConfig();
    }

    private void SelectCustomTimeline(string key, AggregatedTimeline tl)
    {
        selectedCustomKey  = key;
        editingTimeline    = tl;
        editEncounterName  = tl.EncounterName;
        editSpecName       = tl.SpecName;
        editDurationSec    = (float)(tl.AverageDurationMs / 1000.0);
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

    private const double AutoCooldownToleranceSec = 0.35;
    private const double AutoCastLockToleranceSec = 0.05;
    private const double ConflictCastLockToleranceSec = 1.00;
    private const double BaseActionAnimationLockSec = 0.60;
    private const double SimulatedAnimationLockLatencySec = 0.02;
    private const double AutoOgcdLockSec = BaseActionAnimationLockSec + SimulatedAnimationLockLatencySec;
    private const double ComboResetGapSec = 30.0;
    private const double AutoTargetGcdSec = 2.50;
    private const double AutoFirstGcdWindowSec = 3.0;
    private const double AutoGcdSearchBeforeSec = 0.80;
    private const double AutoGcdSearchAfterSec = 1.35;
    private const double AutoGcdDowntimeGapSec = 6.0;
    private const double AutoMaxForcedGcdGapSec = 3.0;
    private const double AutoBurstCadenceSec = 120.0;
    private const double AutoBurstEarlyWindowSec = 10.0;
    private const double AutoBurstLateWindowSec = 15.0;
    private const double AutoStructuredLookaheadSec = 1.75;
    private const double AutoSafeFallbackLookaheadSec = 4.5;
private const double AutoFutureLegalLookaheadSec = 6.0;
private const double AutoHighConfidenceGcdFrequencyPct = 25.0;
private const double AutoStateDrivenOgcdMinFrequency = 0.10;
private const double AutoGcdMinFrequency = 0.10;
private const double AutoGcdSlotMatchToleranceSec = 0.01;

    private sealed class AutoTimelineState
    {
        public List<TimelineEntry> SelectedEntries { get; set; } = [];
        public Dictionary<string, List<double>> CooldownQueues { get; set; } = [];
        public Dictionary<string, int> GaugeState { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> GrantedState { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ActionState { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> PassiveGaugeProgress { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> TimedWindowEndByAbility { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> TimedLockoutEndByAbility { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AvailableAbilityNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string SpecName { get; set; } = string.Empty;
        public string? CardDrawState { get; set; }
        public uint LastGcdId { get; set; }
        public string? LastComboAbilityName { get; set; }
        public string? LastSelectedGcdAbilityName { get; set; }
        public double LastComboTimeSec { get; set; } = double.NegativeInfinity;
        public double LastOgcdTimeSec { get; set; } = double.NegativeInfinity;
        public double CastLockUntilSec { get; set; }
        public double LastTimeSec { get; set; }
        public int NextOgcdIndex { get; set; }
        public double Score { get; set; }

        public AutoTimelineState Clone()
        {
            var cooldowns = new Dictionary<string, List<double>>(CooldownQueues.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (cooldownKey, queue) in CooldownQueues)
                cooldowns[cooldownKey] = [.. queue];

            return new AutoTimelineState
            {
                SelectedEntries = [.. SelectedEntries],
                CooldownQueues = cooldowns,
                GaugeState = new Dictionary<string, int>(GaugeState, StringComparer.OrdinalIgnoreCase),
                GrantedState = new Dictionary<string, int>(GrantedState, StringComparer.OrdinalIgnoreCase),
                ActionState = new Dictionary<string, int>(ActionState, StringComparer.OrdinalIgnoreCase),
                PassiveGaugeProgress = new Dictionary<string, double>(PassiveGaugeProgress, StringComparer.OrdinalIgnoreCase),
                TimedWindowEndByAbility = new Dictionary<string, double>(TimedWindowEndByAbility, StringComparer.OrdinalIgnoreCase),
                TimedLockoutEndByAbility = new Dictionary<string, double>(TimedLockoutEndByAbility, StringComparer.OrdinalIgnoreCase),
                AvailableAbilityNames = new HashSet<string>(AvailableAbilityNames, StringComparer.OrdinalIgnoreCase),
                SpecName = SpecName,
                CardDrawState = CardDrawState,
                LastGcdId = LastGcdId,
                LastComboAbilityName = LastComboAbilityName,
                LastSelectedGcdAbilityName = LastSelectedGcdAbilityName,
                LastComboTimeSec = LastComboTimeSec,
                LastOgcdTimeSec = LastOgcdTimeSec,
                CastLockUntilSec = CastLockUntilSec,
                LastTimeSec = LastTimeSec,
                NextOgcdIndex = NextOgcdIndex,
                Score = Score,
            };
        }
    }

    private sealed class AutoTimelineBuildResult
    {
        public required List<TimelineEntry> Entries { get; init; }
        public string DebugReport { get; init; } = string.Empty;
    }

    private sealed class AutoGaugeLegalitySegmentDecision
    {
        public required TimelineEntry Entry { get; init; }
        public required bool Keep { get; init; }
        public required string Summary { get; init; }
        public string? BeforeGaugeState { get; init; }
        public string? AfterGaugeState { get; init; }
        public bool UsedOpenerBorrow { get; init; }
    }

    private sealed class AutoGaugeLegalityBranch
    {
        public required AutoTimelineState State { get; init; }
        public required List<AutoGaugeLegalitySegmentDecision> Decisions { get; init; }
        public required double Score { get; init; }
    }

    private sealed class AutoGrantedChildRule
    {
        public string ChildAbilityName { get; init; } = string.Empty;
        public HashSet<string> ParentAbilityNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ConsumerAbilityNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public int AllowedUsesPerParentWindow { get; init; } = 1;
        public AutoGrantedWindowSelectionMode WindowSelectionMode { get; init; } = AutoGrantedWindowSelectionMode.FirstChronological;
    }

    private enum AutoGrantedWindowSelectionMode
    {
        FirstChronological,
        HighestFrequencyPerParentWindow,
    }

    private sealed class AutoComboBranchContext
    {
        public string StarterAbilityName { get; init; } = string.Empty;
        public int StarterSlotIndex { get; init; }
        public double LastMatchedTimeSec { get; init; }
        public double LastMatchedRecastSec { get; init; } = AutoTargetGcdSec;
        public int NextStepIndex { get; init; }
        public IReadOnlyList<string> ChosenLine { get; init; } = [];
        public HashSet<string> BlockedAbilityNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string ChosenLineSummary { get; init; } = string.Empty;
    }

    private sealed class AutoOpenerBufferInfo
    {
        public bool Enabled { get; init; }
        public string VariantName { get; init; } = string.Empty;
        public int StartSlotOffset { get; init; }
        public int GcdSlotCount { get; init; }
        public double EndTimeSec { get; init; }
        public IReadOnlyList<string> GcdSequence { get; init; } = [];
        public IReadOnlyList<BalanceOpenerDatabase.OpenerStep> Steps { get; init; } = [];
    }

    private sealed class AutoTimelineDebugRecorder
    {
        private readonly List<string> lines = [];

        public void Add(string line = "")
            => lines.Add(line);

        public string Build()
            => string.Join(Environment.NewLine, lines);
    }

    private sealed class AutoGcdCandidateEvaluation
    {
        public required TimelineEntry Entry { get; init; }
        public required double ScheduledTimeSec { get; init; }
        public required bool IsLegal { get; init; }
        public required bool ComboMismatch { get; init; }
        public required bool CooldownBlocked { get; init; }
        public required double CooldownEarlyBySec { get; init; }
        public required int GaugePenalty { get; init; }
        public required int ActionPenalty { get; init; }
        public required int CardPenalty { get; init; }
        public required int Penalty { get; init; }
        public required double Score { get; init; }
        public string FailureReason { get; init; } = string.Empty;
    }

    private sealed class AutoGrantedCandidateDecision
    {
        public bool IsTracked { get; init; }
        public bool IsAllowed { get; init; }
        public string Summary { get; init; } = string.Empty;
        public AutoGrantedChildRule? ChildRule { get; init; }
        public TimelineEntry? ParentEntry { get; init; }
        public TimelineEntry? ExistingWindowConsumerEntry { get; init; }
        public bool UsesVirtualParentWindow { get; init; }
    }

    private static readonly Dictionary<string, string[]> StateAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hawk's Eye"] = ["Refulgent Arrow Ready"],
        ["Threefold Fan Dance"] = ["Fan Dance III Ready"],
        ["Fourfold Fan Dance"] = ["Fan Dance III Ready"],
        ["Hypervelocity Ready"] = ["Ready to Blast"],
        ["Jugular Rip Ready"] = ["Ready to Rip"],
        ["Abdomen Tear Ready"] = ["Ready to Tear"],
        ["Eye Gouge Ready"] = ["Ready to Gouge"],
        ["Oracle Ready"] = ["Divining"],
        ["Glare IV Ready"] = ["Sacred Sight"],
    };

    private static readonly Dictionary<string, string[]> ComboPredecessorAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["True Thrust"] = ["Raiden Thrust"],
    };

    private static readonly HashSet<string> ComboValidationExemptAbilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "Drakesbane",
        "Gekko",
        "Kasha",
        "Solid Barrel",
    };

    private static IEnumerable<string> EnumerateEquivalentStateNames(string stateName)
    {
        yield return stateName;
        if (StateAliases.TryGetValue(stateName, out var aliases))
        {
            foreach (var alias in aliases)
                yield return alias;
        }
    }

    private bool ShouldIgnoreConflictRequirement(string specName, string requirementName)
    {
        if (!ConflictIgnoredRequirementNamesBySpec.TryGetValue(specName, out var ignoredNames))
            return false;

        foreach (var equivalentStateName in EnumerateEquivalentStateNames(requirementName))
        {
            if (ignoredNames.Contains(equivalentStateName))
                return true;
        }

        return false;
    }

    private bool ShouldIgnoreConflictCooldown(string specName, string abilityName)
    {
        return ConflictIgnoredCooldownNamesBySpec.TryGetValue(specName, out var ignoredNames) &&
               ignoredNames.Contains(abilityName);
    }

    private static string GetCooldownBucketKey(
        Data.RecastDatabase.RecastInfo? info,
        int fallbackAbilityId = 0,
        string fallbackAbilityName = "")
    {
        if (info != null && info.AbilityId > 0)
            return $"ability:{info.AbilityId}";
        if (fallbackAbilityId > 0)
            return $"ability:{fallbackAbilityId}";

        return $"name:{fallbackAbilityName}";
    }

    private void ApplyAutoTimeline(AggregatedTimeline tl)
    {
        var timelineKey = TimelineDatabase.MakeKey(tl.EncounterId, tl.SpecName);
        var isCustomTimeline = plugin.Configuration.CustomTimelines.ContainsKey(timelineKey);
        var allowedOutputAbilityIds = tl.Entries
            .Select(entry => entry.AbilityId)
            .ToHashSet();
        var sourceTimeline = PrepareAutoTimelineSourceClone(tl);
        var result = BuildAutoTimelineResult(sourceTimeline, captureDebug: false);
        var finalEntries = isCustomTimeline
            ? FilterAutoTimelineResultEntries(result.Entries, allowedOutputAbilityIds)
            : result.Entries;
        tl.Entries = finalEntries;
    }

    private AutoTimelineBuildResult BuildAutoTimelineResult(AggregatedTimeline tl, bool captureDebug)
    {
        var debug = captureDebug ? new AutoTimelineDebugRecorder() : null;
        var gcdEntries = tl.Entries
            .Where(IsGcdEntry)
            .OrderBy(e => e.TimeOffsetSec)
            .ThenByDescending(e => e.Frequency)
            .ToList();

        var ogcdEntries = tl.Entries
            .Where(e => !IsGcdEntry(e))
            .OrderBy(e => e.TimeOffsetSec)
            .ThenByDescending(e => e.Frequency)
            .ToList();

        debug?.Add($"Auto Timeline | {tl.EncounterName} / {tl.SpecName}");
        debug?.Add($"Duration: {tl.AverageDurationMs / 1000.0:F3}s");
        debug?.Add($"Source entries: {tl.Entries.Count} | GCD: {gcdEntries.Count} | oGCD: {ogcdEntries.Count}");
        debug?.Add($"Rules: GCD >= {(AutoGcdMinFrequency * 100.0):F1}% | oGCD >= {(AutoStateDrivenOgcdMinFrequency * 100.0):F1}% | slot width {AutoTargetGcdSec:F2}s | cooldown tolerance {AutoCooldownToleranceSec:F2}s");
        debug?.Add("Legend: keep = selected by the current pass, prune/block = removed by a rule, lose = eligible but beaten by another candidate in the same choice.");
        debug?.Add();
        var rawAbilityNames = tl.Entries
            .Select(entry => entry.AbilityName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fightDurationSec = Math.Max(0.0, tl.AverageDurationMs / 1000.0);
        var openerBuffer = BuildAutoOpenerBufferInfo(gcdEntries, tl.SpecName, debug);

        var selectedOgcdEntries = SelectAutoOgcdEntries(
            ogcdEntries,
            tl.SpecName,
            openerBuffer,
            debug);
        var selectedGcdEntries = SelectAutoGcdEntries(
            gcdEntries,
            selectedOgcdEntries,
            tl.SpecName,
            fightDurationSec,
            openerBuffer,
            debug);
        (selectedGcdEntries, selectedOgcdEntries) = ApplyGrantedActionSelections(
            selectedGcdEntries,
            selectedOgcdEntries,
            tl.SpecName,
            openerBuffer,
            debug);
        var finalEntries = BuildAutoTimelineWithFixedGcds(
            selectedGcdEntries,
            selectedOgcdEntries,
            tl.SpecName,
            rawAbilityNames,
            openerBuffer,
            debug);

        debug?.Add();
        debug?.Add($"Final selected GCDs: {selectedGcdEntries.Count}");
        debug?.Add($"Final selected oGCDs: {selectedOgcdEntries.Count}");
        debug?.Add($"Final scheduled entries: {finalEntries.Count}");

        return new AutoTimelineBuildResult
        {
            Entries = finalEntries,
            DebugReport = debug?.Build() ?? string.Empty,
        };
    }

    private List<TimelineEntry> SelectAutoGcdEntries(
        List<TimelineEntry> gcdEntries,
        List<TimelineEntry> selectedOgcdEntries,
        string specName,
        double fightDurationSec,
        AutoOpenerBufferInfo openerBuffer,
        AutoTimelineDebugRecorder? debug = null)
    {
        debug?.Add("GCD Selection");
        var comboHints = JobComboDatabase.GetHints(specName);
        var grantedRules = GrantedActionDatabase.GetRules(specName);
        var grantedChildRules = BuildGrantedChildRules(gcdEntries.Concat(selectedOgcdEntries), grantedRules);
        var keptEntries = new List<TimelineEntry>();
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockedUntilByAbility = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        AutoComboBranchContext? activeComboBranch = null;
        var sortedGcdEntries = gcdEntries
            .Where(entry => entry.TimeOffsetSec <= fightDurationSec + AutoCooldownToleranceSec)
            .OrderBy(e => e.TimeOffsetSec)
            .ThenByDescending(e => e.Frequency)
            .ToList();
        if (sortedGcdEntries.Count == 0)
        {
            debug?.Add("  No GCD entries available.");
            debug?.Add();
            return keptEntries;
        }

        debug?.Add(openerBuffer.Enabled
            ? $"  opener guideline active; variant {openerBuffer.VariantName} starts at opener slot {openerBuffer.StartSlotOffset} and runs until {FormatTime(openerBuffer.EndTimeSec)}"
            : "  opener guideline inactive; no opener buffer is active");

        var nextSlotTimeSec = 0.0;
        var slotIndex = 0;
        while (nextSlotTimeSec <= fightDurationSec + AutoCooldownToleranceSec)
        {
            if (IsAutoComboContextExpired(activeComboBranch, nextSlotTimeSec))
                activeComboBranch = null;

            var openerRequirement = GetAutoOpenerSlotRequirement(openerBuffer, slotIndex);
            var exactSlotCandidates = GetAutoGcdSlotCandidates(sortedGcdEntries, usedKeys, nextSlotTimeSec);
            var rawSlotCandidates = new List<TimelineEntry>(exactSlotCandidates);
            var openerLookaroundCount = 0;
            if (!string.IsNullOrWhiteSpace(openerRequirement))
            {
                foreach (var openerCandidate in GetAutoOpenerRequirementCandidates(sortedGcdEntries, usedKeys, nextSlotTimeSec, openerRequirement))
                {
                    if (rawSlotCandidates.Any(existing =>
                            string.Equals(GetAutoEntryIdentityKey(existing), GetAutoEntryIdentityKey(openerCandidate), StringComparison.OrdinalIgnoreCase)))
                        continue;

                    rawSlotCandidates.Add(openerCandidate);
                    openerLookaroundCount++;
                }
            }
            var comboLookaroundCount = 0;
            foreach (var comboCandidate in GetAutoComboContinuationCandidates(sortedGcdEntries, usedKeys, activeComboBranch, nextSlotTimeSec))
            {
                if (rawSlotCandidates.Any(existing =>
                        string.Equals(GetAutoEntryIdentityKey(existing), GetAutoEntryIdentityKey(comboCandidate), StringComparison.OrdinalIgnoreCase)))
                    continue;

                rawSlotCandidates.Add(comboCandidate);
                comboLookaroundCount++;
            }

            debug?.Add($"  slot | #{slotIndex:00} | {FormatAutoDebugSlotWindow(nextSlotTimeSec)} | raw candidates {exactSlotCandidates.Count}");
            if (IsWithinAutoOpenerBuffer(openerBuffer, nextSlotTimeSec))
                debug?.Add($"    opener buffer | active until {FormatTime(openerBuffer.EndTimeSec)}; shifted opener, granted, and gauge rules may borrow pre-pull state here");
            if (!string.IsNullOrWhiteSpace(openerRequirement))
                debug?.Add($"    opener requirement | expects {openerRequirement} from {openerBuffer.VariantName}");
            if (openerLookaroundCount > 0)
                debug?.Add($"    opener lookaround | added {openerLookaroundCount} matching candidate(s) from {FormatTime(Math.Max(0.0, nextSlotTimeSec - AutoTargetGcdSec))}-{FormatTime(nextSlotTimeSec + AutoTargetGcdSec)}");
            if (TryGetAutoComboContinuationWindow(activeComboBranch, out var comboWindowStartSec, out var comboWindowEndSec) &&
                GetAutoComboNextAbility(activeComboBranch) is { } comboContinuationAbility)
                debug?.Add($"    combo window | next {comboContinuationAbility} is favored in {FormatTime(comboWindowStartSec)}-{FormatTime(comboWindowEndSec)}");
            if (comboLookaroundCount > 0)
                debug?.Add($"    combo lookaround | added {comboLookaroundCount} continuation candidate(s) from the active combo window");
            if (activeComboBranch != null && !string.IsNullOrWhiteSpace(activeComboBranch.ChosenLineSummary))
                debug?.Add($"    combo context | {activeComboBranch.ChosenLineSummary}");

            var slotCandidates = new List<TimelineEntry>();
            foreach (var candidate in rawSlotCandidates)
            {
                if (!string.IsNullOrWhiteSpace(openerRequirement) &&
                    !DoesOpenerAbilityMatch(candidate.AbilityName, openerRequirement))
                {
                    debug?.Add($"    block | {FormatAutoDebugEntry(candidate)} | blocked by opener requirement: slot expects {openerRequirement} from {openerBuffer.VariantName}");
                    continue;
                }

                var blockers = GetGcdCandidateBlockers(
                    candidate,
                    nextSlotTimeSec,
                    keptEntries,
                    selectedOgcdEntries,
                    grantedChildRules,
                    activeComboBranch,
                    blockedUntilByAbility,
                    openerBuffer,
                    openerRequirement);
                if (blockers.Count > 0)
                {
                    debug?.Add($"    block | {FormatAutoDebugEntry(candidate)} | {string.Join("; ", blockers)}");
                    continue;
                }

                slotCandidates.Add(candidate);
                debug?.Add($"    allow | {FormatAutoDebugEntry(candidate)} | {GetGcdCandidateEligibilitySummary(candidate, nextSlotTimeSec, keptEntries, selectedOgcdEntries, grantedChildRules, comboHints, activeComboBranch, openerBuffer, openerRequirement)}");
            }

            TimelineEntry? chosenEntry = null;
            if (slotCandidates.Count > 0)
            {
                chosenEntry = FindDueComboContinuationCandidate(
                    slotCandidates,
                    activeComboBranch);
            }

            if (chosenEntry == null && slotCandidates.Count > 0)
            {
                chosenEntry = FindDueGrantedChildCandidate(
                    slotCandidates,
                    keptEntries,
                    selectedOgcdEntries,
                    grantedChildRules,
                    openerBuffer);
            }

            if (chosenEntry == null)
            {
                chosenEntry = FindDueDotCandidate(slotCandidates, keptEntries);
            }

            if (chosenEntry == null)
            {
                chosenEntry = slotCandidates
                    .OrderByDescending(entry => entry.Frequency)
                    .ThenBy(entry => entry.TimeOffsetSec)
                    .FirstOrDefault();
            }

            if (chosenEntry != null)
            {
                var isComboContinuation = IsComboContinuationMatch(chosenEntry, activeComboBranch);
                var winnerReason = BuildGcdSelectionReason(
                    chosenEntry,
                    nextSlotTimeSec,
                    slotCandidates,
                    keptEntries,
                    selectedOgcdEntries,
                    grantedChildRules,
                    comboHints,
                    activeComboBranch,
                    openerBuffer,
                    openerRequirement);
                var scheduledChosenEntry = KeepAutoGcdSelection(
                    chosenEntry,
                    nextSlotTimeSec,
                    keptEntries,
                    usedKeys,
                    blockedUntilByAbility,
                    comboHints,
                    debug,
                    isComboContinuation
                        ? "combo follow"
                        : grantedChildRules.ContainsKey(chosenEntry.AbilityName)
                        ? "granted"
                        : DotDatabase.Lookup(chosenEntry.AbilityName) != null
                            ? "dot"
                            : JobComboDatabase.IsComboStarter(chosenEntry.AbilityName)
                                ? "combo start"
                                : "generic",
                    winnerReason);

                foreach (var losingCandidate in slotCandidates.Where(candidate =>
                             !string.Equals(GetAutoEntryIdentityKey(candidate), GetAutoEntryIdentityKey(chosenEntry), StringComparison.OrdinalIgnoreCase)))
                {
                    debug?.Add($"    lose | {FormatAutoDebugEntry(losingCandidate)} | eligible, but {chosenEntry.AbilityName} won this slot because {winnerReason}");
                }

                if (JobComboDatabase.IsComboStarter(chosenEntry.AbilityName))
                {
                    activeComboBranch = SelectAutoComboBranchContext(
                        comboHints,
                        chosenEntry.AbilityName,
                        slotIndex,
                        scheduledChosenEntry.TimeOffsetSec,
                        sortedGcdEntries,
                        usedKeys,
                        debug);
                }
                else
                {
                    activeComboBranch = AdvanceAutoComboBranchContext(
                        activeComboBranch,
                        scheduledChosenEntry);
                }
            }
            else
            {
                debug?.Add(rawSlotCandidates.Count == 0
                    ? $"    result | no in-window entries above {(AutoGcdMinFrequency * 100.0):F1}% remained for this slot"
                    : !string.IsNullOrWhiteSpace(openerRequirement)
                        ? $"    result | opener requirement expected {openerRequirement}, but no matching candidate survived this slot"
                        : "    result | every in-window candidate was blocked by an active pruning rule");
                debug?.Add($"  slot-drop | {FormatTime(nextSlotTimeSec)} | no candidate");
            }

            var chosenSlotIntervalSec = chosenEntry != null
                ? GetAutoTimelineGcdRecastSec(chosenEntry)
                : AutoTargetGcdSec;
            var nextSlotAnchorSec = GetNextAutoGcdSlotStartSec(nextSlotTimeSec, chosenEntry, chosenSlotIntervalSec);
            if (chosenEntry != null &&
                Math.Abs(nextSlotAnchorSec - (nextSlotTimeSec + AutoTargetGcdSec)) > 0.01)
            {
                debug?.Add($"  slot-anchor | next slot re-anchored to {FormatTime(nextSlotAnchorSec)} from kept {FormatAutoDebugEntry(chosenEntry)} to preserve GCD cadence");
            }

            slotIndex++;
            nextSlotTimeSec = nextSlotAnchorSec;
        }

        debug?.Add();
        return keptEntries
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private (List<TimelineEntry> GcdEntries, List<TimelineEntry> OgcdEntries) ApplyGrantedActionSelections(
        List<TimelineEntry> baseSelectedGcdEntries,
        List<TimelineEntry> baseSelectedOgcdEntries,
        string specName,
        AutoOpenerBufferInfo openerBuffer,
        AutoTimelineDebugRecorder? debug = null)
    {
        debug?.Add("Granted Actions");
        var grantedRules = GrantedActionDatabase.GetRules(specName);
        var grantedChildRules = BuildGrantedChildRules(baseSelectedGcdEntries.Concat(baseSelectedOgcdEntries), grantedRules);
        if (grantedChildRules.Count == 0)
        {
            debug?.Add("  none configured");
            debug?.Add();
            return (baseSelectedGcdEntries, baseSelectedOgcdEntries);
        }

        if (openerBuffer.Enabled)
            debug?.Add($"  opener buffer | missing parents before {FormatTime(openerBuffer.EndTimeSec)} may be borrowed from pre-pull state once per child window");

        var keptEntries = new List<TimelineEntry>();
        foreach (var entry in baseSelectedGcdEntries
                     .Concat(baseSelectedOgcdEntries)
                     .OrderBy(item => item.TimeOffsetSec)
                     .ThenByDescending(item => item.Frequency))
        {
            var grantedDecision = AnalyzeGrantedCandidateDecision(entry, keptEntries, grantedChildRules, openerBuffer);
            if (!grantedDecision.IsTracked)
            {
                keptEntries.Add(entry);
                continue;
            }

            if (!grantedDecision.IsAllowed)
            {
                debug?.Add($"  prune | {FormatAutoDebugEntry(entry)} | {grantedDecision.Summary}");
                continue;
            }

            keptEntries.Add(entry);
            debug?.Add($"  keep | {FormatAutoDebugEntry(entry)} | {grantedDecision.Summary}");
        }

        debug?.Add();
        return (
            keptEntries.Where(IsGcdEntry).OrderBy(entry => entry.TimeOffsetSec).ThenByDescending(entry => entry.Frequency).ToList(),
            keptEntries.Where(entry => !IsGcdEntry(entry)).OrderBy(entry => entry.TimeOffsetSec).ThenByDescending(entry => entry.Frequency).ToList());
    }

    private void RemoveConflictingSelectedGcdEntries(
        List<TimelineEntry> selected,
        HashSet<string> usedKeys,
        TimelineEntry replacement,
        AutoTimelineDebugRecorder? debug)
    {
        var removals = selected
            .Where(entry =>
                IsGcdEntry(entry) &&
                !string.Equals(entry.AbilityName, replacement.AbilityName, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(entry.TimeOffsetSec - replacement.TimeOffsetSec) < AutoTargetGcdSec - AutoCooldownToleranceSec)
            .ToList();

        foreach (var removal in removals)
        {
            selected.Remove(removal);
            usedKeys.Remove(GetAutoEntryIdentityKey(removal));
            debug?.Add($"  prune | {removal.AbilityName} @ {FormatTime(removal.TimeOffsetSec)} | replaced by granted child {replacement.AbilityName}");
        }
    }

    private List<TimelineEntry> BuildAutoTimelineWithFixedGcds(
        List<TimelineEntry> gcdEntries,
        List<TimelineEntry> selectedOgcdEntries,
        string specName,
        ISet<string> rawAbilityNames,
        AutoOpenerBufferInfo openerBuffer,
        AutoTimelineDebugRecorder? debug = null)
    {
        debug?.Add("Gauge Legality");
        var gaugeRules = GaugeSimulator.GetRules(specName);
        var state = CreateAutoTimelineState(specName, gaugeRules, grantedRules: null, rawAbilityNames);
        var finalEntries = new List<TimelineEntry>();
        var orderedEntries = gcdEntries
            .Concat(selectedOgcdEntries)
            .OrderBy(item => item.TimeOffsetSec)
            .ThenByDescending(item => item.Frequency)
            .ToList();
        if (openerBuffer.Enabled)
            debug?.Add($"  opener buffer | numeric gauge minimums before {FormatTime(openerBuffer.EndTimeSec)} may borrow pre-pull resources");

        if (gaugeRules != null && orderedEntries.Any(entry => IsAutoTimelineTrueGaugeAbility(entry.AbilityName, gaugeRules)))
        {
            for (var index = 0; index < orderedEntries.Count; index++)
            {
                var entry = orderedEntries[index];
                var decision = EvaluateAutoGaugeLegalityDecision(state, gaugeRules, specName, openerBuffer, entry);
                if (decision.Keep)
                {
                    finalEntries.Add(entry);
                    if (decision.BeforeGaugeState == null)
                        debug?.Add($"  keep | {FormatAutoDebugEntry(entry)} | {decision.Summary}");
                    else
                        debug?.Add($"  keep | {FormatAutoDebugEntry(entry)} | {decision.Summary} | before {decision.BeforeGaugeState} -> after {decision.AfterGaugeState}");
                }
                else
                {
                    var stateText = decision.BeforeGaugeState == null
                        ? string.Empty
                        : $" | state {decision.BeforeGaugeState}";
                    debug?.Add($"  prune | {FormatAutoDebugEntry(entry)} | {decision.Summary}{stateText}");
                }

                if (!decision.Keep ||
                    !TryGetAutoGaugeRefillResourceNames(entry.AbilityName, gaugeRules, out var refillGaugeNames))
                    continue;

                var nextRefillIndex = FindNextAutoGaugeRefillIndex(orderedEntries, index + 1, gaugeRules, refillGaugeNames);
                if (nextRefillIndex <= index + 1)
                    continue;

                var (segmentEntries, updatedState) = OptimizeAutoGaugeSegment(
                    orderedEntries,
                    index + 1,
                    nextRefillIndex,
                    state,
                    gaugeRules,
                    openerBuffer,
                    refillGaugeNames);
                foreach (var segmentDecision in segmentEntries)
                {
                    if (segmentDecision.Keep)
                    {
                        finalEntries.Add(segmentDecision.Entry);
                        if (segmentDecision.BeforeGaugeState == null)
                            debug?.Add($"  keep | {FormatAutoDebugEntry(segmentDecision.Entry)} | {segmentDecision.Summary}");
                        else
                            debug?.Add($"  keep | {FormatAutoDebugEntry(segmentDecision.Entry)} | {segmentDecision.Summary} | before {segmentDecision.BeforeGaugeState} -> after {segmentDecision.AfterGaugeState}");
                    }
                    else
                    {
                        var stateText = segmentDecision.BeforeGaugeState == null
                            ? string.Empty
                            : $" | state {segmentDecision.BeforeGaugeState}";
                        debug?.Add($"  prune | {FormatAutoDebugEntry(segmentDecision.Entry)} | {segmentDecision.Summary}{stateText}");
                    }
                }

                state = updatedState;
                index = nextRefillIndex - 1;
            }
        }
        else
        {
            foreach (var entry in orderedEntries)
            {
                var decision = EvaluateAutoGaugeLegalityDecision(state, gaugeRules, specName, openerBuffer, entry);
                if (!decision.Keep)
                {
                    var stateText = decision.BeforeGaugeState == null
                        ? string.Empty
                        : $" | state {decision.BeforeGaugeState}";
                    debug?.Add($"  prune | {FormatAutoDebugEntry(entry)} | {decision.Summary}{stateText}");
                    continue;
                }

                finalEntries.Add(entry);
                if (decision.BeforeGaugeState == null)
                    debug?.Add($"  keep | {FormatAutoDebugEntry(entry)} | {decision.Summary}");
                else
                    debug?.Add($"  keep | {FormatAutoDebugEntry(entry)} | {decision.Summary} | before {decision.BeforeGaugeState} -> after {decision.AfterGaugeState}");
            }
        }

        debug?.Add();
        List<string>? postSelectionDebugNotes = debug == null ? null : new List<string>();
        var postSelectionEntries = TimelineJobRules.ApplyPostSelectionRules(
            specName,
            finalEntries,
            promoteMacrocosmosToVisualGcd: false,
            debugNotes: postSelectionDebugNotes);
        if (postSelectionDebugNotes is { Count: > 0 })
        {
            debug.Add("Post-Selection Job Rules");
            foreach (var note in postSelectionDebugNotes)
                debug.Add(note);
            debug.Add();
        }

        return postSelectionEntries
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private AutoGaugeLegalitySegmentDecision EvaluateAutoGaugeLegalityDecision(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        string specName,
        AutoOpenerBufferInfo openerBuffer,
        TimelineEntry entry)
    {
        AdvancePassiveResources(state, gaugeRules, entry.TimeOffsetSec);
        var relevantGaugeNames = GetRelevantGaugeNames(gaugeRules, entry.AbilityName);
        var beforeGaugeState = relevantGaugeNames.Count == 0
            ? null
            : FormatGaugeStateForDebug(state, relevantGaugeNames);
        var insufficiencyReason = GetNumericGaugeInsufficiencyReason(state, gaugeRules, entry.AbilityName);
        var timedWindowReason = GetTimedUsageWindowReason(
            specName,
            state.TimedWindowEndByAbility,
            entry.AbilityName,
            entry.TimeOffsetSec);
        var timedLockoutReason = GetTimedUsageLockoutReason(
            specName,
            state.TimedLockoutEndByAbility,
            entry.AbilityName,
            entry.TimeOffsetSec);
        var scholarBurstReservationReason = GetScholarBurstAetherflowReservationReason(
            specName,
            state.TimedWindowEndByAbility,
            state.GaugeState,
            entry.AbilityName,
            entry.TimeOffsetSec);
        var rejectionReason = insufficiencyReason ?? timedWindowReason ?? timedLockoutReason ?? scholarBurstReservationReason;
        var canBorrowPrepullGauge = insufficiencyReason != null &&
                                    IsWithinAutoOpenerBuffer(openerBuffer, entry.TimeOffsetSec) &&
                                    !IsAutoTimelineTrueGaugeSpender(entry.AbilityName, gaugeRules) &&
                                    CanBorrowAutoOpenerPrepullGauge(state, gaugeRules, entry.AbilityName);
        if (rejectionReason != null)
        {
            if (canBorrowPrepullGauge)
            {
                ApplyNumericGaugeEffects(state, gaugeRules, entry.AbilityName, allowPrepullSeed: true);
                ApplyTimedUsageWindowState(specName, state.TimedWindowEndByAbility, entry.AbilityName, entry.TimeOffsetSec);
                ApplyTimedUsageLockoutState(specName, state.TimedLockoutEndByAbility, entry.AbilityName, entry.TimeOffsetSec);
                return new AutoGaugeLegalitySegmentDecision
                {
                    Entry = entry,
                    Keep = true,
                    Summary = $"opener buffer borrowed pre-pull gauge because {insufficiencyReason}",
                    BeforeGaugeState = beforeGaugeState,
                    AfterGaugeState = relevantGaugeNames.Count == 0 ? null : FormatGaugeStateForDebug(state, relevantGaugeNames),
                    UsedOpenerBorrow = true,
                };
            }

            return new AutoGaugeLegalitySegmentDecision
            {
                Entry = entry,
                Keep = false,
                Summary = rejectionReason,
                BeforeGaugeState = beforeGaugeState,
            };
        }

        ApplyNumericGaugeEffects(state, gaugeRules, entry.AbilityName);
        ApplyTimedUsageWindowState(specName, state.TimedWindowEndByAbility, entry.AbilityName, entry.TimeOffsetSec);
        ApplyTimedUsageLockoutState(specName, state.TimedLockoutEndByAbility, entry.AbilityName, entry.TimeOffsetSec);
        return new AutoGaugeLegalitySegmentDecision
        {
            Entry = entry,
            Keep = true,
            Summary = relevantGaugeNames.Count == 0 ? "no numeric gauge rule" : "gauge ok",
            BeforeGaugeState = beforeGaugeState,
            AfterGaugeState = relevantGaugeNames.Count == 0 ? null : FormatGaugeStateForDebug(state, relevantGaugeNames),
        };
    }

    private static bool TryGetAutoGaugeRefillResourceNames(
        string abilityName,
        GaugeSimulator.JobGaugeRules gaugeRules,
        out IReadOnlyList<string> gaugeNames)
    {
        gaugeNames = [];
        if (!gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
            return false;

        var refillGaugeNames = effects
            .Where(effect =>
                GaugeSimulator.IsTrueGaugeResource(effect.GaugeName) &&
                (effect.SetValue is int || effect.Delta > 0))
            .Select(effect => effect.GaugeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (refillGaugeNames.Count == 0)
            return false;

        gaugeNames = refillGaugeNames;
        return true;
    }

    private static bool IsAutoGaugeSegmentSpender(
        string abilityName,
        GaugeSimulator.JobGaugeRules gaugeRules,
        IReadOnlyCollection<string> gaugeNames)
    {
        if (!gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
            return false;

        return effects.Any(effect =>
            gaugeNames.Contains(effect.GaugeName) &&
            GaugeSimulator.IsTrueGaugeResource(effect.GaugeName) &&
            (effect.MinRequired > 0 || effect.Delta < 0));
    }

    private static int FindNextAutoGaugeRefillIndex(
        IReadOnlyList<TimelineEntry> orderedEntries,
        int startIndex,
        GaugeSimulator.JobGaugeRules gaugeRules,
        IReadOnlyCollection<string> gaugeNames)
    {
        for (var index = startIndex; index < orderedEntries.Count; index++)
        {
            if (!TryGetAutoGaugeRefillResourceNames(orderedEntries[index].AbilityName, gaugeRules, out var refillGaugeNames))
                continue;

            if (refillGaugeNames.Any(gaugeNames.Contains))
                return index;
        }

        return orderedEntries.Count;
    }

    private (List<AutoGaugeLegalitySegmentDecision> Decisions, AutoTimelineState UpdatedState) OptimizeAutoGaugeSegment(
        IReadOnlyList<TimelineEntry> orderedEntries,
        int startIndexInclusive,
        int endIndexExclusive,
        AutoTimelineState startingState,
        GaugeSimulator.JobGaugeRules gaugeRules,
        AutoOpenerBufferInfo openerBuffer,
        IReadOnlyCollection<string> gaugeNames)
    {
        var branches = new List<AutoGaugeLegalityBranch>
        {
            new()
            {
                State = startingState.Clone(),
                Decisions = [],
                Score = 0.0,
            },
        };

        for (var index = startIndexInclusive; index < endIndexExclusive; index++)
        {
            var entry = orderedEntries[index];
            var nextBranches = new List<AutoGaugeLegalityBranch>();
            foreach (var branch in branches)
            {
                var evaluationState = branch.State.Clone();
                var decision = EvaluateAutoGaugeLegalityDecision(
                    evaluationState,
                    gaugeRules,
                    startingState.SpecName,
                    openerBuffer,
                    entry);

                if (!IsAutoGaugeSegmentSpender(entry.AbilityName, gaugeRules, gaugeNames))
                {
                    var decisions = new List<AutoGaugeLegalitySegmentDecision>(branch.Decisions.Count + 1);
                    decisions.AddRange(branch.Decisions);
                    decisions.Add(decision);
                    nextBranches.Add(new AutoGaugeLegalityBranch
                    {
                        State = evaluationState,
                        Decisions = decisions,
                        Score = branch.Score,
                    });
                    continue;
                }

                var prunedDecisions = new List<AutoGaugeLegalitySegmentDecision>(branch.Decisions.Count + 1);
                prunedDecisions.AddRange(branch.Decisions);
                prunedDecisions.Add(new AutoGaugeLegalitySegmentDecision
                {
                    Entry = entry,
                    Keep = false,
                    Summary = "stronger gauge spenders won this refill window",
                    BeforeGaugeState = decision.BeforeGaugeState,
                });
                nextBranches.Add(new AutoGaugeLegalityBranch
                {
                    State = branch.State.Clone(),
                    Decisions = prunedDecisions,
                    Score = branch.Score,
                });

                if (!decision.Keep)
                {
                    var illegalDecisions = new List<AutoGaugeLegalitySegmentDecision>(branch.Decisions.Count + 1);
                    illegalDecisions.AddRange(branch.Decisions);
                    illegalDecisions.Add(decision);
                    nextBranches.Add(new AutoGaugeLegalityBranch
                    {
                        State = branch.State.Clone(),
                        Decisions = illegalDecisions,
                        Score = branch.Score - 5000.0,
                    });
                    continue;
                }

                var keptDecisions = new List<AutoGaugeLegalitySegmentDecision>(branch.Decisions.Count + 1);
                keptDecisions.AddRange(branch.Decisions);
                keptDecisions.Add(decision);
                nextBranches.Add(new AutoGaugeLegalityBranch
                {
                    State = evaluationState,
                    Decisions = keptDecisions,
                    Score = branch.Score + ScoreAutoGaugeSegmentEntry(branch.State, entry, gaugeRules),
                });
            }

            branches = CollapseAutoGaugeSegmentBranches(nextBranches);
        }

        var bestBranch = branches
            .OrderByDescending(branch => branch.Score)
            .ThenByDescending(branch => branch.Decisions.Count(decision => decision.Keep))
            .First();
        return (bestBranch.Decisions, bestBranch.State);
    }

    private List<AutoGaugeLegalityBranch> CollapseAutoGaugeSegmentBranches(
        IReadOnlyList<AutoGaugeLegalityBranch> branches)
    {
        var bestByKey = new Dictionary<string, AutoGaugeLegalityBranch>(StringComparer.OrdinalIgnoreCase);
        foreach (var branch in branches)
        {
            var key = BuildAutoGaugeSegmentBranchKey(branch.State);
            if (!bestByKey.TryGetValue(key, out var existing) || branch.Score > existing.Score)
                bestByKey[key] = branch;
        }

        return bestByKey.Values
            .OrderByDescending(branch => branch.Score)
            .Take(24)
            .ToList();
    }

    private string BuildAutoGaugeSegmentBranchKey(AutoTimelineState state)
    {
        var gaugeSnapshot = string.Join(
            ";",
            state.GaugeState
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        var timedWindowSnapshot = string.Join(
            ";",
            state.TimedWindowEndByAbility
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value:F3}"));
        var timedLockoutSnapshot = string.Join(
            ";",
            state.TimedLockoutEndByAbility
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value:F3}"));
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{state.LastTimeSec:F3}|{gaugeSnapshot}|{timedWindowSnapshot}|{timedLockoutSnapshot}");
    }

    private double ScoreAutoGaugeSegmentEntry(
        AutoTimelineState state,
        TimelineEntry entry,
        GaugeSimulator.JobGaugeRules gaugeRules)
    {
        var score = entry.Frequency * 1000.0;
        score += entry.AverageUses * 10.0;

        if (TryGetTimedUsageWindowRule(state.SpecName, entry.AbilityName, out _, out _) &&
            state.TimedWindowEndByAbility.TryGetValue(entry.AbilityName, out var timedWindowEndSec) &&
            entry.TimeOffsetSec <= timedWindowEndSec + AutoCooldownToleranceSec)
        {
            score += 250.0;
        }

        if (GetScholarBurstAetherflowReservationReason(
                state.SpecName,
                state.TimedWindowEndByAbility,
                state.GaugeState,
                entry.AbilityName,
                entry.TimeOffsetSec) != null)
            score -= 250.0;

        if (IsAutoTimelineTrueGaugeSpender(entry.AbilityName, gaugeRules))
            score += 40.0;

        return score;
    }

    private IEnumerable<TimelineEntry> OrderReplayEntriesForEvaluation(
        IEnumerable<TimelineEntry> entries,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        var sorted = entries
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
        if (sorted.Count <= 1)
            return sorted;

        var ordered = new List<TimelineEntry>(sorted.Count);
        var cluster = new List<TimelineEntry>();
        double? clusterStartTime = null;

        foreach (var entry in sorted)
        {
            if (clusterStartTime == null ||
                Math.Abs(entry.TimeOffsetSec - clusterStartTime.Value) <= AutoCooldownToleranceSec)
            {
                cluster.Add(entry);
                clusterStartTime ??= entry.TimeOffsetSec;
                continue;
            }

            ordered.AddRange(OrderReplayCluster(cluster, gaugeRules, grantedRules));
            cluster.Clear();
            cluster.Add(entry);
            clusterStartTime = entry.TimeOffsetSec;
        }

        if (cluster.Count > 0)
            ordered.AddRange(OrderReplayCluster(cluster, gaugeRules, grantedRules));

        return ordered;
    }

    private IEnumerable<TimelineEntry> OrderReplayCluster(
        List<TimelineEntry> cluster,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        if (cluster.Count <= 1)
            return cluster;

        var remaining = new List<TimelineEntry>(cluster);
        var ordered = new List<TimelineEntry>(cluster.Count);

        while (remaining.Count > 0)
        {
            TimelineEntry? best = null;
            var bestBlockingCount = int.MaxValue;
            var bestDependencyCount = int.MinValue;

            foreach (var candidate in remaining)
            {
                var blockingCount = 0;
                var dependencyCount = 0;
                foreach (var other in remaining)
                {
                    if (ReferenceEquals(candidate, other))
                        continue;

                    if (ShouldReplayEntryPrecede(other, candidate, gaugeRules, grantedRules))
                        blockingCount++;

                    if (ShouldReplayEntryPrecede(candidate, other, gaugeRules, grantedRules))
                        dependencyCount++;
                }

                if (best == null ||
                    blockingCount < bestBlockingCount ||
                    (blockingCount == bestBlockingCount && dependencyCount > bestDependencyCount) ||
                    (blockingCount == bestBlockingCount && dependencyCount == bestDependencyCount &&
                     candidate.Frequency > best.Frequency) ||
                    (blockingCount == bestBlockingCount && dependencyCount == bestDependencyCount &&
                     Math.Abs(candidate.Frequency - best.Frequency) < 0.0001 &&
                     string.Compare(candidate.AbilityName, best.AbilityName, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    best = candidate;
                    bestBlockingCount = blockingCount;
                    bestDependencyCount = dependencyCount;
                }
            }

            ordered.Add(best!);
            remaining.Remove(best!);
        }

        return ordered;
    }

    private bool ShouldReplayEntryPrecede(
        TimelineEntry first,
        TimelineEntry second,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        var secondPredecessors = JobComboDatabase.GetPredecessors(second.AbilityName);
        if (secondPredecessors.Contains(first.AbilityName))
            return true;

        return AbilityGrantsRequiredState(first.AbilityName, second.AbilityName, gaugeRules, grantedRules);
    }

    private bool AbilityGrantsRequiredState(
        string grantorAbilityName,
        string consumerAbilityName,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        var grantorRule = plugin.ActionStateDatabase.Lookup(0, grantorAbilityName);
        var consumerRule = plugin.ActionStateDatabase.Lookup(0, consumerAbilityName);
        if (grantorRule == null || consumerRule == null)
            return false;

        var grantedStates = grantorRule.Effects
            .Where(effect => effect.Delta > 0)
            .SelectMany(effect => EnumerateEquivalentStateNames(effect.StateName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (grantedStates.Count == 0)
            return false;

        foreach (var effect in consumerRule.Effects)
        {
            if (effect.MinRequired <= 0)
                continue;

            if (ShouldIgnoreActionStateRequirement(consumerRule, effect, consumerAbilityName, gaugeRules, grantedRules))
                continue;

            foreach (var requiredState in EnumerateEquivalentStateNames(effect.StateName))
            {
                if (grantedStates.Contains(requiredState))
                    return true;
            }
        }

        return false;
    }

    private List<TimelineEntry> SelectAutoOgcdEntries(
        List<TimelineEntry> ogcdEntries,
        string specName,
        AutoOpenerBufferInfo openerBuffer,
        AutoTimelineDebugRecorder? debug = null)
    {
        debug?.Add("oGCD Selection");
        var cooldownKeptEntries = new List<TimelineEntry>();
        foreach (var group in ogcdEntries
                     .GroupBy(entry => entry.AbilityName, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sample = group.First();
            var info = plugin.RecastDatabase.Lookup(sample.AbilityId, sample.AbilityName);
            var recastSec = Math.Max(0.1, info?.RecastSec ?? 0.0);
            var orderedCandidates = group
                .OrderBy(entry => entry.TimeOffsetSec)
                .ThenByDescending(entry => entry.Frequency)
                .ToList();
            var aboveThresholdCandidates = orderedCandidates
                .Where(entry => entry.Frequency >= AutoStateDrivenOgcdMinFrequency)
                .ToList();
            var belowThresholdCount = group.Count() - aboveThresholdCandidates.Count;
            debug?.Add($"  ability | {group.Key} | recast {recastSec:F1}s | above threshold {aboveThresholdCandidates.Count} | below threshold {belowThresholdCount}");
            if (orderedCandidates.Count == 0)
                continue;

            var currentWindowStartSec = orderedCandidates[0].TimeOffsetSec;
            var finalCandidateTimeSec = orderedCandidates[^1].TimeOffsetSec;
            TimelineEntry? previousKeptCandidate = null;
            var nextReadyTimeSec = currentWindowStartSec;
            var thresholdText = $"{AutoStateDrivenOgcdMinFrequency * 100.0:F1}%";

            while (currentWindowStartSec <= finalCandidateTimeSec + AutoCooldownToleranceSec)
            {
                var currentWindowEndSec = currentWindowStartSec + recastSec;
                var windowCandidates = orderedCandidates
                    .Where(candidate =>
                        candidate.TimeOffsetSec >= currentWindowStartSec - 0.001 &&
                        candidate.TimeOffsetSec < currentWindowEndSec - AutoCooldownToleranceSec)
                    .ToList();

                if (windowCandidates.Count == 0)
                {
                    currentWindowStartSec += recastSec;
                    continue;
                }

                var windowAboveThresholdCandidates = windowCandidates
                    .Where(candidate => candidate.Frequency >= AutoStateDrivenOgcdMinFrequency)
                    .ToList();
                var selectedWindowPool = windowAboveThresholdCandidates.Count > 0
                    ? windowAboveThresholdCandidates
                    : windowCandidates;
                var chosenCandidate = selectedWindowPool
                    .OrderByDescending(candidate => candidate.Frequency)
                    .ThenBy(candidate => candidate.TimeOffsetSec)
                    .First();
                var chosenKey = GetAutoEntryIdentityKey(chosenCandidate);
                var windowText = $"{FormatTime(currentWindowStartSec)}-{FormatTime(currentWindowEndSec)}";
                var usedFallback = windowAboveThresholdCandidates.Count == 0;

                cooldownKeptEntries.Add(chosenCandidate);

                if (previousKeptCandidate == null)
                {
                    var initialReason = usedFallback
                        ? $"fallback highest instance within initial cooldown window {windowText}; no candidate met {thresholdText} in this window; establishes cooldown baseline"
                        : $"strongest instance within initial cooldown window {windowText}; establishes cooldown baseline";
                    nextReadyTimeSec = chosenCandidate.TimeOffsetSec + recastSec;
                    debug?.Add($"    keep | {FormatAutoDebugEntry(chosenCandidate)} | {initialReason} | next ready {FormatTime(nextReadyTimeSec)}");
                }
                else
                {
                    var keepReasonPrefix = $"cooldown reopened after {FormatTime(nextReadyTimeSec)} from prior keep {FormatAutoDebugEntry(previousKeptCandidate)}";
                    var keepReason = usedFallback
                        ? $"{keepReasonPrefix} | fallback highest instance within cooldown window {windowText}; no candidate met {thresholdText} in this window"
                        : $"{keepReasonPrefix} | strongest instance within cooldown window {windowText}";
                    nextReadyTimeSec = chosenCandidate.TimeOffsetSec + recastSec;
                    debug?.Add($"    keep | {FormatAutoDebugEntry(chosenCandidate)} | {keepReason} | next ready {FormatTime(nextReadyTimeSec)}");
                }

                foreach (var candidate in windowCandidates)
                {
                    if (string.Equals(GetAutoEntryIdentityKey(candidate), chosenKey, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var pruneReason = usedFallback
                        ? $"cooldown window {windowText} fell back to stronger keep {FormatAutoDebugEntry(chosenCandidate)} because no candidate met {thresholdText}"
                        : $"cooldown window {windowText} already committed to stronger keep {FormatAutoDebugEntry(chosenCandidate)}";
                    debug?.Add($"    prune | {FormatAutoDebugEntry(candidate)} | {pruneReason}");
                }

                previousKeptCandidate = chosenCandidate;
                currentWindowStartSec = nextReadyTimeSec - AutoCooldownToleranceSec;
            }
        }

        var grantedRules = GrantedActionDatabase.GetRules(specName);
        var grantedChildRules = BuildGrantedChildRules(ogcdEntries, grantedRules);
        if (grantedChildRules.Count == 0)
        {
            debug?.Add();
            return cooldownKeptEntries
                .OrderBy(entry => entry.TimeOffsetSec)
                .ThenByDescending(entry => entry.Frequency)
                .ToList();
        }

        debug?.Add("  granted windows");
        if (openerBuffer.Enabled)
            debug?.Add($"    opener buffer | missing parents before {FormatTime(openerBuffer.EndTimeSec)} may be borrowed from pre-pull state once per child window");

        var grantedKeptEntries = new List<TimelineEntry>();
        foreach (var entry in cooldownKeptEntries
                     .OrderBy(item => item.TimeOffsetSec)
                     .ThenByDescending(item => item.Frequency))
        {
            var grantedDecision = AnalyzeGrantedCandidateDecision(entry, grantedKeptEntries, grantedChildRules, openerBuffer);
            if (!grantedDecision.IsTracked)
            {
                grantedKeptEntries.Add(entry);
                continue;
            }

            if (!grantedDecision.IsAllowed)
            {
                if (TryReplaceGrantedWindowAlternative(entry, grantedDecision, grantedKeptEntries, debug))
                    continue;

                debug?.Add($"    prune | {FormatAutoDebugEntry(entry)} | {grantedDecision.Summary}");
                continue;
            }

            grantedKeptEntries.Add(entry);
            debug?.Add($"    keep | {FormatAutoDebugEntry(entry)} | {grantedDecision.Summary}");
        }

        debug?.Add();
        var finalOgcdEntries = ApplyAutoOpenerOgcdGuideline(
            ogcdEntries,
            grantedKeptEntries,
            specName,
            openerBuffer,
            debug);
        return finalOgcdEntries
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private List<TimelineEntry> ApplyAutoOpenerOgcdGuideline(
        List<TimelineEntry> sourceOgcdEntries,
        List<TimelineEntry> selectedOgcdEntries,
        string specName,
        AutoOpenerBufferInfo openerBuffer,
        AutoTimelineDebugRecorder? debug)
    {
        if (!openerBuffer.Enabled || openerBuffer.Steps.Count == 0)
            return selectedOgcdEntries;

        var openerOgcdSteps = openerBuffer.Steps
            .Where(step => step.IsOgcd && !string.IsNullOrWhiteSpace(step.AbilityName))
            .Select(step => step.AbilityName)
            .ToList();
        if (openerOgcdSteps.Count == 0)
            return selectedOgcdEntries;

        var gaugeRules = GaugeSimulator.GetRules(specName);
        var keptKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<TimelineEntry>(selectedOgcdEntries.Count);
        var chosenEntriesByKey = new Dictionary<string, TimelineEntry>(StringComparer.OrdinalIgnoreCase);
        var lastChosenTimeSec = double.NegativeInfinity;
        var openerCandidatePool = sourceOgcdEntries
            .Where(entry =>
                entry.TimeOffsetSec <= openerBuffer.EndTimeSec + AutoCooldownToleranceSec &&
                entry.Frequency >= AutoStateDrivenOgcdMinFrequency)
            .ToList();
        var availableAbilityNames = openerCandidatePool
            .Select(entry => entry.AbilityName)
            .Where(static abilityName => !string.IsNullOrWhiteSpace(abilityName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? GetPriorChosenLegalityRejectionReason(TimelineEntry candidate)
        {
            var state = CreateAutoTimelineState(specName, gaugeRules, grantedRules: null, availableAbilityNames);
            foreach (var priorEntry in chosenEntriesByKey.Values
                         .Where(entry => entry.TimeOffsetSec <= candidate.TimeOffsetSec + AutoCooldownToleranceSec)
                         .OrderBy(entry => entry.TimeOffsetSec)
                         .ThenByDescending(entry => entry.Frequency))
            {
                AdvancePassiveResources(state, gaugeRules, priorEntry.TimeOffsetSec);
                var priorInfo = plugin.RecastDatabase.Lookup(priorEntry.AbilityId, priorEntry.AbilityName);
                ApplyAutoEntry(state, priorEntry, priorInfo, gaugeRules, grantedRules: null, priorEntry.TimeOffsetSec, isGcd: false);
            }

            var candidateInfo = plugin.RecastDatabase.Lookup(candidate.AbilityId, candidate.AbilityName);
            AdvancePassiveResources(state, gaugeRules, candidate.TimeOffsetSec);
            return GetAutoEntryRejectionReason(state, candidate, candidateInfo, gaugeRules, grantedRules: null, candidate.TimeOffsetSec);
        }

        bool CanCompleteOrderedOpenerOgcdSequence(
            int stepIndex,
            double minimumTimeSec,
            HashSet<string> usedKeys)
        {
            if (stepIndex >= openerOgcdSteps.Count)
                return true;

            var remainingAbilityName = openerOgcdSteps[stepIndex];
            var candidates = openerCandidatePool
                .Where(entry =>
                {
                    var key = GetAutoEntryIdentityKey(entry);
                    return !usedKeys.Contains(key) &&
                           DoesOpenerAbilityMatch(entry.AbilityName, remainingAbilityName) &&
                           entry.TimeOffsetSec + AutoCooldownToleranceSec >= minimumTimeSec;
                })
                .OrderByDescending(entry => entry.Frequency)
                .ThenBy(entry => entry.TimeOffsetSec)
                .ToList();
            if (candidates.Count == 0)
                return false;

            foreach (var candidate in candidates)
            {
                var key = GetAutoEntryIdentityKey(candidate);
                usedKeys.Add(key);
                if (CanCompleteOrderedOpenerOgcdSequence(stepIndex + 1, candidate.TimeOffsetSec, usedKeys))
                    return true;

                usedKeys.Remove(key);
            }

            return false;
        }

        debug?.Add("  opener oGCD guideline");
        for (var stepIndex = 0; stepIndex < openerOgcdSteps.Count; stepIndex++)
        {
            var abilityName = openerOgcdSteps[stepIndex];
            var priorChosenTimeSec = lastChosenTimeSec;
            var candidates = openerCandidatePool
                .Where(entry =>
                    !keptKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
                    DoesOpenerAbilityMatch(entry.AbilityName, abilityName))
                .OrderByDescending(entry => entry.Frequency)
                .ThenBy(entry => entry.TimeOffsetSec)
                .ToList();
            if (candidates.Count == 0)
            {
                debug?.Add($"    miss | slot {stepIndex + 1} | {abilityName} | no raw opener-buffer candidate survived for this ordered opener step");
                continue;
            }

            TimelineEntry? keptEntry = null;
            var legalityRejectionByKey = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                var candidateKey = GetAutoEntryIdentityKey(candidate);
                if (candidate.TimeOffsetSec + AutoCooldownToleranceSec < priorChosenTimeSec)
                    continue;

                var legalityRejectionReason = GetPriorChosenLegalityRejectionReason(candidate);
                legalityRejectionByKey[candidateKey] = legalityRejectionReason;
                if (legalityRejectionReason != null)
                    continue;

                var simulatedUsedKeys = new HashSet<string>(keptKeys, StringComparer.OrdinalIgnoreCase)
                {
                    candidateKey
                };
                if (!CanCompleteOrderedOpenerOgcdSequence(stepIndex + 1, candidate.TimeOffsetSec, simulatedUsedKeys))
                    continue;

                keptEntry = candidate;
                break;
            }

            keptEntry ??= candidates
                .FirstOrDefault(entry =>
                {
                    var candidateKey = GetAutoEntryIdentityKey(entry);
                    legalityRejectionByKey.TryAdd(candidateKey, GetPriorChosenLegalityRejectionReason(entry));
                    return entry.TimeOffsetSec + AutoCooldownToleranceSec >= priorChosenTimeSec &&
                           legalityRejectionByKey[candidateKey] == null;
                })
                ?? candidates.First();
            var keptKey = GetAutoEntryIdentityKey(keptEntry);
            keptKeys.Add(keptKey);
            chosenEntriesByKey[keptKey] = keptEntry;
            var preservedSequence = keptEntry.TimeOffsetSec + AutoCooldownToleranceSec >= priorChosenTimeSec;
            lastChosenTimeSec = keptEntry.TimeOffsetSec;
            var keepReason = preservedSequence
                ? "strongest unique raw opener-buffer candidate kept in ordered opener sequence"
                : "strongest unique raw opener-buffer candidate kept after ordered sequence ran out; earlier fallback preserved this opener step";
            debug?.Add($"    keep | {FormatAutoDebugEntry(keptEntry)} | opener slot {stepIndex + 1} requires {abilityName}; {keepReason}");

            foreach (var prunedEntry in candidates
                         .Where(entry => !string.Equals(GetAutoEntryIdentityKey(entry), keptKey, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(entry => entry.Frequency)
                         .ThenBy(entry => entry.TimeOffsetSec))
            {
                var prunedKey = GetAutoEntryIdentityKey(prunedEntry);
                legalityRejectionByKey.TryAdd(prunedKey, GetPriorChosenLegalityRejectionReason(prunedEntry));
                var legalityReason = legalityRejectionByKey[prunedKey];
                var pruneReason = legalityReason != null
                    ? $"opener slot {stepIndex + 1} would be pruned later by legality checks: {legalityReason}"
                    : prunedEntry.TimeOffsetSec + AutoCooldownToleranceSec >= priorChosenTimeSec
                    ? $"opener slot {stepIndex + 1} already committed to a stronger {abilityName} candidate in the ordered opener sequence"
                    : $"opener slot {stepIndex + 1} already committed to a stronger {abilityName} fallback candidate after in-order options ran out";
                debug?.Add($"    prune | {FormatAutoDebugEntry(prunedEntry)} | {pruneReason}");
            }
        }

        foreach (var entry in chosenEntriesByKey.Values
                     .OrderBy(item => item.TimeOffsetSec)
                     .ThenByDescending(item => item.Frequency))
        {
            var key = GetAutoEntryIdentityKey(entry);
            if (keptKeys.Contains(key))
                result.Add(entry);
        }

        foreach (var entry in selectedOgcdEntries
                     .OrderBy(item => item.TimeOffsetSec)
                     .ThenByDescending(item => item.Frequency))
        {
            if (entry.TimeOffsetSec > openerBuffer.EndTimeSec + AutoCooldownToleranceSec)
            {
                result.Add(entry);
                continue;
            }

            var key = GetAutoEntryIdentityKey(entry);
            if (result.Any(existing => GetAutoEntryIdentityKey(existing) == key))
                continue;

            if (keptKeys.Contains(key))
            {
                result.Add(entry);
                continue;
            }

            if (openerOgcdSteps.Any(openerAbilityName => DoesOpenerAbilityMatch(entry.AbilityName, openerAbilityName)))
                continue;

            if (IsAutoTimelineTrueGaugeAbility(entry.AbilityName, gaugeRules))
            {
                debug?.Add($"    prune | {FormatAutoDebugEntry(entry)} | opener-buffer true-gauge action is not part of the configured opener oGCD roster");
                continue;
            }

            result.Add(entry);
        }

        debug?.Add();
        return result;
    }

    private List<TimelineEntry> RepairAutoGcdEntries(
        List<TimelineEntry> sourceGcdEntries,
        List<TimelineEntry> selectedGcdEntries,
        List<TimelineEntry> selectedOgcdEntries,
        string specName,
        ISet<string> rawAbilityNames,
        AutoTimelineDebugRecorder? debug = null)
    {
        debug?.Add("GCD Repair");
        debug?.Add("  Archived.");
        debug?.Add();
        return selectedGcdEntries
            .OrderBy(e => e.TimeOffsetSec)
            .ThenByDescending(e => e.Frequency)
            .ToList();
    }

    private (BalanceOpenerDatabase.JobOpenerVariant? Variant, int StartSlotOffset, List<TimelineEntry> Entries) SelectAutoOpenerEntries(
        List<TimelineEntry> gcdEntries,
        string specName,
        AutoTimelineDebugRecorder? debug)
    {
        var hints = BalanceOpenerDatabase.GetHints(specName);
        if (hints == null || hints.Variants.Count == 0)
        {
            debug?.Add("  opener | none configured");
            return (null, 0, []);
        }

        BalanceOpenerDatabase.JobOpenerVariant? bestVariant = null;
        var bestStartSlotOffset = 0;
        var bestEntries = new List<TimelineEntry>();
        var bestMatchedCount = int.MinValue;
        var bestScore = double.NegativeInfinity;
        foreach (var variant in hints.Variants)
        {
            var variantGcdSequence = variant.GcdSequence.ToArray();
            for (var startOffset = 0; startOffset < variantGcdSequence.Length; startOffset++)
            {
                var bestVariantEntries = new List<TimelineEntry>();
                var bestVariantScore = 0.0;
                var matchedCount = 0;
                var usedVariantKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var gcdIndex = 0; gcdIndex + startOffset < variantGcdSequence.Length; gcdIndex++)
                {
                    var abilityName = variantGcdSequence[gcdIndex + startOffset];
                    if (!IsConcreteOpenerAbilityName(abilityName) || !IsStandardTimelineGcdAbility(abilityName))
                        continue;

                    var slotTimeSec = gcdIndex * AutoTargetGcdSec;
                    var candidate = GetAutoOpenerRequirementCandidates(gcdEntries, usedVariantKeys, slotTimeSec, abilityName)
                        .FirstOrDefault();
                    if (candidate == null)
                        continue;

                    usedVariantKeys.Add(GetAutoEntryIdentityKey(candidate));
                    bestVariantEntries.Add(candidate);
                    bestVariantScore += candidate.Frequency * 100.0;
                    matchedCount++;
                }

                var remainingConcreteCount = variantGcdSequence
                    .Skip(startOffset)
                    .Count(abilityName => IsConcreteOpenerAbilityName(abilityName) && IsStandardTimelineGcdAbility(abilityName));
                var startAbilityName = variantGcdSequence[startOffset];
                debug?.Add($"  opener-variant | {variant.Name} | start {startOffset:00} ({startAbilityName}) | matched {matchedCount}/{Math.Max(1, remainingConcreteCount)} | score {bestVariantScore:F1}");
                var isBetter = matchedCount > bestMatchedCount ||
                               (matchedCount == bestMatchedCount && bestVariantScore > bestScore) ||
                               (matchedCount == bestMatchedCount && Math.Abs(bestVariantScore - bestScore) < 0.0001 && startOffset < bestStartSlotOffset);
                if (!isBetter)
                    continue;

                bestVariant = variant;
                bestStartSlotOffset = startOffset;
                bestEntries = bestVariantEntries;
                bestMatchedCount = matchedCount;
                bestScore = bestVariantScore;
            }
        }

        if (bestVariant != null)
            debug?.Add($"  opener-picked | {bestVariant.Name} | start {bestStartSlotOffset:00} ({bestVariant.GcdSequence[bestStartSlotOffset]}) | GCDs {bestEntries.Count}");

        return (bestVariant, bestStartSlotOffset, bestEntries);
    }

    private static bool IsConcreteOpenerAbilityName(string abilityName)
    {
        if (string.IsNullOrWhiteSpace(abilityName))
            return false;

        return !string.Equals(abilityName, "Step", StringComparison.OrdinalIgnoreCase) &&
               !abilityName.Contains("Priority GCD", StringComparison.OrdinalIgnoreCase) &&
               !abilityName.Contains(" / ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DoesOpenerAbilityMatch(string entryAbilityName, string openerAbilityName)
    {
        if (string.Equals(entryAbilityName, openerAbilityName, StringComparison.OrdinalIgnoreCase))
            return true;

        return EnumerateEquivalentOpenerAbilityNames(openerAbilityName)
            .Any(name => string.Equals(name, entryAbilityName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateEquivalentOpenerAbilityNames(string abilityName)
    {
        yield return abilityName;

        if (string.Equals(abilityName, "Standard Finish", StringComparison.OrdinalIgnoreCase))
            yield return "Double Standard Finish";
        else if (string.Equals(abilityName, "Double Standard Finish", StringComparison.OrdinalIgnoreCase))
            yield return "Standard Finish";

        if (string.Equals(abilityName, "Technical Finish", StringComparison.OrdinalIgnoreCase))
            yield return "Quadruple Technical Finish";
        else if (string.Equals(abilityName, "Quadruple Technical Finish", StringComparison.OrdinalIgnoreCase))
            yield return "Technical Finish";
    }

    private List<TimelineEntry> GetAutoOpenerRequirementCandidates(
        List<TimelineEntry> gcdEntries,
        ISet<string> usedKeys,
        double slotTimeSec,
        string openerAbilityName)
    {
        var exactWindowEndSec = slotTimeSec + AutoTargetGcdSec;
        var searchStartSec = Math.Max(0.0, slotTimeSec - AutoTargetGcdSec);
        var searchEndSec = slotTimeSec + AutoTargetGcdSec + AutoCooldownToleranceSec;
        return gcdEntries
            .Where(entry =>
                !usedKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
                entry.Frequency >= AutoGcdMinFrequency &&
                DoesOpenerAbilityMatch(entry.AbilityName, openerAbilityName) &&
                entry.TimeOffsetSec >= searchStartSec - 0.001 &&
                entry.TimeOffsetSec < searchEndSec - 0.001)
            .OrderByDescending(entry => IsWithinAutoSlotWindow(entry.TimeOffsetSec, slotTimeSec))
            .ThenBy(entry => Math.Abs(entry.TimeOffsetSec - slotTimeSec))
            .ThenByDescending(entry => entry.Frequency)
            .ThenBy(entry => entry.TimeOffsetSec)
            .ToList();
    }

    private List<TimelineEntry> GetAutoGcdSlotCandidates(
        List<TimelineEntry> gcdEntries,
        HashSet<string> usedKeys,
        double slotTimeSec)
    {
        var windowStartSec = Math.Max(0.0, slotTimeSec - AutoCooldownToleranceSec);
        var windowEndSec = slotTimeSec + AutoTargetGcdSec + AutoCooldownToleranceSec;
        return gcdEntries
            .Where(entry =>
                !usedKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
                entry.Frequency >= AutoGcdMinFrequency &&
                entry.TimeOffsetSec >= windowStartSec - AutoGcdSlotMatchToleranceSec &&
                entry.TimeOffsetSec < windowEndSec + AutoGcdSlotMatchToleranceSec)
            .OrderByDescending(entry => IsWithinAutoSlotWindow(entry.TimeOffsetSec, slotTimeSec))
            .ThenBy(entry => Math.Abs(entry.TimeOffsetSec - slotTimeSec))
            .ThenByDescending(entry => entry.Frequency)
            .ThenBy(entry => entry.TimeOffsetSec)
            .ToList();
    }

    private static bool IsWithinAutoSlotWindow(double entryTimeSec, double slotTimeSec)
        => entryTimeSec >= slotTimeSec - AutoGcdSlotMatchToleranceSec &&
           entryTimeSec < slotTimeSec + AutoTargetGcdSec + AutoGcdSlotMatchToleranceSec;

    private static string BuildOpenerRequirementSummary(
        TimelineEntry entry,
        double slotTimeSec,
        string? openerRequirement)
    {
        if (string.IsNullOrWhiteSpace(openerRequirement))
            return string.Empty;

        return IsWithinAutoSlotWindow(entry.TimeOffsetSec, slotTimeSec)
            ? $"matches opener requirement {openerRequirement}; "
            : $"matches opener requirement {openerRequirement} via opener timing tolerance from {FormatTime(entry.TimeOffsetSec)}; ";
    }

    private TimelineEntry? ChooseAutoGcdSlotEntry(
        List<TimelineEntry> allGcdEntries,
        List<TimelineEntry> slotCandidates,
        HashSet<string> usedKeys,
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        JobComboDatabase.JobComboHints? comboHints,
        string? burstPreferredAbility,
        double slotTimeSec,
        Dictionary<string, double> lastAbilityTimes,
        Dictionary<string, double> pseudoCooldowns,
        AutoTimelineDebugRecorder? debug)
    {
        var dueDotCandidate = slotCandidates
            .Where(entry => IsDotCandidateDue(entry, slotTimeSec, lastAbilityTimes))
            .OrderByDescending(entry => entry.Frequency)
            .ThenBy(entry => entry.TimeOffsetSec)
            .FirstOrDefault(entry => IsAutoGcdCandidateLegal(
                state,
                entry,
                gaugeRules,
                grantedRules,
                slotTimeSec,
                lastAbilityTimes,
                pseudoCooldowns));
        if (dueDotCandidate != null)
        {
            debug?.Add($"  slot-choice | due DoT | {dueDotCandidate.AbilityName}");
            return dueDotCandidate;
        }

        var grantedCandidate = FindNextStateDrivenAutoGcdCandidate(
            allGcdEntries,
            usedKeys,
            state,
            gaugeRules,
            grantedRules,
            slotTimeSec,
            lastAbilityTimes,
            pseudoCooldowns);
        if (grantedCandidate != null)
        {
            debug?.Add($"  slot-choice | granted/state | {grantedCandidate.AbilityName}");
            return grantedCandidate;
        }

        if (!string.IsNullOrWhiteSpace(burstPreferredAbility))
        {
            var burstCandidate = slotCandidates
                .Where(entry => string.Equals(entry.AbilityName, burstPreferredAbility, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.Frequency)
                .ThenBy(entry => entry.TimeOffsetSec)
                .FirstOrDefault(entry => IsAutoGcdCandidateLegal(
                    state,
                    entry,
                    gaugeRules,
                    grantedRules,
                    slotTimeSec,
                    lastAbilityTimes,
                    pseudoCooldowns));
            if (burstCandidate != null)
            {
                debug?.Add($"  slot-choice | burst bias | {burstCandidate.AbilityName}");
                return burstCandidate;
            }
        }

        if (comboHints != null)
        {
            var comboStarterCandidate = slotCandidates
                .Where(entry => comboHints.ComboStarters.Contains(entry.AbilityName))
                .OrderByDescending(entry => entry.Frequency)
                .ThenBy(entry => entry.TimeOffsetSec)
                .FirstOrDefault(entry => IsAutoGcdCandidateLegal(
                    state,
                    entry,
                    gaugeRules,
                    grantedRules,
                    slotTimeSec,
                    lastAbilityTimes,
                    pseudoCooldowns));
            if (comboStarterCandidate != null)
            {
                debug?.Add($"  slot-choice | combo start | {comboStarterCandidate.AbilityName}");
                return comboStarterCandidate;
            }
        }

        return slotCandidates
            .OrderByDescending(entry => entry.Frequency)
            .ThenBy(entry => entry.TimeOffsetSec)
            .FirstOrDefault(entry => IsAutoGcdCandidateLegal(
                state,
                entry,
                gaugeRules,
                grantedRules,
                slotTimeSec,
                lastAbilityTimes,
                pseudoCooldowns));
    }

    private TimelineEntry? FindNextExpectedComboCandidate(
        List<TimelineEntry> allGcdEntries,
        HashSet<string> usedKeys,
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        HashSet<string> expectedComboAbilities,
        double slotTimeSec,
        Dictionary<string, double> lastAbilityTimes,
        Dictionary<string, double> pseudoCooldowns)
    {
        if (expectedComboAbilities.Count == 0)
            return null;

        return allGcdEntries
            .Where(entry =>
                entry.Frequency >= AutoStateDrivenOgcdMinFrequency &&
                !usedKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
                expectedComboAbilities.Contains(entry.AbilityName) &&
                entry.TimeOffsetSec >= slotTimeSec - AutoCooldownToleranceSec)
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .FirstOrDefault(entry => IsAutoGcdCandidateLegal(
                state,
                entry,
                gaugeRules,
                grantedRules,
                slotTimeSec,
                lastAbilityTimes,
                pseudoCooldowns));
    }

    private TimelineEntry? FindNextStateDrivenAutoGcdCandidate(
        List<TimelineEntry> allGcdEntries,
        HashSet<string> usedKeys,
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        double slotTimeSec,
        Dictionary<string, double> lastAbilityTimes,
        Dictionary<string, double> pseudoCooldowns)
    {
        return allGcdEntries
            .Where(entry =>
                entry.Frequency >= AutoStateDrivenOgcdMinFrequency &&
                !usedKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
                entry.TimeOffsetSec >= slotTimeSec - AutoCooldownToleranceSec &&
                IsStateDrivenGcdCandidateReady(state, entry, gaugeRules, grantedRules))
            .OrderByDescending(entry => GetStateDrivenSelectionPriority(entry, slotTimeSec, lastAbilityTimes))
            .ThenByDescending(entry => GetRecentGrantorTimeSec(entry, slotTimeSec, lastAbilityTimes))
            .ThenBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .FirstOrDefault(entry => IsAutoGcdCandidateLegal(
                state,
                entry,
                gaugeRules,
                grantedRules,
                slotTimeSec,
                lastAbilityTimes,
                pseudoCooldowns));
    }

    private bool IsStateDrivenGcdCandidateReady(
        AutoTimelineState state,
        TimelineEntry entry,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        var actionRule = plugin.ActionStateDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        if (actionRule != null)
        {
            foreach (var effect in actionRule.Effects)
            {
                if (effect.MinRequired <= 0)
                    continue;

                if (!ShouldEnforceActionStateRequirement(effect.StateName, state.AvailableAbilityNames, gaugeRules, grantedRules))
                    continue;

                if (GetEffectiveStateValue(effect.StateName, state.ActionState, state.GaugeState, state.GrantedState) >= effect.MinRequired)
                    return true;
            }
        }

        if (grantedRules != null &&
            grantedRules.EffectByName.TryGetValue(entry.AbilityName, out var grantedEffects) &&
            grantedEffects.Any(effect => effect.MinRequired > 0 && state.GrantedState.GetValueOrDefault(effect.ResourceName) >= effect.MinRequired))
        {
            return true;
        }

        return false;
    }

    private int GetStateDrivenSelectionPriority(
        TimelineEntry entry,
        double slotTimeSec,
        Dictionary<string, double> lastAbilityTimes)
    {
        var actionRule = plugin.ActionStateDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        if (actionRule == null)
            return 0;

        var hasRecentGrantor = false;
        var grantsAnotherState = actionRule.Effects.Any(effect => effect.Delta > 0);
        foreach (var effect in actionRule.Effects)
        {
            if (effect.MinRequired <= 0)
                continue;

            foreach (var grantorName in plugin.ActionStateDatabase.GetGrantors(effect.StateName))
            {
                if (lastAbilityTimes.TryGetValue(grantorName, out var grantorTimeSec) &&
                    grantorTimeSec <= slotTimeSec + AutoCooldownToleranceSec)
                {
                    hasRecentGrantor = true;
                    break;
                }
            }

            if (hasRecentGrantor)
                break;
        }

        if (!hasRecentGrantor)
            return 0;

        if (JobComboDatabase.IsComboStarter(entry.AbilityName))
            return 3;

        if (grantsAnotherState)
            return 2;

        return 1;
    }

    private double GetRecentGrantorTimeSec(
        TimelineEntry entry,
        double slotTimeSec,
        Dictionary<string, double> lastAbilityTimes)
    {
        var actionRule = plugin.ActionStateDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        if (actionRule == null)
            return double.NegativeInfinity;

        var best = double.NegativeInfinity;
        foreach (var effect in actionRule.Effects)
        {
            if (effect.MinRequired <= 0)
                continue;

            foreach (var grantorName in plugin.ActionStateDatabase.GetGrantors(effect.StateName))
            {
                if (!lastAbilityTimes.TryGetValue(grantorName, out var grantorTimeSec))
                    continue;

                if (grantorTimeSec > slotTimeSec + AutoCooldownToleranceSec)
                    continue;

                best = Math.Max(best, grantorTimeSec);
            }
        }

        return best;
    }

    private bool IsAutoGcdCandidateLegal(
        AutoTimelineState state,
        TimelineEntry entry,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        double slotTimeSec,
        Dictionary<string, double> lastAbilityTimes,
        Dictionary<string, double> pseudoCooldowns)
    {
        if (pseudoCooldowns.TryGetValue(entry.AbilityName, out var pseudoCooldownSec) &&
            lastAbilityTimes.TryGetValue(entry.AbilityName, out var lastTimeSec) &&
            entry.TimeOffsetSec < lastTimeSec + pseudoCooldownSec - AutoCooldownToleranceSec)
            return false;

        if (DotDatabase.Lookup(entry.AbilityName) is { } dotRule &&
            lastAbilityTimes.TryGetValue(entry.AbilityName, out var lastDotTimeSec) &&
            slotTimeSec < lastDotTimeSec + dotRule.RefreshReadySec - AutoCooldownToleranceSec)
            return false;

        var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        return CanAcceptAutoEntry(state, entry, info, gaugeRules, grantedRules, entry.TimeOffsetSec);
    }

    private bool IsDotCandidateDue(
        TimelineEntry entry,
        double slotTimeSec,
        Dictionary<string, double> lastAbilityTimes)
    {
        var dotRule = DotDatabase.Lookup(entry.AbilityName);
        if (dotRule == null)
            return false;

        return !lastAbilityTimes.TryGetValue(entry.AbilityName, out var lastTimeSec) ||
               slotTimeSec >= lastTimeSec + dotRule.RefreshReadySec - AutoCooldownToleranceSec;
    }

    private bool IsPseudoCooldownBlocked(
        TimelineEntry entry,
        double slotTimeSec,
        List<TimelineEntry> keptEntries,
        Dictionary<string, double> pseudoCooldowns)
    {
        if (!pseudoCooldowns.TryGetValue(entry.AbilityName, out var pseudoCooldownSec))
            return false;

        for (var i = keptEntries.Count - 1; i >= 0; i--)
        {
            var previous = keptEntries[i];
            if (!string.Equals(previous.AbilityName, entry.AbilityName, StringComparison.OrdinalIgnoreCase))
                continue;

            return slotTimeSec < previous.TimeOffsetSec + pseudoCooldownSec - AutoCooldownToleranceSec;
        }

        return false;
    }

    private static Dictionary<string, double> BuildLastSelectedAbilityTimes(List<TimelineEntry> entries)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.OrderBy(entry => entry.TimeOffsetSec))
            result[entry.AbilityName] = entry.TimeOffsetSec;

        return result;
    }

    private bool HasGrantedActionRequirement(string abilityName)
    {
        var rule = plugin.ActionStateDatabase.Lookup(0, abilityName);
        return rule != null && rule.Effects.Any(effect => effect.MinRequired > 0);
    }

    private HashSet<string> SelectAutoDotGcdEntryKeys(List<TimelineEntry> gcdEntries)
    {
        var keptKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in gcdEntries
                     .Where(entry => DotDatabase.Lookup(entry.AbilityName) != null)
                     .GroupBy(entry => entry.AbilityName, StringComparer.OrdinalIgnoreCase))
        {
            var dotRule = DotDatabase.Lookup(group.Key);
            if (dotRule == null)
                continue;

            double? nextReadyTimeSec = null;
            foreach (var candidate in group
                         .Where(entry => entry.Frequency >= AutoGcdMinFrequency)
                         .OrderBy(entry => entry.TimeOffsetSec)
                         .ThenByDescending(entry => entry.Frequency))
            {
                if (nextReadyTimeSec != null &&
                    candidate.TimeOffsetSec < nextReadyTimeSec.Value - AutoCooldownToleranceSec)
                    continue;

                keptKeys.Add(GetAutoEntryIdentityKey(candidate));
                nextReadyTimeSec = candidate.TimeOffsetSec + dotRule.RefreshReadySec;
            }
        }

        return keptKeys;
    }

    private static bool IsAbilityBlocked(
        string abilityName,
        double slotTimeSec,
        IReadOnlyDictionary<string, double> blockedUntilByAbility)
        => blockedUntilByAbility.TryGetValue(abilityName, out var blockedUntilSec) &&
           slotTimeSec < blockedUntilSec - AutoCooldownToleranceSec;

    private Dictionary<string, AutoGrantedChildRule> BuildGrantedChildRules(
        IEnumerable<TimelineEntry> entries,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        var result = new Dictionary<string, AutoGrantedChildRule>(StringComparer.OrdinalIgnoreCase);
        var trackedAbilityNames = entries
            .Select(entry => entry.AbilityName)
            .Where(abilityName => !string.IsNullOrWhiteSpace(abilityName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (trackedAbilityNames.Length == 0)
            return result;

        var actionRulesByAbility = trackedAbilityNames.ToDictionary(
            abilityName => abilityName,
            abilityName => plugin.ActionStateDatabase.Lookup(0, abilityName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var abilityName in trackedAbilityNames)
        {
            if (TryBuildCardDrawGrantedRule(abilityName, grantedRules, out var cardDrawRule))
            {
                result[abilityName] = cardDrawRule;
                continue;
            }

            var actionRule = actionRulesByAbility[abilityName];
            if (actionRule == null)
                continue;

            var requiredStateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var effect in actionRule.Effects.Where(effect => effect.MinRequired > 0))
            {
                foreach (var equivalentStateName in EnumerateEquivalentStateNames(effect.StateName))
                {
                    requiredStateNames.Add(equivalentStateName);
                    foreach (var grantorName in plugin.ActionStateDatabase.GetGrantors(equivalentStateName))
                        parentNames.Add(grantorName);
                }
            }

            if (parentNames.Count == 0)
                continue;

            var consumerNames = trackedAbilityNames
                .Where(otherAbilityName =>
                    actionRulesByAbility.TryGetValue(otherAbilityName, out var otherActionRule) &&
                    ActionConsumesAnyGrantedState(otherActionRule, requiredStateNames))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (consumerNames.Count == 0)
                consumerNames.Add(abilityName);

            result[abilityName] = new AutoGrantedChildRule
            {
                ChildAbilityName = abilityName,
                ParentAbilityNames = parentNames,
                ConsumerAbilityNames = consumerNames,
                AllowedUsesPerParentWindow = 1,
            };
        }

        return result;
    }

    private static bool TryBuildCardDrawGrantedRule(
        string abilityName,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        out AutoGrantedChildRule rule)
    {
        rule = null!;
        if (grantedRules?.CardDraw is not { } cardDraw)
            return false;

        if (cardDraw.AstralCards.Contains(abilityName))
        {
            rule = new AutoGrantedChildRule
            {
                ChildAbilityName = abilityName,
                ParentAbilityNames = [cardDraw.AstralDrawName],
                ConsumerAbilityNames = [abilityName],
                AllowedUsesPerParentWindow = 1,
            };
            return true;
        }

        if (cardDraw.UmbralCards.Contains(abilityName))
        {
            rule = new AutoGrantedChildRule
            {
                ChildAbilityName = abilityName,
                ParentAbilityNames = [cardDraw.UmbralDrawName],
                ConsumerAbilityNames = [abilityName],
                AllowedUsesPerParentWindow = 1,
            };
            return true;
        }

        return false;
    }

    private bool ActionConsumesAnyGrantedState(
        ActionStateDatabase.ActionStateRule? actionRule,
        IReadOnlySet<string> requiredStateNames)
    {
        if (actionRule == null || requiredStateNames.Count == 0)
            return false;

        foreach (var effect in actionRule.Effects)
        {
            if (effect.MinRequired <= 0)
                continue;

            foreach (var equivalentStateName in EnumerateEquivalentStateNames(effect.StateName))
            {
                if (requiredStateNames.Contains(equivalentStateName))
                    return true;
            }
        }

        return false;
    }

    private bool IsGrantedCandidateAllowed(
        TimelineEntry entry,
        IReadOnlyList<TimelineEntry> keptGcdEntries,
        IReadOnlyList<TimelineEntry> selectedOgcdEntries,
        IReadOnlyDictionary<string, AutoGrantedChildRule> grantedChildRules,
        AutoOpenerBufferInfo openerBuffer)
    {
        if (!grantedChildRules.ContainsKey(entry.AbilityName))
            return true;

        var earlierSelectedEntries = keptGcdEntries
            .Concat(selectedOgcdEntries)
            .Where(selected => selected.TimeOffsetSec <= entry.TimeOffsetSec + 0.001)
            .OrderBy(selected => selected.TimeOffsetSec)
            .ThenByDescending(selected => selected.Frequency)
            .ToList();
        return AnalyzeGrantedCandidateDecision(entry, earlierSelectedEntries, grantedChildRules, openerBuffer).IsAllowed;
    }

    private bool IsGrantedSelectedEntryAllowed(
        TimelineEntry entry,
        IReadOnlyList<TimelineEntry> earlierSelectedEntries,
        IReadOnlyDictionary<string, AutoGrantedChildRule> grantedChildRules,
        AutoOpenerBufferInfo openerBuffer)
    {
        return AnalyzeGrantedCandidateDecision(entry, earlierSelectedEntries, grantedChildRules, openerBuffer).IsAllowed;
    }

    private AutoGrantedCandidateDecision AnalyzeGrantedCandidateDecision(
        TimelineEntry entry,
        IReadOnlyList<TimelineEntry> earlierSelectedEntries,
        IReadOnlyDictionary<string, AutoGrantedChildRule> grantedChildRules,
        AutoOpenerBufferInfo openerBuffer)
    {
        if (!grantedChildRules.TryGetValue(entry.AbilityName, out var childRule))
        {
            return new AutoGrantedCandidateDecision
            {
                IsTracked = false,
                IsAllowed = true,
            };
        }

        var lastParentEntry = GetLastGrantedParentEntry(
            childRule.ParentAbilityNames,
            earlierSelectedEntries,
            entry.TimeOffsetSec);
        var parentNamesText = string.Join(", ", childRule.ParentAbilityNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        var consumerNamesText = string.Join(", ", childRule.ConsumerAbilityNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        var previousTrackedChildEntry = earlierSelectedEntries
            .LastOrDefault(selected =>
                childRule.ConsumerAbilityNames.Contains(selected.AbilityName));
        if (lastParentEntry == null)
        {
            if (IsWithinAutoOpenerBuffer(openerBuffer, entry.TimeOffsetSec))
            {
                var virtualWindowUses = CountGrantedWindowUses(
                    childRule,
                    earlierSelectedEntries,
                    windowStartTimeSec: null);
                if (virtualWindowUses >= childRule.AllowedUsesPerParentWindow)
                {
                    return new AutoGrantedCandidateDecision
                    {
                        IsTracked = true,
                        IsAllowed = false,
                        Summary = $"opener buffer already spent its virtual pre-pull parent [{parentNamesText}] on {virtualWindowUses}/{childRule.AllowedUsesPerParentWindow} tracked use(s) from [{consumerNamesText}]",
                        ChildRule = childRule,
                        ExistingWindowConsumerEntry = FindLatestGrantedWindowConsumerEntry(childRule, earlierSelectedEntries, windowStartTimeSec: null),
                        UsesVirtualParentWindow = true,
                    };
                }

                return new AutoGrantedCandidateDecision
                {
                    IsTracked = true,
                    IsAllowed = true,
                    Summary = $"opener buffer assumed a pre-pull parent from [{parentNamesText}] before pull; tracked use {virtualWindowUses + 1}/{childRule.AllowedUsesPerParentWindow} from [{consumerNamesText}] may borrow that missing parent window",
                    ChildRule = childRule,
                    UsesVirtualParentWindow = true,
                };
            }

            return new AutoGrantedCandidateDecision
            {
                IsTracked = true,
                IsAllowed = false,
                Summary = $"requires a parent before this child; expected one of [{parentNamesText}] before {FormatTime(entry.TimeOffsetSec)}, but none was selected",
                ChildRule = childRule,
            };
        }

        var priorWindowUses = CountGrantedWindowUses(
            childRule,
            earlierSelectedEntries,
            lastParentEntry.TimeOffsetSec);
        if (priorWindowUses >= childRule.AllowedUsesPerParentWindow)
        {
            return new AutoGrantedCandidateDecision
            {
                IsTracked = true,
                IsAllowed = false,
                Summary = $"latest parent window is {FormatAutoDebugEntry(lastParentEntry)}; [{consumerNamesText}] already spent {priorWindowUses}/{childRule.AllowedUsesPerParentWindow} allowed use(s) in that window",
                ChildRule = childRule,
                ParentEntry = lastParentEntry,
                ExistingWindowConsumerEntry = FindLatestGrantedWindowConsumerEntry(childRule, earlierSelectedEntries, lastParentEntry.TimeOffsetSec),
            };
        }

        var keepSummary = priorWindowUses == 0
            ? $"first tracked child from [{consumerNamesText}] after parent {FormatAutoDebugEntry(lastParentEntry)}"
            : $"latest parent window {FormatAutoDebugEntry(lastParentEntry)} is spending tracked use {priorWindowUses + 1}/{childRule.AllowedUsesPerParentWindow} from [{consumerNamesText}]";
        return new AutoGrantedCandidateDecision
        {
            IsTracked = true,
            IsAllowed = true,
            Summary = keepSummary,
            ChildRule = childRule,
            ParentEntry = lastParentEntry,
        };
    }

    private static int CountGrantedWindowUses(
        AutoGrantedChildRule childRule,
        IReadOnlyList<TimelineEntry> selectedEntries,
        double? windowStartTimeSec)
    {
        var count = 0;
        foreach (var selectedEntry in selectedEntries)
        {
            if (windowStartTimeSec.HasValue &&
                selectedEntry.TimeOffsetSec < windowStartTimeSec.Value - AutoCooldownToleranceSec)
            {
                continue;
            }

            if (childRule.ConsumerAbilityNames.Contains(selectedEntry.AbilityName))
                count++;
        }

        return count;
    }

    private static TimelineEntry? FindLatestGrantedWindowConsumerEntry(
        AutoGrantedChildRule childRule,
        IReadOnlyList<TimelineEntry> selectedEntries,
        double? windowStartTimeSec)
    {
        TimelineEntry? best = null;
        foreach (var selectedEntry in selectedEntries)
        {
            if (windowStartTimeSec.HasValue &&
                selectedEntry.TimeOffsetSec < windowStartTimeSec.Value - AutoCooldownToleranceSec)
            {
                continue;
            }

            if (childRule.ConsumerAbilityNames.Contains(selectedEntry.AbilityName))
                best = selectedEntry;
        }

        return best;
    }

    private bool TryReplaceGrantedWindowAlternative(
        TimelineEntry candidate,
        AutoGrantedCandidateDecision decision,
        List<TimelineEntry> grantedKeptEntries,
        AutoTimelineDebugRecorder? debug)
    {
        if (decision.ChildRule is not { } childRule ||
            childRule.WindowSelectionMode != AutoGrantedWindowSelectionMode.HighestFrequencyPerParentWindow ||
            childRule.AllowedUsesPerParentWindow != 1 ||
            decision.ExistingWindowConsumerEntry is not { } existingEntry)
        {
            return false;
        }

        var candidateIsStronger = candidate.Frequency > existingEntry.Frequency + 0.001 ||
                                  (Math.Abs(candidate.Frequency - existingEntry.Frequency) <= 0.001 &&
                                   candidate.TimeOffsetSec < existingEntry.TimeOffsetSec - 0.001);
        if (!candidateIsStronger)
            return false;

        grantedKeptEntries.Remove(existingEntry);
        grantedKeptEntries.Add(candidate);

        var parentSummary = decision.UsesVirtualParentWindow
            ? "the opener buffer's virtual pre-pull parent window"
            : decision.ParentEntry == null
                ? "the active parent window"
                : $"parent window {FormatAutoDebugEntry(decision.ParentEntry)}";
        debug?.Add($"    lose | {FormatAutoDebugEntry(existingEntry)} | replaced within {parentSummary} by stronger alternative {FormatAutoDebugEntry(candidate)}");
        debug?.Add($"    keep | {FormatAutoDebugEntry(candidate)} | strongest tracked alternative kept for {parentSummary}; replaced {FormatAutoDebugEntry(existingEntry)}");
        return true;
    }

    private static TimelineEntry? GetLastGrantedParentEntry(
        ISet<string> parentAbilityNames,
        IReadOnlyList<TimelineEntry> selectedEntries,
        double maxTimeSec)
    {
        TimelineEntry? best = null;
        foreach (var selectedEntry in selectedEntries)
        {
            if (selectedEntry.TimeOffsetSec > maxTimeSec + 0.001)
                break;

            if (parentAbilityNames.Contains(selectedEntry.AbilityName))
                best = selectedEntry;
        }

        return best;
    }

    private TimelineEntry? FindDueGrantedChildCandidate(
        IReadOnlyList<TimelineEntry> slotCandidates,
        IReadOnlyList<TimelineEntry> keptGcdEntries,
        IReadOnlyList<TimelineEntry> selectedOgcdEntries,
        IReadOnlyDictionary<string, AutoGrantedChildRule> grantedChildRules,
        AutoOpenerBufferInfo openerBuffer)
    {
        return slotCandidates
            .Where(candidate =>
                grantedChildRules.ContainsKey(candidate.AbilityName) &&
                IsGrantedCandidateAllowed(candidate, keptGcdEntries, selectedOgcdEntries, grantedChildRules, openerBuffer))
            .OrderBy(candidate => candidate.TimeOffsetSec)
            .ThenByDescending(candidate => candidate.Frequency)
            .FirstOrDefault();
    }

    private bool IsDotCandidateReady(
        TimelineEntry entry,
        IReadOnlyList<TimelineEntry> keptEntries)
    {
        var dotRule = DotDatabase.Lookup(entry.AbilityName);
        if (dotRule == null)
            return true;

        for (var i = keptEntries.Count - 1; i >= 0; i--)
        {
            var previous = keptEntries[i];
            if (!string.Equals(previous.AbilityName, entry.AbilityName, StringComparison.OrdinalIgnoreCase))
                continue;

            return entry.TimeOffsetSec >= previous.TimeOffsetSec + dotRule.RefreshReadySec - AutoCooldownToleranceSec;
        }

        return true;
    }

    private TimelineEntry? FindDueDotCandidate(
        IReadOnlyList<TimelineEntry> slotCandidates,
        IReadOnlyList<TimelineEntry> keptEntries)
    {
        return slotCandidates
            .Where(candidate =>
                DotDatabase.Lookup(candidate.AbilityName) != null &&
                IsDotCandidateReady(candidate, keptEntries))
            .OrderBy(candidate => candidate.TimeOffsetSec)
            .ThenByDescending(candidate => candidate.Frequency)
            .FirstOrDefault();
    }

    private TimelineEntry? FindDueComboContinuationCandidate(
        IReadOnlyList<TimelineEntry> slotCandidates,
        AutoComboBranchContext? activeComboBranch)
    {
        var expectedAbilityName = GetAutoComboNextAbility(activeComboBranch);
        if (string.IsNullOrWhiteSpace(expectedAbilityName) ||
            !TryGetAutoComboContinuationWindow(activeComboBranch, out var comboWindowStartSec, out var comboWindowEndSec))
        {
            return null;
        }

        return slotCandidates
            .Where(candidate =>
                string.Equals(candidate.AbilityName, expectedAbilityName, StringComparison.OrdinalIgnoreCase) &&
                candidate.TimeOffsetSec >= comboWindowStartSec - AutoCooldownToleranceSec &&
                candidate.TimeOffsetSec <= comboWindowEndSec + AutoCooldownToleranceSec)
            .OrderByDescending(candidate => candidate.Frequency)
            .ThenBy(candidate => Math.Abs(candidate.TimeOffsetSec - comboWindowStartSec))
            .ThenBy(candidate => candidate.TimeOffsetSec)
            .FirstOrDefault();
    }

    private List<string> GetGcdCandidateBlockers(
        TimelineEntry entry,
        double slotTimeSec,
        IReadOnlyList<TimelineEntry> keptEntries,
        IReadOnlyList<TimelineEntry> selectedOgcdEntries,
        IReadOnlyDictionary<string, AutoGrantedChildRule> grantedChildRules,
        AutoComboBranchContext? activeComboBranch,
        IReadOnlyDictionary<string, double> blockedUntilByAbility,
        AutoOpenerBufferInfo openerBuffer,
        string? openerRequirement)
    {
        var blockers = new List<string>();
        var isWithinOpenerBuffer = IsWithinAutoOpenerBuffer(openerBuffer, entry.TimeOffsetSec);
        if (blockedUntilByAbility.TryGetValue(entry.AbilityName, out var blockedUntilSec) &&
            slotTimeSec < blockedUntilSec - AutoCooldownToleranceSec)
        {
            blockers.Add($"pseudo cooldown still active until {FormatTime(blockedUntilSec)}");
        }

        if (!isWithinOpenerBuffer && IsComboBranchBlocked(entry.AbilityName, activeComboBranch))
        {
            var comboContextText = string.IsNullOrWhiteSpace(activeComboBranch?.ChosenLineSummary)
                ? $"active combo branch from {activeComboBranch?.StarterAbilityName}"
                : activeComboBranch!.ChosenLineSummary;
            blockers.Add($"blocked by combo branch: {comboContextText}");
        }

        var comboBlocker = GetComboCandidateBlocker(entry, keptEntries, activeComboBranch, openerBuffer, openerRequirement);
        if (!string.IsNullOrWhiteSpace(comboBlocker))
            blockers.Add(comboBlocker);

        if (DotDatabase.Lookup(entry.AbilityName) != null && !IsDotCandidateReady(entry, keptEntries))
            blockers.Add(BuildDotCandidateNotReadyReason(entry, keptEntries));

        if (grantedChildRules.ContainsKey(entry.AbilityName))
        {
            var earlierSelectedEntries = keptEntries
                .Concat(selectedOgcdEntries)
                .Where(selected => selected.TimeOffsetSec <= entry.TimeOffsetSec + 0.001)
                .OrderBy(selected => selected.TimeOffsetSec)
                .ThenByDescending(selected => selected.Frequency)
                .ToList();
            var grantedDecision = AnalyzeGrantedCandidateDecision(entry, earlierSelectedEntries, grantedChildRules, openerBuffer);
            if (!grantedDecision.IsAllowed)
                blockers.Add(grantedDecision.Summary);
        }

        return blockers;
    }

    private string GetGcdCandidateEligibilitySummary(
        TimelineEntry entry,
        double slotTimeSec,
        IReadOnlyList<TimelineEntry> keptEntries,
        IReadOnlyList<TimelineEntry> selectedOgcdEntries,
        IReadOnlyDictionary<string, AutoGrantedChildRule> grantedChildRules,
        JobComboDatabase.JobComboHints? comboHints,
        AutoComboBranchContext? activeComboBranch,
        AutoOpenerBufferInfo openerBuffer,
        string? openerRequirement)
    {
        var openerPrefix = BuildOpenerRequirementSummary(entry, slotTimeSec, openerRequirement);
        var comboPrefix = BuildComboCandidateEligibilitySummary(entry, slotTimeSec, keptEntries, comboHints, activeComboBranch, openerBuffer, openerRequirement);
        if (grantedChildRules.ContainsKey(entry.AbilityName))
        {
            var earlierSelectedEntries = keptEntries
                .Concat(selectedOgcdEntries)
                .Where(selected => selected.TimeOffsetSec <= entry.TimeOffsetSec + 0.001)
                .OrderBy(selected => selected.TimeOffsetSec)
                .ThenByDescending(selected => selected.Frequency)
                .ToList();
            return openerPrefix + comboPrefix + AnalyzeGrantedCandidateDecision(entry, earlierSelectedEntries, grantedChildRules, openerBuffer).Summary;
        }

        if (DotDatabase.Lookup(entry.AbilityName) != null)
        {
            var dotReason = BuildDotCandidateReadyReason(entry, keptEntries);
            return IsWithinAutoOpenerBuffer(openerBuffer, entry.TimeOffsetSec)
                ? $"{openerPrefix}{comboPrefix}{dotReason}; opener buffer is active for this timestamp"
                : $"{openerPrefix}{comboPrefix}{dotReason}";
        }

        return IsWithinAutoOpenerBuffer(openerBuffer, entry.TimeOffsetSec)
            ? $"{openerPrefix}{comboPrefix}eligible under current slot rules; opener buffer is active for shifted opener and pre-pull allowances"
            : $"{openerPrefix}{comboPrefix}eligible under current slot rules";
    }

    private string BuildGcdSelectionReason(
        TimelineEntry chosenEntry,
        double slotTimeSec,
        IReadOnlyList<TimelineEntry> allowedSlotCandidates,
        IReadOnlyList<TimelineEntry> keptEntries,
        IReadOnlyList<TimelineEntry> selectedOgcdEntries,
        IReadOnlyDictionary<string, AutoGrantedChildRule> grantedChildRules,
        JobComboDatabase.JobComboHints? comboHints,
        AutoComboBranchContext? activeComboBranch,
        AutoOpenerBufferInfo openerBuffer,
        string? openerRequirement)
    {
        var openerSuffix = IsWithinAutoOpenerBuffer(openerBuffer, chosenEntry.TimeOffsetSec)
            ? "; opener buffer active for this timestamp"
            : string.Empty;
        var openerReasonPrefix = BuildOpenerRequirementSummary(chosenEntry, slotTimeSec, openerRequirement);
        var comboReasonPrefix = BuildComboCandidateEligibilitySummary(chosenEntry, slotTimeSec, keptEntries, comboHints, activeComboBranch, openerBuffer, openerRequirement);
        if (grantedChildRules.ContainsKey(chosenEntry.AbilityName))
        {
            var earlierSelectedEntries = keptEntries
                .Concat(selectedOgcdEntries)
                .Where(selected => selected.TimeOffsetSec <= chosenEntry.TimeOffsetSec + 0.001)
                .OrderBy(selected => selected.TimeOffsetSec)
                .ThenByDescending(selected => selected.Frequency)
                .ToList();
            var grantedDecision = AnalyzeGrantedCandidateDecision(chosenEntry, earlierSelectedEntries, grantedChildRules, openerBuffer);
            return $"{openerReasonPrefix}{comboReasonPrefix}{grantedDecision.Summary}; granted children have priority over generic GCDs in the slot{openerSuffix}";
        }

        if (DotDatabase.Lookup(chosenEntry.AbilityName) != null)
            return $"{openerReasonPrefix}{comboReasonPrefix}{BuildDotCandidateReadyReason(chosenEntry, keptEntries)}; due DoTs have priority over generic GCDs in the slot{openerSuffix}";

        var selectedBecause = IsComboContinuationMatch(chosenEntry, activeComboBranch)
            ? $"active combo continuation held the slot against {allowedSlotCandidates.Count} allowed candidate(s)"
            : JobComboDatabase.IsComboStarter(chosenEntry.AbilityName)
            ? $"highest-frequency eligible combo starter ({chosenEntry.Frequency * 100.0:F1}%)"
            : $"highest-frequency eligible candidate ({chosenEntry.Frequency * 100.0:F1}%)";
        if (activeComboBranch != null &&
            activeComboBranch.BlockedAbilityNames.Count > 0 &&
            activeComboBranch.ChosenLine.Any(name => string.Equals(name, chosenEntry.AbilityName, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(activeComboBranch.ChosenLineSummary))
            return $"{openerReasonPrefix}{comboReasonPrefix}{selectedBecause}; active combo context: {activeComboBranch.ChosenLineSummary}{openerSuffix}";

        return $"{openerReasonPrefix}{comboReasonPrefix}{selectedBecause} among {allowedSlotCandidates.Count} allowed candidate(s){openerSuffix}";
    }

    private AutoOpenerBufferInfo BuildAutoOpenerBufferInfo(
        List<TimelineEntry> gcdEntries,
        string specName,
        AutoTimelineDebugRecorder? debug)
    {
        debug?.Add("Opener Buffer");
        var hints = BalanceOpenerDatabase.GetHints(specName);
        if (hints == null || hints.Variants.Count == 0)
        {
            debug?.Add("  disabled | no opener data is configured for this job");
            debug?.Add();
            return new AutoOpenerBufferInfo();
        }

        var (variant, startSlotOffset, matchedEntries) = SelectAutoOpenerEntries(gcdEntries, specName, debug);
        if (variant == null)
        {
            debug?.Add("  disabled | no opener variant could be identified");
            debug?.Add();
            return new AutoOpenerBufferInfo();
        }

        var concreteGcdCount = variant.GcdSequence.Count(IsConcreteOpenerAbilityName);
        if (matchedEntries.Count == 0 || variant.GcdSequence.Count <= startSlotOffset)
        {
            debug?.Add($"  disabled | {variant.Name} was the closest opener match, but no concrete opener GCDs appeared above threshold");
            debug?.Add();
            return new AutoOpenerBufferInfo();
        }

        var remainingSequence = variant.GcdSequence.Skip(startSlotOffset).ToArray();
        var openerBuffer = new AutoOpenerBufferInfo
        {
            Enabled = true,
            VariantName = variant.Name,
            StartSlotOffset = startSlotOffset,
            GcdSlotCount = remainingSequence.Length,
            EndTimeSec = remainingSequence.Length * AutoTargetGcdSec,
            GcdSequence = variant.GcdSequence.ToArray(),
            Steps = variant.Steps.ToArray(),
        };

        debug?.Add($"  active | variant {variant.Name} | start slot {startSlotOffset:00} ({variant.GcdSequence[startSlotOffset]}) | matched concrete GCDs {matchedEntries.Count}/{Math.Max(1, concreteGcdCount)} | buffer 0:00.0-{FormatTime(openerBuffer.EndTimeSec)}");
        debug?.Add("  relaxed rules | shifted opener slots may borrow pre-pull combo predecessors; granted parents and numeric gauge minimums may also seed from pre-pull state");
        debug?.Add("  opener requirement | concrete opener GCD slots are enforced against the shifted opener start; abstract slots still use normal selection");
        debug?.Add();
        return openerBuffer;
    }

    private string? GetAutoOpenerSlotRequirement(
        AutoOpenerBufferInfo openerBuffer,
        int slotIndex)
    {
        if (!openerBuffer.Enabled || openerBuffer.GcdSequence.Count == 0)
            return null;

        var sequenceIndex = openerBuffer.StartSlotOffset + slotIndex;
        if (sequenceIndex < 0 || sequenceIndex >= openerBuffer.GcdSequence.Count)
            return null;

        var abilityName = openerBuffer.GcdSequence[sequenceIndex];
        return IsConcreteOpenerAbilityName(abilityName) && IsStandardTimelineGcdAbility(abilityName)
            ? abilityName
            : null;
    }

    private static bool IsWithinAutoOpenerBuffer(
        AutoOpenerBufferInfo openerBuffer,
        double timeSec)
        => openerBuffer.Enabled &&
           timeSec <= openerBuffer.EndTimeSec + AutoCooldownToleranceSec;

    private string BuildDotCandidateReadyReason(
        TimelineEntry entry,
        IReadOnlyList<TimelineEntry> keptEntries)
    {
        var dotRule = DotDatabase.Lookup(entry.AbilityName);
        if (dotRule == null)
            return "not a DoT";

        var previousEntry = keptEntries
            .LastOrDefault(selected =>
                string.Equals(selected.AbilityName, entry.AbilityName, StringComparison.OrdinalIgnoreCase));
        if (previousEntry == null)
            return $"first observed DoT application; refresh window uses {dotRule.RefreshBufferSec:F1}s buffer on {dotRule.DurationSec:F1}s duration";

        var readyTimeSec = previousEntry.TimeOffsetSec + dotRule.RefreshReadySec;
        return $"refresh window is open after prior {FormatAutoDebugEntry(previousEntry)}; ready since {FormatTime(readyTimeSec)}";
    }

    private string BuildDotCandidateNotReadyReason(
        TimelineEntry entry,
        IReadOnlyList<TimelineEntry> keptEntries)
    {
        var dotRule = DotDatabase.Lookup(entry.AbilityName);
        if (dotRule == null)
            return "DoT rule unavailable";

        var previousEntry = keptEntries
            .LastOrDefault(selected =>
                string.Equals(selected.AbilityName, entry.AbilityName, StringComparison.OrdinalIgnoreCase));
        if (previousEntry == null)
            return "DoT unexpectedly lacked a prior application";

        var readyTimeSec = previousEntry.TimeOffsetSec + dotRule.RefreshReadySec;
        return $"DoT refresh window stays closed until {FormatTime(readyTimeSec)} after prior {FormatAutoDebugEntry(previousEntry)}";
    }

    private static bool IsComboBranchBlocked(
        string abilityName,
        AutoComboBranchContext? comboBranch)
        => comboBranch != null &&
           comboBranch.BlockedAbilityNames.Contains(abilityName);

    private string? GetComboCandidateBlocker(
        TimelineEntry entry,
        IReadOnlyList<TimelineEntry> keptEntries,
        AutoComboBranchContext? activeComboBranch,
        AutoOpenerBufferInfo openerBuffer,
        string? openerRequirement)
    {
        if (IsComboContinuationMatch(entry, activeComboBranch))
            return null;

        var predecessors = JobComboDatabase.GetPredecessors(entry.AbilityName);
        if (predecessors.Count == 0)
            return null;

        var previousKeptGcd = keptEntries.LastOrDefault();
        if (previousKeptGcd != null &&
            predecessors.Contains(previousKeptGcd.AbilityName))
        {
            return null;
        }

        if (CanBorrowPrepullComboPredecessor(entry.AbilityName, openerBuffer, openerRequirement))
            return null;

        return previousKeptGcd == null
            ? $"requires combo predecessor {FormatAbilityChoices(predecessors)} in the prior kept GCD slot, but no earlier GCD was kept"
            : $"requires combo predecessor {FormatAbilityChoices(predecessors)} in the prior kept GCD slot, but last kept GCD was {FormatAutoDebugEntry(previousKeptGcd)}";
    }

    private string BuildComboCandidateEligibilitySummary(
        TimelineEntry entry,
        double slotTimeSec,
        IReadOnlyList<TimelineEntry> keptEntries,
        JobComboDatabase.JobComboHints? comboHints,
        AutoComboBranchContext? activeComboBranch,
        AutoOpenerBufferInfo openerBuffer,
        string? openerRequirement)
    {
        var comboPseudoCooldownSec = GetComboPseudoCooldownSec(comboHints, entry.AbilityName);
        var parts = new List<string>();
        if (comboPseudoCooldownSec > 0.0)
            parts.Add($"combo pseudo cooldown spans {comboPseudoCooldownSec:F1}s between repeats");

        if (IsComboContinuationMatch(entry, activeComboBranch) &&
            TryGetAutoComboContinuationWindow(activeComboBranch, out var comboWindowStartSec, out var comboWindowEndSec))
        {
            var timingText = IsWithinAutoSlotWindow(entry.TimeOffsetSec, slotTimeSec)
                ? $"active combo window favors this continuation in {FormatTime(comboWindowStartSec)}-{FormatTime(comboWindowEndSec)}"
                : $"active combo window favors this continuation in {FormatTime(comboWindowStartSec)}-{FormatTime(comboWindowEndSec)} via combo timing tolerance from {FormatTime(entry.TimeOffsetSec)}";
            parts.Add(timingText);
        }

        var predecessors = JobComboDatabase.GetPredecessors(entry.AbilityName);
        if (predecessors.Count > 0)
        {
            var previousKeptGcd = keptEntries.LastOrDefault();
            if (previousKeptGcd != null &&
                predecessors.Contains(previousKeptGcd.AbilityName))
            {
                parts.Add($"combo predecessor {previousKeptGcd.AbilityName} was the previous kept GCD");
            }
            else if (IsComboContinuationMatch(entry, activeComboBranch) &&
                     activeComboBranch != null &&
                     activeComboBranch.NextStepIndex > 0)
            {
                parts.Add($"prior combo step {activeComboBranch.ChosenLine[activeComboBranch.NextStepIndex - 1]} already started this continuation window");
            }
            else if (CanBorrowPrepullComboPredecessor(entry.AbilityName, openerBuffer, openerRequirement))
            {
                parts.Add("shifted opener borrowed a pre-pull combo predecessor for this step");
            }
        }
        else if (JobComboDatabase.IsComboStarter(entry.AbilityName))
        {
            parts.Add("combo starter can reopen combo branch selection");
        }

        if (activeComboBranch != null &&
            activeComboBranch.BlockedAbilityNames.Count > 0 &&
            activeComboBranch.ChosenLine.Any(name => string.Equals(name, entry.AbilityName, StringComparison.OrdinalIgnoreCase)))
        {
            parts.Add("active combo branch keeps this line and prunes alternate followups until the next starter");
        }

        return parts.Count == 0
            ? string.Empty
            : string.Join("; ", parts) + "; ";
    }

    private static bool CanBorrowPrepullComboPredecessor(
        string abilityName,
        AutoOpenerBufferInfo openerBuffer,
        string? openerRequirement)
        => openerBuffer.Enabled &&
           !string.IsNullOrWhiteSpace(openerRequirement) &&
           DoesOpenerAbilityMatch(abilityName, openerRequirement) &&
           JobComboDatabase.GetPredecessors(abilityName).Count > 0;

    private static string FormatAbilityChoices(IEnumerable<string> abilityNames)
        => string.Join(" / ", abilityNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

    private AutoComboBranchContext? SelectAutoComboBranchContext(
        JobComboDatabase.JobComboHints? comboHints,
        string starterAbilityName,
        int starterSlotIndex,
        double starterTimeSec,
        List<TimelineEntry> sourceGcdEntries,
        ISet<string> usedKeys,
        AutoTimelineDebugRecorder? debug)
    {
        if (comboHints == null)
            return null;

        var candidateLines = comboHints.ComboLines
            .Where(line => line.Count > 0 &&
                           string.Equals(line[0], starterAbilityName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidateLines.Count == 0)
            return null;

        if (candidateLines.Count == 1)
            return new AutoComboBranchContext
            {
                StarterAbilityName = starterAbilityName,
                StarterSlotIndex = starterSlotIndex,
                LastMatchedTimeSec = starterTimeSec,
                LastMatchedRecastSec = GetAutoTimelineGcdRecastSec(starterAbilityName),
                NextStepIndex = 1,
                ChosenLine = candidateLines[0].ToArray(),
                ChosenLineSummary = $"single combo line after {starterAbilityName}: {string.Join(" > ", candidateLines[0])}",
            };

        var divergenceIndex = GetComboBranchDivergenceIndex(candidateLines);
        if (divergenceIndex < 0)
            return new AutoComboBranchContext
            {
                StarterAbilityName = starterAbilityName,
                StarterSlotIndex = starterSlotIndex,
                LastMatchedTimeSec = starterTimeSec,
                LastMatchedRecastSec = GetAutoTimelineGcdRecastSec(starterAbilityName),
                NextStepIndex = 1,
                ChosenLine = candidateLines[0].ToArray(),
                ChosenLineSummary = $"single combo line after {starterAbilityName}: {string.Join(" > ", candidateLines[0])}",
            };

        IReadOnlyList<string>? chosenLine = null;
        var bestBranchFrequency = double.NegativeInfinity;
        var bestMatchedSteps = int.MinValue;
        var bestTotalScore = double.NegativeInfinity;
        var branchSummaries = new List<string>();
        foreach (var line in candidateLines)
        {
            var branchAbilityName = line[divergenceIndex];
            var branchSlotTimeSec = starterTimeSec + GetAutoComboLineOffsetSec(line, divergenceIndex);
            var branchFrequency = sourceGcdEntries
                .Where(entry =>
                    !usedKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
                    string.Equals(entry.AbilityName, branchAbilityName, StringComparison.OrdinalIgnoreCase) &&
                    entry.TimeOffsetSec >= branchSlotTimeSec - AutoGcdSlotMatchToleranceSec &&
                    entry.TimeOffsetSec < branchSlotTimeSec + AutoTargetGcdSec + AutoGcdSlotMatchToleranceSec)
                .Select(entry => entry.Frequency)
                .DefaultIfEmpty(0.0)
                .Max();

            var (matchedSteps, totalScore) = GetComboBranchSelectionScore(
                line,
                starterTimeSec,
                sourceGcdEntries,
                usedKeys);
            branchSummaries.Add($"{branchAbilityName} {branchFrequency * 100.0:F1}% | matched {matchedSteps} | score {totalScore:F1}");
            if (chosenLine == null ||
                branchFrequency > bestBranchFrequency ||
                (Math.Abs(branchFrequency - bestBranchFrequency) < 0.0001 && matchedSteps > bestMatchedSteps) ||
                (Math.Abs(branchFrequency - bestBranchFrequency) < 0.0001 && matchedSteps == bestMatchedSteps && totalScore > bestTotalScore))
            {
                chosenLine = line;
                bestBranchFrequency = branchFrequency;
                bestMatchedSteps = matchedSteps;
                bestTotalScore = totalScore;
            }
        }

        if (chosenLine == null)
            return null;

        var chosenLineAbilityNames = chosenLine.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blockedAbilityNames = candidateLines
            .Where(line => !ReferenceEquals(line, chosenLine))
            .SelectMany(line => line.Skip(divergenceIndex))
            .Where(abilityName => !chosenLineAbilityNames.Contains(abilityName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chosenLineSummary = $"{starterAbilityName} branch -> {string.Join(" > ", chosenLine)} | compared {string.Join(" ; ", branchSummaries)}";
        if (blockedAbilityNames.Count > 0)
            debug?.Add($"  combo-branch | {chosenLineSummary}");

        return new AutoComboBranchContext
        {
            StarterAbilityName = starterAbilityName,
            StarterSlotIndex = starterSlotIndex,
            LastMatchedTimeSec = starterTimeSec,
            LastMatchedRecastSec = GetAutoTimelineGcdRecastSec(starterAbilityName),
            NextStepIndex = 1,
            ChosenLine = chosenLine.ToArray(),
            BlockedAbilityNames = blockedAbilityNames,
            ChosenLineSummary = chosenLineSummary,
        };
    }

    private static string? GetAutoComboNextAbility(
        AutoComboBranchContext? comboBranch)
    {
        if (comboBranch == null ||
            comboBranch.NextStepIndex < 0 ||
            comboBranch.NextStepIndex >= comboBranch.ChosenLine.Count)
        {
            return null;
        }

        return comboBranch.ChosenLine[comboBranch.NextStepIndex];
    }

    private static bool TryGetAutoComboContinuationWindow(
        AutoComboBranchContext? comboBranch,
        out double windowStartSec,
        out double windowEndSec)
    {
        windowStartSec = 0.0;
        windowEndSec = 0.0;
        if (comboBranch == null || string.IsNullOrWhiteSpace(GetAutoComboNextAbility(comboBranch)))
            return false;

        var comboStepRecastSec = comboBranch.LastMatchedRecastSec > 0.0
            ? comboBranch.LastMatchedRecastSec
            : AutoTargetGcdSec;
        windowStartSec = comboBranch.LastMatchedTimeSec + comboStepRecastSec;
        windowEndSec = windowStartSec + comboStepRecastSec;
        return true;
    }

    private static bool IsAutoComboContextExpired(
        AutoComboBranchContext? comboBranch,
        double slotTimeSec)
    {
        if (comboBranch == null)
            return false;

        if (!TryGetAutoComboContinuationWindow(comboBranch, out _, out var comboWindowEndSec))
            return true;

        return slotTimeSec > comboWindowEndSec + AutoCooldownToleranceSec;
    }

    private static bool IsComboContinuationMatch(
        TimelineEntry entry,
        AutoComboBranchContext? comboBranch)
    {
        var expectedAbilityName = GetAutoComboNextAbility(comboBranch);
        return !string.IsNullOrWhiteSpace(expectedAbilityName) &&
               TryGetAutoComboContinuationWindow(comboBranch, out var comboWindowStartSec, out var comboWindowEndSec) &&
               string.Equals(entry.AbilityName, expectedAbilityName, StringComparison.OrdinalIgnoreCase) &&
               entry.TimeOffsetSec >= comboWindowStartSec - AutoCooldownToleranceSec &&
               entry.TimeOffsetSec <= comboWindowEndSec + AutoCooldownToleranceSec;
    }

    private static IEnumerable<TimelineEntry> GetAutoComboContinuationCandidates(
        IReadOnlyList<TimelineEntry> sourceGcdEntries,
        ISet<string> usedKeys,
        AutoComboBranchContext? comboBranch,
        double slotTimeSec)
    {
        var expectedAbilityName = GetAutoComboNextAbility(comboBranch);
        if (string.IsNullOrWhiteSpace(expectedAbilityName) ||
            !TryGetAutoComboContinuationWindow(comboBranch, out var comboWindowStartSec, out var comboWindowEndSec))
        {
            return [];
        }

        var slotWindowEndSec = slotTimeSec + AutoTargetGcdSec;
        if (slotWindowEndSec < comboWindowStartSec - AutoCooldownToleranceSec ||
            slotTimeSec > comboWindowEndSec + AutoCooldownToleranceSec)
        {
            return [];
        }

        return sourceGcdEntries
            .Where(entry =>
                !usedKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
                string.Equals(entry.AbilityName, expectedAbilityName, StringComparison.OrdinalIgnoreCase) &&
                entry.TimeOffsetSec >= comboWindowStartSec - AutoCooldownToleranceSec &&
                entry.TimeOffsetSec <= comboWindowEndSec + AutoCooldownToleranceSec)
            .OrderByDescending(entry => entry.Frequency)
            .ThenBy(entry => Math.Abs(entry.TimeOffsetSec - comboWindowStartSec))
            .ThenBy(entry => entry.TimeOffsetSec)
            .ToList();
    }

    private AutoComboBranchContext? AdvanceAutoComboBranchContext(
        AutoComboBranchContext? comboBranch,
        TimelineEntry chosenEntry)
    {
        if (!IsComboContinuationMatch(chosenEntry, comboBranch) ||
            comboBranch == null)
        {
            return comboBranch;
        }

        var nextStepIndex = comboBranch.NextStepIndex + 1;
        if (nextStepIndex >= comboBranch.ChosenLine.Count)
            return null;

        return new AutoComboBranchContext
        {
            StarterAbilityName = comboBranch.StarterAbilityName,
            StarterSlotIndex = comboBranch.StarterSlotIndex,
            LastMatchedTimeSec = chosenEntry.TimeOffsetSec,
            LastMatchedRecastSec = GetAutoTimelineGcdRecastSec(chosenEntry),
            NextStepIndex = nextStepIndex,
            ChosenLine = comboBranch.ChosenLine,
            BlockedAbilityNames = comboBranch.BlockedAbilityNames,
            ChosenLineSummary = comboBranch.ChosenLineSummary,
        };
    }

    private static int GetComboBranchDivergenceIndex(
        IReadOnlyList<IReadOnlyList<string>> candidateLines)
    {
        if (candidateLines.Count <= 1)
            return -1;

        var maxLength = candidateLines.Max(line => line.Count);
        for (var index = 0; index < maxLength; index++)
        {
            var abilityNames = candidateLines
                .Where(line => line.Count > index)
                .Select(line => line[index])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (abilityNames.Count > 1)
                return index;
        }

        return -1;
    }

    private (int MatchedSteps, double TotalScore) GetComboBranchSelectionScore(
        IReadOnlyList<string> line,
        double starterTimeSec,
        IReadOnlyList<TimelineEntry> sourceGcdEntries,
        ISet<string> usedKeys)
    {
        var matchedSteps = 0;
        var totalScore = 0.0;
        for (var stepIndex = 0; stepIndex < line.Count; stepIndex++)
        {
            var slotTimeSec = starterTimeSec + GetAutoComboLineOffsetSec(line, stepIndex);
            var candidate = sourceGcdEntries
                .Where(entry =>
                    !usedKeys.Contains(GetAutoEntryIdentityKey(entry)) &&
                    string.Equals(entry.AbilityName, line[stepIndex], StringComparison.OrdinalIgnoreCase) &&
                    entry.TimeOffsetSec >= slotTimeSec - AutoGcdSlotMatchToleranceSec &&
                    entry.TimeOffsetSec < slotTimeSec + AutoTargetGcdSec + AutoGcdSlotMatchToleranceSec)
                .OrderByDescending(entry => entry.Frequency)
                .ThenBy(entry => entry.TimeOffsetSec)
                .FirstOrDefault();
            if (candidate == null)
                continue;

            matchedSteps++;
            totalScore += candidate.Frequency * 100.0;
        }

        return (matchedSteps, totalScore);
    }

    private TimelineEntry KeepAutoGcdSelection(
        TimelineEntry chosenEntry,
        double slotTimeSec,
        List<TimelineEntry> keptEntries,
        HashSet<string> usedKeys,
        Dictionary<string, double> blockedUntilByAbility,
        JobComboDatabase.JobComboHints? comboHints,
        AutoTimelineDebugRecorder? debug,
        string reason,
        string detail)
    {
        var chosenKey = GetAutoEntryIdentityKey(chosenEntry);
        if (!usedKeys.Add(chosenKey))
            return chosenEntry;

        var scheduledEntry = CreateScheduledEntry(chosenEntry, slotTimeSec);
        keptEntries.Add(scheduledEntry);
        var comboDurationSec = GetComboPseudoCooldownSec(comboHints, chosenEntry.AbilityName);
        if (comboDurationSec > 0.0)
        {
            blockedUntilByAbility[chosenEntry.AbilityName] = Math.Max(
                blockedUntilByAbility.GetValueOrDefault(chosenEntry.AbilityName),
                scheduledEntry.TimeOffsetSec + comboDurationSec);
        }

        debug?.Add($"  slot-choice | {reason} | {chosenEntry.AbilityName}");
        debug?.Add($"  slot-keep | {FormatAutoDebugEntry(scheduledEntry)} | slot {FormatAutoDebugSlotWindow(slotTimeSec)} | {detail}");
        return scheduledEntry;
    }

    private static string FormatAutoDebugEntry(TimelineEntry entry)
        => $"{entry.AbilityName} @ {FormatTime(entry.TimeOffsetSec)} | freq {(entry.Frequency * 100.0):F1}% | avg uses {entry.AverageUses:F2}";

    private static string FormatAutoDebugSlotWindow(double slotTimeSec)
        => $"{FormatTime(slotTimeSec)}-{FormatTime(slotTimeSec + AutoTargetGcdSec)}";

    private static double GetNextAutoGcdSlotStartSec(
        double currentSlotTimeSec,
        TimelineEntry? chosenEntry,
        double slotIntervalSec)
    {
        _ = chosenEntry;
        return currentSlotTimeSec + slotIntervalSec;
    }

    private static IReadOnlyList<string> GetRelevantGaugeNames(
        GaugeSimulator.JobGaugeRules? gaugeRules,
        string abilityName)
    {
        if (gaugeRules == null || !gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
            return [];

        return effects
            .Select(effect => effect.GaugeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FormatGaugeStateForDebug(
        AutoTimelineState state,
        IReadOnlyList<string> relevantGaugeNames)
    {
        if (relevantGaugeNames.Count == 0)
            return "no tracked gauge";

        return string.Join(
            ", ",
            relevantGaugeNames.Select(name => $"{name}={state.GaugeState.GetValueOrDefault(name)}"));
    }

    private double GetComboPseudoCooldownSec(
        JobComboDatabase.JobComboHints? comboHints,
        string abilityName)
    {
        if (comboHints == null)
            return 0.0;

        var maxLineDurationSec = comboHints.ComboLines
            .Where(line => line.Any(name => string.Equals(name, abilityName, StringComparison.OrdinalIgnoreCase)))
            .Select(GetAutoComboLineDurationSec)
            .DefaultIfEmpty(0.0)
            .Max();

        return maxLineDurationSec <= 0.0
            ? 0.0
            : maxLineDurationSec;
    }

    private Dictionary<string, double> BuildGcdPseudoCooldowns(string specName)
    {
        var pseudoCooldowns = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (JobComboDatabase.GetHints(specName) is { } comboHints)
        {
            foreach (var line in comboHints.ComboLines)
            {
                var pseudoCooldownSec = GetAutoComboLineDurationSec(line);
                foreach (var abilityName in line)
                {
                    pseudoCooldowns[abilityName] = Math.Max(
                        pseudoCooldowns.GetValueOrDefault(abilityName),
                        pseudoCooldownSec);
                }
            }
        }

        foreach (var abilityName in DotDatabase.AbilityNames)
        {
            if (DotDatabase.Lookup(abilityName) is not { } dotRule)
                continue;

            pseudoCooldowns[abilityName] = Math.Max(
                pseudoCooldowns.GetValueOrDefault(abilityName),
                dotRule.RefreshReadySec);
        }

        return pseudoCooldowns;
    }

    private double GetAutoTimelineGcdRecastSec(TimelineEntry? entry)
    {
        if (entry == null)
            return AutoTargetGcdSec;

        var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        return GetAutoTimelineGcdRecastSec(info);
    }

    private double GetAutoTimelineGcdRecastSec(string abilityName)
        => GetAutoTimelineGcdRecastSec(plugin.RecastDatabase.Lookup(0, abilityName));

    private static double GetAutoTimelineGcdRecastSec(Data.RecastDatabase.RecastInfo? info)
        => info != null && info.IsGcdAction && info.RecastSec >= 1.5 && info.RecastSec <= 4.5
            ? info.RecastSec
            : AutoTargetGcdSec;

    private double GetAutoComboLineOffsetSec(
        IReadOnlyList<string> line,
        int stepIndex)
    {
        var offsetSec = 0.0;
        for (var index = 0; index < stepIndex && index < line.Count; index++)
            offsetSec += GetAutoTimelineGcdRecastSec(line[index]);

        return offsetSec;
    }

    private double GetAutoComboLineDurationSec(IReadOnlyList<string> line)
        => line.Count <= 1
            ? 0.0
            : GetAutoComboLineOffsetSec(line, line.Count - 1);

    private string? GetSoftBurstPreferredAbility(
        BalanceOpenerDatabase.JobOpenerVariant? openerVariant,
        double slotTimeSec)
    {
        if (openerVariant == null)
            return null;

        var burstSequence = openerVariant.GcdSequence
            .Where(IsStandardTimelineGcdAbility)
            .Take(12)
            .ToArray();
        if (burstSequence.Length == 0)
            return null;

        for (var burstIndex = 1; burstIndex < 10; burstIndex++)
        {
            var burstStartSec = burstIndex * AutoBurstCadenceSec;
            if (slotTimeSec < burstStartSec - AutoBurstEarlyWindowSec ||
                slotTimeSec > burstStartSec + AutoBurstLateWindowSec)
                continue;

            var relativeSec = Math.Max(0.0, slotTimeSec - burstStartSec);
            var gcdIndex = Math.Clamp((int)Math.Round(relativeSec / AutoTargetGcdSec), 0, burstSequence.Length - 1);
            return burstSequence[gcdIndex];
        }

        return null;
    }

    private bool IsStandardTimelineGcdAbility(string abilityName)
    {
        var info = plugin.RecastDatabase.Lookup(0, abilityName);
        return IsAutoTimelineGcd(info);
    }

    private double GetEarliestAutoScheduleTime(
        AutoTimelineState state,
        TimelineEntry entry,
        Data.RecastDatabase.RecastInfo? info,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        double requestedTimeSec)
    {
        var scheduledTimeSec = Math.Max(requestedTimeSec, state.CastLockUntilSec);
        if (!double.IsNegativeInfinity(state.LastOgcdTimeSec))
            scheduledTimeSec = Math.Max(scheduledTimeSec, state.LastOgcdTimeSec + AutoOgcdLockSec);

        return GetCooldownReadyTime(state, info, grantedRules, entry.AbilityName, scheduledTimeSec, AutoCooldownToleranceSec);
    }

    private double ScoreAutoEntryCandidate(
        AutoTimelineState state,
        TimelineEntry entry,
        Data.RecastDatabase.RecastInfo? info,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        double scheduledTimeSec)
    {
        var score = entry.Frequency * 42.0;
        score += entry.AverageUses * 2.0;
        score -= Math.Abs(scheduledTimeSec - entry.TimeOffsetSec) * 8.0;
        return score;
    }

    private bool IsGcdEntry(TimelineEntry entry)
    {
        var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        return IsAutoTimelineGcd(info);
    }

    private static bool IsAutoTimelineGcd(Data.RecastDatabase.RecastInfo? info)
        => info?.IsGcdAction == true;

    private bool IsComboTrackedAction(Data.RecastDatabase.RecastInfo? info, string abilityName)
    {
        if (info == null || !info.IsGcdAction)
            return false;

        if (ShouldValidateComboRequirement(info, abilityName) && info.ComboActionId > 0)
            return true;

        var knownFollowers = JobComboDatabase.GetFollowers(abilityName);
        if (knownFollowers.Count > 0)
            return true;

        return plugin.RecastDatabase.GetAllActions().Any(action =>
            action.IsGcdAction &&
            action.ComboActionId == info.AbilityId);
    }

    private HashSet<string> GetComboPredecessorNames(Data.RecastDatabase.RecastInfo info)
    {
        var names = JobComboDatabase.GetPredecessors(info.Name);
        if (info.ComboActionId <= 0)
            return names;

        foreach (var name in plugin.RecastDatabase.GetAllActions()
            .Where(action =>
                action.IsGcdAction &&
                action.AbilityId == info.ComboActionId)
            .Select(action => action.Name))
        {
            names.Add(name);
        }

        foreach (var name in names.ToArray())
        {
            if (!ComboPredecessorAliases.TryGetValue(name, out var aliases))
                continue;

            foreach (var alias in aliases)
                names.Add(alias);
        }

        return names;
    }

    private bool ShouldValidateComboRequirement(
        Data.RecastDatabase.RecastInfo info,
        string abilityName)
    {
        if (ComboValidationExemptAbilities.Contains(abilityName))
            return false;

        if (!info.IsGcdAction || info.ActionCategory != 3 || info.ComboActionId <= 0)
            return false;

        var actionRule = plugin.ActionStateDatabase.Lookup((int)info.AbilityId, abilityName);
        return actionRule == null ||
               !actionRule.Effects.Any(effect =>
                   effect.MinRequired > 0 &&
                   !effect.StateName.StartsWith("Action Grant::", StringComparison.OrdinalIgnoreCase));
    }

    private bool ShouldIgnoreActionStateRequirement(
        ActionStateDatabase.ActionStateRule actionRule,
        ActionStateDatabase.ActionStateEffect effect,
        string abilityName,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        if (!effect.StateName.StartsWith("Action Grant::", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<GaugeSimulator.GaugeEffect> gaugeEffects = [];
            IReadOnlyList<GrantedActionDatabase.GrantedActionEffect> grantedEffects = [];
            var hasGaugeEffects = gaugeRules?.EffectByName.TryGetValue(abilityName, out gaugeEffects) == true;
            var hasGrantedEffects = grantedRules?.EffectByName.TryGetValue(abilityName, out grantedEffects) == true;
            if (hasGaugeEffects || hasGrantedEffects)
            {
                foreach (var equivalentStateName in EnumerateEquivalentStateNames(effect.StateName))
                {
                    var matchesKnownGauge = gaugeRules?.Resources.Any(resource =>
                        string.Equals(resource.Name, equivalentStateName, StringComparison.OrdinalIgnoreCase));
                    var matchesKnownGrant = grantedRules?.Resources.Any(resource =>
                        string.Equals(resource.Name, equivalentStateName, StringComparison.OrdinalIgnoreCase));
                    if (matchesKnownGauge != true && matchesKnownGrant != true)
                        continue;

                    var explicitlyRequiredByGaugeRule = (gaugeEffects ?? []).Any(gaugeEffect =>
                        gaugeEffect.MinRequired > 0 &&
                        EnumerateEquivalentStateNames(gaugeEffect.GaugeName).Any(gaugeStateName =>
                            string.Equals(gaugeStateName, equivalentStateName, StringComparison.OrdinalIgnoreCase)));
                    var explicitlyRequiredByGrantRule = (grantedEffects ?? []).Any(grantedEffect =>
                        grantedEffect.MinRequired > 0 &&
                        EnumerateEquivalentStateNames(grantedEffect.ResourceName).Any(grantedStateName =>
                            string.Equals(grantedStateName, equivalentStateName, StringComparison.OrdinalIgnoreCase)));
                    if (!explicitlyRequiredByGaugeRule && !explicitlyRequiredByGrantRule)
                        return true;
                }
            }

            return false;
        }

        if (gaugeRules?.EffectByName.ContainsKey(abilityName) == true ||
            grantedRules?.EffectByName.ContainsKey(abilityName) == true)
            return true;

        return actionRule.Effects.Any(other =>
            !ReferenceEquals(other, effect) &&
            other.MinRequired > 0 &&
            !other.StateName.StartsWith("Action Grant::", StringComparison.OrdinalIgnoreCase));
    }

    private bool ShouldEnforceActionStateRequirement(
        string stateName,
        ISet<string> abilityNames,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        foreach (var equivalentStateName in EnumerateEquivalentStateNames(stateName))
        {
            if (gaugeRules?.Resources.Any(resource =>
                    string.Equals(resource.Name, equivalentStateName, StringComparison.OrdinalIgnoreCase)) == true)
                return true;

            if (grantedRules?.Resources.Any(resource =>
                    string.Equals(resource.Name, equivalentStateName, StringComparison.OrdinalIgnoreCase)) == true)
                return true;
        }

        return plugin.ActionStateDatabase.HasGrantorInSet(stateName, abilityNames);
    }

    private double GetNextAutoGcdReadyTime(
        AutoTimelineState state,
        Data.RecastDatabase.RecastInfo? info,
        double scheduledTimeSec,
        double baseGcdRecastSec)
    {
        var actionRecastSec = info?.RecastSec is > 0.0 and <= 4.5 ? info.RecastSec : baseGcdRecastSec;
        var castSec = Math.Max(0.0, info?.CastTimeSec ?? 0.0);
        return Math.Max(scheduledTimeSec + Math.Max(baseGcdRecastSec, Math.Max(actionRecastSec, castSec)), state.CastLockUntilSec);
    }

    private double GetAutoBaseGcdRecastSec(IReadOnlyList<TimelineEntry> gcdEntries)
    {
        if (gcdEntries.Count == 0)
            return AutoTargetGcdSec;

        var weightedRecasts = gcdEntries
            .Select(entry => plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName))
            .Where(info => info != null && info.IsGcdAction && info.RecastSec >= 1.5 && info.RecastSec <= 4.5)
            .GroupBy(info => Math.Round(info!.RecastSec, 2))
            .Select(group => new
            {
                RecastSec = group.Key,
                Weight = group.Count(),
            })
            .OrderByDescending(group => group.Weight)
            .ThenBy(group => group.RecastSec)
            .ToList();

        if (weightedRecasts.Count == 0)
            return AutoTargetGcdSec;

        return weightedRecasts[0].RecastSec;
    }

    private static TimelineEntry CreateScheduledEntry(TimelineEntry entry, double timeOffsetSec)
        => new()
        {
            TimeOffsetSec = timeOffsetSec,
            AbilityId = entry.AbilityId,
            AbilityName = entry.AbilityName,
            AbilityIcon = entry.AbilityIcon,
            Frequency = entry.Frequency,
            AverageUses = entry.AverageUses,
            IsGcd = entry.IsGcd,
        };

    private static string GetAutoEntryIdentityKey(TimelineEntry entry)
        => string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0}|{1}|{2:F3}|{3:F4}|{4:F4}",
            entry.AbilityId,
            entry.AbilityName,
            entry.TimeOffsetSec,
            entry.Frequency,
            entry.AverageUses);

    private static bool MatchesCastRule(
        GrantedActionDatabase.InstantCastRule rule,
        string abilityName,
        bool isCastTimeSpell)
        => (rule.AppliesToAnyCastTimeSpell && isCastTimeSpell) ||
           rule.AbilityNames.Contains(abilityName);

    private static bool MatchesCastRule(
        GrantedActionDatabase.HardcastGrantRule rule,
        string abilityName,
        bool isCastTimeSpell)
        => (rule.AppliesToAnyCastTimeSpell && isCastTimeSpell) ||
           rule.AbilityNames.Contains(abilityName);

    private GrantedActionDatabase.RepeatableGrantedActionRule? FindRepeatableGrantedActionRule(
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        if (grantedRules == null)
            return null;

        return grantedRules.RepeatableGrantedActionRules.FirstOrDefault(rule =>
            string.Equals(rule.TriggerName, abilityName, StringComparison.OrdinalIgnoreCase) ||
            rule.ConsumerNames.Contains(abilityName));
    }

    private bool UsesRepeatableGrantedActionCharge(
        Dictionary<string, int> grantedState,
        GrantedActionDatabase.RepeatableGrantedActionRule rule,
        string abilityName)
    {
        if (rule.ConsumerNames.Contains(abilityName))
            return grantedState.GetValueOrDefault(rule.ResourceName) >= rule.ConsumeCount;

        return rule.TriggerConsumesWhenResourcePresent &&
               string.Equals(rule.TriggerName, abilityName, StringComparison.OrdinalIgnoreCase) &&
               grantedState.GetValueOrDefault(rule.ResourceName) >= rule.ConsumeCount;
    }

    private bool HasRepeatableGrantedActionCharge(
        Dictionary<string, int> grantedState,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        var repeatRule = FindRepeatableGrantedActionRule(grantedRules, abilityName);
        return repeatRule != null &&
               UsesRepeatableGrantedActionCharge(grantedState, repeatRule, abilityName);
    }

    private bool ShouldBypassCooldown(
        Dictionary<string, int> grantedState,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        var repeatRule = FindRepeatableGrantedActionRule(grantedRules, abilityName);
        return repeatRule != null &&
               repeatRule.SkipCooldownWhenConsuming &&
               UsesRepeatableGrantedActionCharge(grantedState, repeatRule, abilityName);
    }

    private GrantedActionDatabase.InstantCastRule? FindInstantCastRule(
        Dictionary<string, int> grantedState,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        Data.RecastDatabase.RecastInfo? info,
        string abilityName)
    {
        if (grantedRules == null || info == null || info.CastTimeSec <= AutoCastLockToleranceSec)
            return null;

        foreach (var rule in grantedRules.InstantCastRules)
        {
            if (!MatchesCastRule(rule, abilityName, isCastTimeSpell: true))
                continue;

            if (grantedState.GetValueOrDefault(rule.ResourceName) >= rule.MinRequired)
                return rule;
        }

        return null;
    }

    private double GetEffectiveCastTimeSec(
        Dictionary<string, int> grantedState,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        Data.RecastDatabase.RecastInfo? info,
        string abilityName,
        out GrantedActionDatabase.InstantCastRule? instantCastRule)
    {
        instantCastRule = null;
        if (info == null || info.CastTimeSec <= AutoCastLockToleranceSec)
            return 0.0;

        instantCastRule = FindInstantCastRule(grantedState, grantedRules, info, abilityName);
        return instantCastRule != null ? 0.0 : info.CastTimeSec;
    }

    private void ApplyCastStateTransitions(
        Dictionary<string, int> grantedState,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        Data.RecastDatabase.RecastInfo? info,
        string abilityName,
        double effectiveCastTimeSec,
        GrantedActionDatabase.InstantCastRule? instantCastRule)
    {
        if (grantedRules == null || info == null)
            return;

        if (instantCastRule != null)
        {
            var maxValue = grantedRules.Resources
                .FirstOrDefault(r => string.Equals(r.Name, instantCastRule.ResourceName, StringComparison.OrdinalIgnoreCase))
                ?.MaxValue ?? int.MaxValue;
            grantedState[instantCastRule.ResourceName] = Math.Clamp(
                grantedState.GetValueOrDefault(instantCastRule.ResourceName) - instantCastRule.Consume,
                0,
                maxValue);
            return;
        }

        if (effectiveCastTimeSec <= AutoCastLockToleranceSec)
            return;

        foreach (var rule in grantedRules.HardcastGrantRules)
        {
            if (!MatchesCastRule(rule, abilityName, isCastTimeSpell: true))
                continue;

            var maxValue = grantedRules.Resources
                .FirstOrDefault(r => string.Equals(r.Name, rule.ResourceName, StringComparison.OrdinalIgnoreCase))
                ?.MaxValue ?? int.MaxValue;
            grantedState[rule.ResourceName] = Math.Clamp(
                grantedState.GetValueOrDefault(rule.ResourceName) + rule.Delta,
                0,
                maxValue);
        }
    }

    private AutoTimelineState CreateAutoTimelineState(
        string specName,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        ISet<string>? availableAbilityNames = null)
    {
        var state = new AutoTimelineState
        {
            SpecName = specName,
        };
        if (availableAbilityNames != null)
            state.AvailableAbilityNames = new HashSet<string>(availableAbilityNames, StringComparer.OrdinalIgnoreCase);
        if (gaugeRules == null)
            return state;

        foreach (var resource in gaugeRules.Resources)
        {
            state.GaugeState[resource.Name] = resource.InitialValue;
            state.PassiveGaugeProgress[resource.Name] = 0.0;
        }

        if (grantedRules != null)
        {
            foreach (var resource in grantedRules.Resources)
                state.GrantedState[resource.Name] = resource.InitialValue;
        }

        return state;
    }

    private static bool TryGetTimedUsageWindowRule(
        string specName,
        string abilityName,
        out string parentAbilityName,
        out double durationSec)
    {
        if (string.Equals(specName, "Scholar", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(abilityName, "Energy Drain", StringComparison.OrdinalIgnoreCase))
        {
            parentAbilityName = "Chain Stratagem";
            durationSec = 20.0;
            return true;
        }

        parentAbilityName = string.Empty;
        durationSec = 0.0;
        return false;
    }

    private static bool TryGetTimedUsageLockoutRule(
        string specName,
        string abilityName,
        out string sourceAbilityName,
        out double durationSec)
    {
        if (string.Equals(specName, "Scholar", StringComparison.OrdinalIgnoreCase) &&
            abilityName is "Fey Blessing" or "Whispering Dawn" or "Fey Illumination" or "Summon Seraph" or "Seraphism")
        {
            sourceAbilityName = "Dissipation";
            durationSec = 30.0;
            return true;
        }

        sourceAbilityName = string.Empty;
        durationSec = 0.0;
        return false;
    }

    private static bool IsScholarAetherflowSpender(string abilityName)
        => abilityName is "Energy Drain" or "Energy Siphon" or "Lustrate" or "Indomitability" or "Excogitation" or "Sacred Soil";

    private static bool IsScholarBurstReservedAetherflowSpender(string abilityName)
        => IsScholarAetherflowSpender(abilityName) &&
           !string.Equals(abilityName, "Energy Drain", StringComparison.OrdinalIgnoreCase);

    private static string? GetScholarBurstAetherflowReservationReason(
        string specName,
        IReadOnlyDictionary<string, double> timedWindowEndByAbility,
        IReadOnlyDictionary<string, int> gaugeState,
        string abilityName,
        double timeSec)
    {
        if (!string.Equals(specName, "Scholar", StringComparison.OrdinalIgnoreCase) ||
            !IsScholarBurstReservedAetherflowSpender(abilityName))
            return null;

        if (!timedWindowEndByAbility.TryGetValue("Energy Drain", out var energyDrainWindowEndSec) ||
            timeSec > energyDrainWindowEndSec + AutoCooldownToleranceSec)
            return null;

        var aetherflow = gaugeState.GetValueOrDefault("Aetherflow");
        if (aetherflow <= 0)
            return null;

        return $"Aetherflow is reserved for Energy Drain during the active Chain Stratagem window through {FormatCsvTime(energyDrainWindowEndSec)}";
    }

    private static string? GetTimedUsageLockoutReason(
        string specName,
        IReadOnlyDictionary<string, double> timedLockoutEndByAbility,
        string abilityName,
        double timeSec)
    {
        if (!TryGetTimedUsageLockoutRule(specName, abilityName, out var sourceAbilityName, out _))
            return null;

        if (!timedLockoutEndByAbility.TryGetValue(abilityName, out var lockoutEndSec))
            return null;

        if (timeSec > lockoutEndSec + AutoCooldownToleranceSec)
            return null;

        return $"blocked by {sourceAbilityName} through {FormatCsvTime(lockoutEndSec)}";
    }

    private static string? GetTimedUsageWindowReason(
        string specName,
        IReadOnlyDictionary<string, double> timedWindowEndByAbility,
        string abilityName,
        double timeSec)
    {
        if (!TryGetTimedUsageWindowRule(specName, abilityName, out var parentAbilityName, out var durationSec))
            return null;

        if (!timedWindowEndByAbility.TryGetValue(abilityName, out var windowEndSec))
            return $"requires {parentAbilityName} window (within {durationSec:F0}s after use), but no active window exists";

        if (timeSec <= windowEndSec + AutoCooldownToleranceSec)
            return null;

        return $"requires {parentAbilityName} window through {FormatCsvTime(windowEndSec)}, but it expired before {FormatCsvTime(timeSec)}";
    }

    private static void ApplyTimedUsageWindowState(
        string specName,
        IDictionary<string, double> timedWindowEndByAbility,
        string abilityName,
        double timeSec)
    {
        if (string.Equals(specName, "Scholar", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(abilityName, "Chain Stratagem", StringComparison.OrdinalIgnoreCase))
        {
            timedWindowEndByAbility["Energy Drain"] = timeSec + 20.0;
        }
    }

    private static void ApplyTimedUsageLockoutState(
        string specName,
        IDictionary<string, double> timedLockoutEndByAbility,
        string abilityName,
        double timeSec)
    {
        if (!string.Equals(specName, "Scholar", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(abilityName, "Dissipation", StringComparison.OrdinalIgnoreCase))
            return;

        var lockoutEndSec = timeSec + 30.0;
        foreach (var lockedAbilityName in new[] { "Fey Blessing", "Whispering Dawn", "Fey Illumination", "Summon Seraph", "Seraphism" })
            timedLockoutEndByAbility[lockedAbilityName] = lockoutEndSec;
    }

    private void AdvanceAutoTimelineState(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        List<TimelineEntry> ogcdEntries,
        double targetTimeSec,
        AutoTimelineDebugRecorder? debug = null)
    {
        while (state.NextOgcdIndex < ogcdEntries.Count &&
               ogcdEntries[state.NextOgcdIndex].TimeOffsetSec <= targetTimeSec + 0.001)
        {
            var entry = ogcdEntries[state.NextOgcdIndex++];
            var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
            var scheduledTimeSec = GetEarliestAutoScheduleTime(
                state,
                entry,
                info,
                grantedRules,
                entry.TimeOffsetSec);
            if (scheduledTimeSec > targetTimeSec - AutoCastLockToleranceSec)
            {
                debug?.Add($"  oGCD deferred | {entry.AbilityName} | requested {FormatTime(entry.TimeOffsetSec)} | earliest {FormatTime(scheduledTimeSec)}");
                continue;
            }

            AdvancePassiveResources(state, gaugeRules, scheduledTimeSec);
            var rejectionReason = GetAutoEntryRejectionReason(state, entry, info, gaugeRules, grantedRules, scheduledTimeSec);
            if (rejectionReason != null)
            {
                debug?.Add($"  oGCD dropped | {entry.AbilityName} @ {FormatTime(scheduledTimeSec)} | {rejectionReason}");
                continue;
            }

            var score = ScoreAutoEntryCandidate(state, entry, info, gaugeRules, scheduledTimeSec);
            var scheduledEntry = CreateScheduledEntry(entry, scheduledTimeSec);
            ApplyAutoEntry(state, scheduledEntry, info, gaugeRules, grantedRules, scheduledTimeSec, isGcd: false);
            state.SelectedEntries.Add(scheduledEntry);
            state.Score += score;
            debug?.Add($"  oGCD kept | {scheduledEntry.AbilityName} @ {FormatTime(scheduledEntry.TimeOffsetSec)} | score {score:F1}");
        }

        AdvancePassiveResources(state, gaugeRules, targetTimeSec);
    }

    private void DrainRemainingAutoOgcds(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        List<TimelineEntry> ogcdEntries,
        AutoTimelineDebugRecorder? debug = null)
    {
        while (state.NextOgcdIndex < ogcdEntries.Count)
        {
            var entry = ogcdEntries[state.NextOgcdIndex++];
            var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
            var scheduledTimeSec = GetEarliestAutoScheduleTime(
                state,
                entry,
                info,
                grantedRules,
                entry.TimeOffsetSec);
            AdvancePassiveResources(state, gaugeRules, scheduledTimeSec);
            var rejectionReason = GetAutoEntryRejectionReason(state, entry, info, gaugeRules, grantedRules, scheduledTimeSec);
            if (rejectionReason != null)
            {
                debug?.Add($"  oGCD tail-drop | {entry.AbilityName} @ {FormatTime(scheduledTimeSec)} | {rejectionReason}");
                continue;
            }

            var score = ScoreAutoEntryCandidate(state, entry, info, gaugeRules, scheduledTimeSec);
            var scheduledEntry = CreateScheduledEntry(entry, scheduledTimeSec);
            ApplyAutoEntry(state, scheduledEntry, info, gaugeRules, grantedRules, scheduledTimeSec, isGcd: false);
            state.SelectedEntries.Add(scheduledEntry);
            state.Score += score;
            debug?.Add($"  oGCD tail-keep | {scheduledEntry.AbilityName} @ {FormatTime(scheduledEntry.TimeOffsetSec)} | score {score:F1}");
        }
    }

    private void AdvancePassiveResources(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        double targetTimeSec)
    {
        if (targetTimeSec <= state.LastTimeSec)
        {
            state.LastTimeSec = Math.Max(state.LastTimeSec, targetTimeSec);
            return;
        }

        var elapsed = targetTimeSec - state.LastTimeSec;
        if (state.LastGcdId != 0 &&
            targetTimeSec - state.LastComboTimeSec >= ComboResetGapSec)
        {
            state.LastGcdId = 0;
            state.LastComboAbilityName = null;
        }

        if (gaugeRules != null)
        {
            foreach (var resource in gaugeRules.Resources)
            {
                if (resource.PassiveGenerationIntervalSec <= 0)
                    continue;

                var progress = state.PassiveGaugeProgress.GetValueOrDefault(resource.Name) + elapsed;
                var ticks = (int)(progress / resource.PassiveGenerationIntervalSec);
                if (ticks > 0)
                {
                    state.GaugeState[resource.Name] = Math.Clamp(
                        state.GaugeState.GetValueOrDefault(resource.Name) + ticks,
                        0,
                        resource.MaxValue);
                    progress -= ticks * resource.PassiveGenerationIntervalSec;
                }

                state.PassiveGaugeProgress[resource.Name] = progress;
            }
        }

        state.LastTimeSec = targetTimeSec;
    }

    private static bool ShouldTrackPersonalCooldown(Data.RecastDatabase.RecastInfo? info)
        => info != null &&
           !info.IsGcdAction &&
           (info.RecastSec >= 5.0 || info.MaxCharges > 1);

    private static void RefreshCooldownQueue(List<double> queue, double nowSec, double toleranceSec)
        => queue.RemoveAll(t => t <= nowSec + toleranceSec);

    private double GetCooldownReadyTime(
        AutoTimelineState state,
        Data.RecastDatabase.RecastInfo? info,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName,
        double requestedTimeSec,
        double toleranceSec)
    {
        if (!ShouldTrackPersonalCooldown(info) ||
            ShouldBypassCooldown(state.GrantedState, grantedRules, abilityName))
            return requestedTimeSec;

        var cooldownKey = GetCooldownBucketKey(info, (int)info!.AbilityId, info.Name);
        if (!state.CooldownQueues.TryGetValue(cooldownKey, out var queue))
        {
            queue = [];
            state.CooldownQueues[cooldownKey] = queue;
        }

        RefreshCooldownQueue(queue, requestedTimeSec, toleranceSec);
        return queue.Count < info.MaxCharges
            ? requestedTimeSec
            : Math.Max(requestedTimeSec, queue.Min());
    }

    private bool HasPersonalCooldownReady(
        AutoTimelineState state,
        Data.RecastDatabase.RecastInfo? info,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName,
        double nowSec,
        double toleranceSec,
        out double earlyBySec)
    {
        earlyBySec = 0.0;
        if (!ShouldTrackPersonalCooldown(info) ||
            ShouldBypassCooldown(state.GrantedState, grantedRules, abilityName))
            return true;

        var cooldownKey = GetCooldownBucketKey(info, (int)info!.AbilityId, info.Name);
        if (!state.CooldownQueues.TryGetValue(cooldownKey, out var queue))
        {
            queue = [];
            state.CooldownQueues[cooldownKey] = queue;
        }

        RefreshCooldownQueue(queue, nowSec, toleranceSec);
        if (queue.Count < info.MaxCharges)
            return true;

        var nextReady = queue.Min();
        earlyBySec = Math.Max(0.0, nextReady - nowSec);
        return false;
    }

    private void RegisterCooldownUse(
        AutoTimelineState state,
        Data.RecastDatabase.RecastInfo? info,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName,
        double nowSec)
    {
        if (!ShouldTrackPersonalCooldown(info) ||
            ShouldBypassCooldown(state.GrantedState, grantedRules, abilityName))
            return;

        var cooldownKey = GetCooldownBucketKey(info, (int)info!.AbilityId, info.Name);
        if (!state.CooldownQueues.TryGetValue(cooldownKey, out var queue))
        {
            queue = [];
            state.CooldownQueues[cooldownKey] = queue;
        }

        RefreshCooldownQueue(queue, nowSec, AutoCooldownToleranceSec);
        queue.Add(nowSec + info.RecastSec);
        queue.Sort();
    }

    private int GetActionStatePenalty(
        AutoTimelineState state,
        Data.RecastDatabase.RecastInfo? info,
        string abilityName,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules)
    {
        var penalty = 0;
        var actionRule = plugin.ActionStateDatabase.Lookup((int)(info?.AbilityId ?? 0), abilityName);
        if (actionRule == null)
            return 0;

        foreach (var effect in actionRule.Effects)
        {
            if (effect.MinRequired > 0)
            {
                if (ShouldIgnoreActionStateRequirement(actionRule, effect, abilityName, gaugeRules, grantedRules))
                    continue;

                if (!ShouldEnforceActionStateRequirement(effect.StateName, state.AvailableAbilityNames, gaugeRules, grantedRules))
                    continue;

                var have = GetEffectiveStateValue(effect.StateName, state.ActionState, state.GaugeState, state.GrantedState);
                if (have < effect.MinRequired)
                    penalty += 320 + ((effect.MinRequired - have) * 140);
            }
        }

        return penalty;
    }

    private static void ApplyActionStateEffects(
        Dictionary<string, int> actionState,
        ActionStateDatabase.ActionStateRule actionRule)
    {
        foreach (var effect in actionRule.Effects)
        {
            if (effect.Delta == 0)
                continue;

            actionState[effect.StateName] = Math.Clamp(
                actionState.GetValueOrDefault(effect.StateName) + effect.Delta,
                0,
                effect.MaxValue);
        }
    }

    private static int GetEffectiveStateValue(
        string stateName,
        params IReadOnlyDictionary<string, int>[] stateSets)
    {
        var best = 0;
        foreach (var equivalentStateName in EnumerateEquivalentStateNames(stateName))
        {
            foreach (var stateSet in stateSets)
                best = Math.Max(best, stateSet.GetValueOrDefault(equivalentStateName));
        }

        return best;
    }

    private int GetGaugePenalty(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        var penalty = 0;
        var ignoreRefreshMaximum = ShouldKeepAutoGaugeGeneratorOnCooldown(abilityName, gaugeRules);
        if (gaugeRules != null && gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
        {
            var bypassGaugeSpendChecks = HasRepeatableGrantedActionCharge(state.GrantedState, grantedRules, abilityName);
            foreach (var effect in effects)
            {
                if (bypassGaugeSpendChecks && (effect.MinRequired > 0 || effect.Delta < 0))
                    continue;

                if (effect.MinRequired > 0)
                {
                    var have = state.GaugeState.GetValueOrDefault(effect.GaugeName);
                    if (have < effect.MinRequired)
                        penalty += 180 + (effect.MinRequired - have) * 50;
                }

                if (!ignoreRefreshMaximum && effect.MaxAllowedBeforeUse < int.MaxValue)
                {
                    var have = state.GaugeState.GetValueOrDefault(effect.GaugeName);
                    if (have > effect.MaxAllowedBeforeUse)
                        penalty += 420 + ((have - effect.MaxAllowedBeforeUse) * 220);
                }

                if (effect.Delta > 0)
                {
                    var resource = gaugeRules.Resources.FirstOrDefault(r =>
                        string.Equals(r.Name, effect.GaugeName, StringComparison.OrdinalIgnoreCase));
                    if (resource?.AvoidOvercap == true)
                    {
                        var have = state.GaugeState.GetValueOrDefault(effect.GaugeName);
                        var overflow = Math.Max(0, (have + effect.Delta) - resource.MaxValue);
                        if (overflow > 0)
                            penalty += 300 + overflow * 200;
                    }
                }
            }
        }

        var repeatRule = FindRepeatableGrantedActionRule(grantedRules, abilityName);
        if (repeatRule != null && UsesRepeatableGrantedActionCharge(state.GrantedState, repeatRule, abilityName))
        {
            var have = state.GrantedState.GetValueOrDefault(repeatRule.ResourceName);
            if (have < repeatRule.ConsumeCount)
                penalty += 220 + ((repeatRule.ConsumeCount - have) * 80);
        }

        return penalty;
    }

    private int GetGaugeInsufficiencyPenalty(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        var penalty = 0;
        var ignoreRefreshMaximum = ShouldKeepAutoGaugeGeneratorOnCooldown(abilityName, gaugeRules);
        if (gaugeRules != null && gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
        {
            var bypassGaugeSpendChecks = HasRepeatableGrantedActionCharge(state.GrantedState, grantedRules, abilityName);
            foreach (var effect in effects)
            {
                if (bypassGaugeSpendChecks && (effect.MinRequired > 0 || effect.Delta < 0))
                    continue;

                var have = state.GaugeState.GetValueOrDefault(effect.GaugeName);
                if (effect.MinRequired > 0 && have < effect.MinRequired)
                    penalty += 180 + (effect.MinRequired - have) * 50;

                if (!ignoreRefreshMaximum &&
                    effect.MaxAllowedBeforeUse < int.MaxValue &&
                    have > effect.MaxAllowedBeforeUse)
                    penalty += 420 + ((have - effect.MaxAllowedBeforeUse) * 220);
            }
        }

        var repeatRule = FindRepeatableGrantedActionRule(grantedRules, abilityName);
        if (repeatRule != null && UsesRepeatableGrantedActionCharge(state.GrantedState, repeatRule, abilityName))
        {
            var have = state.GrantedState.GetValueOrDefault(repeatRule.ResourceName);
            if (have < repeatRule.ConsumeCount)
                penalty += 220 + ((repeatRule.ConsumeCount - have) * 80);
        }

        return penalty;
    }

    private string? GetNumericGaugeInsufficiencyReason(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        string abilityName)
    {
        if (gaugeRules == null || !gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
            return null;

        foreach (var effect in effects)
        {
            var have = state.GaugeState.GetValueOrDefault(effect.GaugeName);
            if (effect.MinRequired > 0 && have < effect.MinRequired)
                return $"requires {effect.GaugeName} >= {effect.MinRequired}, but only {have} is available";
        }

        return null;
    }

    private static bool ShouldKeepAutoGaugeGeneratorOnCooldown(
        string abilityName,
        GaugeSimulator.JobGaugeRules? gaugeRules)
    {
        if (gaugeRules == null || !gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
            return false;

        var hasTrueGaugeGain = false;
        foreach (var effect in effects)
        {
            if (!GaugeSimulator.IsTrueGaugeResource(effect.GaugeName))
                continue;

            if (effect.MaxAllowedBeforeUse < int.MaxValue)
                return false;

            if (effect.Delta < 0 || effect.MinRequired > 0)
                return false;

            if (effect.Delta > 0 || effect.SetValue is int)
                hasTrueGaugeGain = true;
        }

        return hasTrueGaugeGain;
    }

    private static bool IsAutoTimelineTrueGaugeAbility(
        string abilityName,
        GaugeSimulator.JobGaugeRules? gaugeRules)
    {
        if (gaugeRules == null || !gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
            return false;

        return effects.Any(effect => GaugeSimulator.IsTrueGaugeResource(effect.GaugeName));
    }

    private static bool IsAutoTimelineTrueGaugeSpender(
        string abilityName,
        GaugeSimulator.JobGaugeRules? gaugeRules)
    {
        if (gaugeRules == null || !gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
            return false;

        return effects.Any(effect =>
            GaugeSimulator.IsTrueGaugeResource(effect.GaugeName) &&
            (effect.MinRequired > 0 || effect.Delta < 0));
    }

    private static bool CanBorrowAutoOpenerPrepullGauge(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        string abilityName)
    {
        if (gaugeRules == null || !gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
            return false;

        var canBorrowForShortage = false;
        foreach (var effect in effects)
        {
            var have = state.GaugeState.GetValueOrDefault(effect.GaugeName);
            if (effect.MaxAllowedBeforeUse < int.MaxValue && have > effect.MaxAllowedBeforeUse)
                return false;

            if (effect.MinRequired > 0 && have < effect.MinRequired)
                canBorrowForShortage = true;
        }

        return canBorrowForShortage;
    }

    private static void ApplyGaugeEffectToState(
        IDictionary<string, int> gaugeState,
        GaugeSimulator.JobGaugeRules gaugeRules,
        GaugeSimulator.GaugeEffect effect,
        bool allowPrepullSeed = false)
    {
        var resource = gaugeRules.Resources.FirstOrDefault(r =>
            string.Equals(r.Name, effect.GaugeName, StringComparison.OrdinalIgnoreCase));

        if (allowPrepullSeed && effect.MinRequired > 0)
        {
            var preSeedValue = Math.Clamp(
                effect.MinRequired,
                0,
                resource?.MaxValue ?? int.MaxValue);
            var currentGaugeValue = gaugeState.TryGetValue(effect.GaugeName, out var existingValue)
                ? existingValue
                : 0;
            if (currentGaugeValue < preSeedValue)
                gaugeState[effect.GaugeName] = preSeedValue;
        }

        if (effect.SetValue is int setValue)
        {
            gaugeState[effect.GaugeName] = Math.Clamp(
                setValue,
                0,
                resource?.MaxValue ?? int.MaxValue);
        }

        if (effect.Delta == 0)
            return;

        gaugeState[effect.GaugeName] = Math.Clamp(
            (gaugeState.TryGetValue(effect.GaugeName, out var currentValue) ? currentValue : 0) + effect.Delta,
            0,
            resource?.MaxValue ?? int.MaxValue);
    }

    private void ApplyNumericGaugeEffects(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        string abilityName,
        bool allowPrepullSeed = false)
    {
        if (gaugeRules == null || !gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
            return;

        foreach (var effect in effects)
            ApplyGaugeEffectToState(state.GaugeState, gaugeRules, effect, allowPrepullSeed);
    }

    private void ApplyGaugeEffects(
        AutoTimelineState state,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        if (gaugeRules != null && gaugeRules.EffectByName.TryGetValue(abilityName, out var effects))
        {
            var bypassGaugeSpendChecks = HasRepeatableGrantedActionCharge(state.GrantedState, grantedRules, abilityName);
            foreach (var effect in effects)
            {
                if (bypassGaugeSpendChecks && effect.Delta < 0)
                    continue;

                ApplyGaugeEffectToState(state.GaugeState, gaugeRules, effect);
            }
        }

        if (grantedRules != null && grantedRules.EffectByName.TryGetValue(abilityName, out var grantedEffects))
        {
            foreach (var effect in grantedEffects)
            {
                if (effect.Delta == 0)
                    continue;

                var resource = grantedRules.Resources.FirstOrDefault(r =>
                    string.Equals(r.Name, effect.ResourceName, StringComparison.OrdinalIgnoreCase));

                state.GrantedState[effect.ResourceName] = Math.Clamp(
                    state.GrantedState.GetValueOrDefault(effect.ResourceName) + effect.Delta,
                    0,
                    resource?.MaxValue ?? int.MaxValue);
            }
        }

        var repeatRule = FindRepeatableGrantedActionRule(grantedRules, abilityName);
        if (repeatRule == null)
            return;

        var resourceDef = grantedRules?.Resources.FirstOrDefault(r =>
            string.Equals(r.Name, repeatRule.ResourceName, StringComparison.OrdinalIgnoreCase));
        var maxValue = resourceDef?.MaxValue ?? int.MaxValue;
        if (UsesRepeatableGrantedActionCharge(state.GrantedState, repeatRule, abilityName))
        {
            state.GrantedState[repeatRule.ResourceName] = Math.Clamp(
                state.GrantedState.GetValueOrDefault(repeatRule.ResourceName) - repeatRule.ConsumeCount,
                0,
                maxValue);
        }
        else if (string.Equals(repeatRule.TriggerName, abilityName, StringComparison.OrdinalIgnoreCase))
        {
            state.GrantedState[repeatRule.ResourceName] = Math.Clamp(
                state.GrantedState.GetValueOrDefault(repeatRule.ResourceName) + repeatRule.GrantCount,
                0,
                maxValue);
        }
    }

    private int GetCardDrawPenalty(
        AutoTimelineState state,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        if (grantedRules?.CardDraw is not { } cardDraw)
            return 0;

        if (cardDraw.AstralCards.Contains(abilityName))
            return string.Equals(state.CardDrawState, "Astral", StringComparison.OrdinalIgnoreCase) ? 0 : 150;

        if (cardDraw.UmbralCards.Contains(abilityName))
            return string.Equals(state.CardDrawState, "Umbral", StringComparison.OrdinalIgnoreCase) ? 0 : 150;

        return 0;
    }

    private void ApplyCardDrawState(
        AutoTimelineState state,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        string abilityName)
    {
        if (grantedRules?.CardDraw is not { } cardDraw)
            return;

        if (string.Equals(abilityName, cardDraw.AstralDrawName, StringComparison.OrdinalIgnoreCase))
            state.CardDrawState = "Astral";
        else if (string.Equals(abilityName, cardDraw.UmbralDrawName, StringComparison.OrdinalIgnoreCase))
            state.CardDrawState = "Umbral";
    }

    private string? GetAutoEntryRejectionReason(
        AutoTimelineState state,
        TimelineEntry entry,
        Data.RecastDatabase.RecastInfo? info,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        double scheduledTimeSec)
    {
        if (!HasPersonalCooldownReady(state, info, grantedRules, entry.AbilityName, scheduledTimeSec, AutoCooldownToleranceSec, out _))
            return "cooldown not ready";

        var gaugePenalty = GetGaugeInsufficiencyPenalty(state, gaugeRules, grantedRules, entry.AbilityName);
        if (gaugePenalty > 0)
            return $"gauge penalty {gaugePenalty}";

        var actionPenalty = GetActionStatePenalty(state, info, entry.AbilityName, gaugeRules, grantedRules);
        if (actionPenalty > 0)
            return $"action-state penalty {actionPenalty}";

        var timedWindowReason = GetTimedUsageWindowReason(
            state.SpecName,
            state.TimedWindowEndByAbility,
            entry.AbilityName,
            scheduledTimeSec);
        if (timedWindowReason != null)
            return timedWindowReason;

        var timedLockoutReason = GetTimedUsageLockoutReason(
            state.SpecName,
            state.TimedLockoutEndByAbility,
            entry.AbilityName,
            scheduledTimeSec);
        if (timedLockoutReason != null)
            return timedLockoutReason;

        var scholarBurstReservationReason = GetScholarBurstAetherflowReservationReason(
            state.SpecName,
            state.TimedWindowEndByAbility,
            state.GaugeState,
            entry.AbilityName,
            scheduledTimeSec);
        if (scholarBurstReservationReason != null)
            return scholarBurstReservationReason;

        var cardPenalty = GetCardDrawPenalty(state, grantedRules, entry.AbilityName);
        return cardPenalty == 0 ? null : $"card-state penalty {cardPenalty}";
    }

    private bool CanAcceptAutoEntry(
        AutoTimelineState state,
        TimelineEntry entry,
        Data.RecastDatabase.RecastInfo? info,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        double scheduledTimeSec)
    {
        return GetAutoEntryRejectionReason(state, entry, info, gaugeRules, grantedRules, scheduledTimeSec) == null;
    }

    private void ApplyAutoEntry(
        AutoTimelineState state,
        TimelineEntry entry,
        Data.RecastDatabase.RecastInfo? info,
        GaugeSimulator.JobGaugeRules? gaugeRules,
        GrantedActionDatabase.JobGrantedActionRules? grantedRules,
        double timeSec,
        bool isGcd)
    {
        var effectiveCastTimeSec = GetEffectiveCastTimeSec(
            state.GrantedState, grantedRules, info, entry.AbilityName, out var instantCastRule);

        RegisterCooldownUse(state, info, grantedRules, entry.AbilityName, timeSec);
        ApplyCastStateTransitions(
            state.GrantedState, grantedRules, info, entry.AbilityName, effectiveCastTimeSec, instantCastRule);
        ApplyGaugeEffects(state, gaugeRules, grantedRules, entry.AbilityName);
        var actionRule = plugin.ActionStateDatabase.Lookup((int)(info?.AbilityId ?? 0), entry.AbilityName);
        if (actionRule != null)
            ApplyActionStateEffects(state.ActionState, actionRule);
        ApplyCardDrawState(state, grantedRules, entry.AbilityName);
        ApplyTimedUsageWindowState(state.SpecName, state.TimedWindowEndByAbility, entry.AbilityName, timeSec);
        ApplyTimedUsageLockoutState(state.SpecName, state.TimedLockoutEndByAbility, entry.AbilityName, timeSec);

        if (isGcd && info != null)
        {
            state.LastSelectedGcdAbilityName = entry.AbilityName;
            if (IsComboTrackedAction(info, entry.AbilityName))
            {
                state.LastGcdId = info.AbilityId;
                state.LastComboAbilityName = entry.AbilityName;
                state.LastComboTimeSec = timeSec;
            }
            state.CastLockUntilSec = effectiveCastTimeSec > AutoCastLockToleranceSec
                ? timeSec + effectiveCastTimeSec
                : timeSec;
        }
        else if (!isGcd)
        {
            state.LastOgcdTimeSec = timeSec;
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

    private void DrawAutoTimelineFromFetchedLogsSection()
    {
        if (!ImGui.CollapsingHeader("Auto Timeline from Fetched FFLogs##riAutoHeader"))
            return;

        ImGui.TextDisabled("Create a custom Auto Timeline directly from cached raw FFLogs parse data.");

        if (zones.Count == 0 || allSpecNames.Count == 0)
        {
            ImGui.TextDisabled("Load fight and job lists first, then fetch logs for the encounter you want.");
            return;
        }

        if (riAutoSelectedZone < 0 || riAutoSelectedZone >= zones.Count)
            riAutoSelectedZone = Math.Clamp(selectedZone, 0, Math.Max(0, zones.Count - 1));
        if (riAutoSelectedSpec < 0 || riAutoSelectedSpec >= allSpecNames.Count)
            riAutoSelectedSpec = Math.Clamp(selectedSpec, 0, Math.Max(0, allSpecNames.Count - 1));

        var autoZones = zones.Select(zone => zone.Name).ToList();
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Zone##riAutoZone", ref riAutoSelectedZone, autoZones, -1))
            riAutoSelectedEncounter = 0;

        var autoEncounters = riAutoSelectedZone >= 0 && riAutoSelectedZone < zones.Count
            ? zones[riAutoSelectedZone].Encounters
            : [];
        var autoEncounterNames = autoEncounters.Select(encounter => encounter.Name).ToList();
        if (riAutoSelectedEncounter < 0 || riAutoSelectedEncounter >= autoEncounterNames.Count)
            riAutoSelectedEncounter = autoEncounterNames.Count > 0 ? 0 : -1;

        ImGui.SetNextItemWidth(220);
        if (autoEncounterNames.Count > 0)
        {
            ImGui.Combo("Fight##riAutoFight", ref riAutoSelectedEncounter, autoEncounterNames, -1);
        }
        else
        {
            var noEncounter = 0;
            ImGui.Combo("Fight##riAutoFight", ref noEncounter, new List<string> { "(none)" }, -1);
        }

        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Job##riAutoJob", ref riAutoSelectedSpec, allSpecNames, -1);

        var hasAutoSelection =
            riAutoSelectedZone >= 0 && riAutoSelectedZone < zones.Count &&
            riAutoSelectedEncounter >= 0 && riAutoSelectedEncounter < autoEncounters.Count &&
            riAutoSelectedSpec >= 0 && riAutoSelectedSpec < allSpecNames.Count;
        var autoEncounterId = hasAutoSelection ? autoEncounters[riAutoSelectedEncounter].Id : 0;
        var autoSpecName = hasAutoSelection ? allSpecNames[riAutoSelectedSpec] : string.Empty;
        var canCreateAutoTimeline = hasAutoSelection &&
                                    !riFetching &&
                                    !riImporting &&
                                    plugin.TimelineStore.GetTimeline(autoEncounterId, autoSpecName) != null;

        if (!canCreateAutoTimeline)
            ImGui.BeginDisabled();
        if (ImGui.Button("Create Auto Timeline##riAutoCreate"))
            CreateAutoTimelineFromFetchedLogs(autoEncounters[riAutoSelectedEncounter], autoSpecName);
        if (!canCreateAutoTimeline)
            ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            if (!hasAutoSelection)
            {
                ImGui.TextDisabled("Choose a valid fight and job first.");
            }
            else if (plugin.TimelineStore.GetTimeline(autoEncounterId, autoSpecName) == null)
            {
                ImGui.TextDisabled("No fetched logs are cached for this fight/job yet.");
                ImGui.TextDisabled("Use \"Update from FFLogs\" on the Encounter Timeline tab first.");
            }
            else
            {
                ImGui.Text("Build a custom Auto Timeline straight from cached raw FFLogs parse data.");
            }
            ImGui.EndTooltip();
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
                var entries = BuildExactTimelineEntries(events);

                var timeline = new AggregatedTimeline
                {
                    EncounterId       = 0,
                    EncounterName     = fight.Name,
                    SpecName          = jobDisplay,
                    AverageDurationMs = fight.DurationMs,
                    ParseCount        = 1,
                    Entries           = entries,
                    AutoTimelineSourceEntries = entries
                        .Select(CloneTimelineEntry)
                        .ToList(),
                    CachedFflogsParses =
                    [
                        new CachedFflogsParseTimeline
                        {
                            ParseIndex = 1,
                            ReportCode = code,
                            FightId = fight.Id,
                            RankingAmount = 0.0,
                            DurationSec = fight.DurationMs / 1000.0,
                            Entries = entries
                                .Select(CloneTimelineEntry)
                                .ToList(),
                        }
                    ],
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

    private void CreateAutoTimelineFromFetchedLogs(Encounter encounter, string specName)
    {
        var sourceTimeline = plugin.TimelineStore.GetTimeline(encounter.Id, specName);
        RefreshTimelineRuntimeMetadata(sourceTimeline);
        RefreshEncounterTimelineAggregationFromCachedParses(sourceTimeline);
        if (sourceTimeline == null)
        {
            riStatus = $"No fetched logs are cached for {encounter.Name} / {specName}.";
            riStatusIsError = true;
            return;
        }

        var key = TimelineDatabase.MakeKey(encounter.Id, specName);
        var rawSourceEntries = BuildAutoTimelineSourceFromCachedParses(sourceTimeline);
        if (rawSourceEntries.Count == 0)
        {
            riStatus = $"No raw cached FFLogs parse data is available for {encounter.Name} / {specName}. Fetch it again from FFLogs first.";
            riStatusIsError = true;
            return;
        }

        var customTimeline = CloneTimeline(sourceTimeline);
        customTimeline.Entries = [];
        customTimeline.AutoTimelineSourceEntries = rawSourceEntries;
        ApplyAutoTimeline(customTimeline);

        plugin.Configuration.CustomTimelines[key] = customTimeline;
        plugin.SaveConfig();

        SelectCustomTimeline(key, customTimeline);
        customEditorDirty = false;
        riStatus = $"Created Auto Timeline for {encounter.Name} / {specName} from cached raw FFLogs data.";
        riStatusIsError = false;
    }

    private List<TimelineEntry> BuildExactTimelineEntries(IEnumerable<CastEvent> events)
    {
        return events
            .OrderBy(e => e.Timestamp)
            .Select(e =>
            {
                var info = plugin.RecastDatabase.Lookup(e.AbilityGameID, e.AbilityName);
                return new TimelineEntry
                {
                    TimeOffsetSec = e.Timestamp / 1000.0,
                    AbilityId = info != null ? (int)info.AbilityId : e.AbilityGameID,
                    AbilityName = !string.IsNullOrWhiteSpace(info?.Name) ? info.Name : e.AbilityName,
                    AbilityIcon = e.AbilityIcon,
                    Frequency = 1.0,
                    AverageUses = 1.0,
                    IsGcd = info?.IsGcdAction ?? false,
                };
            })
            .ToList();
    }

    private List<TimelineEntry> BuildAutoTimelineSourceFromCachedParses(AggregatedTimeline tl)
    {
        if (tl.CachedFflogsParses.Count == 0)
            return [];
        var exactParses = new List<IReadOnlyList<TimelineEntry>>();

        foreach (var parse in tl.CachedFflogsParses
                     .Where(parse => parse.Entries.Count > 0)
                     .OrderBy(parse => parse.ParseIndex))
        {
            var orderedEntries = parse.Entries
                .OrderBy(entry => entry.TimeOffsetSec)
                .ThenByDescending(entry => entry.Frequency)
                .ToList();
            if (orderedEntries.Count == 0)
                continue;

            var parseEntries = new List<TimelineEntry>(orderedEntries.Count);

            foreach (var entry in orderedEntries)
            {
                if (entry.AbilityId <= 7)
                    continue;

                var (abilityId, abilityName, abilityIcon, isGcdAction) = NormalizeAutoTimelineSourceEntry(entry);
                parseEntries.Add(new TimelineEntry
                {
                    TimeOffsetSec = entry.TimeOffsetSec,
                    AbilityId = abilityId,
                    AbilityName = abilityName,
                    AbilityIcon = abilityIcon,
                    Frequency = 1.0,
                    AverageUses = 1.0,
                    IsGcd = isGcdAction,
                });
            }

            exactParses.Add(parseEntries);
        }

        return AutoTimelineSourceBuilder.BuildFromExactParses(exactParses, tl.CachedFflogsParses.Count);
    }

    private (int abilityId, string abilityName, string abilityIcon, bool isGcdAction)
        NormalizeAutoTimelineSourceEntry(TimelineEntry entry)
    {
        var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        return (
            info != null ? (int)info.AbilityId : entry.AbilityId,
            !string.IsNullOrWhiteSpace(info?.Name) ? info.Name : entry.AbilityName,
            !string.IsNullOrWhiteSpace(entry.AbilityIcon) ? entry.AbilityIcon : string.Empty,
            info?.IsGcdAction ?? entry.IsGcd);
    }

    private void RefreshEncounterTimelineAggregationFromCachedParses(AggregatedTimeline? tl)
    {
        if (tl == null || !NeedsEncounterTimelineAggregationRefresh(tl))
            return;

        var rebuiltSourceEntries = BuildAutoTimelineSourceFromCachedParses(tl);
        if (rebuiltSourceEntries.Count == 0)
            return;

        tl.AutoTimelineSourceEntries = rebuiltSourceEntries
            .Select(CloneTimelineEntry)
            .ToList();
        tl.Entries = TimelineEntryCondenser.Condense(
            rebuiltSourceEntries,
            Math.Max(tl.ParseCount, tl.CachedFflogsParses.Count),
            plugin.RecastDatabase);
        RefreshTimelineRuntimeMetadata(tl);
        plugin.TimelineStore.SaveTimeline(tl);
    }

    private static bool NeedsEncounterTimelineAggregationRefresh(AggregatedTimeline tl)
    {
        if (tl.CachedFflogsParses.Count == 0)
            return false;

        if (tl.AutoTimelineSourceEntries.Count == 0)
            return true;

        if (tl.AutoTimelineSourceEntries.Any(entry => entry.AverageUses > 1.001))
            return true;

        return !AutoTimelineSourceBuilder.UsesFixedSlotAggregation(tl.AutoTimelineSourceEntries);
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
            currentTimeline = null;
            skillVisibility.Clear();
            HideEmbeddedTimelinePreview();
        }

        ImGui.SameLine();

        // Encounter (Fight) selector
        var encounters = selectedZone < zones.Count ? zones[selectedZone].Encounters : [];
        var encounterNames = encounters.Select(e => e.Name).ToList();
        ImGui.SetNextItemWidth(200);
        if (encounterNames.Count > 0)
        {
            if (ImGui.Combo("Fight", ref selectedEncounter, encounterNames, -1))
            {
                currentTimeline = null;
                skillVisibility.Clear();
                HideEmbeddedTimelinePreview();
            }
        }
        else
        {
            var noFights = 0;
            ImGui.Combo("Fight", ref noFights, new List<string> { "(none)" }, -1);
        }

        ImGui.SameLine();

        // Job selector
        ImGui.SetNextItemWidth(200);
        if (ImGui.Combo("Job", ref selectedSpec, allSpecNames, -1))
        {
            currentTimeline = null;
            skillVisibility.Clear();
            HideEmbeddedTimelinePreview();
        }

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
            canLoad = plugin.TimelineStore.GetTimeline(chkEncId, chkSpec) != null;
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
            showEmbeddedTimelinePreview = !showEmbeddedTimelinePreview;
            if (!showEmbeddedTimelinePreview)
                plugin.OverlayWindow.ResetEmbeddedPreview();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            if (hasTimeline)
                ImGui.Text("Preview the timeline in-tab (starts paused).");
            else
                ImGui.Text("Load a timeline first to preview the overlay.");
            ImGui.EndTooltip();
        }

        if (showEmbeddedTimelinePreview && hasTimeline)
        {
            var previewHeight = MathF.Max(164f, plugin.Configuration.OverlayIconSize * 2.6f + 42f);
            if (ImGui.GetContentRegionAvail().X >= 320f)
                ImGui.SameLine();

            showEmbeddedTimelinePreview = plugin.OverlayWindow.DrawEmbeddedPreview(
                currentTimeline!,
                new Vector2(0f, previewHeight),
                "##EncounterTimelineEmbeddedPreview");
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
        HideEmbeddedTimelinePreview();
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

        HideEmbeddedTimelinePreview();
        currentTimeline = plugin.TimelineStore.GetTimeline(encounterId, specName);
        RefreshTimelineRuntimeMetadata(currentTimeline);
        RefreshEncounterTimelineAggregationFromCachedParses(currentTimeline);

        // Build skill visibility map — includes all abilities; threshold filtering is handled at draw time
        skillVisibility.Clear();
        if (currentTimeline != null)
        {
            var key = TimelineDatabase.MakeKey(currentTimeline.EncounterId, currentTimeline.SpecName);
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
        bool IsEntryGcd(TimelineEntry e)
        {
            var info = plugin.RecastDatabase.Lookup(e.AbilityId, e.AbilityName);
            return info?.IsGcdAction == true;
        }
        var gcdIds  = currentTimeline.Entries.Where(IsEntryGcd).Select(e => e.AbilityId).ToHashSet();
        var ogcdIds = currentTimeline.Entries.Where(e => !IsEntryGcd(e)).Select(e => e.AbilityId).ToHashSet();

        if (ImGui.Button("Show All GCDs"))
        {
            foreach (var id in gcdIds) skillVisibility[id] = true;
            SaveSkillFilters();
        }
        ImGui.SameLine();
        if (ImGui.Button("Hide All GCDs"))
        {
            foreach (var id in gcdIds) skillVisibility[id] = false;
            SaveSkillFilters();
        }
        ImGui.SameLine();
        if (ImGui.Button("Show All oGCDs"))
        {
            foreach (var id in ogcdIds) skillVisibility[id] = true;
            SaveSkillFilters();
        }
        ImGui.SameLine();
        if (ImGui.Button("Hide All oGCDs"))
        {
            foreach (var id in ogcdIds) skillVisibility[id] = false;
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

    private (int AbilityId, string AbilityName) GetTimelineDisplayAbilityIdentity(TimelineEntry entry)
    {
        var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
        var abilityId = info != null ? (int)info.AbilityId : entry.AbilityId;
        var abilityName = !string.IsNullOrWhiteSpace(info?.Name)
            ? info.Name
            : entry.AbilityName;

        if (string.IsNullOrWhiteSpace(abilityName))
            abilityName = $"#{abilityId}";

        return (abilityId, abilityName);
    }

    private void DrawTimeline()
    {
        if (currentTimeline == null)
            return;

        var cfg      = plugin.Configuration;
        var iconSize = cfg.MainIconSize * cfg.MainIconScale;
        var rowHeight = iconSize + 4f;

        ImGui.Text($"{currentTimeline.EncounterName} - {currentTimeline.SpecName}");
        ImGui.SameLine();
        ImGui.TextDisabled($"({currentTimeline.ParseCount} parses, avg {currentTimeline.AverageDurationMs / 1000.0:F1}s)");

        // Gather visible abilities — per-ability threshold takes priority over global
        var displayEntries = TimelineJobRules.ApplyPostSelectionRules(
            currentTimeline.SpecName,
            currentTimeline.Entries,
            promoteMacrocosmosToVisualGcd: true);
        var visibleEntries = displayEntries
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
            .GroupBy(GetTimelineDisplayAbilityIdentity)
            .Select(g => new
            {
                AbilityId   = g.Key.AbilityId,
                AbilityName = g.Key.AbilityName,
                FirstTime   = g.Min(e => e.TimeOffsetSec),
                Entries     = g.ToList(),
            })
            .OrderBy(a => a.AbilityName)
            .ToList();

        var timelineKey      = TimelineDatabase.MakeKey(currentTimeline.EncounterId, currentTimeline.SpecName);
        var isCustomTimeline = plugin.Configuration.CustomTimelines.ContainsKey(timelineKey);
        TimelineEntry? iconCtxEntry = null;

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
            var labelClipMin = new Vector2(origin.X + 2, rowY);
            var labelClipMax = new Vector2(timeOriginX - 6, rowY + rowHeight);
            drawList.PushClipRect(labelClipMin, labelClipMax, true);
            drawList.AddText(new Vector2(origin.X + 4, rowY + (rowHeight - 13f) * 0.5f), textColor, labelText);
            drawList.PopClipRect();

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

                var hitMin  = iconPos;
                var hitMax  = iconPos + new Vector2(iconSize, iconSize);
                if (ImGui.IsMouseHoveringRect(hitMin, hitMax))
                {
                    ImGui.BeginTooltip();
                    ImGui.Text(entry.AbilityName);
                    ImGui.Separator();
                    ImGui.Text($"Time: {FormatTime(entry.TimeOffsetSec)}");
                    ImGui.Text($"Frequency: {entry.Frequency:P0} of parses");
                    ImGui.Text($"Avg Uses: {entry.AverageUses:F1}x in this window");
                    ImGui.EndTooltip();

                    if (isCustomTimeline && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                        iconCtxEntry = entry;
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

        // Right-click removal for custom timeline icons
        if (iconCtxEntry != null)
        {
            var removedAny = RemoveDisplayedEntryFromTimeline(currentTimeline, iconCtxEntry);
            if (selectedCustomKey != null &&
                plugin.Configuration.CustomTimelines.TryGetValue(selectedCustomKey, out var selectedCustomTimeline) &&
                !ReferenceEquals(selectedCustomTimeline, currentTimeline))
            {
                removedAny |= RemoveDisplayedEntryFromTimeline(selectedCustomTimeline, iconCtxEntry);
            }

            if (removedAny && !currentTimeline.Entries.Any(e => e.AbilityId == iconCtxEntry.AbilityId))
                skillVisibility.Remove(iconCtxEntry.AbilityId);

            if (removedAny)
                RequestDeferredConfigSave();
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

    private static bool RemoveDisplayedEntryFromTimeline(AggregatedTimeline timeline, TimelineEntry displayedEntry)
    {
        var sourceEntry = ResolveDisplayedTimelineEntry(timeline.Entries, displayedEntry);
        if (sourceEntry == null)
            return false;

        return timeline.Entries.Remove(sourceEntry);
    }

    private static TimelineEntry? ResolveDisplayedTimelineEntry(
        IEnumerable<TimelineEntry> sourceEntries,
        TimelineEntry displayedEntry)
    {
        const double exactTimeToleranceSec = 0.01;

        var exactMatch = sourceEntries.FirstOrDefault(sourceEntry =>
            sourceEntry.AbilityId == displayedEntry.AbilityId &&
            string.Equals(sourceEntry.AbilityName, displayedEntry.AbilityName, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(sourceEntry.TimeOffsetSec - displayedEntry.TimeOffsetSec) <= exactTimeToleranceSec &&
            Math.Abs(sourceEntry.Frequency - displayedEntry.Frequency) <= 0.0001 &&
            Math.Abs(sourceEntry.AverageUses - displayedEntry.AverageUses) <= 0.0001);
        if (exactMatch != null)
            return exactMatch;

        return sourceEntries
            .Where(sourceEntry =>
                sourceEntry.AbilityId == displayedEntry.AbilityId &&
                string.Equals(sourceEntry.AbilityName, displayedEntry.AbilityName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(sourceEntry => Math.Abs(sourceEntry.TimeOffsetSec - displayedEntry.TimeOffsetSec))
            .ThenByDescending(sourceEntry => sourceEntry.Frequency)
            .FirstOrDefault();
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
            var cachedParseTimelines = new List<CachedFflogsParseTimeline>();
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
                            cachedParseTimelines.Add(new CachedFflogsParseTimeline
                            {
                                ParseIndex = successfulRankings.Count + 1,
                                ReportCode = ranking.ReportCode,
                                FightId = ranking.FightId,
                                RankingAmount = ranking.Amount,
                                DurationSec = fightEnd / 1000.0,
                                Entries = BuildExactTimelineEntries(events),
                            });
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
                timeline.CachedFflogsParses = cachedParseTimelines
                    .OrderBy(parse => parse.ParseIndex)
                    .ToList();
                RefreshTimelineRuntimeMetadata(timeline);
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
        var isCustomTimeline = plugin.Configuration.CustomTimelines.ContainsKey(key);

        // Deep-copy via JSON, then strip entries that are currently filtered out.
        // Keep the raw Auto Timeline source list separate from the condensed display
        // list, but apply the same active skill filters to both so the copied custom
        // timeline reflects the Encounter Timeline view without losing raw timing data.
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(currentTimeline);
        var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<AggregatedTimeline>(json)!;

        copy.Entries = FilterCopiedTimelineEntries(copy.Entries);
        copy.CachedFflogsParses = isCustomTimeline
            ? currentTimeline.CachedFflogsParses
                .Select(CloneCachedFflogsParseTimeline)
                .ToList()
            : FilterCopiedCachedFflogsParses(currentTimeline.CachedFflogsParses);
        copy.AutoTimelineSourceEntries = BuildFilteredCopiedRawSource(currentTimeline, isCustomTimeline);

        plugin.Configuration.CustomTimelines[key] = copy;
        plugin.SaveConfig();

        // Refresh the editor if this key is already selected so it shows the new copy
        SelectCustomTimeline(key, copy);
    }

    private List<TimelineEntry> FilterCopiedTimelineEntries(IEnumerable<TimelineEntry> entries)
    {
        return entries
            .Where(e => skillVisibility.GetValueOrDefault(e.AbilityId, true))
            .Where(e => e.Frequency >= GetAbilityThreshold(e.AbilityId))
            .Select(CloneTimelineEntry)
            .ToList();
    }

    private List<CachedFflogsParseTimeline> FilterCopiedCachedFflogsParses(IEnumerable<CachedFflogsParseTimeline> parses)
    {
        return parses
            .Select(parse => new CachedFflogsParseTimeline
            {
                ParseIndex = parse.ParseIndex,
                ReportCode = parse.ReportCode,
                FightId = parse.FightId,
                RankingAmount = parse.RankingAmount,
                DurationSec = parse.DurationSec,
                Entries = FilterCopiedTimelineEntries(parse.Entries),
            })
            .Where(parse => parse.Entries.Count > 0)
            .ToList();
    }

    private List<TimelineEntry> BuildFilteredCopiedRawSource(AggregatedTimeline timeline, bool isCustomTimeline)
    {
        var cachedSourceEntries = BuildAutoTimelineSourceFromCachedParses(timeline);
        if (cachedSourceEntries.Count > 0)
            return cachedSourceEntries;

        if (isCustomTimeline)
        {
            if (timeline.AutoTimelineSourceEntries.Count > 0)
                return FilterCopiedTimelineEntries(timeline.AutoTimelineSourceEntries);

            return FilterCopiedTimelineEntries(timeline.Entries);
        }

        if (timeline.AutoTimelineSourceEntries.Count > 0)
            return FilterCopiedTimelineEntries(timeline.AutoTimelineSourceEntries);

        return FilterCopiedTimelineEntries(timeline.Entries);
    }

    private static List<TimelineEntry> FlattenCachedFflogsSourceEntries(IEnumerable<CachedFflogsParseTimeline> parses)
    {
        return parses
            .OrderBy(parse => parse.ParseIndex)
            .SelectMany(parse => parse.Entries)
            .Select(CloneTimelineEntry)
            .ToList();
    }

    private static bool IsSuspiciousFlattenedCustomSource(AggregatedTimeline tl)
    {
        if (tl.AutoTimelineSourceEntries.Count == 0 || tl.Entries.Count == 0)
            return false;

        if (tl.AutoTimelineSourceEntries.Count < Math.Max(500, tl.Entries.Count * 3))
            return false;

        var allFullFrequency = tl.AutoTimelineSourceEntries.All(entry => entry.Frequency >= 0.999);
        if (!allFullFrequency)
            return false;

        var sourceHasAnyGcd = tl.AutoTimelineSourceEntries.Any(entry => entry.IsGcd);
        var entriesHaveAnyGcd = tl.Entries.Any(entry => entry.IsGcd);
        return !sourceHasAnyGcd && entriesHaveAnyGcd;
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
        CfgTooltip("oGCD icon size as a fraction of the GCD icon size.\nSmaller values help dense weave windows stay readable and match the ATR-style playback look.");

        var ogcdOffset = cfg.OGCDVerticalOffset;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("oGCD Vertical Offset", ref ogcdOffset, -1.0f, 1.0f, "%.2f"))
        { cfg.OGCDVerticalOffset = ogcdOffset; changed = true; }
        CfgTooltip("Move oGCDs upward from the main action lane.\n0.1 matches the ATR-style default.\nNegative values move them back down.");

        var ogcdHOffset = cfg.OGCDHorizontalOffset;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("oGCD Horizontal Offset", ref ogcdHOffset, -100f, 100f, "%.0f px"))
        { cfg.OGCDHorizontalOffset = ogcdHOffset; changed = true; }
        CfgTooltip("Nudge oGCD slot placement left (negative) or right (positive) after legal weave slots are chosen.");

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
            cfg.OverlayPixelsPerSec    = 72.0f;
            cfg.OverlayIconSize        = 44.0f;
            cfg.OverlayTimeBehind      = 1.5f;
            cfg.OverlayBgOpacity       = 1.0f;
            cfg.OverlayPastAlpha       = 1.0f;
            cfg.OverlayFreqThreshold   = 0.0f;
            cfg.OverlayShowGrid        = true;
            cfg.OverlayMaxStackedIcons = 3;
            cfg.OGCDSizeRatio          = 0.75f;
            cfg.OGCDVerticalOffset     = 0.1f;
            cfg.OGCDHorizontalOffset   = 0f;
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

        if (ImGui.Button("Clear Cached Logs", default))
        {
            plugin.TimelineStore.ClearAll();
            plugin.EncounterTracker.RebuildZoneMappings();

            currentTimeline = null;
            skillVisibility.Clear();
            expandedSkillNodes.Clear();
            HideEmbeddedTimelinePreview();

            cacheClearNotice = "Cleared cached logs.";
            updateProgress = 0f;
        }

        if (!string.IsNullOrWhiteSpace(cacheClearNotice))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(cacheClearNotice);
        }
    }

    private static string FormatAutoStateSnapshot(AutoTimelineState state)
    {
        var gaugeParts = state.GaugeState
            .Where(kvp => kvp.Value > 0)
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => $"{kvp.Key}={kvp.Value}")
            .ToList();
        var actionParts = state.ActionState
            .Where(kvp => kvp.Value > 0)
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => $"{kvp.Key}={kvp.Value}")
            .ToList();

        var parts = new List<string>
        {
            $"lastGcd={state.LastGcdId}",
            $"lastCombo={state.LastComboAbilityName ?? "-"}",
            $"card={state.CardDrawState ?? "-"}",
        };
        if (gaugeParts.Count > 0)
            parts.Add($"gauge[{string.Join(", ", gaugeParts)}]");
        if (actionParts.Count > 0)
            parts.Add($"action[{string.Join(", ", actionParts)}]");

        return string.Join(" | ", parts);
    }

    private AggregatedTimeline PrepareAutoTimelineSourceClone(AggregatedTimeline tl)
    {
        RefreshTimelineRuntimeMetadata(tl);
        var timelineKey = TimelineDatabase.MakeKey(tl.EncounterId, tl.SpecName);
        var isCustomTimeline = plugin.Configuration.CustomTimelines.ContainsKey(timelineKey);
        var storeTimeline = plugin.TimelineStore.GetTimeline(tl.EncounterId, tl.SpecName);
        RefreshTimelineRuntimeMetadata(storeTimeline);
        var rebuiltCachedSource = BuildAutoTimelineSourceFromCachedParses(tl);
        if (rebuiltCachedSource.Count > 0)
            tl.AutoTimelineSourceEntries = rebuiltCachedSource;
        else if (!isCustomTimeline &&
                 storeTimeline != null &&
                 !ReferenceEquals(storeTimeline, tl))
        {
            var storeCachedSource = BuildAutoTimelineSourceFromCachedParses(storeTimeline);
            if (storeCachedSource.Count > 0)
                tl.AutoTimelineSourceEntries = storeCachedSource;
        }

        if (isCustomTimeline && IsSuspiciousFlattenedCustomSource(tl))
        {
            tl.AutoTimelineSourceEntries = tl.Entries
                .Select(CloneTimelineEntry)
                .ToList();
        }

        if (tl.AutoTimelineSourceEntries.Count == 0)
        {
            if (isCustomTimeline)
            {
                tl.AutoTimelineSourceEntries = tl.Entries
                    .Select(CloneTimelineEntry)
                    .ToList();
            }
            else
            {
                tl.AutoTimelineSourceEntries = [];
            }
        }

        var effectiveSourceEntries = tl.AutoTimelineSourceEntries
            .Select(CloneTimelineEntry)
            .ToList();
        if (ShouldSupplementAutoTimelineGcdShell(effectiveSourceEntries) &&
            storeTimeline != null &&
            !ReferenceEquals(storeTimeline, tl))
        {
            var donorEntries = BuildAutoTimelineSourceFromCachedParses(storeTimeline);
            if (donorEntries.Count > 0)
                effectiveSourceEntries = MergeAutoTimelineSourceWithGcdShell(effectiveSourceEntries, donorEntries);
        }

        var sourceTimeline = CloneTimeline(tl);
        sourceTimeline.Entries = effectiveSourceEntries;
        sourceTimeline.AutoTimelineSourceEntries = effectiveSourceEntries
            .Select(CloneTimelineEntry)
            .ToList();
        RefreshTimelineRuntimeMetadata(sourceTimeline);
        return sourceTimeline;
    }

    private bool ShouldSupplementAutoTimelineGcdShell(IReadOnlyCollection<TimelineEntry> entries)
        => entries.Count > 0 && entries.Count(IsGcdEntry) < 12;

    private List<TimelineEntry> FilterAutoTimelineResultEntries(
        IEnumerable<TimelineEntry> entries,
        ISet<int> allowedAbilityIds)
    {
        if (allowedAbilityIds.Count == 0)
            return entries
                .Select(CloneTimelineEntry)
                .ToList();

        return entries
            .Where(entry => allowedAbilityIds.Contains(entry.AbilityId))
            .Select(CloneTimelineEntry)
            .ToList();
    }

    private List<TimelineEntry> MergeAutoTimelineSourceWithGcdShell(
        IEnumerable<TimelineEntry> primaryEntries,
        IEnumerable<TimelineEntry> donorEntries)
    {
        var merged = primaryEntries
            .Select(CloneTimelineEntry)
            .ToList();
        var seenKeys = merged
            .Select(GetAutoEntryIdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var donorEntry in donorEntries
                     .Where(IsGcdEntry)
                     .OrderBy(entry => entry.TimeOffsetSec)
                     .ThenByDescending(entry => entry.Frequency))
        {
            var key = GetAutoEntryIdentityKey(donorEntry);
            if (!seenKeys.Add(key))
                continue;

            merged.Add(CloneTimelineEntry(donorEntry));
        }

        return merged
            .OrderBy(entry => entry.TimeOffsetSec)
            .ThenByDescending(entry => entry.Frequency)
            .ToList();
    }

    private static AggregatedTimeline CloneTimeline(AggregatedTimeline tl)
    {
        return new AggregatedTimeline
        {
            EncounterId = tl.EncounterId,
            EncounterName = tl.EncounterName,
            SpecName = tl.SpecName,
            AverageDurationMs = tl.AverageDurationMs,
            ParseCount = tl.ParseCount,
            Entries = tl.Entries
                .Select(CloneTimelineEntry)
                .ToList(),
            AutoTimelineSourceEntries = tl.AutoTimelineSourceEntries
                .Select(CloneTimelineEntry)
                .ToList(),
            CachedFflogsParses = tl.CachedFflogsParses
                .Select(CloneCachedFflogsParseTimeline)
                .ToList(),
            BossEntries = tl.BossEntries
                .Select(entry => new BossTimelineEntry
                {
                    AbilityId = entry.AbilityId,
                    AbilityName = entry.AbilityName,
                    CastStartSec = entry.CastStartSec,
                    CastEndSec = entry.CastEndSec,
                })
                .ToList(),
        };
    }

    private static CachedFflogsParseTimeline CloneCachedFflogsParseTimeline(CachedFflogsParseTimeline parse)
    {
        return new CachedFflogsParseTimeline
        {
            ParseIndex = parse.ParseIndex,
            ReportCode = parse.ReportCode,
            FightId = parse.FightId,
            RankingAmount = parse.RankingAmount,
            DurationSec = parse.DurationSec,
            Entries = parse.Entries
                .Select(CloneTimelineEntry)
                .ToList(),
        };
    }

    private static TimelineEntry CloneTimelineEntry(TimelineEntry entry)
    {
        return new TimelineEntry
        {
            TimeOffsetSec = entry.TimeOffsetSec,
            AbilityId = entry.AbilityId,
            AbilityName = entry.AbilityName,
            AbilityIcon = entry.AbilityIcon,
            Frequency = entry.Frequency,
            AverageUses = entry.AverageUses,
            IsGcd = entry.IsGcd,
        };
    }

    private void RefreshTimelineRuntimeMetadata(AggregatedTimeline? tl)
    {
        if (tl == null)
            return;

        RefreshTimelineEntryRuntimeMetadata(tl.Entries);
        RefreshTimelineEntryRuntimeMetadata(tl.AutoTimelineSourceEntries);
        foreach (var parse in tl.CachedFflogsParses)
            RefreshTimelineEntryRuntimeMetadata(parse.Entries);
    }

    private void RefreshTimelineEntryRuntimeMetadata(IEnumerable<TimelineEntry> entries)
    {
        foreach (var entry in entries)
        {
            var info = plugin.RecastDatabase.Lookup(entry.AbilityId, entry.AbilityName);
            if (info != null)
                entry.IsGcd = info.IsGcdAction;
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

