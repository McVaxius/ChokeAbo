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
    private readonly MaterialWindowLock movementLock = new();
    private bool materialBackdropPending;
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
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(Math.Max(340, widest / scale), 340), MaximumSize = new Vector2(1500, 1000) };
        UiGui.ReserveTitleSpace(this, "Choke-abo — " + UiText.T("Popup Capture"), Math.Max(340, widest / scale));
        windowMotion.Prepare(this, reducedMotion: !plugin.Configuration.UiStylingEnabled, roundedCorners: plugin.Configuration.UiStylingEnabled);
        materialBackdropPending = UiGui.PrepareMaterialBackdrop(plugin.Appearance.BackgroundEnabled);
        movementLock.Apply(this, plugin.Configuration.UiWindowsLocked);
    }

    public override void PostDraw()
    {
        movementLock.Restore(this);
        UiGui.RestoreMaterialBackdrop(ref materialBackdropPending);
        windowMotion.Restore(this);
        UiGui.PaintTitleWithImageAndStatus(this, "Choke-abo \u2014 " + UiText.T("Popup Capture"), status: plugin.TitlebarStatus);
    }

    public override void Draw()
    {
        UiGui.RestoreMaterialBackdrop(ref materialBackdropPending);
        plugin.Appearance.PrepareBackgroundOpacity(WindowName, ImGui.IsWindowAppearing());
        MaterialSkin.Backdrop();
        using var structuralBackgrounds = MaterialSkin.StructuralBackgroundStyle();
        windowMotion.DrawChrome();
        if (MaterialTheme.Current.IsLcars)
            using (UiText.Font(UiFontRole.Title)) MaterialLcars.Header(UiText.T("Popup Capture"));
        UiGui.TitleWithButtons("Choke-abo Popup Capture", "Choke-abo — " + UiText.T("Popup Capture"), this);
        DrawCaptureButton("Start/Stop Retirement", PopupCaptureKind.Retirement);
        DrawCaptureButton("Start/Stop Covering Selector", PopupCaptureKind.CoveringSelector);
        DrawCaptureButton("Start/Stop Fledgling Selector", PopupCaptureKind.FledglingSelector);
        DrawCaptureButton("Start/Stop Adoption", PopupCaptureKind.Adoption);
        if (UiGui.Button("Open Folder", new Vector2(-1f, 0), MaterialIcon.Folder))
            plugin.PopupCaptureRecorder.OpenFolder();
    }

    private void DrawCaptureButton(string label, PopupCaptureKind kind)
    {
        if (UiGui.Button(label, new Vector2(-1f, 0), kind switch { PopupCaptureKind.CoveringSelector => MaterialIcon.Person, PopupCaptureKind.FledglingSelector => MaterialIcon.Egg, PopupCaptureKind.Adoption => MaterialIcon.Home, _ => MaterialIcon.Play }))
            plugin.TogglePopupCapture(kind);
    }
}
