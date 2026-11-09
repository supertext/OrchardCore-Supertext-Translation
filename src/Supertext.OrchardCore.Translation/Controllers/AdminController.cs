using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using OrchardCore.Admin;
using OrchardCore.ContentLocalization;
using OrchardCore.ContentLocalization.Models;
using OrchardCore.ContentManagement;
using OrchardCore.Contents;
using OrchardCore.DisplayManagement.Notify;
using OrchardCore.Localization;
using Supertext.OrchardCore.Translation.Api;
using Supertext.OrchardCore.Translation.Sdk.Files;
using Supertext.OrchardCore.Translation.Services;
using Supertext.OrchardCore.Translation.ViewModels;

namespace Supertext.OrchardCore.Translation.Controllers;

/// <summary>
/// "Translate with Supertext": translates a content item into the cultures the editor picks.
/// Missing localizations are created (as drafts); existing ones are overwritten after the
/// editor confirms it. The same page exports the item for translation (XLIFF or HTML) and
/// imports translated files, written back the same way.
/// </summary>
[Admin]
public sealed class AdminController(
    IContentManager contentManager,
    IContentLocalizationManager localizationManager,
    ILocalizationService localizationService,
    IAuthorizationService authorizationService,
    ContentTranslator translator,
    SupertextConfiguration configuration,
    SupertextScope scope,
    INotifier notifier,
    SupertextMessages messages,
    SupertextReporting reporting,
    IHtmlLocalizer<AdminController> htmlLocalizer,
    IStringLocalizer<AdminController> stringLocalizer,
    ILogger<AdminController> logger) : Controller
{
    private readonly IHtmlLocalizer H = htmlLocalizer;
    private readonly IStringLocalizer S = stringLocalizer;

    [HttpGet]
    [Admin("Supertext/Translate/{contentItemId}", "Supertext.Translate")]
    public async Task<IActionResult> Translate(string contentItemId, string returnUrl = null)
    {
        var source = await contentManager.GetAsync(contentItemId, VersionOptions.Latest);
        if (source is null)
        {
            return NotFound();
        }
        if (!await CanTranslateAsync(source))
        {
            return Forbid();
        }
        await reporting.OnRequestAsync();
        return View(await BuildModelAsync(source, returnUrl, []));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Admin("Supertext/Translate/{contentItemId}", "Supertext.Translate.Post")]
    public async Task<IActionResult> Translate(string contentItemId, string[] cultures, bool confirmOverwrite, string returnUrl = null)
    {
        var source = await contentManager.GetAsync(contentItemId, VersionOptions.Latest);
        if (source is null)
        {
            return NotFound();
        }
        if (!await CanTranslateAsync(source))
        {
            return Forbid();
        }

        await reporting.OnRequestAsync();
        var model = await BuildModelAsync(source, returnUrl, cultures ?? []);
        var selected = model.Targets.Where(t => t.Selected).ToList();
        if (selected.Count == 0)
        {
            await notifier.WarningAsync(H["Choose at least one language."]);
            return View(model);
        }
        if (model.ApiKeyMissing)
        {
            return View(model);
        }
        if (selected.Any(t => t.ExistingContentItemId is not null) && !confirmOverwrite)
        {
            model.ShowOverwriteWarning = true;
            return View(model);
        }

        // Localizations created here are translated explicitly below.
        scope.SuppressAutomaticTranslation = true;

        // One language after the other: Supertext limits requests per second per key.
        var keyRejected = false;
        foreach (var target in selected)
        {
            try
            {
                var existing = target.ExistingContentItemId is null ? null : await contentManager.GetAsync(target.ExistingContentItemId, VersionOptions.Latest);
                if (existing is not null && !await authorizationService.AuthorizeAsync(User, CommonPermissions.EditContent, existing))
                {
                    await notifier.ErrorAsync(H["{0}: you may not edit the existing translation.", target.CultureName]);
                    continue;
                }

                var prepared = await translator.PrepareAsync(source, model.SourceCulture, target.Culture, HttpContext.RequestAborted);

                await WriteDraftAsync(source, target.Culture, existing, prepared);

                if (prepared.Result.Untranslated > 0)
                {
                    await notifier.WarningAsync(H["{0}: translated as a draft, but {1} text(s) kept the source wording. Please review them.", target.CultureName, prepared.Result.Untranslated]);
                }
                else
                {
                    await notifier.SuccessAsync(H["{0}: translated as a draft ({1} texts). Review and publish it when it's ready.", target.CultureName, prepared.Result.Segments]);
                }
            }
            catch (SupertextException e)
            {
                logger.LogWarning(e, "Supertext translation of {ContentItemId} into {Culture} failed.", source.ContentItemId, target.Culture);
                await notifier.ErrorAsync(H["{0}: not translated. {1}", target.CultureName, messages.Describe(e)]);
                if (e.Code == SupertextException.AuthenticationFailure && !keyRejected)
                {
                    keyRejected = true;
                    await reporting.AuthenticationFailedAsync(e.Status);
                }
            }
            catch (InvalidOperationException e)
            {
                logger.LogWarning(e, "Could not create the {Culture} localization of {ContentItemId}.", target.Culture, source.ContentItemId);
                await notifier.ErrorAsync(H["{0}: the localization could not be created.", target.CultureName]);
            }
        }

        return RedirectToAction(nameof(Translate), new { contentItemId, returnUrl });
    }

    /// <summary>Export for translation: the item's texts as an XLIFF file (one or more cultures) or an HTML file (one culture).</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Admin("Supertext/Export/{contentItemId}", "Supertext.Export")]
    public async Task<IActionResult> Export(string contentItemId, string format, string[] exportCultures, string returnUrl = null)
    {
        var source = await contentManager.GetAsync(contentItemId, VersionOptions.Latest);
        if (source is null)
        {
            return NotFound();
        }
        if (!await CanTranslateAsync(source))
        {
            return Forbid();
        }

        var model = await BuildModelAsync(source, returnUrl, []);
        model.ExportFormat = format == TranslationFileExchange.FormatHtml ? TranslationFileExchange.FormatHtml : TranslationFileExchange.FormatXliff;
        var cultures = model.Targets.Where(t => (exportCultures ?? []).Contains(t.Culture, StringComparer.OrdinalIgnoreCase)).Select(t => t.Culture).ToList();
        model.ExportCultures = cultures;
        if (cultures.Count == 0)
        {
            await notifier.WarningAsync(H["Choose at least one language."]);
            return View(nameof(Translate), model);
        }
        if (model.ExportFormat == TranslationFileExchange.FormatHtml && cultures.Count != 1)
        {
            await notifier.WarningAsync(H["An HTML file holds one language: choose exactly one."]);
            return View(nameof(Translate), model);
        }

        var (_, segments) = await translator.CollectAsync(source);
        if (segments.Count == 0)
        {
            await notifier.WarningAsync(H["Nothing to translate."]);
            return View(nameof(Translate), model);
        }

        var settings = await configuration.GetSettingsAsync();
        var sourceCode = TranslationFileExchange.LanguageCode(() => SupertextConfiguration.SourceLanguageCode(settings, model.SourceCulture), model.SourceCulture);
        var jobs = cultures
            .Select(c => TranslationFileExchange.Job(source.ContentItemId, c, sourceCode, TranslationFileExchange.LanguageCode(() => SupertextConfiguration.TargetLanguageCode(settings, c), c), segments))
            .ToList();
        var file = TranslationFileExchange.Export(model.ExportFormat, source.ContentItemId, cultures, jobs);
        return File(Encoding.UTF8.GetBytes(file.Content), file.ContentType, file.FileName);
    }

    /// <summary>
    /// Import translation: reads an uploaded XLIFF or HTML file (in memory, nothing stored) and writes
    /// each job's translations into the localization of its item and culture, as a draft, like an API translation.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(UploadRequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadRequestLimit)]
    [Admin("Supertext/Import/{contentItemId}", "Supertext.Import")]
    public async Task<IActionResult> Import(string contentItemId, IFormFile file, bool includeChanged, bool confirmOverwrite, string returnUrl = null)
    {
        var page = await contentManager.GetAsync(contentItemId, VersionOptions.Latest);
        if (page is null)
        {
            return NotFound();
        }
        if (!await CanTranslateAsync(page))
        {
            return Forbid();
        }

        var model = await BuildModelAsync(page, returnUrl, []);
        model.IncludeChanged = includeChanged;
        if (file is null || file.Length == 0)
        {
            await notifier.WarningAsync(H["Choose a file to import."]);
            return View(nameof(Translate), model);
        }

        IReadOnlyList<TranslationFile> jobs;
        try
        {
            await using var stream = file.OpenReadStream();
            jobs = TranslationFileExchange.Read(stream, file.Length);
        }
        catch (TranslationFileException e)
        {
            model.ImportError = FileErrorMessage(e.Code);
            return View(nameof(Translate), model);
        }
        catch (TranslationFileGlueException e)
        {
            model.ImportError = GlueErrorMessage(e);
            return View(nameof(Translate), model);
        }

        // Localizations created here get the imported texts, not an API translation.
        scope.SuppressAutomaticTranslation = true;
        var supported = (await localizationService.GetSupportedCulturesAsync()).ToList();
        model.ImportResults = [];
        foreach (var job in jobs)
        {
            var summary = new ImportJobSummary { Title = job.Reference, Culture = job.TargetLanguage, CultureName = job.TargetLanguage };
            model.ImportResults.Add(summary);
            try
            {
                var (id, culture) = TranslationFileExchange.Locate(job);
                summary.Culture = culture;
                summary.CultureName = CultureName(culture);
                var source = await contentManager.GetAsync(id, VersionOptions.Latest)
                    ?? throw new TranslationFileGlueException(TranslationFileGlueException.NotFound);
                summary.Title = source.DisplayText;
                var siteCulture = supported.FirstOrDefault(c => c.Equals(culture, StringComparison.OrdinalIgnoreCase));
                if (siteCulture is null || siteCulture.Equals(await SourceCultureAsync(source), StringComparison.OrdinalIgnoreCase))
                {
                    throw new TranslationFileGlueException(TranslationFileGlueException.NoLanguage, culture);
                }
                if (!await CanTranslateAsync(source))
                {
                    summary.Error = S["You may not translate this content item with Supertext."].Value;
                    continue;
                }

                var existing = await FindLocalizationAsync(source, siteCulture);
                if (existing is not null && !await authorizationService.AuthorizeAsync(User, CommonPermissions.EditContent, existing))
                {
                    summary.Error = S["{0}: you may not edit the existing translation.", summary.CultureName].Value;
                    continue;
                }

                var (data, segments) = await translator.CollectAsync(source);
                var counts = TranslationFileExchange.Import(job, segments, includeChanged);
                summary.Counts = counts;
                if (counts.Imported == 0)
                {
                    continue;
                }
                if (existing is not null && !confirmOverwrite)
                {
                    summary.Counts = null;
                    summary.Error = S["This language already has a translation. Tick “Yes, overwrite the existing translations” and import the file again."].Value;
                    continue;
                }

                var untranslated = segments.Count - counts.Imported;
                summary.SavedContentItemId = await WriteDraftAsync(source, siteCulture, existing, new PreparedTranslation(data, new TranslationResult(segments.Count, untranslated)));
            }
            catch (TranslationFileGlueException e)
            {
                summary.Error = GlueErrorMessage(e);
            }
            catch (TranslationFileException e)
            {
                summary.Error = FileErrorMessage(e.Code);
            }
            catch (InvalidOperationException e)
            {
                logger.LogWarning(e, "Could not create the {Culture} localization for an imported translation file.", summary.Culture);
                summary.Error = S["{0}: the localization could not be created.", summary.CultureName].Value;
            }
        }
        return View(nameof(Translate), model);
    }

    /// <summary>The request limit for imports: the file limit plus room for the form; larger files are refused with a message.</summary>
    private const long UploadRequestLimit = TranslationFileExchange.MaxUploadBytes + 1024 * 1024;

    private string FileErrorMessage(string code) => code switch
    {
        TranslationFileErrorCodes.MissingMetadata => S["This file wasn't exported by the Supertext module, or its header was removed."].Value,
        TranslationFileErrorCodes.TooLarge => S["The file is too large (at most 10 MB)."].Value,
        _ => S["This file isn't a valid XLIFF or HTML translation file."].Value,
    };

    private string GlueErrorMessage(TranslationFileGlueException e) => e.Code switch
    {
        TranslationFileGlueException.OtherSystem => S["This file belongs to another system ({0}).", e.Argument].Value,
        TranslationFileGlueException.NoLanguage => S["The site has no language {0}.", e.Argument].Value,
        TranslationFileGlueException.TooLarge => S["The file is too large (at most 10 MB)."].Value,
        _ => S["The content item this file was exported from no longer exists."].Value,
    };

    /// <summary>Writes prepared content into the target culture's localization (created if missing) and saves it as a draft.</summary>
    private async Task<string> WriteDraftAsync(ContentItem source, string culture, ContentItem existing, PreparedTranslation prepared)
    {
        var item = existing is null
            ? await localizationManager.LocalizeAsync(source, culture)
            : await contentManager.GetAsync(existing.ContentItemId, VersionOptions.DraftRequired);
        ContentTranslator.Apply(prepared, item);
        await contentManager.UpdateAsync(item);
        await contentManager.SaveDraftAsync(item);
        return item.ContentItemId;
    }

    /// <summary>The latest version of the source's localization in a culture, or null.</summary>
    private async Task<ContentItem> FindLocalizationAsync(ContentItem source, string culture)
    {
        var set = source.Localization()?.LocalizationSet;
        if (string.IsNullOrEmpty(set))
        {
            return null;
        }
        return (await localizationManager.GetItemsForSetAsync(set))
            .Where(i => string.Equals(i.Localization()?.Culture, culture, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.Latest)
            .FirstOrDefault();
    }

    private async Task<string> SourceCultureAsync(ContentItem source)
    {
        var part = source.Localization();
        return !string.IsNullOrEmpty(part?.Culture) ? part.Culture : await localizationService.GetDefaultCultureAsync();
    }

    private async Task<bool> CanTranslateAsync(ContentItem item)
        => await authorizationService.AuthorizeAsync(User, SupertextPermissions.TranslateWithSupertext, item)
            && await authorizationService.AuthorizeAsync(User, ContentLocalizationPermissions.LocalizeContent, item)
            && await authorizationService.AuthorizeAsync(User, CommonPermissions.EditContent, item);

    private async Task<TranslateViewModel> BuildModelAsync(ContentItem source, string returnUrl, string[] selected)
    {
        var part = source.Localization();
        var sourceCulture = !string.IsNullOrEmpty(part?.Culture) ? part.Culture : await localizationService.GetDefaultCultureAsync();
        var existing = string.IsNullOrEmpty(part?.LocalizationSet) ? [] : (await localizationManager.GetItemsForSetAsync(part.LocalizationSet)).ToList();

        var model = new TranslateViewModel
        {
            ContentItemId = source.ContentItemId,
            Title = source.DisplayText,
            ContentType = source.ContentType,
            SourceCulture = sourceCulture,
            SourceCultureName = CultureName(sourceCulture),
            HasDraft = source.HasDraft(),
            ApiKeyMissing = (await configuration.GetConnectionAsync()).ApiKey == string.Empty,
            ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null,
        };

        foreach (var culture in await localizationService.GetSupportedCulturesAsync())
        {
            if (culture.Equals(sourceCulture, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // Prefer the latest version (a draft) over the published one.
            var localized = existing
                .Where(i => string.Equals(i.Localization()?.Culture, culture, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.Latest)
                .FirstOrDefault();
            model.Targets.Add(new TranslateTarget
            {
                Culture = culture,
                CultureName = CultureName(culture),
                Selected = selected.Contains(culture, StringComparer.OrdinalIgnoreCase),
                ExistingContentItemId = localized?.ContentItemId,
                ExistingTitle = localized?.DisplayText,
                ExistingPublished = localized is not null && await contentManager.HasPublishedVersionAsync(localized),
                ExistingHasDraft = localized?.HasDraft() ?? false,
            });
        }
        return model;
    }

    private static string CultureName(string culture) => CultureNames.Get(culture);
}
