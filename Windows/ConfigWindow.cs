using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ATKTip.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private bool pendingDeferredUiSettingsSave;
    private DateTime pendingDeferredUiSettingsSaveAt = DateTime.MinValue;
    private static readonly TimeSpan DeferredUiSettingsSaveDelay = TimeSpan.FromMilliseconds(350);

    private string clientId = string.Empty;
    private string clientSecret = string.Empty;
    private bool initialized;

    public ConfigWindow(Plugin plugin)
        : base("ATKTip - Configuration##ATKTipConfig")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 500),
            MaximumSize = new Vector2(700, 900),
        };
    }

    public override void OnOpen()
    {
        clientId = plugin.Configuration.FFLogsClientId;
        clientSecret = plugin.Configuration.FFLogsClientSecret;
        initialized = true;
    }

    public override void Draw()
    {
        if (!initialized)
            OnOpen();

        ProcessDeferredUiSettingsSave();

        DrawApiCredentials();
        ImGui.Spacing();
        ImGui.Spacing();
        DrawOverlaySettings();
        ImGui.Spacing();
        ImGui.Spacing();
        DrawOverlayAppearance();
        ImGui.Spacing();
        ImGui.Spacing();
        DrawMainTimelineSettings();
        ImGui.Spacing();
        ImGui.Spacing();
        DrawCustomTimelines();
        ImGui.Spacing();
        ImGui.Spacing();
        DrawAntsSettings();
        ImGui.Spacing();
        ImGui.Spacing();
        DrawResetSection();
        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.TextDisabled("ATKTip v0.1.0");
        ImGui.TextDisabled("Timeline data sourced from FFLogs top parses.");
    }

    private void DrawApiCredentials()
    {
        ImGui.TextUnformatted("FFLogs API Credentials");
        ImGui.Separator();

        // Step-by-step guide
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

        // Client ID field
        ImGui.TextUnformatted("Client ID");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("The Client ID from your FFLogs API client.");
            ImGui.Text("Found at: fflogs.com/api/clients");
            ImGui.EndTooltip();
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##ClientId", ref clientId, 256, ImGuiInputTextFlags.None);

        ImGui.Spacing();

        // Client Secret field
        ImGui.TextUnformatted("Client Secret");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("The Client Secret from your FFLogs API client.");
            ImGui.Text("This is shown once when you create the client.");
            ImGui.Text("If you lost it, delete the old client and create a new one.");
            ImGui.EndTooltip();
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##ClientSecret", ref clientSecret, 256, ImGuiInputTextFlags.Password);

        ImGui.Spacing();

        if (ImGui.Button("Save Credentials", default))
        {
            plugin.Configuration.FFLogsClientId = clientId;
            plugin.Configuration.FFLogsClientSecret = clientSecret;
            plugin.SaveConfig();
        }

        ImGui.SameLine();

        // Status indicator
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

    private void DrawOverlaySettings()
    {
        ImGui.TextUnformatted("Overlay Settings");
        ImGui.Separator();

        var cfg = plugin.Configuration;

        var overlayEnabled = cfg.OverlayEnabled;
        if (ImGui.Checkbox("Enable Overlay", ref overlayEnabled))
        {
            cfg.OverlayEnabled = overlayEnabled;
            RequestDeferredUiSettingsSave();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("Show a compact live timeline during combat.");
            ImGui.Text("Starts automatically when you pull a boss.");
            ImGui.EndTooltip();
        }

        var overlayLocked = cfg.OverlayLocked;
        if (ImGui.Checkbox("Lock Overlay Position", ref overlayLocked))
        {
            cfg.OverlayLocked = overlayLocked;
            RequestDeferredUiSettingsSave();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("When locked, the overlay cannot be moved or resized.");
            ImGui.Text("Uncheck this to reposition the overlay, then re-lock it.");
            ImGui.EndTooltip();
        }

    }

    private void DrawOverlayAppearance()
    {
        ImGui.TextUnformatted("Overlay Appearance");
        ImGui.Separator();

        var cfg = plugin.Configuration;
        var changed = false;

        // ── Timing ──
        ImGui.TextDisabled("Timing");

        var pxPerSec = cfg.OverlayPixelsPerSec;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Scroll Speed (px/sec)", ref pxPerSec, 20.0f, 200.0f, "%.0f"))
        {
            cfg.OverlayPixelsPerSec = pxPerSec;
            changed = true;
        }
        Tooltip("How many pixels represent one second of time.\nHigher = more spread out, lower = more compact.");

        var timeBehind = cfg.OverlayTimeBehind;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Look-Behind (sec)", ref timeBehind, 0.0f, 10.0f, "%.1f"))
        {
            cfg.OverlayTimeBehind = timeBehind;
            changed = true;
        }
        Tooltip("How many seconds of past abilities to keep visible.\nThis controls where the 'now' line sits.");

        ImGui.Spacing();

        // ── Icons ──
        ImGui.TextDisabled("Icons");

        var iconSize = cfg.OverlayIconSize;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Icon Size", ref iconSize, 16.0f, 64.0f, "%.0f"))
        {
            cfg.OverlayIconSize = iconSize;
            changed = true;
        }
        Tooltip("Size of the primary ability icon in pixels.");

        var maxStacked = cfg.OverlayMaxStackedIcons;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Max Stacked Icons", ref maxStacked, 1, 6))
        {
            cfg.OverlayMaxStackedIcons = maxStacked;
            changed = true;
        }
        Tooltip("Maximum number of abilities shown per time bucket.\nThe most frequent ability is shown largest.");

        ImGui.Spacing();

        // ── oGCD ──
        ImGui.TextDisabled("oGCD");

        var ogcdSizeRatio = cfg.OGCDSizeRatio;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("oGCD Size", ref ogcdSizeRatio, 0.25f, 1.0f, "%.2f"))
        {
            cfg.OGCDSizeRatio = ogcdSizeRatio;
            changed = true;
        }
        Tooltip("oGCD icon size as a fraction of the GCD icon size.\nSmaller values help dense weave windows stay readable and match the ATR-style playback look.");

        var ogcdOffset = cfg.OGCDVerticalOffset;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("oGCD Vertical Offset", ref ogcdOffset, -1.0f, 1.0f, "%.2f"))
        {
            cfg.OGCDVerticalOffset = ogcdOffset;
            changed = true;
        }
        Tooltip("Move oGCDs upward from the main action lane.\n0.1 matches the ATR-style default.\nNegative values move them back down.");

        var ogcdHOffset = cfg.OGCDHorizontalOffset;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("oGCD Horizontal Offset", ref ogcdHOffset, -100f, 100f, "%.0f px"))
        {
            cfg.OGCDHorizontalOffset = ogcdHOffset;
            changed = true;
        }
        Tooltip("Nudge oGCD slot placement left (negative) or right (positive) after legal weave slots are chosen.");

        ImGui.Spacing();

        // ── Visual ──
        ImGui.TextDisabled("Visual");

        var bgOpacity = cfg.OverlayBgOpacity;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Background Opacity", ref bgOpacity, 0.0f, 1.0f, "%.2f"))
        {
            cfg.OverlayBgOpacity = bgOpacity;
            changed = true;
        }
        Tooltip("Opacity of the overlay background.\n0 = fully transparent, 1 = solid.");

        var pastAlpha = cfg.OverlayPastAlpha;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Past Icon Dimming", ref pastAlpha, 0.0f, 1.0f, "%.2f"))
        {
            cfg.OverlayPastAlpha = pastAlpha;
            changed = true;
        }
        Tooltip("Opacity multiplier for abilities that have already passed.\n0 = invisible, 1 = same as upcoming.");

        var freqPct = cfg.OverlayFreqThreshold * 100f;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Min Frequency", ref freqPct, 0f, 100f, "%.0f%%"))
        {
            cfg.OverlayFreqThreshold = freqPct / 100f;
            changed = true;
        }
        Tooltip("Hide abilities used in fewer than this % of parses.\nUseful to filter out rare/niche casts.");

        ImGui.Spacing();

        // ── Toggles ──
        ImGui.TextDisabled("Elements");

        var showGrid = cfg.OverlayShowGrid;
        if (ImGui.Checkbox("Show Grid Lines", ref showGrid))
        {
            cfg.OverlayShowGrid = showGrid;
            changed = true;
        }
        Tooltip("Show vertical time grid lines on the overlay.");

        ImGui.Spacing();

        // ── Boss Bar ──
        ImGui.TextDisabled("Boss Bar");

        var bossCustom = cfg.BossBarUseCustomColor;
        if (ImGui.Checkbox("Custom Boss Bar Color", ref bossCustom))
        {
            cfg.BossBarUseCustomColor = bossCustom;
            changed = true;
        }
        Tooltip("Use a single custom color for all boss cast bars\ninstead of the default per-ability coloring.");

        if (cfg.BossBarUseCustomColor)
        {
            var bossCol = cfg.BossBarColor;
            ImGui.SetNextItemWidth(200);
            if (ImGui.ColorEdit4("Boss Bar Color", ref bossCol))
            {
                cfg.BossBarColor = bossCol;
                changed = true;
            }
            Tooltip("Color applied to every boss cast bar.");
        }

        if (changed)
            RequestDeferredUiSettingsSave();
    }

    private void DrawMainTimelineSettings()
    {
        ImGui.TextUnformatted("Main Timeline");
        ImGui.Separator();

        var cfg     = plugin.Configuration;
        var changed = false;

        var iconSize = cfg.MainIconSize;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Icon Size##Main", ref iconSize, 8.0f, 48.0f, "%.0f"))
        {
            cfg.MainIconSize = iconSize;
            changed = true;
        }
        Tooltip("Size of skill icons in the main timeline window.");

        var iconScale = cfg.MainIconScale;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Icon Scale##Main", ref iconScale, 0.5f, 2.0f, "%.2f"))
        {
            cfg.MainIconScale = iconScale;
            changed = true;
        }
        Tooltip("Scale multiplier applied on top of Icon Size.");

        var iconOpacity = cfg.MainIconOpacity;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Icon Opacity##Main", ref iconOpacity, 0.0f, 1.0f, "%.2f"))
        {
            cfg.MainIconOpacity = iconOpacity;
            changed = true;
        }
        Tooltip("Maximum opacity of skill icons.\nMultiplied by each ability's usage frequency.");

        ImGui.Spacing();
        ImGui.TextDisabled("Boss bar color is shared with the overlay — see Boss Bar above.");

        if (changed)
            RequestDeferredUiSettingsSave();
    }

    private void DrawCustomTimelines()
    {
        ImGui.TextUnformatted("Custom Timelines");
        ImGui.Separator();

        var customTimelines = plugin.Configuration.CustomTimelines;
        if (customTimelines.Count == 0)
        {
            ImGui.TextDisabled("No custom timelines saved yet.");
            ImGui.TextDisabled("Use 'Copy Timeline' in the main window to create one.");
        }
        else
        {
            string? toRemove = null;
            foreach (var (key, tl) in customTimelines)
            {
                ImGui.TextUnformatted($"{tl.EncounterName} - {tl.SpecName}");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Delete##{key}"))
                    toRemove = key;
            }

            if (toRemove != null)
            {
                plugin.CustomTimelineStore.RemoveTimeline(plugin.Configuration, toRemove);
                plugin.Configuration.RemoveTimelineReferences(toRemove);
                plugin.SaveTimelineUserState();
            }
        }
    }

    private void DrawAntsSettings()
    {
        ImGui.TextUnformatted("Ability Ants");
        ImGui.Separator();

        var cfg     = plugin.Configuration;
        var changed = false;

        var enabled = cfg.AntsEnabled;
        if (ImGui.Checkbox("Enable Ability Ants", ref enabled))
        {
            cfg.AntsEnabled = enabled;
            changed = true;
        }
        Tooltip("Draw an animated dashed border around abilities as they cross the now-line.\nMatches FFXIV's combo/proc highlight style.");

        if (cfg.AntsEnabled)
        {
            ImGui.Spacing();

            var before = cfg.AntsDurationBefore;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderFloat("Before (sec)", ref before, 0.0f, 4.0f, "%.1f"))
            {
                cfg.AntsDurationBefore = before;
                changed = true;
            }
            Tooltip("How many seconds before crossing the now-line the ants appear.");

            var after = cfg.AntsDurationAfter;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderFloat("After (sec)", ref after, 0.0f, 4.0f, "%.1f"))
            {
                cfg.AntsDurationAfter = after;
                changed = true;
            }
            Tooltip("How many seconds after crossing the now-line the ants remain.");

            ImGui.Spacing();
            ImGui.TextDisabled($"Total window: {cfg.AntsDurationBefore + cfg.AntsDurationAfter:F1}s  " +
                               $"({cfg.AntsDurationBefore:F1}s before + {cfg.AntsDurationAfter:F1}s after)");

            ImGui.Spacing();
            ImGui.Spacing();

            // ── Custom ants ──────────────────────────────────────────────
            ImGui.TextDisabled("Custom ants are always enabled.");

            {
                ImGui.Spacing();

                var dashCol = cfg.AntsColor;
                if (ImGui.ColorEdit4("Dash colour##ants", ref dashCol,
                    ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
                {
                    cfg.AntsColor = dashCol;
                    changed = true;
                }
                Tooltip("Colour of the marching dashes.");

                var gapCol = cfg.AntsGapColor;
                if (ImGui.ColorEdit4("Gap colour##ants", ref gapCol,
                    ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
                {
                    cfg.AntsGapColor = gapCol;
                    changed = true;
                }
                Tooltip("Colour drawn in the gaps between dashes. Set alpha to 0 for transparent gaps.");

                ImGui.Spacing();

                var dashLen = cfg.AntsDashLength;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Dash length (px)", ref dashLen, 1.0f, 30.0f, "%.0f"))
                {
                    cfg.AntsDashLength = dashLen;
                    changed = true;
                }

                var gapLen = cfg.AntsGapLength;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Gap length (px)", ref gapLen, 0.0f, 20.0f, "%.0f"))
                {
                    cfg.AntsGapLength = gapLen;
                    changed = true;
                }

                var speed = cfg.AntsSpeed;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("March speed (px/s)", ref speed, 5.0f, 200.0f, "%.0f"))
                {
                    cfg.AntsSpeed = speed;
                    changed = true;
                }
                Tooltip("How fast the dashes march around the border.");

                var thick = cfg.AntsThickness;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Line thickness (px)", ref thick, 1.0f, 6.0f, "%.1f"))
                {
                    cfg.AntsThickness = thick;
                    changed = true;
                }

                var padding = cfg.AntsBorderPadding;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Border expansion (px)", ref padding, -10.0f, 10.0f, "%.1f"))
                {
                    cfg.AntsBorderPadding = padding;
                    changed = true;
                }
                Tooltip("Expand (+) or shrink (-) the ants border relative to the slot edge.\n0 = flush with slot bounds.");

                var xOff = cfg.AntsXOffset;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Horizontal offset (px)##ants", ref xOff, -20.0f, 20.0f, "%.1f"))
                {
                    cfg.AntsXOffset = xOff;
                    changed = true;
                }
                Tooltip("Shift the ants border left (-) or right (+).");

                var yOff = cfg.AntsYOffset;
                ImGui.SetNextItemWidth(200);
                if (ImGui.SliderFloat("Vertical offset (px)##ants", ref yOff, -20.0f, 20.0f, "%.1f"))
                {
                    cfg.AntsYOffset = yOff;
                    changed = true;
                }
                Tooltip("Shift the ants border up (-) or down (+).");

                ImGui.Spacing();
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.2f, 0.8f, 1.0f, 1.0f), "GCD Ants");
                ImGui.Separator();

                var gcdEnabled = cfg.GcdAntsEnabled;
                if (ImGui.Checkbox("Enable GCD Ants", ref gcdEnabled))
                {
                    cfg.GcdAntsEnabled = gcdEnabled;
                    changed = true;
                }
                Tooltip("Show ants on the next upcoming GCD as it approaches the now-line.\nOnly the single closest GCD entry glows at a time.");

                if (cfg.GcdAntsEnabled)
                {
                    var gcdBefore = cfg.GcdAntsDurationBefore;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Before (sec)##gcdAnts", ref gcdBefore, 0.0f, 4.0f, "%.1f"))
                    {
                        cfg.GcdAntsDurationBefore = gcdBefore;
                        changed = true;
                    }
                    Tooltip("How many seconds before crossing the now-line the ants appear.");

                    var gcdAfter = cfg.GcdAntsDurationAfter;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("After (sec)##gcdAnts", ref gcdAfter, 0.0f, 4.0f, "%.1f"))
                    {
                        cfg.GcdAntsDurationAfter = gcdAfter;
                        changed = true;
                    }
                    Tooltip("How many seconds after crossing the now-line the ants remain.");

                    ImGui.Spacing();
                    ImGui.TextDisabled($"Window: {cfg.GcdAntsDurationBefore + cfg.GcdAntsDurationAfter:F1}s  " +
                                       $"({cfg.GcdAntsDurationBefore:F1}s before + {cfg.GcdAntsDurationAfter:F1}s after)");

                    var gcdDashCol = cfg.GcdAntsColor;
                    if (ImGui.ColorEdit4("Dash colour##gcdAnts", ref gcdDashCol,
                        ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
                    {
                        cfg.GcdAntsColor = gcdDashCol;
                        changed = true;
                    }

                    var gcdGapCol = cfg.GcdAntsGapColor;
                    if (ImGui.ColorEdit4("Gap colour##gcdAnts", ref gcdGapCol,
                        ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf))
                    {
                        cfg.GcdAntsGapColor = gcdGapCol;
                        changed = true;
                    }
                    Tooltip("Set alpha to 0 for transparent gaps.");

                    var gcdDashLen = cfg.GcdAntsDashLength;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Dash length (px)##gcdAnts", ref gcdDashLen, 1.0f, 30.0f, "%.0f"))
                    {
                        cfg.GcdAntsDashLength = gcdDashLen;
                        changed = true;
                    }

                    var gcdGapLen = cfg.GcdAntsGapLength;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Gap length (px)##gcdAnts", ref gcdGapLen, 0.0f, 20.0f, "%.0f"))
                    {
                        cfg.GcdAntsGapLength = gcdGapLen;
                        changed = true;
                    }

                    var gcdSpeed = cfg.GcdAntsSpeed;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("March speed (px/s)##gcdAnts", ref gcdSpeed, 5.0f, 200.0f, "%.0f"))
                    {
                        cfg.GcdAntsSpeed = gcdSpeed;
                        changed = true;
                    }
                    Tooltip("How fast the dashes march around the border.");

                    var gcdThick = cfg.GcdAntsThickness;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Line thickness (px)##gcdAnts", ref gcdThick, 1.0f, 6.0f, "%.1f"))
                    {
                        cfg.GcdAntsThickness = gcdThick;
                        changed = true;
                    }

                    var gcdPadding = cfg.GcdAntsBorderPadding;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Border expansion (px)##gcdAnts", ref gcdPadding, -10.0f, 10.0f, "%.1f"))
                    {
                        cfg.GcdAntsBorderPadding = gcdPadding;
                        changed = true;
                    }
                    Tooltip("Expand (+) or shrink (-) the ants border relative to the slot edge.\n0 = flush with slot bounds.");

                    var gcdXOff = cfg.GcdAntsXOffset;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Horizontal offset (px)##gcdAnts", ref gcdXOff, -20.0f, 20.0f, "%.1f"))
                    {
                        cfg.GcdAntsXOffset = gcdXOff;
                        changed = true;
                    }
                    Tooltip("Shift the ants border left (-) or right (+).");

                    var gcdYOff = cfg.GcdAntsYOffset;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.SliderFloat("Vertical offset (px)##gcdAnts", ref gcdYOff, -20.0f, 20.0f, "%.1f"))
                    {
                        cfg.GcdAntsYOffset = gcdYOff;
                        changed = true;
                    }
                    Tooltip("Shift the ants border up (-) or down (+).");
                }
            }
        }

        if (changed)
            RequestDeferredUiSettingsSave();
    }

    private void DrawResetSection()
    {
        ImGui.TextUnformatted("Reset");
        ImGui.Separator();

        var red = new Vector4(1.0f, 0.35f, 0.35f, 1.0f);
        ImGui.TextColored(in red, "This will reset all settings to their default values.");
        ImGui.Spacing();

        if (ImGui.Button("Reset to Defaults", default))
        {
            var cfg = plugin.Configuration;

            cfg.OverlayEnabled          = true;
            cfg.OverlayLocked           = true;
            cfg.OverlayPixelsPerSec     = 72.0f;
            cfg.OverlayIconSize         = 44.0f;
            cfg.OverlayTimeBehind       = 1.5f;
            cfg.OverlayBgOpacity        = 1.0f;
            cfg.OverlayPastAlpha        = 1.0f;
            cfg.OverlayFreqThreshold    = 0.0f;
            cfg.OverlayShowGrid         = true;
            cfg.OverlayMaxStackedIcons  = 3;
            cfg.OGCDSizeRatio           = 0.75f;
            cfg.OGCDVerticalOffset      = 0.1f;
            cfg.OGCDHorizontalOffset    = 0f;
            cfg.BossBarUseCustomColor   = false;
            cfg.BossBarColor            = new Vector4(0.85f, 0.35f, 0.20f, 1.00f);
            cfg.MainIconSize            = 22.0f;
            cfg.MainIconOpacity         = 1.0f;
            cfg.MainIconScale           = 1.0f;
            cfg.AutoTimelineGcdRecastSec = 2.5f;
            cfg.AutoTimelineDotRefreshBufferSec = 6.0f;
            cfg.AutoTimelineDisabledAbilities.Clear();
            cfg.AntsEnabled             = true;
            cfg.OgcdAntsEnabled         = true;
            cfg.AntsDurationBefore      = 1.5f;
            cfg.AntsDurationAfter       = 1.5f;
            cfg.AntsCustomEnabled       = true;
            cfg.AntsColor               = new Vector4(1.0f, 0.85f, 0.0f, 1.0f);
            cfg.AntsGapColor            = new Vector4(0.0f, 0.0f, 0.0f, 0.5f);
            cfg.AntsDashLength          = 6.0f;
            cfg.AntsGapLength           = 4.0f;
            cfg.AntsSpeed               = 40.0f;
            cfg.AntsThickness           = 2.0f;
            cfg.AntsBorderPadding       = 0.0f;
            cfg.AntsXOffset             = -5.0f;
            cfg.AntsYOffset             = 0.0f;
            cfg.GcdAntsEnabled          = true;
            cfg.GcdAntsDurationBefore   = 0.5f;
            cfg.GcdAntsDurationAfter    = 0.5f;
            cfg.GcdAntsColor            = new Vector4(0.2f, 0.8f, 1.0f, 1.0f);
            cfg.GcdAntsGapColor         = new Vector4(0.0f, 0.0f, 0.0f, 0.5f);
            cfg.GcdAntsDashLength       = 6.0f;
            cfg.GcdAntsGapLength        = 4.0f;
            cfg.GcdAntsSpeed            = 40.0f;
            cfg.GcdAntsThickness        = 2.0f;
            cfg.GcdAntsBorderPadding    = 0.0f;
            cfg.GcdAntsXOffset          = -5.0f;
            cfg.GcdAntsYOffset          = 0.0f;

            plugin.SaveUiSettings();
        }
    }

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text(text);
            ImGui.EndTooltip();
        }
    }

    private void RequestDeferredUiSettingsSave()
    {
        pendingDeferredUiSettingsSave = true;
        pendingDeferredUiSettingsSaveAt = DateTime.UtcNow + DeferredUiSettingsSaveDelay;
    }

    private void ProcessDeferredUiSettingsSave()
    {
        if (!pendingDeferredUiSettingsSave || DateTime.UtcNow < pendingDeferredUiSettingsSaveAt)
            return;

        if (ImGui.IsAnyItemActive() ||
            ImGui.IsMouseDown(ImGuiMouseButton.Left) ||
            ImGui.IsMouseDown(ImGuiMouseButton.Right) ||
            ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            pendingDeferredUiSettingsSaveAt = DateTime.UtcNow + DeferredUiSettingsSaveDelay;
            return;
        }

        plugin.SaveUiSettings();
        pendingDeferredUiSettingsSave = false;
    }
}
