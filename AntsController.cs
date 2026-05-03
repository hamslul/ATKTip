using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HotbarSlotType = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType;

namespace ATKTip;

/// <summary>
/// Hooks <c>ActionManager.IsActionHighlighted</c> to drive FFXIV's native marching-ants
/// combo/proc highlight. ATKTip may optionally add its own standard or custom ants
/// for overlay-managed actions, but never suppresses FFXIV's native highlight.
/// </summary>
public sealed unsafe class AntsController : IDisposable
{
    private readonly Plugin  plugin;
    private readonly IGameGui gameGui;
    private readonly Hook<IsActionHighlightedDelegate>? hook;
    private bool hookEnabled;

    private delegate bool IsActionHighlightedDelegate(
        ActionManager* actionManager,
        ActionType     actionType,
        uint           actionId);

    // Standard action-bar addon names: bar 0 = "_ActionBar", bars 1-9 = "_ActionBar01".."_ActionBar09"
    private static readonly string[] HotbarAddonNames =
    [
        "_ActionBar",    "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04",
        "_ActionBar05",  "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09",
    ];

    public AntsController(Plugin plugin, IGameGui gameGui, IGameInteropProvider gameInterop)
    {
        this.plugin  = plugin;
        this.gameGui = gameGui;

        hook = gameInterop.HookFromAddress<IsActionHighlightedDelegate>(
            (nint)ActionManager.MemberFunctionPointers.IsActionHighlighted,
            HandleIsActionHighlighted);
    }

    // ── Hook ────────────────────────────────────────────────────────────

    private bool HandleIsActionHighlighted(
        ActionManager* actionManager,
        ActionType     actionType,
        uint           actionId)
    {
        var original = hook!.Original(actionManager, actionType, actionId);

        // Only intercept regular hotbar actions.
        if (actionType != ActionType.Action)
            return original;

        var cfg = plugin.Configuration;
        if (!plugin.OverlayWindow.CanManageAbilityAnts())
            return original;

        // Custom mode draws ATKTip ants separately and leaves FFXIV's native highlight alone.
        if (cfg.AntsEnabled && cfg.AntsCustomEnabled)
            return original;

        // Standard mode: pass through native highlights; additionally highlight ATKTip abilities.
        if (original) return true;

        var inAntsStd = plugin.OverlayWindow.IsAbilityInAntsWindow((int)actionId);
        return cfg.AntsEnabled && inAntsStd;
    }

    // ── Draw (called on UI thread via UiBuilder.Draw) ────────────────────

    public void Draw()
    {
        SyncHookState();

        var cfg = plugin.Configuration;
        if (!cfg.AntsEnabled || !cfg.AntsCustomEnabled) return;

        var (gcdIds, ogcdIds) = plugin.OverlayWindow.GetAntsAbilityIds();
        if (gcdIds.Count == 0 && ogcdIds.Count == 0) return;

        var actionManager = ActionManager.Instance();
        var hotbarModule  = UIModule.Instance()->GetRaptureHotbarModule();
        if (hotbarModule == null) return;

        var time = (float)ImGui.GetTime();

        for (var barIdx = 0; barIdx < HotbarAddonNames.Length; barIdx++)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(HotbarAddonNames[barIdx]);
            if (addon == null || !addon->IsVisible) continue;

            var nodeList  = addon->UldManager.NodeList;
            var nodeCount = (int)addon->UldManager.NodeListCount;
            var scale     = addon->Scale;

            // Locate the 12 slot button component nodes by their stable NodeId (8–19).
            // NodeId-based lookup is more reliable than size-filtering because component
            // nodes can have Width=Height=0 when their layout is managed by child nodes.
            var slotNodes = stackalloc AtkResNode*[12];
            for (var k = 0; k < 12; k++) slotNodes[k] = null;

            for (var i = 0; i < nodeCount; i++)
            {
                var n = nodeList[i];
                if (n == null) continue;
                if ((uint)n->Type < 1000) continue;   // component nodes only
                var nid = n->NodeId;
                if (nid < 8 || nid > 19) continue;
                slotNodes[nid - 8] = n;
            }

            for (var slotIdx = 0; slotIdx < 12; slotIdx++)
            {
                var node = slotNodes[slotIdx];
                if (node == null) continue;

                var slot = hotbarModule->GetSlotById((uint)barIdx, (uint)slotIdx);
                if (slot == null) continue;
                if (slot->CommandType != HotbarSlotType.Action) continue;

                var rawId = (int)slot->CommandId;

                // For GCDs: resolve the current combo step so hotbar base IDs (e.g. Fast
                // Blade = 9) match the specific FFLogs-logged action (e.g. Royal Authority).
                var adjustedId = (int)actionManager->GetAdjustedActionId((uint)rawId);

                bool isGcdSlot;
                if (gcdIds.Contains(adjustedId))
                    isGcdSlot = true;
                else if (ogcdIds.Contains(rawId))
                    isGcdSlot = false;
                else
                    continue;

                // Width/Height may be 0 for component nodes; fall back to FFXIV's
                // default slot size of 46 native units in that case.
                var nodeW = node->Width  > 0 ? (float)node->Width  : 46f;
                var nodeH = node->Height > 0 ? (float)node->Height : 46f;

                // Apply a baseline 10 px inset so that padding = 0 looks correct by default.
                var padding = (isGcdSlot ? cfg.GcdAntsBorderPadding : cfg.AntsBorderPadding) - 10f;

                var xOffset = isGcdSlot ? cfg.GcdAntsXOffset : cfg.AntsXOffset;
                var x = node->ScreenX - padding + xOffset;
                var y = node->ScreenY - padding;
                var w = nodeW * node->ScaleX * scale + padding * 2f;
                var h = nodeH * node->ScaleY * scale + padding * 2f;

                if (w <= 2 || h <= 2) continue;

                DrawMarchingAnts(x, y, w, h, time, cfg, isGcdSlot);
            }
        }
    }

    // ── Marching ants renderer ──────────────────────────────────────────

    private static void DrawMarchingAnts(
        float x, float y, float w, float h,
        float time, Configuration cfg, bool isGcd)
    {
        var dashLen = isGcd ? cfg.GcdAntsDashLength : cfg.AntsDashLength;
        var gapLen  = isGcd ? cfg.GcdAntsGapLength  : cfg.AntsGapLength;
        var speed   = isGcd ? cfg.GcdAntsSpeed      : cfg.AntsSpeed;
        var thick   = isGcd ? cfg.GcdAntsThickness  : cfg.AntsThickness;
        var period  = dashLen + gapLen;
        if (period <= 0) return;

        var dashCol = ImGui.ColorConvertFloat4ToU32(isGcd ? cfg.GcdAntsColor    : cfg.AntsColor);
        var gapCol  = ImGui.ColorConvertFloat4ToU32(isGcd ? cfg.GcdAntsGapColor : cfg.AntsGapColor);

        // Clockwise corners: TL → TR → BR → BL
        Span<Vector2> corners = stackalloc Vector2[4]
        {
            new(x,     y),
            new(x + w, y),
            new(x + w, y + h),
            new(x,     y + h),
        };
        Span<float> segLens = stackalloc float[4] { w, h, w, h };

        var offset = (time * speed) % period;
        if (offset < 0) offset += period;

        var dl = ImGui.GetForegroundDrawList();

        var perimPos = 0f;
        for (var side = 0; side < 4; side++)
        {
            var from   = corners[side];
            var to     = corners[(side + 1) % 4];
            var segLen = segLens[side];
            if (segLen <= 0) continue;
            var dir = (to - from) / segLen;

            var posOnSeg = 0f;
            while (posOnSeg < segLen)
            {
                var patternPos = (perimPos + posOnSeg + offset) % period;
                var inDash     = patternPos < dashLen;
                var remaining  = inDash ? dashLen - patternPos : period - patternPos;
                var segEnd     = Math.Min(posOnSeg + remaining, segLen);

                var p1  = from + dir * posOnSeg;
                var p2  = from + dir * segEnd;
                var col = inDash ? dashCol : gapCol;

                dl.AddLine(p1, p2, col, thick);

                posOnSeg = segEnd + 0.01f;  // nudge past boundary to avoid infinite loop
            }

            perimPos += segLen;
        }
    }

    public void Dispose()
    {
        if (hookEnabled)
            hook?.Disable();
        hook?.Dispose();
    }

    private void SyncHookState()
    {
        if (hook == null)
            return;

        var shouldEnable =
            plugin.Configuration.AntsEnabled &&
            !plugin.Configuration.AntsCustomEnabled &&
            plugin.OverlayWindow.CanManageAbilityAnts();

        if (shouldEnable == hookEnabled)
            return;

        if (shouldEnable)
            hook.Enable();
        else
            hook.Disable();

        hookEnabled = shouldEnable;
    }
}
