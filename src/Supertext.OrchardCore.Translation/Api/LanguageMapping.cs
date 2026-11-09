namespace Supertext.OrchardCore.Translation.Api;

/// <summary>
/// Orchard Core cultures → Supertext language codes, by the Supertext SDK's rules (<c>Languages</c>,
/// the same in every Supertext plugin): the <b>Language mapping</b> setting (a select per culture,
/// stored as <c>culture=code</c> per line) wins; else a culture that equals a Supertext code apart
/// from case and <c>_</c>/<c>-</c> (<c>de-CH</c>, <c>es</c>) is used as it is; else the SDK's default
/// where one is obvious (<c>de</c> → <c>de-DE</c>, <c>en</c> → <c>en-US</c>, <c>zh-TW</c> → <c>zh-Hant</c>,
/// <c>es-ES</c> → <c>es</c>); anything else (<c>en-IE</c>, <c>de-LI</c>, <c>gsw</c>) is the error
/// <c>unsupported_language</c> and nothing is sent. One mapping serves both directions:
/// as the source, a mapped code is sent in its source form (<c>de-CH</c> → <c>de</c>).
/// No Orchard Core dependency (unit-tested).
/// </summary>
public static class LanguageMapping
{
    /// <summary>Target languages Supertext offers (code, English name, whether formal/informal is offered).</summary>
    public static IReadOnlyList<Sdk.V1.TargetLanguage> Targets => Sdk.V1.Languages.Targets;

    /// <summary>Supertext's targets sorted by English name, for the mapping selects.</summary>
    public static IReadOnlyList<Sdk.V1.TargetLanguage> TargetOptions => Sdk.V1.Languages.TargetOptions();

    /// <summary>What the settings page preselects for a culture without a stored code: the exact match, else the SDK default ("de" → "de-DE"), else null ("en-IE").</summary>
    public static string SuggestTarget(string culture) => Sdk.V1.Languages.SuggestTarget(culture);

    /// <summary>Whether a code is one of Supertext's targets (apart from case and <c>_</c>/<c>-</c>).</summary>
    public static bool IsSupportedTarget(string code) => Sdk.V1.Languages.IsSupportedTarget(code);

    /// <summary>
    /// The stored mapping brought up to date (run on every read, persisted by the next save): a code
    /// that isn't one of Supertext's targets but has an obvious one is replaced by it ("es-ES" → "es",
    /// "de_ch" → "de-CH", "de" → "de-DE"); any other code is kept as it is, so nothing configured is lost.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Migrate(IReadOnlyDictionary<string, string> mapping)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (culture, code) in mapping)
        {
            result[culture] = IsSupportedTarget(code) ? MatchTarget(code) : SuggestTarget(code) ?? code;
        }
        return result;
    }

    /// <summary>The stored text (<c>culture=code</c> per line) as a dictionary; blank and malformed lines are skipped, the first entry per culture wins.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var pair = line.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim() != string.Empty && pair[1].Trim() != string.Empty)
            {
                result.TryAdd(pair[0].Trim(), pair[1].Trim());
            }
        }
        return result;
    }

    /// <summary>The setting's text for a mapping: one <c>culture=code</c> per line, blank codes left out.</summary>
    public static string Serialize(IEnumerable<KeyValuePair<string, string>> mapping)
        => string.Join("\n", mapping
            .Where(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => p.Key.Trim() + "=" + p.Value.Trim()));

    /// <summary>The Supertext target code for a culture.</summary>
    /// <exception cref="SupertextException">Template for <c>unsupported_language</c> when the culture is neither mapped, nor a Supertext code, nor has a default.</exception>
    public static string Target(string culture, IReadOnlyDictionary<string, string> mapping)
        => Resolve(() => Sdk.V1.Languages.Target(culture, mapping));

    /// <summary>The Supertext source code for a culture ("en-US" → "en"); empty stays empty (Supertext detects the language).</summary>
    /// <exception cref="SupertextException">Template for <c>unsupported_language</c>.</exception>
    public static string Source(string culture, IReadOnlyDictionary<string, string> mapping)
        => Resolve(() => Sdk.V1.Languages.Source(culture, mapping));

    /// <summary>The target code, or null when the culture has none yet (for the settings page).</summary>
    public static string TargetOrNull(string culture, IReadOnlyDictionary<string, string> mapping)
    {
        try
        {
            return Sdk.V1.Languages.Target(culture, mapping);
        }
        catch (Sdk.V1.SupertextException)
        {
            return null;
        }
    }

    /// <summary>The Supertext target that equals the culture apart from case and <c>_</c>/<c>-</c> ("de_ch" → "de-CH"; "de" → null, its default is <see cref="SuggestTarget"/>).</summary>
    public static string MatchTarget(string culture) => Sdk.V1.Languages.MatchTarget(culture);

    /// <summary>Whether Supertext offers formal/informal for a target code; null for a code that isn't in the SDK's list.</summary>
    public static bool? SupportsPoliteness(string target) => Sdk.V1.Languages.SupportsPoliteness(target);

    /// <summary>
    /// The form of address to send for a target: the setting, except "default" for a target Supertext
    /// offers no formal/informal choice for (English, Swiss German, Romansh, Albanian, Serbian), which it
    /// would reject. The settings page says so next to those languages. Unknown (mapped) codes get the setting.
    /// </summary>
    public static string PolitenessFor(string target, string politeness)
        => politeness is "more" or "less" && SupportsPoliteness(target) != false ? politeness : "default";

    private static string Resolve(Func<string> resolve)
    {
        try
        {
            return resolve();
        }
        catch (Sdk.V1.SupertextException e)
        {
            throw SupertextClient.FromSdk(e);
        }
    }
}
