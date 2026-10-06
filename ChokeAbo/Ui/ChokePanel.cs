using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;

namespace ChokeAbo.Ui;

// Autosize presentation cards while preserving the previous native ID root for their controls.
internal ref struct ChokePanel
{
    private MaterialStyleScope style;
    private ImDrawListSplitterPtr layers;
    private ImDrawListPtr draw;
    private Vector2 min;
    private float width;
    internal bool Visible { get; }
    internal ChokePanel(string id, float width = 0, float? padding = null)
    {
        var root = ImGui.GetID("");
        min = ImGui.GetCursorScreenPos();
        this.width = width > 0 ? width : ImGui.GetContentRegionAvail().X;
        draw = ImGui.GetWindowDrawList();
        layers = ImGui.ImDrawListSplitter();
        layers.Split(draw, 2); layers.SetCurrentChannel(draw, 1);
        style = new();
        style.Style(ImGuiStyleVar.CellPadding, new Vector2(padding ?? (ChokePresentation.Compact ? 12 : 16)) * MaterialTheme.Metrics.Scale);
        Visible = ImGui.BeginTable(id, 1, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX, new Vector2(width, 0));
        if (Visible) { ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0); }
        ImGuiP.PushOverrideID(root);
    }
    public void Dispose()
    {
        ImGui.PopID();
        if (Visible)
        {
            ImGui.EndTable();
            var bottom = ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().ItemSpacing.Y;
            layers.SetCurrentChannel(draw, 0);
            ChokePresentation.Surface(min, new Vector2(min.X + width, bottom));
        }
        layers.Merge(draw); layers.Destroy(); style.Dispose();
    }
}
