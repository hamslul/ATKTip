using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ATKTip.Windows;

public sealed class AutoModalWindow : Window
{
    private string message = string.Empty;
    private Action? onAcknowledge;

    public AutoModalWindow()
        : base("AUTO##ATKTipAutoModal",
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse)
    {
        RespectCloseHotkey = false;
        IsOpen = false;
        Size = new Vector2(520f, 0f);
        SizeCondition = ImGuiCond.Always;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420f, 140f),
            MaximumSize = new Vector2(620f, 260f),
        };
    }

    public void Show(string popupMessage, Action? acknowledge = null)
    {
        message = popupMessage ?? string.Empty;
        onAcknowledge = acknowledge;
        IsOpen = true;
    }

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        var center = viewport.WorkPos + viewport.WorkSize / 2f;
        ImGui.SetNextWindowPos(center, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
    }

    public override void Draw()
    {
        ImGui.Spacing();
        ImGui.TextWrapped(message);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var buttonWidth = 120f;
        var availWidth = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(MathF.Max(0f, (availWidth - buttonWidth) * 0.5f));
        if (ImGui.Button("Okay", new Vector2(buttonWidth, 0f)))
        {
            IsOpen = false;
            var callback = onAcknowledge;
            onAcknowledge = null;
            callback?.Invoke();
        }
    }
}
