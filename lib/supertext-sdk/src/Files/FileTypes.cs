namespace Supertext.OrchardCore.Translation.Sdk.Files;

/// <summary>One text of a job: the integration's key, the text, and whether it is HTML.</summary>
/// <param name="Key">The integration's own key for the text (unique in the job), e.g. <c>title</c> or <c>body.3</c>.</param>
/// <param name="Text">The text, or an HTML fragment when <paramref name="Html"/> is true.</param>
/// <param name="Html">Whether <paramref name="Text"/> is HTML.</param>
public sealed record FileSegment(string Key, string Text, bool Html = false);

/// <summary>The texts of one item in one language pair, as exported into a file.</summary>
/// <param name="Plugin">The integration's name (its repository name), e.g. <c>Contao-Supertext-Translation</c>.</param>
/// <param name="Reference">What the integration needs to find the item again, e.g. <c>page:12</c>. Opaque to the SDK.</param>
/// <param name="SourceLanguage">The source language code.</param>
/// <param name="TargetLanguage">The target language code.</param>
/// <param name="Segments">The texts; keys unique in the job.</param>
public sealed record FileJob(string Plugin, string Reference, string SourceLanguage, string TargetLanguage, IReadOnlyList<FileSegment> Segments);

/// <summary>One unit read back from a file.</summary>
/// <param name="Key">The segment's key.</param>
/// <param name="Checksum">The source's checksum at export time, or <c>null</c> when the file doesn't carry one.</param>
public sealed record FileUnit(string Key, string? Checksum);

/// <summary>
/// One job read back from a file by <see cref="TranslationFiles.Read"/> (an XLIFF file can hold several).
/// Pass it to <see cref="TranslationFiles.ImportTranslations"/>; only instances returned by <c>Read</c> carry the translations.
/// </summary>
/// <param name="Format">One of <see cref="TranslationFileFormats"/>.</param>
/// <param name="Plugin">The integration's name from the file.</param>
/// <param name="Reference">The item reference from the file.</param>
/// <param name="SourceLanguage">The source language from the file.</param>
/// <param name="TargetLanguage">The target language from the file.</param>
/// <param name="Units">The units in the file's order.</param>
public sealed record TranslationFile(string Format, string Plugin, string Reference, string SourceLanguage, string TargetLanguage, IReadOnlyList<FileUnit> Units);

/// <summary>The translation of one current segment.</summary>
/// <param name="Key">The segment's key.</param>
/// <param name="Status">One of <see cref="ImportStatuses"/>.</param>
/// <param name="Text">The translation (plain text or HTML, like the segment) for <c>ok</c> and <c>source_changed</c>, else <c>null</c>.</param>
public sealed record ImportedSegment(string Key, string Status, string? Text);

/// <summary>The values of <see cref="TranslationFile.Format"/>.</summary>
public static class TranslationFileFormats
{
    /// <summary>An XLIFF 1.2 file.</summary>
    public const string Xliff = "xliff";

    /// <summary>An HTML file.</summary>
    public const string Html = "html";
}

/// <summary>The values of <see cref="ImportedSegment.Status"/>. The values are fixed and the same in every Supertext SDK.</summary>
public static class ImportStatuses
{
    /// <summary>Translated, source unchanged.</summary>
    public const string Ok = "ok";

    /// <summary>Translated, but the source changed since the export (or the file has no checksum): the integration decides.</summary>
    public const string SourceChanged = "source_changed";

    /// <summary>No translation in the file.</summary>
    public const string Untranslated = "untranslated";

    /// <summary>The translation uses markup the current source doesn't have.</summary>
    public const string InvalidMarkup = "invalid_markup";

    /// <summary>The file has no such key.</summary>
    public const string Missing = "missing";
}

/// <summary>
/// The error codes of <see cref="TranslationFileException.Code"/>. The values are fixed and the same in
/// every Supertext SDK.
/// </summary>
public static class TranslationFileErrorCodes
{
    /// <summary>Export: no jobs, an empty or invalid metadata field or key, duplicate keys, or only blank segments.</summary>
    public const string InvalidJob = "invalid_job";

    /// <summary>Read: neither XLIFF nor HTML, not well-formed XML, a DTD; import: a file not returned by <c>Read</c>.</summary>
    public const string InvalidFile = "invalid_file";

    /// <summary>Read: the file lacks the job's metadata (plugin, reference, languages, segments).</summary>
    public const string MissingMetadata = "missing_metadata";

    /// <summary>Read: the file has more than <see cref="TranslationFiles.MaxFileCharacters"/> characters.</summary>
    public const string TooLarge = "too_large";
}

/// <summary>
/// An error from <see cref="TranslationFiles"/>. <see cref="Exception.Message"/> is English;
/// use <see cref="Code"/> (one of <see cref="TranslationFileErrorCodes"/>) to show your own text.
/// </summary>
public sealed class TranslationFileException : Exception
{
    /// <summary>Creates an error.</summary>
    public TranslationFileException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>One of <see cref="TranslationFileErrorCodes"/>.</summary>
    public string Code { get; }
}
