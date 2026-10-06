using System.Numerics;
using ChokeAbo.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using AethertekUI;
using AethertekUI.Dalamud;
using ChokeAbo.Ui;

namespace ChokeAbo.Windows;

public sealed class PopupCaptureWindow : Window
{
    private readonly MaterialWindowMotion windowMotion = new();
    private readonly Plugin plugin;

    public PopupCaptureWindow(Plugin plugin)
        : base("Choke-abo Popup Capture##ChokeAboPopupCapture")
    {
        this.plugin = plugin;
        Flags |= ImGuiWindowFlags.HorizontalScrollbar;
        Size = new Vector2(350, 405); SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340f, 340f),
            MaximumSize = new Vector2(1200f, 1000f),
        };
    }

    public override void PreDraw()
    {
        var scale = MaterialTheme.Metrics.Scale;
        var widest = new[] { "Start/Stop Retirement", "Start/Stop Covering Selector", "Start/Stop Fledgling Selector", "Start/Stop Adoption", "Open Folder" }
            .Max(label => UiGui.ButtonWidth(label, MaterialIcon.Play)) + 2 * ImGui.GetStyle().WindowPadding.X;
        var titleWidth = MaterialText.Measure("Choke-abo — " + UiText.T("Popup Capture")).X + ImGui.GetFontSize()
            + ImGui.GetStyle().ItemInnerSpacing.X + 2 * ImGui.GetFrameHeight() + 2 * ImGui.GetStyle().FramePadding.X;
        widest = Math.Max(widest, titleWidth);
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(Math.Max(340, widest / scale), 340), MaximumSize = new Vector2(1500, 1000) };
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw() => windowMotion.Restore(this);

    public override void Draw()
    {
        windowMotion.DrawChrome();
        UiGui.Title("Choke-abo Popup Capture", "Choke-abo — " + UiText.T("Popup Capture"));
        DrawCaptureButton("Start/Stop Retirement", PopupCaptureKind.Retirement);
        DrawCaptureButton("Start/Stop Covering Selector", PopupCaptureKind.CoveringSelector);
        DrawCaptureButton("Start/Stop Fledgling Selector", PopupCaptureKind.FledglingSelector);
        DrawCaptureButton("Start/Stop Adoption", PopupCaptureKind.Adoption);
        if (UiGui.Button("Open Folder", new Vector2(-1f, ChokePresentation.ControlHeight * MaterialTheme.Metrics.Scale), MaterialIcon.Folder))
            plugin.PopupCaptureRecorder.OpenFolder();
    }

    private void DrawCaptureButton(string label, PopupCaptureKind kind)
    {
        if (UiGui.Button(label, new Vector2(-1f, ChokePresentation.ControlHeight * MaterialTheme.Metrics.Scale), kind switch { PopupCaptureKind.CoveringSelector => MaterialIcon.Person, PopupCaptureKind.FledglingSelector => MaterialIcon.Egg, PopupCaptureKind.Adoption => MaterialIcon.Home, _ => MaterialIcon.Play }))
            plugin.TogglePopupCapture(kind);
    }
}
