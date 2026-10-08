using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ChokeAbo.Ui;

// The native widget keeps its original label/ID; visible text is measured and painted in the selected language.
internal static class UiGui
{
    private static string Visible(string label) => label.Split("##", 2)[0];
    internal static float ButtonWidth(string label, MaterialIcon icon = MaterialIcon.None) => MaterialText.Measure(UiText.T(Visible(label))).X
        + 2 * ImGui.GetStyle().FramePadding.X + (icon == MaterialIcon.None ? 0 : 28 * MaterialTheme.Metrics.Scale);
    internal static float CheckboxWidth(string label) => ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + MaterialText.Measure(UiText.T(Visible(label))).X;
    internal static float ToggleWidth(string label) => Math.Max((ChokePresentation.Compact ? 170 : 180) * MaterialTheme.Metrics.Scale,
        MaterialText.Measure(UiText.T(Visible(label))).X + 100 * MaterialTheme.Metrics.Scale);
    internal static void SameLineIfFits(float width)
    {
        var window = ImGuiP.GetCurrentWindow();
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width <= window.InnerRect.Max.X - window.WindowPadding.X) ImGui.SameLine();
    }
    internal static void Text(string value) { ImGui.PushTextWrapPos(0); try { MaterialText.Text(UiText.T(value)); } finally { ImGui.PopTextWrapPos(); } }
    internal static void TextUnformatted(string value) => MaterialText.Text(UiText.T(value));
    internal static void TextWrapped(string value) => MaterialText.TextWrapped(UiText.T(value));
    internal static void TextDisabled(string value) { ImGui.PushTextWrapPos(0); try { MaterialText.TextDisabled(UiText.T(value)); } finally { ImGui.PopTextWrapPos(); } }
    internal static void TextColored(Vector4 color, string value) { ImGui.PushTextWrapPos(0); try { MaterialText.TextColored(color, UiText.T(value)); } finally { ImGui.PopTextWrapPos(); } }
    internal static void SetTooltip(string value) => MaterialText.SetTooltip(UiText.T(value));
    internal static void BulletText(string value) { ImGui.Bullet(); ImGui.SameLine(); Text(value); }
    internal static bool Button(string original, Vector2 pixels = default, MaterialIcon icon = MaterialIcon.None, string? display = null)
    {
        var translated = display ?? UiText.T(Visible(original));
        using var controls = ImGui.GetStyle().FramePadding.Y == 0 || MaterialControls.Context == MaterialControlContext.Dense
            ? default(MaterialControls.ControlScope) : MaterialControls.Push(MaterialControlContext.Toolbar);
        using var lineHeight = MaterialText.PushLineHeight(translated);
        var style = ImGui.GetStyle(); var color = style.Colors[(int)ImGuiCol.Text];
        var iconWidth = icon == MaterialIcon.None ? 0 : 28 * MaterialTheme.Metrics.Scale;
        var natural = MaterialText.Measure(translated).X + 2 * style.FramePadding.X + iconWidth;
        var width = MaterialLayout.FitNextItemWidth(pixels.X, pixels.X > 0 ? Math.Max(pixels.X, natural) : natural);
        var vertical = MaterialControls.Context == MaterialControlContext.Dense ? ChokePresentation.Compact ? 1 : 2 : ChokePresentation.Compact ? 2 : 4;
        var height = Math.Max(pixels.Y, Math.Max(ImGui.GetFrameHeight(), icon == MaterialIcon.None ? 0 : (22 + 2 * vertical) * MaterialTheme.Metrics.Scale));
        var background = style.Colors[(int)ImGuiCol.Button];
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border, Vector4.Zero);
        var clicked = ImGui.Button(original, new Vector2(width, height));
        ImGui.PopStyleColor(5);
        var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax(); var dl = ImGui.GetWindowDrawList();
        var colors = MaterialTheme.Current.Colors;
        var primary = background == colors.Primary;
        var fill = ImGui.IsItemActive() ? style.Colors[(int)ImGuiCol.ButtonActive] : ImGui.IsItemHovered() ? style.Colors[(int)ImGuiCol.ButtonHovered] : primary ? background : colors.SurfaceContainerHigh;
        MaterialCanvas.Surface(min, max, fill, primary ? fill : Vector4.Lerp(fill, colors.Surface, .65f), 4 * MaterialTheme.Metrics.Scale);
        dl.AddRect(min, max, MaterialCanvas.Color(primary || ImGui.IsItemFocused() ? colors.Primary : colors.OutlineVariant), 4 * MaterialTheme.Metrics.Scale,
            ImDrawFlags.None, (ImGui.IsItemFocused() ? 2 : 1) * MaterialTheme.Metrics.Scale);
        var ink = color;
        var textSize = MaterialText.Measure(translated);
        var origin = min + new Vector2(Math.Max(style.FramePadding.X, (max.X - min.X - textSize.X - iconWidth) * .5f), (max.Y - min.Y - textSize.Y) * .5f);
        dl.PushClipRect(min, max, true);
        try
        {
        if (icon != MaterialIcon.None) MaterialIcons.Draw(icon, origin + new Vector2(0, (textSize.Y - 22 * MaterialTheme.Metrics.Scale) * .5f), 22 * MaterialTheme.Metrics.Scale,
            icon == MaterialIcon.Heart ? new Vector4(1f, .42f, .46f, ink.W) : ink);
        MaterialText.AddText(dl, origin + new Vector2(iconWidth, 0), MaterialCanvas.Color(ink), translated);
        }
        finally { dl.PopClipRect(); }
        if (textSize.X + iconWidth + 2 * style.FramePadding.X > width && ImGui.IsItemHovered()) MaterialText.SetTooltip(translated);
        return clicked;
    }
    internal static bool SmallButton(string original) => Button(original);
    internal static bool Toggle(string original, ref bool value)
    {
        var scale = MaterialTheme.Metrics.Scale; var style = ImGui.GetStyle(); var colors = MaterialTheme.Current.Colors;
        var translated = UiText.T(Visible(original));
        var track = new Vector2(56, 30) * scale; var gap = 12 * scale;
        var width = MaterialLayout.FitNextItemWidth(0, ToggleWidth(original));
        var height = ChokePresentation.ControlHeight * scale;
        if (MaterialText.RequiresShaping(translated)) height = Math.Max(height, MaterialText.Measure(translated).Y + 2 * style.FramePadding.Y);
        using var invisible = new MaterialStyleScope();
        invisible.Color(ImGuiCol.FrameBg, Vector4.Zero); invisible.Color(ImGuiCol.FrameBgHovered, Vector4.Zero);
        invisible.Color(ImGuiCol.FrameBgActive, Vector4.Zero); invisible.Color(ImGuiCol.CheckMark, Vector4.Zero);
        invisible.Color(ImGuiCol.Text, Vector4.Zero); invisible.Color(ImGuiCol.Border, Vector4.Zero);
        invisible.Style(ImGuiStyleVar.FramePadding, new Vector2(style.FramePadding.X, Math.Max(0, (height - ImGui.GetTextLineHeight()) * .5f)));
        invisible.Style(ImGuiStyleVar.ItemInnerSpacing, new Vector2(width - height - MaterialText.Measure(Visible(original)).X, style.ItemInnerSpacing.Y));
        var changed = ImGui.Checkbox(original, ref value);
        var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax(); var draw = ImGui.GetWindowDrawList();
        ChokePresentation.Surface(min, max);
        if (ImGui.IsItemFocused()) draw.AddRect(min, max, MaterialCanvas.Color(colors.Primary), 4 * scale, ImDrawFlags.None, 2 * scale);
        var inset = Math.Max(8 * scale, (max.X - min.X - track.X - gap - MaterialText.Measure(translated).X) * .5f);
        var y = min.Y + (max.Y - min.Y - track.Y) * .5f;
        draw.AddRectFilled(new Vector2(min.X + inset, y), new Vector2(min.X + inset + track.X, y + track.Y), MaterialCanvas.Color(value ? colors.Primary : colors.SurfaceContainerHighest), track.Y * .5f);
        draw.AddCircleFilled(new Vector2(min.X + inset + (value ? track.X - track.Y * .5f : track.Y * .5f), y + track.Y * .5f), track.Y * .4f, MaterialCanvas.Color(value ? colors.OnPrimary : colors.OnSurfaceVariant), 24);
        draw.PushClipRect(min, max, true);
        try { MaterialText.AddText(draw, new Vector2(min.X + inset + track.X + gap, min.Y + (max.Y - min.Y - MaterialText.Measure(translated).Y) * .5f), MaterialCanvas.Color(colors.OnSurface), translated); }
        finally { draw.PopClipRect(); }
        return changed;
    }
    internal static bool Checkbox(string original, ref bool value)
    {
        var translated = UiText.T(Visible(original)); var gap = ImGui.GetStyle().ItemInnerSpacing;
        using var height = MaterialText.PushLineHeight(translated);
        var foreground = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        MaterialLayout.FitNextItemWidth(0, CheckboxWidth(original));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(gap.X + MaterialText.Measure(translated).X - MaterialText.Measure(Visible(original)).X, gap.Y));
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        var changed = ImGui.Checkbox(original, ref value);
        ImGui.PopStyleColor(); ImGui.PopStyleVar();
        MaterialText.AddText(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin() + new Vector2(ImGui.GetFrameHeight() + gap.X,
            (ImGui.GetFrameHeight() - MaterialText.Measure(translated).Y) * .5f), MaterialCanvas.Color(foreground), translated);
        return changed;
    }
    private static float FitField(string original, float? minimumPixels = null)
    {
        var requested = ImGui.CalcItemWidth();
        var minimum = minimumPixels ?? 80 * MaterialTheme.Metrics.Scale;
        var text = UiText.T(Visible(original));
        var labelWidth = text.Length == 0 ? 0 : MaterialText.Measure(text).X + ImGui.GetStyle().ItemInnerSpacing.X;
        var total = MaterialLayout.FitNextItemWidth(requested + labelWidth, minimum + labelWidth);
        var width = MathF.Ceiling(Math.Max(minimum, total - labelWidth));
        ImGui.SetNextItemWidth(width);
        return width;
    }
    private static bool ClipFieldLabel(string original, Vector2 min, float width, ImDrawListPtr draw)
    {
        if (UiText.T(Visible(original)) == Visible(original)) return false;
        var window = ImGui.GetWindowPos();
        draw.PushClipRect(new Vector2(min.X, window.Y), new Vector2(min.X + width, window.Y + ImGui.GetWindowSize().Y), true);
        return true;
    }
    private static void FieldLabel(string original, Vector2 min, float width, ImDrawListPtr draw, ImGuiWindowPtr window, Vector2 previousMax, bool nativeLabelHidden = false)
    {
        var visible = Visible(original); var translated = UiText.T(visible);
        if ((!nativeLabelHidden && translated == visible) || visible.Length == 0) return;
        var position = min + new Vector2(width + ImGui.GetStyle().ItemInnerSpacing.X,
            MaterialText.RequiresShaping(translated) ? (ImGui.GetFrameHeight() - MaterialText.Measure(translated).Y) * .5f : ImGui.GetStyle().FramePadding.Y);
        MaterialText.AddText(draw, position, ImGui.GetColorU32(ImGuiCol.Text), translated);
        // Retain the original widget ID and replace its English label's contribution to native layout.
        var right = position.X + MaterialText.Measure(translated).X;
        window.DC.CursorMaxPos = new Vector2(Math.Max(previousMax.X, right), window.DC.CursorMaxPos.Y);
        window.DC.CursorPosPrevLine = new Vector2(right, window.DC.CursorPosPrevLine.Y);
    }
    internal static bool InputText(string original, ref string value, int length)
    {
        var translated = UiText.T(Visible(original));
        using var height = MaterialText.PushLineHeight(value, translated);
        var width = FitField(original); var min = ImGui.GetCursorScreenPos(); var window = ImGuiP.GetCurrentWindow();
        var previousMax = window.DC.CursorMaxPos; var draw = ImGui.GetWindowDrawList();
        ImGuiP.PushOverrideID(ImGui.GetID(original));
        bool changed;
        try { changed = MaterialShapedInput.SingleLine("", "", ref value, length); }
        finally { ImGui.PopID(); }
        FieldLabel(original, min, width, draw, window, previousMax, nativeLabelHidden: true);
        return changed;
    }
    internal static bool InputInt(string original, ref int value)
    {
        using var height = MaterialText.PushLineHeight(UiText.T(Visible(original)));
        var minimum = Math.Max(80 * MaterialTheme.Metrics.Scale, MaterialText.Measure("-00000").X + 2 * ImGui.GetStyle().FramePadding.X);
        var width = FitField(original, minimum); var min = ImGui.GetCursorScreenPos(); var window = ImGuiP.GetCurrentWindow();
        var previousMax = window.DC.CursorMaxPos; var draw = ImGui.GetWindowDrawList();
        var clipped = ClipFieldLabel(original, min, width, draw);
        var changed = ImGui.InputInt(original, ref value);
        if (clipped) draw.PopClipRect();
        FieldLabel(original, min, width, draw, window, previousMax);
        return changed;
    }
    internal static bool Combo(string original, ref int selected, string[] options, int count)
    {
        var preview = selected >= 0 && selected < count ? UiText.T(options[selected]) : "";
        using var height = MaterialText.PushLineHeight(preview, UiText.T(Visible(original)));
        var width = FitField(original, Math.Max(80 * MaterialTheme.Metrics.Scale, MaterialText.Measure(preview).X + ImGui.GetFrameHeight() + 2 * ImGui.GetStyle().FramePadding.X)); var origin = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
        var window = ImGuiP.GetCurrentWindow(); var previousMax = window.DC.CursorMaxPos;
        var clipped = ClipFieldLabel(original, origin, width, draw);
        bool open;
        try { open = MaterialText.BeginCombo(original, preview); }
        finally { if (clipped) draw.PopClipRect(); }
        try
        {
            FieldLabel(original, origin, width, draw, window, previousMax);
            if (!open) return false;
            var changed = false;
            for (var index = 0; index < count; index++)
            {
                ImGui.PushID(index);
                try
                {
                    var raw = options[index]; var display = UiText.T(raw);
                    ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
                    bool clicked;
                    try { clicked = ImGui.Selectable(raw, selected == index, ImGuiSelectableFlags.None, new Vector2(Math.Max(ImGui.GetContentRegionAvail().X, MaterialText.Measure(display).X), Math.Max(ImGui.GetTextLineHeight(), MaterialText.Measure(display).Y))); }
                    finally { ImGui.PopStyleColor(); }
                    MaterialText.AddText(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin(), MaterialCanvas.Color(MaterialTheme.Current.Colors.OnSurface), display);
                    if (clicked) { selected = index; changed = true; }
                    if (selected == index) ImGui.SetItemDefaultFocus();
                }
                finally { ImGui.PopID(); }
            }
            return changed;
        }
        finally { if (open) ImGui.EndCombo(); }
    }
    internal static bool CollapsingHeader(string original)
    {
        var scale = MaterialTheme.Metrics.Scale;
        var height = (ChokePresentation.Compact ? 48 : 56) * scale;
        var translated = UiText.T(original);
        if (MaterialText.RequiresShaping(translated)) height = Math.Max(height, MaterialText.Measure(translated).Y + 2 * ImGui.GetStyle().FramePadding.Y);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12 * scale, Math.Max(0, (height - ImGui.GetTextLineHeight()) * .5f)));
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        var open = ImGui.CollapsingHeader(original);
        ImGui.PopStyleColor(); ImGui.PopStyleVar();
        var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax(); var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(min, max, true);
        try
        {
        ChokePresentation.Cart(min + new Vector2(16 * scale, (max.Y - min.Y - 32 * scale) * .5f), 32 * scale);
        MaterialText.AddText(draw, min + new Vector2(60 * scale, (max.Y - min.Y - MaterialText.Measure(translated).Y) * .5f), MaterialCanvas.Color(MaterialTheme.Current.Colors.OnSurface), translated);
        MaterialIcons.Draw(open ? MaterialIcon.ChevronDown : MaterialIcon.ArrowRight, new Vector2(max.X - 34 * scale, min.Y + (max.Y - min.Y - 22 * scale) * .5f), 22 * scale, MaterialTheme.Current.Colors.OnSurface);
        }
        finally { draw.PopClipRect(); }
        if (MaterialText.Measure(translated).X + 100 * scale > max.X - min.X && ImGui.IsItemHovered()) MaterialText.SetTooltip(translated);
        return open;
    }
    internal static void TableHeadersRow(Func<string, string>? label = null)
    {
        var height = 0f;
        for (var index = 0; index < ImGui.TableGetColumnCount(); index++)
        {
            var original = ImGui.TableGetColumnName(index);
            var translated = UiText.T(label?.Invoke(original) ?? original);
            if (MaterialText.RequiresShaping(translated)) height = Math.Max(height, MaterialText.Measure(translated).Y + 2 * ImGui.GetStyle().CellPadding.Y);
        }
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers, height);
        for (var index = 0; index < ImGui.TableGetColumnCount(); index++)
        {
            if (!ImGui.TableSetColumnIndex(index)) continue;
            var original = ImGui.TableGetColumnName(index); var position = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
            ImGui.TableHeader(original); var translated = UiText.T(label?.Invoke(original) ?? original);
            if (translated == original) continue;
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(position, position + new Vector2(width, Math.Max(MaterialText.Measure(translated).Y, ImGui.GetItemRectMax().Y - position.Y)), MaterialCanvas.Color(ImGui.GetStyle().Colors[(int)ImGuiCol.TableHeaderBg]));
            MaterialText.AddText(dl, position, MaterialCanvas.Color(MaterialTheme.Current.Colors.OnSurface), translated);
            if (MaterialText.Measure(translated).X > width && ImGui.IsItemHovered()) MaterialText.SetTooltip(translated);
        }
    }
    internal static void Title(string original, string translated)
        => TitleWithButtons(original, translated, null);

    internal static void PaintTitleWithImage(Window owner, string display)
    {
        var window = ImGuiP.FindWindowByName(owner.WindowName);
        if (window.IsNull) return;
        var extraRightWidth = AdditionalTitleButtonWidth(owner, ImGuiP.CalcFontSize(window));
        var texture = ChokePresentation.OriginalIcon;
        MaterialWindowHeader.PaintTitle(window, display, texture?.Handle ?? default,
            texture is null ? Vector2.Zero : new Vector2(texture.Width, texture.Height), extraRightWidth, owner.ShowCloseButton);
    }

    internal static void ReserveTitleSpace(Window owner, string visible, float minimumWidth)
    {
        var style = ImGui.GetStyle();
        var fontSize = ImGui.GetFontSize();
        var collapse = (owner.Flags & (ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.Modal)) == 0
            && style.WindowMenuButtonPosition != ImGuiDir.None;
        var controls = AdditionalTitleButtonWidth(owner, fontSize)
            + ((owner.ShowCloseButton ? 1 : 0) + (collapse ? 1 : 0)) * (fontSize + style.ItemInnerSpacing.X);
        var required = (MaterialText.Measure(visible).X + fontSize + style.ItemInnerSpacing.X
            + controls + style.FramePadding.X * 2 + style.ItemInnerSpacing.X)
            / ImGui.GetIO().FontGlobalScale;
        var bounds = owner.SizeConstraints ?? new WindowSizeConstraints();
        bounds.MinimumSize = new(Math.Max(minimumWidth, required), bounds.MinimumSize.Y);
        owner.SizeConstraints = bounds;
    }

    private static float AdditionalTitleButtonWidth(Window? owner, float fontSize)
    {
        if (owner is null) return 0;
        var count = owner.TitleBarButtons.Count(button => !owner.IsClickthrough || button.AvailableClickthrough);
        if (owner.AllowPinning || owner.AllowClickthrough || owner.AllowBackgroundBlur) count++;
        return count * (fontSize + ImGui.GetStyle().ItemInnerSpacing.X);
    }

    internal static void TitleWithButtons(string original, string translated, Window? owner)
    {
        if (original == translated) return;
        var style = ImGui.GetStyle();
        var flags = ImGuiP.GetCurrentWindow().Flags;
        var collapseLeft = (flags & (ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.Modal)) == 0 && style.WindowMenuButtonPosition == ImGuiDir.Left;
        var position = ImGui.GetWindowPos() + new Vector2(style.FramePadding.X + (collapseLeft ? ImGui.GetFontSize() + style.ItemInnerSpacing.X : 0), style.FramePadding.Y);
        var fontSize = ImGui.GetFontSize();
        var reserved = owner is null ? 2 * ImGui.GetFrameHeight()
            : style.FramePadding.X * 2 + (owner.ShowCloseButton ? fontSize : 0) + AdditionalTitleButtonWidth(owner, fontSize);
        if (owner is not null && (flags & ImGuiWindowFlags.NoCollapse) == 0 && style.WindowMenuButtonPosition == ImGuiDir.Right)
            reserved += fontSize + style.ItemInnerSpacing.X;
        var max = ImGui.GetWindowPos() + new Vector2(Math.Max(0, ImGui.GetWindowSize().X - reserved), ImGui.GetFrameHeight());
        var dl = ImGui.GetWindowDrawList(); dl.PushClipRect(position, max, false);
        try
        {
            dl.AddRectFilled(position, max, MaterialCanvas.Color(style.Colors[(int)(ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) ? ImGuiCol.TitleBgActive : ImGuiCol.TitleBg)]));
            MaterialText.AddText(dl, position, MaterialCanvas.Color(style.Colors[(int)ImGuiCol.Text]), translated);
        }
        finally { dl.PopClipRect(); }
    }
}
