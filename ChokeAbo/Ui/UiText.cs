using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using AethertekUI;

namespace ChokeAbo.Ui;

internal sealed class UiText : IDisposable
{
    [ThreadStatic] private static UiText? current;
    internal static UiText Current => current ?? throw new InvalidOperationException("Enter the ChokeAbo UI frame before drawing.");
    internal static readonly (string Code, string Name)[] Languages = [("en", "English"), ("de", "Deutsch"), ("fr", "Français"),
        ("es", "Español"), ("it", "Italiano"), ("ru", "Русский"), ("ja", "日本語"), ("ko", "한국어"), ("zh-Hans", "简体中文"),
        ("vi", "Tiếng Việt"), ("pt-BR", "Português (Brasil)"), ("id", "Bahasa Indonesia"), ("pl", "Polski"), ("tr", "Türkçe"), ("hi", "हिन्दी")];
    internal const string NativeSymbols = "♡⚫—…·+?♂♀";
    internal static IEnumerable<string> CjkLanguages(string selected) => new[] { "ja", "ko", "zh-Hans" }.OrderBy(code => code == selected ? 0 : 1);
    private readonly ResourceManager manager;
    internal ResourceSet Resources { get; }
    internal IReadOnlyList<string> RequiredText { get; }
    internal CultureInfo Culture { get; }
    internal string Language { get; }
    private readonly Func<UiFontRole, IDisposable> pushFont;
    private readonly (string Key, Lazy<Regex> Pattern, int[] Indices, string Prefix)[] messages;
    internal UiText(string language, Func<UiFontRole, IDisposable> pushFont)
    {
        Language = Languages.Any(l => l.Code == language) ? language : "en";
        Culture = CultureInfo.GetCultureInfo(Language);
        manager = new ResourceManager("ChokeAbo.Localization.Strings_" + Language.Replace('-', '_'), typeof(UiText).Assembly);
        Resources = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false) ?? throw new MissingManifestResourceException(Language);
        this.pushFont = pushFont;
        var english = new ResourceManager("ChokeAbo.Localization.Strings_en", typeof(UiText).Assembly);
        try
        {
            var fallback = english.GetResourceSet(CultureInfo.InvariantCulture, true, false) ?? throw new MissingManifestResourceException("en");
            RequiredText = Values(Resources).Concat(Values(fallback)).Concat(Languages.Select(l => l.Name)).Append(NativeSymbols).Append(Culture.NumberFormat.NumberGroupSeparator).Distinct().ToArray();
        }
        finally { english.ReleaseAllResources(); }
        var hole = new Regex(@"(?<!\{)\{(\d+)(?:,-?\d+)?(?::[^}]+)?\}(?!\})");
        messages = Resources.Cast<DictionaryEntry>().Select(entry => (string)entry.Key)
            .Where(key => hole.IsMatch(key) && hole.Replace(key, "").Any(char.IsLetter))
            .OrderByDescending(key => hole.Replace(key, "").Length).Select(key =>
            {
                var indices = new List<int>(); var pattern = "^"; var offset = 0;
                foreach (Match parameter in hole.Matches(key))
                {
                    pattern += Regex.Escape(key[offset..parameter.Index]) + "(.*?)";
                    indices.Add(int.Parse(parameter.Groups[1].Value, CultureInfo.InvariantCulture));
                    offset = parameter.Index + parameter.Length;
                }
                pattern += Regex.Escape(key[offset..]) + "$";
                return (key, new Lazy<Regex>(() => new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(20))),
                    indices.ToArray(), key[..hole.Match(key).Index]);
            }).ToArray();
    }
    internal static string T(string english) => Current.Label(english);
    internal string Label(string english) => Translate(english, 0);
    internal string Format(string english, params object?[] args) => string.Format(Culture, Label(english), args);
    private string Translate(string english, int depth)
    {
        english = english switch
        {
            "LegacyPreparingCovering" => "Preparing covering",
            "LegacyCovering" => "Covering in progress",
            "LegacyFledglingReady" => "Offspring ready",
            "LegacyRegistering" => "Registering offspring",
            "LegacyRegistered" => "Ready for racing",
            "Planning" => "Ready to plan the next action",
            "PurchasingSupplies" => "Purchasing supplies",
            _ => english,
        };
        if (Resources.GetString(english, true) is { } translated) return translated;
        foreach (var template in messages)
        {
            if (!english.StartsWith(template.Prefix, StringComparison.Ordinal)) continue;
            var match = template.Pattern.Value.Match(english);
            if (!match.Success) continue;
            var arguments = new object[template.Indices.Max() + 1];
            Array.Fill(arguments, string.Empty);
            for (var index = 0; index < template.Indices.Length; index++)
            {
                var argumentIndex = template.Indices[index];
                var value = match.Groups[index + 1].Value;
                var supplyFallback = template.Key == "Buying {0} for {1} {2} within reserves; awaiting its inventory result."
                    && argumentIndex == 0 && value == "Breeding supply";
                arguments[argumentIndex] = depth < 5 && (AuthoredArgument(template.Key, argumentIndex) || supplyFallback) ? Translate(value, depth + 1) : value;
            }
            // Captured service values have already been formatted. Keep names, IDs and leading zeroes raw.
            return string.Format(Culture, Resources.GetString(template.Key, false)!, arguments);
        }
        return english;
    }
    // Only documented status/sex fields contain authored text. Item names, IDs, timestamps and captured values stay raw.
    private static bool AuthoredArgument(string key, int index) => key switch
    {
        "Pass {0}: cleanup/retry in {1:0.0}s. {2}" or "Pass {0}: waiting to verify caps in {1:0.0}s. {2}"
            or "Pass {0}: Chocobo stat refresh failed; retrying refresh in {1:0.0}s. {2}" => index == 2,
        "Pass {0}: refreshing Chocobo stats. {1}" or "Trainer feed flow stayed unstable after {0} recovery attempts. Last issue: {1}" => index == 1,
        "Purchase pass failed: {0}" or "Feeding pass failed: {0}" or "Chocobo stat refresh failed: {0}"
            or "{0}; waiting to verify caps" or "Target feeding purchase: {0}" or "Target feeding: {0}"
            or "Current racer data could not be loaded: {0}" or "Target feed purchase blocked: {0}" or "Target feeding blocked: {0}"
            or "{0} Native UI selection remains unverified (capture reference: {1})." or "Required supply purchase stopped: {0}"
            or "{0} Explicit Resume is required." or "{0} Feeding policy is Stop; explicit Resume is required."
            or "Skipped this feeding round: {0}" or "Buy a G1 {0} registration form to raise a missing owned parent." => index == 0,
        "G{0} {1}: racing rank {2}/40 before retirement." or "Register G{0} {1}."
            or "The active G{0} {1} duplicates an owned usable parent and no productive pair is available."
            or "Race the useful G{0} {1} candidate to rank {2}." or "Retire the active G{0} {1} racer exactly."
            or "Register the exact useful G{0} {1} fledgling." => index == 1,
        _ => false,
    };
    internal static string F(FormattableString text) => F(text.Format, text.GetArguments());
    internal static string F(string english, params object?[] args) => string.Format(Current.Culture, T(english), args);
    internal static string Interpolated(FormattableString text) => F(text.Format, text.GetArguments());
    internal static IDisposable Font(UiFontRole role) => Current.pushFont(role);
    internal Scope Enter() => new(this);
    internal readonly struct Scope : IDisposable
    {
        private readonly UiText? previous;
        internal Scope(UiText value) { previous = current; current = value; }
        public void Dispose() => current = previous;
    }
    internal ushort[] GlyphRanges()
    {
        var chars = RequiredText.Select(MaterialText.NativeGlyphText).SelectMany(t => t).Where(c => !char.IsControl(c))
            .Concat(Enumerable.Range(0x20, 0x024F - 0x20 + 1).Select(i => (char)i))
            .Concat(Enumerable.Range(0x0400, 0x052F - 0x0400 + 1).Select(i => (char)i)).Distinct().Order().ToArray();
        var result = new List<ushort>();
        for (var index = 0; index < chars.Length; index++)
        {
            var first = chars[index]; var last = first;
            while (index + 1 < chars.Length && chars[index + 1] == last + 1) last = chars[++index];
            result.Add(first); result.Add(last);
        }
        result.Add(0); return result.ToArray();
    }
    private static IEnumerable<string> Values(ResourceSet set) => set.Cast<DictionaryEntry>().Select(e => (string)e.Value!);
    public void Dispose() => manager.ReleaseAllResources();
}
