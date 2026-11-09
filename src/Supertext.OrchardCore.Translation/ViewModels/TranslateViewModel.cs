namespace Supertext.OrchardCore.Translation.ViewModels;

public sealed class TranslateViewModel
{
    public string ContentItemId { get; set; }

    public string Title { get; set; }

    public string ContentType { get; set; }

    public string SourceCulture { get; set; }

    public string SourceCultureName { get; set; }

    public bool HasDraft { get; set; }

    public bool ApiKeyMissing { get; set; }

    public bool ConfirmOverwrite { get; set; }

    public bool ShowOverwriteWarning { get; set; }

    public string ReturnUrl { get; set; }

    public List<TranslateTarget> Targets { get; set; } = [];

    /// <summary>Export for translation: the chosen format (<c>xliff</c> or <c>html</c>).</summary>
    public string ExportFormat { get; set; } = "xliff";

    /// <summary>Export for translation: the chosen cultures.</summary>
    public List<string> ExportCultures { get; set; } = [];

    /// <summary>Import translation: the option to import texts whose source changed since the export.</summary>
    public bool IncludeChanged { get; set; }

    /// <summary>Import translation: an error with the whole file (shown instead of a summary).</summary>
    public string ImportError { get; set; }

    /// <summary>Import translation: one row per job in the file, null before an import.</summary>
    public List<ImportJobSummary> ImportResults { get; set; }
}

/// <summary>The result of importing one job (one item in one culture) of a translation file.</summary>
public sealed class ImportJobSummary
{
    public string Title { get; set; }

    public string Culture { get; set; }

    public string CultureName { get; set; }

    /// <summary>Why nothing was imported (other system, not found, no language, permissions), else null.</summary>
    public string Error { get; set; }

    /// <summary>The counts, null when <see cref="Error"/> is set.</summary>
    public Services.ImportCounts Counts { get; set; }

    /// <summary>The localization that was written (as a draft), null if nothing was written.</summary>
    public string SavedContentItemId { get; set; }
}

public sealed class TranslateTarget
{
    public string Culture { get; set; }

    public string CultureName { get; set; }

    public bool Selected { get; set; }

    /// <summary>Content item of the existing localization, null if there is none yet.</summary>
    public string ExistingContentItemId { get; set; }

    public string ExistingTitle { get; set; }

    public bool ExistingPublished { get; set; }

    public bool ExistingHasDraft { get; set; }
}
