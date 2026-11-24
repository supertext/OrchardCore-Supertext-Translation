using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrchardCore.BackgroundJobs;
using OrchardCore.Entities;
using OrchardCore.Settings;
using Supertext.OrchardCore.Translation.Api;
using Supertext.OrchardCore.Translation.PluginHealth;
using Supertext.OrchardCore.Translation.Settings;

namespace Supertext.OrchardCore.Translation.Services;

/// <summary>
/// Runs the Commando Center reporter (<see cref="VersionReports"/>) with what Orchard Core knows and
/// keeps its state in the site document (<see cref="SupertextReportingState"/>):
/// <list type="bullet">
/// <item><see cref="AfterSettingsSavedAsync"/> while Settings → Supertext is saved (the result is shown there),</item>
/// <item><see cref="OnRequestAsync"/> on back-end requests that run the module (settings page, Translate page,
/// the localization hook), after the response in its own scope (<see cref="HttpBackgroundJob"/>),</item>
/// <item><see cref="AuthenticationFailedAsync"/> when Supertext rejects the key during a translation.</item>
/// </list>
/// Nothing here throws into a request: Plugin Health never throws, and storage errors are logged at debug level.
/// </summary>
public sealed class SupertextReporting(
    ISiteService siteService,
    SupertextConfiguration configuration,
    IHttpContextAccessor httpContextAccessor,
    SupertextScope scope,
    ILogger<SupertextReporting> logger)
{
    /// <summary>One reporter run at a time per process, so two requests don't report the same change twice.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>The running Orchard Core version (the OrchardCore.Abstractions assembly the site runs).</summary>
    public static string OrchardVersion { get; } = ModuleVersion.Normalize(
        typeof(global::OrchardCore.Modules.StartupBase).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(global::OrchardCore.Modules.StartupBase).Assembly.GetName().Version?.ToString(3));

    /// <summary>The setting combined with <c>SUPERTEXT_VERSION_REPORTS</c>.</summary>
    public static bool Enabled(SupertextSettings settings) => VersionReports.Enabled(settings.SendVersionInformation);

    /// <summary>The stored reporter state of a site (empty if none or broken).</summary>
    public static ReporterState LoadState(IEntity site)
        => ReporterState.FromJson(site.TryGet<SupertextReportingState>(out var stored) ? stored.Reporter : null);

    /// <summary>Puts the reporter state into a site (saved with the site).</summary>
    public static void StoreState(IEntity site, ReporterState state)
        => site.Alter<SupertextReportingState>(s => s.Reporter = state.ToJson());

    /// <summary>The site's base URL: Settings → General → Base URL, else the current request's address.</summary>
    public string SiteUrl(ISite site)
    {
        var request = httpContextAccessor.HttpContext?.Request;
        return VersionReports.SiteUrl(site?.BaseUrl, request?.Scheme, request?.Host.Value, request?.PathBase.Value);
    }

    /// <summary>The latest stored key check, or null.</summary>
    public async Task<KeyCheck> LastCheckAsync()
    {
        try
        {
            return LoadState(await siteService.GetSiteSettingsAsync()).KeyCheck;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Supertext: could not read the reporter state.");
            return null;
        }
    }

    /// <summary>
    /// Settings → Supertext was saved: checks the key in effect, reports <c>installed</c> or
    /// <c>settings_saved</c>, and puts the new state into <paramref name="site"/> (saved with the settings).
    /// </summary>
    public async Task<ReporterResult> AfterSettingsSavedAsync(ISite site, SupertextSettings settings)
    {
        var siteUrl = SiteUrl(site);
        var state = SafeLoad(site);
        if (siteUrl is null)
        {
            return new ReporterResult(state, state.KeyCheck, null, false);
        }
        await Gate.WaitAsync();
        try
        {
            var result = await VersionReports.RunAsync(ReporterAction.AfterSettingsSaved, Context(settings, siteUrl), state, configuration.ApiKeyInEffect(settings));
            SafeStore(site, result.State);
            LogResult(result);
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A back-end request ran the module: version report and key check after the response (once per request).</summary>
    public Task OnRequestAsync() => InBackgroundAsync(ReporterAction.OnRequest, 0);

    /// <summary>Supertext rejected the key during a translation (401/403): the key check becomes <c>invalid</c>, reported once.</summary>
    public Task AuthenticationFailedAsync(int? httpStatus) => InBackgroundAsync(ReporterAction.AuthenticationFailed, httpStatus ?? 401);

    private async Task InBackgroundAsync(ReporterAction action, int httpStatus)
    {
        try
        {
            if (httpContextAccessor.HttpContext is null)
            {
                return;
            }
            // An authentication failure always counts; onRequest at most once per request.
            if (action == ReporterAction.OnRequest)
            {
                if (scope.ReportScheduled)
                {
                    return;
                }
                scope.ReportScheduled = true;
            }
            var siteUrl = SiteUrl(await siteService.GetSiteSettingsAsync());
            if (siteUrl is null)
            {
                return;
            }
            await HttpBackgroundJob.ExecuteAfterEndOfRequestAsync("Supertext.VersionReport", siteUrl, action, httpStatus,
                (shellScope, url, a, status) => shellScope.ServiceProvider.GetRequiredService<SupertextReporting>().RunAndStoreAsync(a, url, status));
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Supertext: version report not scheduled.");
        }
    }

    /// <summary>Runs one action with the stored state and saves the site if the state changed. Never throws.</summary>
    public async Task<ReporterResult> RunAndStoreAsync(ReporterAction action, string siteUrl, int httpStatus = 401)
    {
        await Gate.WaitAsync();
        try
        {
            var settings = await configuration.GetSettingsAsync();
            var site = await siteService.LoadSiteSettingsAsync();
            var state = LoadState(site);
            var result = await VersionReports.RunAsync(action, Context(settings, siteUrl), state, configuration.ApiKeyInEffect(settings), httpStatus);
            if (result.State.ToJson() != state.ToJson())
            {
                StoreState(site, result.State);
                await siteService.UpdateSiteSettingsAsync(site);
            }
            LogResult(result);
            return result;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Supertext: version report skipped.");
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// "Send configuration to Supertext" (Settings → Supertext, administrators): one <c>extensions</c>
    /// report now. Null with reports off or no site URL. Never throws.
    /// </summary>
    public async Task<ReporterResult> SendConfigurationAsync(IEnumerable<Extension> extensions, IReadOnlyList<PluginHealth.LanguageMapping> languages)
    {
        await Gate.WaitAsync();
        try
        {
            var settings = await configuration.GetSettingsAsync();
            var site = await siteService.LoadSiteSettingsAsync();
            var siteUrl = SiteUrl(site);
            if (!Enabled(settings) || siteUrl is null)
            {
                return null;
            }
            var state = LoadState(site);
            var result = await VersionReports.SendConfigurationAsync(Context(settings, siteUrl), state, extensions, languages);
            if (result.State.ToJson() != state.ToJson())
            {
                StoreState(site, result.State);
                await siteService.UpdateSiteSettingsAsync(site);
            }
            LogResult(result);
            return result;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Supertext: configuration not sent.");
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private ReportingContext Context(SupertextSettings settings, string siteUrl)
        => new(Enabled(settings), siteUrl, ModuleVersion.Current, OrchardVersion, configuration.EndpointInEffect(settings));

    private ReporterState SafeLoad(IEntity site)
    {
        try
        {
            return LoadState(site);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Supertext: could not read the reporter state.");
            return ReporterState.Empty;
        }
    }

    private void SafeStore(IEntity site, ReporterState state)
    {
        try
        {
            StoreState(site, state);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Supertext: could not store the reporter state.");
        }
    }

    private void LogResult(ReporterResult result)
    {
        if (result.Event is not null)
        {
            logger.LogDebug("Supertext: version report {Event} {Outcome}.", result.Event, result.Sent ? "sent" : "not sent");
        }
    }
}
