using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using AethertekUI;
using AethertekUI.Dalamud;
using ChokeAbo.Ui;

namespace ChokeAbo.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly MaterialWindowMotion windowMotion = new();
    private static readonly string[] DtrModes = { "Text only", "Icon + text", "Icon only" };
    private readonly Plugin plugin;
    public ConfigWindow(Plugin plugin) : base($"{PluginInfo.DisplayName} Settings##Config")
    {
        this.plugin = plugin;
        Flags |= ImGuiWindowFlags.HorizontalScrollbar;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(620f, 460f), MaximumSize = new Vector2(1500f, 1300f) };
    }
    public void Dispose() { }
    public override void PreDraw() => windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    public override void PostDraw() => windowMotion.Restore(this);
    public override void Draw()
    {
        windowMotion.DrawChrome();
        var cfg = plugin.Configuration;
        UiGui.Title(PluginInfo.DisplayName + " Settings", PluginInfo.DisplayName + " — " + UiText.T("Settings"));
        plugin.Appearance.DrawWindowAppearanceSettings();
        ImGui.Separator();
        if (UiGui.SmallButton("Ko-fi##ChokeAboConfig"))
            Process.Start(new ProcessStartInfo { FileName = PluginInfo.SupportUrl, UseShellExecute = true });
        UiGui.SameLineIfFits(UiGui.ButtonWidth("Discord"));
        if (UiGui.SmallButton("Discord##ChokeAboConfig"))
            Process.Start(new ProcessStartInfo { FileName = PluginInfo.DiscordUrl, UseShellExecute = true });
        UiGui.TextDisabled(PluginInfo.DiscordFeedbackNote);
        ImGui.Separator();
        var enabled = cfg.PluginEnabled; if (UiGui.Checkbox("Plugin enabled", ref enabled)) { cfg.PluginEnabled = enabled; cfg.Save(); plugin.UpdateDtrBar(); }
        var dtr = cfg.DtrBarEnabled; if (UiGui.Checkbox("Show DTR bar entry", ref dtr)) { cfg.DtrBarEnabled = dtr; cfg.Save(); plugin.UpdateDtrBar(); }
        var mode = cfg.DtrBarMode; if (UiGui.Combo("DTR mode", ref mode, DtrModes, DtrModes.Length)) { cfg.DtrBarMode = mode; cfg.Save(); plugin.UpdateDtrBar(); }
        var onIcon = cfg.DtrIconEnabled; if (UiGui.InputText("DTR enabled glyph", ref onIcon, 8)) { cfg.DtrIconEnabled = onIcon.Length <= 3 ? onIcon : onIcon[..3]; cfg.Save(); plugin.UpdateDtrBar(); }
        var offIcon = cfg.DtrIconDisabled; if (UiGui.InputText("DTR disabled glyph", ref offIcon, 8)) { cfg.DtrIconDisabled = offIcon.Length <= 3 ? offIcon : offIcon[..3]; cfg.Save(); plugin.UpdateDtrBar(); }
        ImGui.Separator(); UiGui.TextUnformatted("Rollout phases"); foreach (var x in PluginInfo.Phases) UiGui.BulletText(x);
        ImGui.Spacing(); UiGui.TextUnformatted("Concept recap"); foreach (var x in PluginInfo.Concept) UiGui.BulletText(x);
    }
}
