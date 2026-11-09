using System.Text.RegularExpressions;

namespace Supertext.OrchardCore.Translation.Api;

/// <summary>
/// An error shown to editors. <see cref="Template"/> is the English text with <c>{0}</c>
/// placeholders for <see cref="Args"/>; <c>SupertextMessages</c> shows it in the user's admin
/// language (msgids of <c>Localization/*.po</c>). <see cref="Exception.Message"/> is the
/// English text with the arguments and the untranslated <see cref="Detail"/> from Supertext (logs).
/// </summary>
public sealed class SupertextException(string template, object[] args = null, string detail = null, Exception inner = null, string code = null, int? status = null)
    : Exception(Format(template, args, detail), inner)
{
    public string Template { get; } = template;

    /// <summary>The SDK's code for a rejected API key (HTTP 401/403).</summary>
    public const string AuthenticationFailure = "authentication_failure";

    /// <summary>The SDK's error code (<c>authentication_failure</c>, <c>unsupported_language</c>, …), null for the module's own errors.</summary>
    public string Code { get; } = code;

    /// <summary>The HTTP status Supertext answered with, if any.</summary>
    public int? Status { get; } = status;

    public object[] Args { get; } = args ?? [];

    /// <summary>Text returned by Supertext or the network stack (not translated).</summary>
    public string Detail { get; } = string.IsNullOrEmpty(detail) ? null : detail;

    private static string Format(string template, object[] args, string detail)
    {
        var text = args is { Length: > 0 } ? string.Format(System.Globalization.CultureInfo.InvariantCulture, template, args) : template;
        return string.IsNullOrEmpty(detail) ? text : text + " (" + detail + ")";
    }
}

/// <summary>Where and how to reach Supertext (resolved from site settings and environment).</summary>
/// <param name="ReviewUrl">Supertext's review service for cultures with <b>Translation review</b> on; empty = review off everywhere.</param>
public sealed record SupertextConnection(string Endpoint, string ApiKey, int PollIntervalSeconds = 2, int PollTimeoutSeconds = 300, string ReviewUrl = "")
{
    /// <summary>
    /// The connection for a target culture: with <paramref name="review"/> on, the same key, timeouts and
    /// HTTP client against the review service (a second SDK client, which also collects the job it
    /// submitted); else this one. Without a review URL, or when it equals the endpoint, always this one.
    /// </summary>
    public SupertextConnection ForReview(bool review)
    {
        var reviewUrl = ReviewUrl?.Trim() ?? string.Empty;
        var endpoint = string.IsNullOrWhiteSpace(Endpoint) ? SupertextClient.DefaultEndpoint : Endpoint.Trim();
        return !review || reviewUrl == string.Empty || reviewUrl.TrimEnd('/') == endpoint.TrimEnd('/')
            ? this
            : this with { Endpoint = reviewUrl, ReviewUrl = string.Empty };
    }
}

/// <summary>
/// Supertext AI file translation (https://api.supertext.com/v1/) through the Supertext SDK
/// bundled in <c>lib/supertext-sdk</c> (namespace <c>Supertext.OrchardCore.Translation.Sdk.V1</c>,
/// compiled into this module): upload one HTML document, poll its status, download the
/// translation, delete the file; the auth header and HTTP 429 retries are the SDK's too.
/// Language codes are resolved before (<see cref="LanguageMapping"/>) and sent as they are. Requests go through the module's <see cref="HttpClient"/>. SDK errors come out as
/// this module's <see cref="SupertextException"/>, whose template <c>SupertextMessages</c> shows
/// in the admin language.
/// </summary>
public sealed partial class SupertextClient(HttpClient http)
{
    public const string DefaultEndpoint = Sdk.V1.SupertextClient.LiveBaseUrl;

    /// <summary>
    /// Supertext's review service (translation proxy of Supertext Localization Review). It speaks the
    /// same API and forwards to the live Supertext API with the site's key, keeping every translation
    /// so reviewers can correct them on the website. Used for cultures with <b>Translation review</b> on.
    /// </summary>
    public const string ReviewUrl = "https://localizationreview-production.up.railway.app/v1/";

    /// <summary>The User-Agent of every request: the module and its version (as in the version reports).</summary>
    public static string UserAgent => "OrchardCore-Supertext-Translation/" + (string.IsNullOrEmpty(Services.ModuleVersion.Current) ? "0.0.0" : Services.ModuleVersion.Current);

    /// <summary>Documents above this size are split into several requests.</summary>
    public const int MaxDocumentCharacters = Sdk.V1.SupertextClient.DefaultMaxCharacters;

    /// <param name="targetLanguage">Supertext target code ("de-CH", "es"; see <see cref="LanguageMapping.Target"/>)</param>
    /// <param name="sourceLanguage">Supertext source code ("en"), empty for auto-detection</param>
    /// <param name="politeness">"default", "more" or "less"</param>
    public Task<string> TranslateDocumentAsync(SupertextConnection connection, string html, string targetLanguage, string sourceLanguage, string politeness, CancellationToken ct = default)
        => CallAsync(() => CreateSdkClient(connection).TranslateHtmlAsync(html, targetLanguage, NullIfEmpty(sourceLanguage), ToPoliteness(politeness), ct));

    /// <summary>
    /// Translates segments in as few documents as the size limit allows.
    /// </summary>
    /// <returns>segment index => translation; missing segments (and HTML whose Liquid came back damaged) are left out</returns>
    public async Task<Dictionary<int, string>> TranslateSegmentsAsync(SupertextConnection connection, IReadOnlyList<HtmlDocument.Segment> segments, string targetLanguage, string sourceLanguage, string politeness, CancellationToken ct = default)
    {
        var prepared = segments.Select(HtmlDocument.ToSdk).ToList();
        var keyed = prepared.Select((p, i) => new KeyValuePair<string, Sdk.V1.Segment>(i.ToString(System.Globalization.CultureInfo.InvariantCulture), p.Segment));
        var result = new Dictionary<int, string>();
        var client = CreateSdkClient(connection);
        foreach (var batch in Sdk.V1.SupertextClient.Batches(keyed, MaxDocumentCharacters))
        {
            var batchSegments = batch.Select(pair => pair.Value).ToList();
            var translated = await CallAsync(() => client.TranslateHtmlAsync(Sdk.V1.HtmlDocument.Build(batchSegments), targetLanguage, NullIfEmpty(sourceLanguage), ToPoliteness(politeness), ct));
            var parsed = Sdk.V1.HtmlDocument.Parse(translated, batchSegments);
            for (var i = 0; i < batch.Count; i++)
            {
                var index = int.Parse(batch[i].Key, System.Globalization.CultureInfo.InvariantCulture);
                var text = HtmlDocument.FromSdk(parsed[i], prepared[index].Liquid);
                if (text is not null)
                {
                    result[index] = text;
                }
            }
        }
        return result;
    }

    /// <summary>Options for the SDK client that runs the key check of the version reports (same endpoint and user agent).</summary>
    public static Sdk.V1.SupertextClientOptions KeyCheckOptions(string endpoint)
        => new()
        {
            BaseUrl = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim(),
            UserAgent = UserAgent,
        };

    /// <summary>Removes a pasted "Supertext-Auth-Key " prefix (Supertext shows the key with it).</summary>
    public static string NormalizeApiKey(string apiKey)
        => AuthPrefix().Replace(apiKey ?? string.Empty, string.Empty).Trim();

    private Sdk.V1.SupertextClient CreateSdkClient(SupertextConnection connection)
    {
        var endpoint = string.IsNullOrWhiteSpace(connection.Endpoint) ? DefaultEndpoint : connection.Endpoint.Trim();
        return new Sdk.V1.SupertextClient(NormalizeApiKey(connection.ApiKey), new Sdk.V1.SupertextClientOptions
        {
            BaseUrl = endpoint,
            PollInterval = TimeSpan.FromSeconds(Math.Max(1, connection.PollIntervalSeconds)),
            Timeout = TimeSpan.FromSeconds(Math.Max(10, connection.PollTimeoutSeconds)),
            UserAgent = UserAgent,
            HttpClient = http,
        });
    }

    private static string NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Sdk.V1.Politeness ToPoliteness(string politeness) => politeness switch
    {
        "more" => Sdk.V1.Politeness.More,
        "less" => Sdk.V1.Politeness.Less,
        _ => Sdk.V1.Politeness.Default,
    };

    private static async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Sdk.V1.SupertextException e)
        {
            throw FromSdk(e);
        }
    }

    /// <summary>
    /// The module's message for an SDK error code. Keep the switch arms literal:
    /// <c>LocalizationTests</c> reads them to check the .po files.
    /// </summary>
    internal static SupertextException FromSdk(Sdk.V1.SupertextException e)
    {
        var template = e.Code switch
        {
            Sdk.V1.SupertextErrorCodes.MissingApiKey => "No Supertext API key configured (Settings → Supertext, or SUPERTEXT_API_KEY). No Supertext account yet? Create one at https://www.supertext.com/person/en/account/signin. Generate your API key at https://www.supertext.com/en/integrations/api (supertext.com → Integrations → API, requires the Admin role).",
            Sdk.V1.SupertextErrorCodes.AuthenticationFailure => "Authentication failed. Please check the Supertext API key. No Supertext account yet? Create one at https://www.supertext.com/person/en/account/signin. Generate your API key at https://www.supertext.com/en/integrations/api (supertext.com → Integrations → API, requires the Admin role).",
            Sdk.V1.SupertextErrorCodes.InvalidLanguagePair => "Supertext does not support this language pair ({0} → {1}). Check the language mapping under Settings → Supertext.",
            Sdk.V1.SupertextErrorCodes.UnsupportedLanguage => "The language {0} has no Supertext language yet. Ask your administrator to map it under Settings → Supertext → Language mapping.",
            Sdk.V1.SupertextErrorCodes.UnsupportedFeature => "Supertext offers no formal or informal tone for this language. Ask your administrator to set the form of address under Settings → Supertext to As in the source.",
            Sdk.V1.SupertextErrorCodes.NotFound => "The requested Supertext resource was not found.",
            Sdk.V1.SupertextErrorCodes.PayloadTooLarge => "The content is too large for Supertext to translate in one go.",
            Sdk.V1.SupertextErrorCodes.TooManyRequests => "Too many requests to Supertext. Please try again shortly.",
            Sdk.V1.SupertextErrorCodes.ServiceUnavailable => "The Supertext service is currently unavailable.",
            Sdk.V1.SupertextErrorCodes.TransportError => "Could not reach Supertext.",
            Sdk.V1.SupertextErrorCodes.NoFileId => "Supertext did not return a file id.",
            Sdk.V1.SupertextErrorCodes.TranslationError => "Supertext failed to translate the document.",
            Sdk.V1.SupertextErrorCodes.QuotaExceeded => "Your Supertext translation limit is exceeded.",
            Sdk.V1.SupertextErrorCodes.FileDeleted => "The Supertext file was deleted before it could be downloaded.",
            Sdk.V1.SupertextErrorCodes.Timeout => "Timed out waiting for the Supertext translation.",
            Sdk.V1.SupertextErrorCodes.IncompleteResponse => "The translated document was empty.",
            _ => "Supertext answered with HTTP {0}.",
        };
        object[] args = e.Code switch
        {
            Sdk.V1.SupertextErrorCodes.InvalidLanguagePair => [string.IsNullOrEmpty(e.SourceLang) ? "?" : e.SourceLang, string.IsNullOrEmpty(e.TargetLang) ? "?" : e.TargetLang],
            Sdk.V1.SupertextErrorCodes.UnsupportedLanguage => [FirstNonEmpty(e.TargetLang, e.SourceLang, "?")],
            Sdk.V1.SupertextErrorCodes.UnexpectedStatus => [e.Status ?? 0],
            _ => [],
        };
        return new SupertextException(template, args, e.Detail, e, e.Code, e.Status);
    }

    private static string FirstNonEmpty(params string[] values) => values.First(v => !string.IsNullOrEmpty(v));

    [GeneratedRegex(@"^\s*Supertext-Auth-Key\s+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthPrefix();
}
