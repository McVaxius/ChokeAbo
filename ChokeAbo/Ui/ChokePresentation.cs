using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Numerics;
using AethertekUI;
using Dalamud.Bindings.ImGui;

namespace ChokeAbo.Ui;

internal enum UiFontRole { Body, BodyStrong, Title, PaneHeading, CompactTitle, CompactPaneHeading }

internal static class ChokePresentation
{
    // Approved regular PNG: main (22,84)-(1141,960), header88; progression (41,262)-(1122,655), stock (41,672)-(1122,857).
    // Compact PNG: main (51,126)-(1106,861), header69; progression (67,273)-(1091,604), stock (67,619)-(1091,773).
    // Preserve native window chrome and additional real workflow detail. Typography: title30/28, pane22/20, body16.
    internal const uint ReferenceAccent = 0xFFBC41;
    internal static readonly float[] FontSizes = [16, 16, 30, 22, 28, 20];
    internal static readonly string[] FontFiles = ["segoeui.ttf", "seguisb.ttf", "seguisb.ttf", "seguisb.ttf", "seguisb.ttf", "seguisb.ttf"];
    internal static float AtlasHeight(UiFontRole role) => FontSizes[(int)role] * 4 / 3;
    internal static bool Compact => MaterialTheme.Current.Density == MaterialDensity.Compact;
    internal static float HeaderHeight => Compact ? 68 : 82;
    internal static float Gap => Compact ? 10 : 15;
    internal static float ControlHeight => Compact ? 48 : 52;
    internal static Vector4 Rgb(uint rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);

    internal static MaterialTheme Theme(uint accent)
    {
        accent &= 0xFFFFFF;
        var selected = Rgb(accent);
        var reference = Rgb(ReferenceAccent);
        var seed = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new(selected.X, selected.Y, selected.Z)));
        var original = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new(reference.X, reference.Y, reference.Z)));
        var hue = seed.Y < .001f ? 0 : seed.Z - original.Z;
        var chroma = seed.Y < .001f ? 0 : seed.Y / original.Y;
        Vector4 Relative(uint rgb)
        {
            var color = Rgb(rgb);
            if (accent == ReferenceAccent) return color;
            var lch = MaterialColor.LabToLch(MaterialColor.SrgbToOklab(new(color.X, color.Y, color.Z)));
            return new(MaterialColor.GamutMap(lch.X, lch.Y * chroma, lch.Z + hue), 1);
        }
        var background = Relative(0x151F26);
        var foreground = Relative(0xF4F6F8);
        var primary = Relative(ReferenceAccent);
        var palette = new OklchPaletteGenerator().Generate(new(selected.X, selected.Y, selected.Z));
        var colors = new MaterialColorScheme(palette)
        {
            Background = background, OnBackground = foreground,
            Surface = Relative(0x19232B), OnSurface = foreground,
            SurfaceContainerLowest = Relative(0x131D25), SurfaceContainerLow = Relative(0x19232B),
            SurfaceContainer = Relative(0x1C262E), SurfaceContainerHigh = Relative(0x242E37), SurfaceContainerHighest = Relative(0x242E37),
            SurfaceVariant = Relative(0x303C46), OnSurfaceVariant = Relative(0xBDC7D1),
            Outline = Relative(0x71808F), OutlineVariant = Relative(0x303942),
            Primary = primary, OnPrimary = MaterialColor.Contrast(primary, background) >= MaterialColor.Contrast(primary, foreground) ? background : foreground,
            PrimaryContainer = Relative(0x493B35), OnPrimaryContainer = foreground,
            Secondary = Relative(0xEEC2A8), OnSecondary = background, SecondaryContainer = Relative(0x303C46), OnSecondaryContainer = foreground,
            Tertiary = Relative(0xC5CBD1), OnTertiary = background, TertiaryContainer = Relative(0x35424D), OnTertiaryContainer = foreground,
            InverseSurface = foreground, InverseOnSurface = background, InversePrimary = Relative(0x926348),
        };
        return new(colors, MaterialDensity.Standard) { SurfaceOpacity = 1 };
    }

    internal static MaterialControlMetrics Controls(float height = 0)
    {
        if (height <= 0) height = ControlHeight;
        var s = MaterialTheme.Metrics.Scale;
        return new() { Height = height * s, Padding = new(12 * s, Math.Max(0, (height * s - ImGui.GetTextLineHeight()) * .5f)),
            Gap = 8 * s, IconSize = 22 * s, Rounding = 4 * s, ItemSpacing = new(10 * s, 6 * s), CellPadding = new(12 * s, 6 * s) };
    }

    internal static void Surface(Vector2 min, Vector2 max)
    {
        var c = MaterialTheme.Current.Colors;
        MaterialCanvas.Surface(min, max, c.SurfaceContainerHigh, c.Surface, 4 * MaterialTheme.Metrics.Scale);
        ImGui.GetWindowDrawList().AddRect(min, max, MaterialCanvas.Color(c.OutlineVariant), 4 * MaterialTheme.Metrics.Scale);
    }

    internal static void Brand(Vector2 origin, float size)
    {
        var dl = ImGui.GetWindowDrawList(); var ink = MaterialCanvas.Color(MaterialTheme.Current.Colors.Primary);
        Vector2 P(float x,float y) => origin + new Vector2(x,y)*size;
        for (var index = 0; index < 24; index++)
        {
            var angle = index * MathF.Tau / 24;
            dl.PathLineTo(P(.45f + MathF.Cos(angle) * .26f, .57f + MathF.Sin(angle) * .28f));
        }
        dl.PathFillConvex(ink);
        dl.AddCircleFilled(P(.46f,.25f),size*.16f,ink,24);
        dl.AddTriangleFilled(P(.34f,.17f),P(.50f,.02f),P(.47f,.21f),ink);
        dl.AddTriangleFilled(P(.48f,.13f),P(.62f,.05f),P(.55f,.24f),ink);
        dl.AddTriangleFilled(P(.33f,.22f),P(.13f,.32f),P(.33f,.35f),ink);
        dl.AddTriangleFilled(P(.62f,.51f),P(.92f,.25f),P(.80f,.62f),ink);
        dl.AddTriangleFilled(P(.57f,.59f),P(.88f,.52f),P(.67f,.78f),ink);
        dl.AddLine(P(.37f,.76f),P(.33f,.93f),ink,Math.Max(1,size*.06f));
        dl.AddLine(P(.56f,.76f),P(.59f,.93f),ink,Math.Max(1,size*.06f));
        dl.AddLine(P(.26f,.93f),P(.40f,.93f),ink,Math.Max(1,size*.06f));
        dl.AddLine(P(.51f,.93f),P(.68f,.93f),ink,Math.Max(1,size*.06f));
        dl.AddCircleFilled(P(.40f,.24f),size*.025f,MaterialCanvas.Color(MaterialTheme.Current.Colors.OnPrimary),12);
    }

    internal static void Cart(Vector2 origin, float size)
    {
        var draw = ImGui.GetWindowDrawList(); var ink = MaterialCanvas.Color(MaterialTheme.Current.Colors.Primary);
        Vector2 P(float x, float y) => origin + new Vector2(x, y) * size;
        var stroke = Math.Max(1, size * .08f);
        draw.AddLine(P(.05f,.1f), P(.2f,.1f), ink, stroke);
        draw.AddLine(P(.2f,.1f), P(.35f,.7f), ink, stroke);
        draw.AddLine(P(.35f,.7f), P(.86f,.7f), ink, stroke);
        draw.PathLineTo(P(.28f,.27f)); draw.PathLineTo(P(.96f,.27f));
        draw.PathLineTo(P(.83f,.57f)); draw.PathLineTo(P(.36f,.57f)); draw.PathFillConvex(ink);
        draw.AddCircleFilled(P(.4f,.88f), size * .09f, ink, 16);
        draw.AddCircleFilled(P(.8f,.88f), size * .09f, ink, 16);
    }
}
