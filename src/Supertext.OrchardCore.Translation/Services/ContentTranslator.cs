using System.Text.Json.Dynamic;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OrchardCore.ContentManagement;
using OrchardCore.ContentManagement.Metadata;
using OrchardCore.ContentManagement.Metadata.Models;
using OrchardCore.ContentManagement.Metadata.Settings;
using Supertext.OrchardCore.Translation.Api;

namespace Supertext.OrchardCore.Translation.Services;

/// <param name="Segments">translatable texts found in the source</param>
/// <param name="Untranslated">texts that came back missing or damaged and kept the source text</param>
public sealed record TranslationResult(int Segments, int Untranslated);

/// <summary>The translated content of a source item, ready to be applied to a target item.</summary>
public sealed record PreparedTranslation(JsonObject Data, TranslationResult Result);

/// <summary>
/// Writes a translation of a source item's content into a target item of the same
/// localization set: the source content is copied over the target (except the parts that
/// belong to the target: culture, URL, alias, list membership), its texts are translated
/// with Supertext in one document per language, and the result is applied to the target.
/// Saving is up to the caller.
/// </summary>
public sealed class ContentTranslator(
    IContentDefinitionManager contentDefinitionManager,
    SupertextClient client,
    SupertextConfiguration configuration,
    ILogger<ContentTranslator> logger)
{
    /// <summary>Parts that identify the target item rather than describe its content.</summary>
    private static readonly string[] TargetOwnedParts = ["LocalizationPart", "AutoroutePart", "AliasPart", "ContainedPart"];

    private readonly Dictionary<string, IReadOnlyList<PartSchema>> _schemas = new(StringComparer.Ordinal);

    /// <summary>Translates the source and applies the result to the target (not saved).</summary>
    public async Task<TranslationResult> TranslateAsync(ContentItem source, ContentItem target, string sourceCulture, string targetCulture, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(source, sourceCulture, targetCulture, ct);
        Apply(prepared, target);
        return prepared.Result;
    }

    /// <summary>Translates the source's content without touching any item yet.</summary>
    public async Task<PreparedTranslation> PrepareAsync(ContentItem source, string sourceCulture, string targetCulture, CancellationToken ct = default)
    {
        var settings = await configuration.GetSettingsAsync();
        var connection = await configuration.GetConnectionAsync();
        if (connection.ApiKey == string.Empty)
        {
            throw new SupertextException("No Supertext API key configured. An administrator can add it under Settings → Supertext.");
        }

        var (data, segments) = await CollectAsync(source, settings);
        var untranslated = 0;
        var targetCode = SupertextConfiguration.TargetLanguageCode(settings, targetCulture);
        var sourceCode = SupertextConfiguration.SourceLanguageCode(settings, sourceCulture);

        var politeness = LanguageMapping.PolitenessFor(targetCode, settings.Politeness);

        // Cultures with Translation review on go through Supertext's review service (same key and protocol).
        var targetConnection = connection.ForReview(SupertextConfiguration.ReviewFor(settings, targetCulture));
        var translations = await client.TranslateSegmentsAsync(targetConnection, segments.Select(s => s.Segment).ToList(), targetCode, sourceCode, politeness, ct);
        for (var i = 0; i < segments.Count; i++)
        {
            if (translations.TryGetValue(i, out var text) && !string.IsNullOrWhiteSpace(text))
            {
                segments[i].Apply(text);
            }
            else
            {
                untranslated++;
                logger.LogWarning("Supertext returned no usable translation for {Path} ({Culture}); the source text was kept.", segments[i].Path, targetCulture);
            }
        }

        return new PreparedTranslation(data, new TranslationResult(segments.Count, untranslated));
    }

    /// <summary>
    /// A copy of the source's content without the parts that belong to a target item, and its
    /// translatable texts (keyed by <see cref="PendingSegment.Path"/>). Applying texts to the
    /// segments changes the copy, which <see cref="Apply"/> then writes to a target. Used by the
    /// API translation and by translation files (export and import), so both see the same texts.
    /// </summary>
    public async Task<(JsonObject Data, List<PendingSegment> Segments)> CollectAsync(ContentItem source)
        => await CollectAsync(source, await configuration.GetSettingsAsync());

    private async Task<(JsonObject Data, List<PendingSegment> Segments)> CollectAsync(ContentItem source, Settings.SupertextSettings settings)
    {
        var data = (JsonObject)Data(source).DeepClone();
        foreach (var owned in TargetOwnedParts)
        {
            data.Remove(owned);
        }

        var walker = new ContentWalker(ResolveTypeAsync, SupertextConfiguration.ExcludedFields(settings));
        return (data, await walker.CollectAsync(data, source.ContentType));
    }

    /// <summary>Copies translated content over the target, keeping the target's own parts.</summary>
    public static void Apply(PreparedTranslation prepared, ContentItem target)
    {
        // Merge through Apply(): it also clears the target's cache of typed parts, so
        // handlers that run on save (TitlePart sets DisplayText) see the new values.
        var replacement = new ContentItem();
        var replacementData = Data(replacement);
        foreach (var (key, value) in prepared.Data)
        {
            replacementData[key] = value?.DeepClone();
        }
        target.Apply(replacement);

        if (prepared.Data["TitlePart"]?["Title"] is JsonValue title && title.TryGetValue<string>(out var displayText))
        {
            target.DisplayText = displayText;
        }
    }

    private static JsonObject Data(ContentItem item) => (JsonDynamicObject)item.Content;

    private async Task<IReadOnlyList<PartSchema>> ResolveTypeAsync(string contentType)
    {
        if (_schemas.TryGetValue(contentType, out var cached))
        {
            return cached;
        }
        var definition = await contentDefinitionManager.GetTypeDefinitionAsync(contentType);
        IReadOnlyList<PartSchema> parts = definition is null
            ? []
            : definition.Parts.Select(p => new PartSchema(
                p.Name,
                p.PartDefinition.Name,
                p.PartDefinition.Fields.Select(f => new FieldSchema(f.Name, f.FieldDefinition.Name, f.GetSettings<ContentPartFieldSettings>().Editor)).ToList()))
              .ToList();
        _schemas[contentType] = parts;
        return parts;
    }
}
