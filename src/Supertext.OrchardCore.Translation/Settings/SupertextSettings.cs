namespace Supertext.OrchardCore.Translation.Settings;

/// <summary>Site settings (Settings → Supertext). Stored in the site document.</summary>
public sealed class SupertextSettings
{
    /// <summary>API key, encrypted with ASP.NET Core data protection.</summary>
    public string ProtectedApiKey { get; set; }

    /// <summary>Empty = https://api.supertext.com/v1/.</summary>
    public string Endpoint { get; set; }

    /// <summary>"default", "more" (formal) or "less" (informal).</summary>
    public string Politeness { get; set; } = "default";

    /// <summary>Translate the copy when an editor creates a localization with Orchard Core's own culture menu.</summary>
    public bool TranslateNewLocalizations { get; set; } = true;

    /// <summary>
    /// Language mapping: one "culture=supertext-code" per line, e.g. "de=de-CH". A culture that isn't
    /// listed uses its exact Supertext code (de-CH, es), else the SDK default (de → de-DE); otherwise it can't be translated.
    /// </summary>
    public string LanguageMapping { get; set; }

    /// <summary>
    /// Cultures with <b>Translation review</b> on: translations into them go through Supertext's review
    /// service (<c>SUPERTEXT_REVIEW_URL</c>, default <see cref="Api.SupertextClient.ReviewUrl"/>). Null or
    /// empty (older installs) = review off everywhere.
    /// </summary>
    public List<string> ReviewCultures { get; set; }

    /// <summary>One "Part.Field", "Part" or "ContentType.Part.Field" per line.</summary>
    public string ExcludedFields { get; set; }

    public int PollTimeoutSeconds { get; set; } = 300;

    /// <summary>"Send version information to Supertext" (on by default; SUPERTEXT_VERSION_REPORTS=0 overrides it).</summary>
    public bool SendVersionInformation { get; set; } = true;
}

/// <summary>
/// The Commando Center reporter's state (what was reported last, the latest API key check), stored
/// in the site document next to <see cref="SupertextSettings"/>: Supertext Plugin Health's <c>ReporterState</c> as JSON (same format as the SDK 0.2 reporter stored).
/// Never contains the API key (only a SHA-256 fingerprint to notice a changed key).
/// </summary>
public sealed class SupertextReportingState
{
    public string Reporter { get; set; }
}
