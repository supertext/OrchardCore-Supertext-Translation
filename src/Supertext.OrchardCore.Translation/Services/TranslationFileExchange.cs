using System.Text;
using Supertext.OrchardCore.Translation.Api;
using Supertext.OrchardCore.Translation.Sdk.Files;

namespace Supertext.OrchardCore.Translation.Services;

/// <summary>A translation file ready for download.</summary>
public sealed record ExportedFile(string FileName, string ContentType, string Content);

/// <summary>
/// What an import did with one job's segments. <paramref name="Imported"/> counts the texts written
/// (with "also import texts whose source changed" ticked, those are included);
/// <paramref name="SourceChanged"/> the ones skipped because their source changed since the export.
/// </summary>
public sealed record ImportCounts(int Imported, int SourceChanged, int Untranslated, int InvalidMarkup, int Missing);

/// <summary>An error of the import glue: the code and its argument (plugin name or culture).</summary>
public sealed class TranslationFileGlueException(string code, string argument = null) : Exception(code)
{
    /// <summary>The file belongs to another system (argument: its plugin name).</summary>
    public const string OtherSystem = "other_system";

    /// <summary>The reference is unknown or its content item no longer exists.</summary>
    public const string NotFound = "not_found";

    /// <summary>The site has no such culture (argument: the culture).</summary>
    public const string NoLanguage = "no_language";

    /// <summary>The upload is larger than <see cref="TranslationFileExchange.MaxUploadBytes"/>.</summary>
    public const string TooLarge = "too_large";

    public string Code { get; } = code;

    public string Argument { get; } = argument;
}

/// <summary>
/// "Export for translation" / "Import translation" without Orchard Core services (unit-tested):
/// builds the SDK's <see cref="FileJob"/>s from the segments <see cref="ContentWalker"/> collects (the
/// same keys as the API translation, <see cref="PendingSegment.Path"/>), names and encodes the file,
/// and applies an imported file's translations to freshly collected segments. The controller does the
/// rest: permissions, loading items, the write-back (<see cref="ContentTranslator.Apply"/>).
/// </summary>
public static class TranslationFileExchange
{
    /// <summary>The <c>plugin</c> of every job: the repository name.</summary>
    public const string PluginName = "OrchardCore-Supertext-Translation";

    /// <summary>The first part of the download's file name.</summary>
    public const string ShortName = "orchardcore";

    /// <summary>Uploads larger than this are refused before they are read.</summary>
    public const long MaxUploadBytes = 10 * 1024 * 1024;

    public const string FormatXliff = "xliff";

    public const string FormatHtml = "html";

    private const string ReferencePrefix = "item:";

    /// <summary>The job reference: <c>item:&lt;ContentItemId&gt;:&lt;culture&gt;</c> (the source item and the target culture).</summary>
    public static string Reference(string contentItemId, string culture) => ReferencePrefix + contentItemId + ":" + culture;

    /// <summary>Parses <see cref="Reference"/>; false for anything else.</summary>
    public static bool TryParseReference(string reference, out string contentItemId, out string culture)
    {
        contentItemId = culture = null;
        if (reference is null || !reference.StartsWith(ReferencePrefix, StringComparison.Ordinal))
        {
            return false;
        }
        var parts = reference[ReferencePrefix.Length..].Split(':');
        if (parts.Length != 2 || parts[0].Trim() == string.Empty || parts[1].Trim() == string.Empty)
        {
            return false;
        }
        contentItemId = parts[0];
        culture = parts[1];
        return true;
    }

    /// <summary>
    /// The Supertext code the language mapping resolves, else the culture with <c>_</c> replaced by
    /// <c>-</c>: a file may go to any translator, so an unmapped culture doesn't block the export.
    /// </summary>
    public static string LanguageCode(Func<string> resolve, string culture)
    {
        try
        {
            return resolve();
        }
        catch (SupertextException)
        {
            return (culture ?? string.Empty).Replace('_', '-');
        }
    }

    /// <summary>The SDK's segments for the collected texts, keyed by their path.</summary>
    public static List<FileSegment> Segments(IEnumerable<PendingSegment> segments)
        => segments.Select(s => new FileSegment(s.Path, s.Segment.Text, s.Segment.IsHtml)).ToList();

    /// <summary>One job: the source item's texts for one target culture.</summary>
    public static FileJob Job(string contentItemId, string targetCulture, string sourceCode, string targetCode, IEnumerable<PendingSegment> segments)
        => new(PluginName, Reference(contentItemId, targetCulture), sourceCode, targetCode, Segments(segments));

    /// <summary>
    /// The file for one item: XLIFF with one <c>&lt;file&gt;</c> per culture, or HTML for exactly one culture.
    /// Named <c>orchardcore-&lt;id&gt;-&lt;culture&gt;.xlf</c> (<c>.html</c>), or <c>orchardcore-&lt;id&gt;.xlf</c> for several cultures.
    /// </summary>
    /// <param name="jobs">One job per target culture, in the order of <paramref name="cultures"/>.</param>
    public static ExportedFile Export(string format, string contentItemId, IReadOnlyList<string> cultures, IReadOnlyList<FileJob> jobs)
    {
        var baseName = ShortName + "-" + SafeName(contentItemId) + (cultures.Count == 1 ? "-" + SafeName(cultures[0]) : string.Empty);
        if (format == FormatHtml)
        {
            if (jobs.Count != 1)
            {
                throw new ArgumentException("An HTML file holds exactly one language.", nameof(jobs));
            }
            return new ExportedFile(baseName + ".html", "text/html; charset=utf-8", TranslationFiles.ExportHtml(jobs[0]));
        }
        return new ExportedFile(baseName + ".xlf", "application/x-xliff+xml", TranslationFiles.ExportXliff(jobs));
    }

    /// <summary>Reads an upload (size checked first) and returns its jobs; <see cref="TranslationFileException"/> for a bad file.</summary>
    public static IReadOnlyList<TranslationFile> Read(Stream upload, long length)
    {
        if (length > MaxUploadBytes)
        {
            throw new TranslationFileGlueException(TranslationFileGlueException.TooLarge);
        }
        using var reader = new StreamReader(upload, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return TranslationFiles.Read(reader.ReadToEnd());
    }

    /// <summary>Checks that a job read from a file is this module's; returns the content item id and culture of its reference.</summary>
    public static (string ContentItemId, string Culture) Locate(TranslationFile file)
    {
        if (!string.Equals(file.Plugin, PluginName, StringComparison.Ordinal))
        {
            throw new TranslationFileGlueException(TranslationFileGlueException.OtherSystem, file.Plugin);
        }
        if (!TryParseReference(file.Reference, out var contentItemId, out var culture))
        {
            throw new TranslationFileGlueException(TranslationFileGlueException.NotFound);
        }
        return (contentItemId, culture);
    }

    /// <summary>
    /// Imports a file's translations into the current segments (collected again from the item): <c>ok</c>
    /// texts, and <c>source_changed</c> ones when <paramref name="includeChanged"/>, are applied through
    /// <see cref="PendingSegment.Apply"/>; <c>untranslated</c>, <c>invalid_markup</c> and <c>missing</c> keep the source text.
    /// </summary>
    public static ImportCounts Import(TranslationFile file, IReadOnlyList<PendingSegment> current, bool includeChanged)
    {
        var byKey = current.GroupBy(s => s.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        int imported = 0, changed = 0, untranslated = 0, invalid = 0, missing = 0;
        foreach (var result in TranslationFiles.ImportTranslations(file, Segments(current)))
        {
            switch (result.Status)
            {
                case ImportStatuses.Ok:
                    byKey[result.Key].Apply(result.Text);
                    imported++;
                    break;
                case ImportStatuses.SourceChanged when includeChanged:
                    byKey[result.Key].Apply(result.Text);
                    imported++;
                    break;
                case ImportStatuses.SourceChanged:
                    changed++;
                    break;
                case ImportStatuses.Untranslated:
                    untranslated++;
                    break;
                case ImportStatuses.InvalidMarkup:
                    invalid++;
                    break;
                default:
                    missing++;
                    break;
            }
        }
        return new ImportCounts(imported, changed, untranslated, invalid, missing);
    }

    private static string SafeName(string value)
        => new((value ?? string.Empty).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
}
