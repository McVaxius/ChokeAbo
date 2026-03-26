using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ChokeAbo.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    public MainWindow(Plugin plugin) : base($"{PluginInfo.DisplayName}##Main")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520f, 420f), MaximumSize = new Vector2(1400f, 1200f) };
    }
    public void Dispose() { }
    public override void Draw()
    {
        var cfg = plugin.Configuration;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        ImGui.Text($"{PluginInfo.DisplayName} v{version}");
        ImGui.SameLine(ImGui.GetWindowWidth() - 120f);
        if (ImGui.SmallButton("Ko-fi")) Process.Start(new ProcessStartInfo { FileName = PluginInfo.SupportUrl, UseShellExecute = true });
        ImGui.Separator();
        var enabled = cfg.PluginEnabled; if (ImGui.Checkbox("Enabled", ref enabled)) { cfg.PluginEnabled = enabled; cfg.Save(); plugin.UpdateDtrBar(); }
        ImGui.SameLine(); var dtr = cfg.DtrBarEnabled; if (ImGui.Checkbox("DTR Bar", ref dtr)) { cfg.DtrBarEnabled = dtr; cfg.Save(); plugin.UpdateDtrBar(); }
        ImGui.SameLine(); if (ImGui.SmallButton("Settings")) plugin.ToggleConfigUi();
        ImGui.SameLine(); if (ImGui.SmallButton("Status to chat")) plugin.PrintStatus("Bootstrap shell loaded.");
        ImGui.TextWrapped(PluginInfo.Summary);
        ImGui.Text($"Repository target: {PluginInfo.Visibility}");
        ImGui.Text($"Command: {PluginInfo.Command}");
        ImGui.Separator(); ImGui.TextUnformatted("Concept"); foreach (var x in PluginInfo.Concept) ImGui.BulletText(x);
        ImGui.Separator(); ImGui.TextUnformatted("Planned services"); foreach (var x in PluginInfo.Services) ImGui.BulletText(x);
        ImGui.Separator(); ImGui.TextUnformatted("First test pass"); foreach (var x in PluginInfo.Tests) ImGui.BulletText(x);
    }
}
