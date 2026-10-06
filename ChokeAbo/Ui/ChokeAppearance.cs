using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using AethertekUI;
using AethertekUI.Dalamud;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace ChokeAbo.Ui;

internal sealed class ChokeAppearance : IDisposable
{
    private readonly Plugin plugin;
    private readonly MaterialTextHost shapedText;
    private UiText text;
    private ChokeFonts fonts;
    private MaterialTheme theme;
    private readonly MaterialWindowFold fontStatusMotion = new();
    private readonly MaterialWindowDecorations fontStatusDecorations = new();
    private readonly Dictionary<string, MaterialWindowOpacity> windowOpacities = new();
    private readonly MaterialOptions<string> languages = new(UiText.Languages.Select(l => new MaterialOption<string>(l.Code, l.Code, l.Name)).ToArray());
    private string appliedLanguage = "";
    private uint appliedAccent;
    private Vector3 accentDraft;
    private int checkedGeneration = -1;
    private bool fontIssueLogged;

    private void Apply()
    {
        var language = UiText.Languages.Any(l => l.Code == plugin.Configuration.UiLanguage) ? plugin.Configuration.UiLanguage : "en";
        if (language != appliedLanguage)
        {
            fonts?.Dispose();
            text?.Dispose();
            text = new(language, PushFont);
            fonts = new(Plugin.PluginInterface.UiBuilder.FontAtlas, text.GlyphRanges(), language);
            appliedLanguage = language;
            checkedGeneration = -1;
            fontIssueLogged = false;
        }
        if (theme is null || appliedAccent != (plugin.Configuration.UiAccentRgb & 0xFFFFFF))
        {
            appliedAccent = plugin.Configuration.UiAccentRgb & 0xFFFFFF;
            theme = ChokePresentation.Theme(appliedAccent);
            var rgb = ChokePresentation.Rgb(appliedAccent);
            accentDraft = new(rgb.X, rgb.Y, rgb.Z);
        }
        theme.Density = plugin.Configuration.UiCompact ? MaterialDensity.Compact : MaterialDensity.Standard;
    }

    internal void Draw(WindowSystem windows)
    {
        Apply();
        if (!windows.Windows.Any(window => window.IsOpen)) return;
        using var resources = text.Enter();
        using var shaping = shapedText.Push();
        if (fonts.Ready && checkedGeneration != fonts.Generation)
        {
            try
            {
                var generation = fonts.Generation;
                foreach (var role in Enum.GetValues<UiFontRole>())
                    shapedText.Renderer.CheckGlyphs(text.RequiredText, ChokePresentation.AtlasHeight(role) * ImGuiHelpers.GlobalScale);
                fonts.CheckGlyphs(text.RequiredText.Select(MaterialText.NativeGlyphText));
                checkedGeneration = generation;
            }
            catch (Exception ex)
            {
                if (!fontIssueLogged) { Plugin.Log.Error(ex, "[ChokeAbo] Required UI glyph coverage failed."); fontIssueLogged = true; }
            }
        }
        using var palette = MaterialTheme.Push(theme, ImGuiHelpers.GlobalScale, MaterialStyleMode.ColorsOnly);
        using var chrome = MaterialWindowChrome.Push();
        if (!fonts.Ready || checkedGeneration != fonts.Generation)
        {
            if (!fontIssueLogged && fonts.LoadException is { } error) { Plugin.Log.Error(error, "[ChokeAbo] Required UI fonts failed to load."); fontIssueLogged = true; }
            ImGui.SetNextWindowSize(new Vector2(460 * ImGuiHelpers.GlobalScale, 0));
            fontStatusMotion.PreDraw("Choke-abo##FontStatus", null, null, reducedMotion: false, prepareDecorations: fontStatusDecorations.Prepare);
            if (ImGui.Begin("Choke-abo##FontStatus", ImGuiWindowFlags.AlwaysAutoResize))
            {
                fontStatusDecorations.Paint();
                MaterialText.TextWrapped(UiText.T(fonts.LoadException is null && !fontIssueLogged ? "Loading UI fonts..." : "UI fonts failed to load. See the plugin log."));
            }
            ImGui.End();
            fontStatusDecorations.Paint();
            fontStatusMotion.PostDraw();
            ApplyWindowOpacity("Choke-abo##FontStatus");
            return;
        }
        using var style = new MaterialStyleScope();
        var s = ImGuiHelpers.GlobalScale;
        style.Style(ImGuiStyleVar.WindowPadding, new Vector2(plugin.Configuration.UiCompact ? 12 : 20) * s);
        style.Style(ImGuiStyleVar.ItemSpacing, new Vector2(plugin.Configuration.UiCompact ? 8 : 12, plugin.Configuration.UiCompact ? 5 : 10) * s);
        style.Style(ImGuiStyleVar.FramePadding, new Vector2(plugin.Configuration.UiCompact ? 10 : 14, plugin.Configuration.UiCompact ? 4 : 7) * s);
        style.Style(ImGuiStyleVar.CellPadding, new Vector2(plugin.Configuration.UiCompact ? 6 : 10, plugin.Configuration.UiCompact ? 4 : 8) * s);
        style.Style(ImGuiStyleVar.FrameRounding, 4 * s);
        style.Style(ImGuiStyleVar.ChildRounding, 4 * s);
        using var body = fonts.Push(UiFontRole.Body);
        windows.Draw();
        foreach (var window in windows.Windows)
            if (window.IsOpen) ApplyWindowOpacity(window.WindowName);
    }

    internal void DrawSelector()
    {
        var language = appliedLanguage;
        using var controls = MaterialControls.Push(ChokePresentation.Controls());
        var changed = MaterialAppearanceSelector.Draw("appearance", ref accentDraft, ref language, languages,
            new(UiText.T("Color"), UiText.T("Language"), UiText.T("Teal"), UiText.T("Blue"), UiText.T("Pink"), UiText.T("Custom RGB")), 140);
        if (changed.AccentChanged)
            plugin.Configuration.UiAccentRgb = ((uint)Math.Clamp((int)MathF.Round(accentDraft.X * 255), 0, 255) << 16)
                | ((uint)Math.Clamp((int)MathF.Round(accentDraft.Y * 255), 0, 255) << 8) | (uint)Math.Clamp((int)MathF.Round(accentDraft.Z * 255), 0, 255);
        if (changed.LanguageChanged) plugin.Configuration.UiLanguage = language;
        if (changed.AccentChanged || changed.LanguageChanged) plugin.Configuration.Save();
    }

    internal float LanguageWidth()
    {
        var controls = ChokePresentation.Controls();
        return Math.Max(140 * MaterialTheme.Metrics.Scale, MathF.Ceiling(MaterialText.Measure(languages.LabelFor(appliedLanguage)).X
            + controls.Height + 3 * controls.Gap + Math.Min(controls.IconSize, controls.Height)));
    }

    internal void DrawAccentSelector()
    {
        using var controls = MaterialControls.Push(ChokePresentation.Controls());
        if (!MaterialAppearanceSelector.DrawAccent("appearance", ref accentDraft,
            new(UiText.T("Color"), UiText.T("Language"), UiText.T("Teal"), UiText.T("Blue"), UiText.T("Pink"), UiText.T("Custom RGB")))) return;
        plugin.Configuration.UiAccentRgb = ((uint)Math.Clamp((int)MathF.Round(accentDraft.X * 255), 0, 255) << 16)
            | ((uint)Math.Clamp((int)MathF.Round(accentDraft.Y * 255), 0, 255) << 8) | (uint)Math.Clamp((int)MathF.Round(accentDraft.Z * 255), 0, 255);
        plugin.Configuration.Save();
    }

    internal void DrawLanguageSelector()
    {
        var language = appliedLanguage;
        using var controls = MaterialControls.Push(ChokePresentation.Controls());
        if (!MaterialAppearanceSelector.DrawLanguage("appearance", ref language, languages, 140)) return;
        plugin.Configuration.UiLanguage = language;
        plugin.Configuration.Save();
    }

    internal ChokeAppearance(Plugin plugin)
    {
        this.plugin = plugin;
        shapedText = new(Plugin.TextureProvider);
        appliedLanguage = UiText.Languages.Any(l => l.Code == plugin.Configuration.UiLanguage) ? plugin.Configuration.UiLanguage : "en";
        text = new(appliedLanguage, PushFont);
        fonts = new(Plugin.PluginInterface.UiBuilder.FontAtlas, text.GlyphRanges(), appliedLanguage);
        appliedAccent = plugin.Configuration.UiAccentRgb & 0xFFFFFF;
        theme = ChokePresentation.Theme(appliedAccent);
        var rgb = ChokePresentation.Rgb(appliedAccent);
        accentDraft = new(rgb.X, rgb.Y, rgb.Z);
    }
    private IDisposable PushFont(UiFontRole role) => fonts.Push(role);
    // These two callers render game-owned DTR text, outside the ImGui shaping host.
    internal string Label(string english) => text.Language == "hi" ? english : text.Label(english);
    internal string Format(string english, params object?[] args) => text.Language == "hi"
        ? string.Format(System.Globalization.CultureInfo.InvariantCulture, english, args) : text.Format(english, args);

    public void Dispose() { fonts?.Dispose(); text?.Dispose(); shapedText.Dispose(); }

    private void ApplyWindowOpacity(string windowName)
    {
        if (!windowOpacities.TryGetValue(windowName, out var opacity))
            windowOpacities.Add(windowName, opacity = new MaterialWindowOpacity());
        opacity.Apply(windowName, plugin.Configuration.UiWindowOpacityPercent / 100f,
            plugin.Configuration.UiTransparencyEnabled, plugin.Configuration.UiAutoFade,
            plugin.Configuration.UiFadedOpacityPercent / 100f, plugin.Configuration.UiUnfocusedDelaySeconds);
    }

    internal void DrawTransparencyToggle()
    {
        var enabled = plugin.Configuration.UiTransparencyEnabled;
        if (UiGui.Checkbox("Transparency##MainWindow", ref enabled))
        { plugin.Configuration.UiTransparencyEnabled = enabled; plugin.Configuration.Save(); }
    }

    internal void DrawWindowAppearanceSettings()
    {
        if (!MaterialText.CollapsingHeader(UiText.T("Window appearance") + "###UiWindowAppearance")) return;
        var compact = plugin.Configuration.UiCompact;
        if (UiGui.Checkbox("Compact mode", ref compact))
        { plugin.Configuration.UiCompact = compact; plugin.Configuration.Save(); }
        DrawSelector();
        var compactVisible = plugin.Configuration.UiCompactVisibleOnMainWindow;
        if (UiGui.Checkbox("Compact visible on main window", ref compactVisible))
        { plugin.Configuration.UiCompactVisibleOnMainWindow = compactVisible; plugin.Configuration.Save(); }
        var languageVisible = plugin.Configuration.UiLanguageVisibleOnMainWindow;
        if (UiGui.Checkbox("Language visible on main window", ref languageVisible))
        { plugin.Configuration.UiLanguageVisibleOnMainWindow = languageVisible; plugin.Configuration.Save(); }
        var enabled = plugin.Configuration.UiTransparencyEnabled;
        if (UiGui.Checkbox("Transparency", ref enabled))
        { plugin.Configuration.UiTransparencyEnabled = enabled; plugin.Configuration.Save(); }
        MaterialText.Text(UiText.T("Opacity (%)"));
        ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(160 * MaterialTheme.Metrics.Scale, 80 * MaterialTheme.Metrics.Scale));
        var normalOpacity = plugin.Configuration.UiWindowOpacityPercent;
        if (ImGui.InputInt("##UiWindowOpacityPercent", ref normalOpacity))
        { plugin.Configuration.UiWindowOpacityPercent = normalOpacity; plugin.Configuration.Save(); }
        var autoFade = plugin.Configuration.UiAutoFade;
        if (UiGui.Checkbox("Auto-fade when unfocused", ref autoFade))
        { plugin.Configuration.UiAutoFade = autoFade; plugin.Configuration.Save(); }
        ImGui.BeginDisabled(!autoFade);
        MaterialText.Text(UiText.T("Unfocused opacity (%)"));
        ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(160 * MaterialTheme.Metrics.Scale, 80 * MaterialTheme.Metrics.Scale));
        var fadedOpacity = plugin.Configuration.UiFadedOpacityPercent;
        if (ImGui.InputInt("##UiFadedOpacityPercent", ref fadedOpacity))
        { plugin.Configuration.UiFadedOpacityPercent = fadedOpacity; plugin.Configuration.Save(); }
        MaterialText.Text(UiText.T("Unfocused delay (seconds)"));
        ImGui.SetNextItemWidth(MaterialLayout.FitNextItemWidth(160 * MaterialTheme.Metrics.Scale, 80 * MaterialTheme.Metrics.Scale));
        var delay = plugin.Configuration.UiUnfocusedDelaySeconds;
        if (ImGui.InputInt("##UiUnfocusedDelaySeconds", ref delay))
        { plugin.Configuration.UiUnfocusedDelaySeconds = delay; plugin.Configuration.Save(); }
        ImGui.EndDisabled();
    }
}
