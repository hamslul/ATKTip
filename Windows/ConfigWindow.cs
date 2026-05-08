using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ATKTip.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;

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
            plugin.SaveConfig();
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
            plugin.SaveConfig();
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
            plugin.SaveConfig();
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
            plugin.SaveConfig();
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
                customTimelines.Remove(toRemove);
                plugin.SaveConfig();
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
            var customEnabled = cfg.AntsCustomEnabled;
            if (ImGui.Checkbox("Custom Ants (replace native highlight)", ref customEnabled))
            {
                cfg.AntsCustomEnabled = customEnabled;
                changed = true;
            }
            Tooltip("Replaces FFXIV's built-in marching-ants highlight with a fully\n" +
                    "customisable ImGui-drawn dashed border. Colour, speed and size\n" +
                    "are all adjustable below.");

            if (cfg.AntsCustomEnabled)
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
            }
        }

        if (changed)
            plugin.SaveConfig();
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
            cfg.AntsEnabled             = true;
            cfg.AntsDurationBefore      = 1.5f;
            cfg.AntsDurationAfter       = 1.5f;
            cfg.AntsCustomEnabled       = false;
            cfg.AntsColor               = new Vector4(1.0f, 0.85f, 0.0f, 1.0f);
            cfg.AntsGapColor            = new Vector4(0.0f, 0.0f, 0.0f, 0.5f);
            cfg.AntsDashLength          = 6.0f;
            cfg.AntsGapLength           = 4.0f;
            cfg.AntsSpeed               = 40.0f;
            cfg.AntsThickness           = 2.0f;
            cfg.AntsBorderPadding       = 0.0f;

            plugin.SaveConfig();
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
}
