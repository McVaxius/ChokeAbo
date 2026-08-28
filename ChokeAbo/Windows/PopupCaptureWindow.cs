using System.Numerics;
using ChokeAbo.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ChokeAbo.Windows;

public sealed class PopupCaptureWindow : Window
{
    private readonly Plugin plugin;

    public PopupCaptureWindow(Plugin plugin)
        : base("Choke-abo Popup Capture##ChokeAboPopupCapture")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(260f, 190f),
            MaximumSize = new Vector2(260f, 190f),
        };
    }

    public override void Draw()
    {
        DrawCaptureButton("Start/Stop Retirement", PopupCaptureKind.Retirement);
        DrawCaptureButton("Start/Stop Covering Selector", PopupCaptureKind.CoveringSelector);
        DrawCaptureButton("Start/Stop Fledgling Selector", PopupCaptureKind.FledglingSelector);
        DrawCaptureButton("Start/Stop Adoption", PopupCaptureKind.Adoption);
        if (ImGui.Button("Open Folder", new Vector2(-1f, 0f)))
            plugin.PopupCaptureRecorder.OpenFolder();
    }

    private void DrawCaptureButton(string label, PopupCaptureKind kind)
    {
        if (ImGui.Button(label, new Vector2(-1f, 0f)))
            plugin.TogglePopupCapture(kind);
    }
}
