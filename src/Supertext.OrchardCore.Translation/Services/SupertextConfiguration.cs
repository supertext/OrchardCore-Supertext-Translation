using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using OrchardCore.Environment.Shell.Configuration;
using OrchardCore.Settings;
using Supertext.OrchardCore.Translation.Api;
using Supertext.OrchardCore.Translation.Settings;

namespace Supertext.OrchardCore.Translation.Services;

/// <summary>
/// The settings in effect: site settings, overridden by the <c>SUPERTEXT_API_KEY</c> /
/// <c>SUPERTEXT_API_ENDPOINT</c> environment variables or the tenant's <c>Supertext</c>
/// configuration section (<c>Supertext:ApiKey</c>, <c>Supertext:Endpoint</c>). The review service
/// for <b>Translation review</b> comes only from <c>SUPERTEXT_REVIEW_URL</c> / <c>Supertext:ReviewUrl</c>
/// (default: Supertext's review service; empty = review off).
/// </summary>
public sealed class SupertextConfiguration(
    ISiteService siteService,
    IDataProtectionProvider dataProtectionProvider,
    IShellConfiguration shellConfiguration,
    ILogger<SupertextConfiguration> logger)
{
    public const string ProtectorPurpose = "Supertext.OrchardCore.Translation.ApiKey";

    public async Task<SupertextSettings> GetSettingsAsync()
        => (await siteService.GetSiteSettingsAsync()).TryGet<SupertextSettings>(out var settings) ? settings : new SupertextSettings();

    public string ApiKeyFromEnvironment
        => FirstNonEmpty(Environment.GetEnvironmentVariable("SUPERTEXT_API_KEY"), shellConfiguration["Supertext:ApiKey"]);

    public string EndpointFromEnvironment
        => FirstNonEmpty(Environment.GetEnvironmentVariable("SUPERTEXT_API_ENDPOINT"), shellConfiguration["Supertext:Endpoint"]);

    /// <summary>
    /// The review service for cultures with <b>Translation review</b> on: <c>SUPERTEXT_REVIEW_URL</c>, else
    /// <c>Supertext:ReviewUrl</c>, else <see cref="SupertextClient.ReviewUrl"/>. Set but empty = review off
    /// for every culture (empty string returned).
    /// </summary>
    public string ReviewUrlInEffect
        => ResolveReviewUrl(Environment.GetEnvironmentVariable("SUPERTEXT_REVIEW_URL"), shellConfiguration["Supertext:ReviewUrl"]);

    /// <summary>The first value that is set (even empty), else Supertext's review service.</summary>
    public static string ResolveReviewUrl(params string[] values)
        => (values.FirstOrDefault(v => v is not null) ?? SupertextClient.ReviewUrl).Trim();

    public async Task<SupertextConnection> GetConnectionAsync()
    {
        var settings = await GetSettingsAsync();
        return new SupertextConnection(EndpointInEffect(settings), ApiKeyInEffect(settings), PollTimeoutSeconds: settings.PollTimeoutSeconds > 0 ? settings.PollTimeoutSeconds : 300, ReviewUrl: ReviewUrlInEffect);
    }

    /// <summary>Whether <b>Translation review</b> is on for an Orchard Core culture (case-insensitive).</summary>
    public static bool ReviewFor(SupertextSettings settings, string culture)
        => !string.IsNullOrWhiteSpace(culture)
            && (settings.ReviewCultures ?? []).Any(c => string.Equals(c?.Trim(), culture.Trim(), StringComparison.OrdinalIgnoreCase));

    public string Protect(string apiKey)
        => string.IsNullOrWhiteSpace(apiKey) ? null : dataProtectionProvider.CreateProtector(ProtectorPurpose).Protect(SupertextClient.NormalizeApiKey(apiKey));

    public string Unprotect(string protectedKey)
    {
        if (string.IsNullOrWhiteSpace(protectedKey))
        {
            return string.Empty;
        }
        try
        {
            return dataProtectionProvider.CreateProtector(ProtectorPurpose).Unprotect(protectedKey);
        }
        catch (Exception e)
        {
            // Happens when the data protection keys were lost (e.g. a new container without them).
            logger.LogWarning(e, "The stored Supertext API key could not be decrypted. Enter it again in Settings → Supertext.");
            return string.Empty;
        }
    }

    /// <summary>The API key in effect for these settings: the environment/configuration, else the stored one (prefix stripped; empty if none).</summary>
    public string ApiKeyInEffect(SupertextSettings settings)
        => SupertextClient.NormalizeApiKey(FirstNonEmpty(ApiKeyFromEnvironment, Unprotect(settings.ProtectedApiKey)));

    /// <summary>The endpoint in effect for these settings: the environment/configuration, else the stored one, else the live API.</summary>
    public string EndpointInEffect(SupertextSettings settings)
        => FirstNonEmpty(EndpointFromEnvironment, settings.Endpoint, SupertextClient.DefaultEndpoint);

    /// <summary>The Language mapping setting (culture → Supertext code), with older values migrated (<see cref="LanguageMapping.Migrate"/>).</summary>
    public static IReadOnlyDictionary<string, string> Mapping(SupertextSettings settings)
        => LanguageMapping.Migrate(LanguageMapping.Parse(settings.LanguageMapping));

    /// <summary>The target code sent to Supertext for an Orchard Core culture: the mapping, else the culture if it is a Supertext code, else the SDK default (de → de-DE).</summary>
    /// <exception cref="SupertextException"><c>unsupported_language</c> when the culture has no Supertext language.</exception>
    public static string TargetLanguageCode(SupertextSettings settings, string culture)
        => LanguageMapping.Target(culture, Mapping(settings));

    /// <summary>The source code sent to Supertext for an Orchard Core culture: the mapping in its source form, else the culture's language (en-US → en).</summary>
    /// <exception cref="SupertextException"><c>unsupported_language</c> when the culture has no Supertext language.</exception>
    public static string SourceLanguageCode(SupertextSettings settings, string culture)
        => LanguageMapping.Source(culture, Mapping(settings));

    public static IEnumerable<string> ExcludedFields(SupertextSettings settings)
        => (settings.ExcludedFields ?? string.Empty).Split('\n', ',').Select(e => e.Trim()).Where(e => e != string.Empty);

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;
}
