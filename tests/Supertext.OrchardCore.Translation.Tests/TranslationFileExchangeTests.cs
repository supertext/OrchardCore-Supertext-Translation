using System.Text;
using System.Text.Json.Nodes;
using Supertext.OrchardCore.Translation.Api;
using Supertext.OrchardCore.Translation.Sdk.Files;
using Supertext.OrchardCore.Translation.Services;
using Supertext.OrchardCore.Translation.Settings;
using Xunit;

namespace Supertext.OrchardCore.Translation.Tests;

/// <summary>Export for translation / Import translation: the glue around the SDK's TranslationFiles.</summary>
public class TranslationFileExchangeTests
{
    private static readonly Dictionary<string, IReadOnlyList<PartSchema>> Types = new()
    {
        ["Article"] =
        [
            new("TitlePart", "TitlePart", []),
            new("Article", "Article", [new("Subtitle", "TextField", null)]),
            new("HtmlBodyPart", "HtmlBodyPart", []),
        ],
    };

    private static ContentWalker Walker()
        => new(type => Task.FromResult(Types.TryGetValue(type, out var parts) ? parts : (IReadOnlyList<PartSchema>)[]), []);

    private static JsonObject Article(string title = "Hello", string body = "<p>Body <b>text</b></p>") => new()
    {
        ["ContentType"] = "Article",
        ["TitlePart"] = new JsonObject { ["Title"] = title },
        ["Article"] = new JsonObject { ["Subtitle"] = new JsonObject { ["Text"] = "A subtitle" } },
        ["HtmlBodyPart"] = new JsonObject { ["Html"] = body },
    };

    private static Task<List<PendingSegment>> Collect(JsonObject item) => Walker().CollectAsync(item, "Article");

    private static async Task<string> ExportXliff(string culture = "fr-CH")
    {
        var segments = await Collect(Article());
        var job = TranslationFileExchange.Job("4abc", culture, "en", "fr-CH", segments);
        return TranslationFileExchange.Export(TranslationFileExchange.FormatXliff, "4abc", [culture], [job]).Content;
    }

    /// <summary>Fills every target of an exported XLIFF file with "[fr] " + source.</summary>
    private static string Translate(string xliff)
        => System.Text.RegularExpressions.Regex.Replace(xliff, @"<source>(.*?)</source>", m => $"<source>{m.Groups[1].Value}</source><target>[fr] {m.Groups[1].Value}</target>");

    [Fact]
    public void Reference_round_trip()
    {
        var reference = TranslationFileExchange.Reference("4abcd3fg", "fr-CH");
        Assert.Equal("item:4abcd3fg:fr-CH", reference);
        Assert.True(TranslationFileExchange.TryParseReference(reference, out var id, out var culture));
        Assert.Equal(("4abcd3fg", "fr-CH"), (id, culture));
        Assert.False(TranslationFileExchange.TryParseReference("page:12", out _, out _));
        Assert.False(TranslationFileExchange.TryParseReference("item::fr", out _, out _));
        Assert.False(TranslationFileExchange.TryParseReference("item:a:b:c", out _, out _));
        Assert.False(TranslationFileExchange.TryParseReference(null, out _, out _));
    }

    [Fact]
    public async Task Export_uses_the_collector_keys_and_the_mapping()
    {
        var segments = await Collect(Article());
        var settings = new SupertextSettings { LanguageMapping = "fr=fr-CH" };
        var target = TranslationFileExchange.LanguageCode(() => SupertextConfiguration.TargetLanguageCode(settings, "fr"), "fr");
        var source = TranslationFileExchange.LanguageCode(() => SupertextConfiguration.SourceLanguageCode(settings, "en-US"), "en-US");
        var job = TranslationFileExchange.Job("4abc", "fr", source, target, segments);

        Assert.Equal(TranslationFileExchange.PluginName, job.Plugin);
        Assert.Equal("item:4abc:fr", job.Reference);
        Assert.Equal(("en", "fr-CH"), (job.SourceLanguage, job.TargetLanguage));
        Assert.Equal(segments.Select(s => s.Path), job.Segments.Select(s => s.Key));
        Assert.True(job.Segments.Single(s => s.Key.EndsWith("/HtmlBodyPart/Html", StringComparison.Ordinal)).Html);

        var file = TranslationFileExchange.Export(TranslationFileExchange.FormatXliff, "4abc", ["fr"], [job]);
        Assert.Equal("orchardcore-4abc-fr.xlf", file.FileName);
        Assert.Equal("application/x-xliff+xml", file.ContentType);
        var read = Assert.Single(TranslationFiles.Read(file.Content));
        Assert.Equal(segments.Select(s => s.Path), read.Units.Select(u => u.Key));

        var html = TranslationFileExchange.Export(TranslationFileExchange.FormatHtml, "4abc", ["fr"], [job]);
        Assert.Equal(("orchardcore-4abc-fr.html", "text/html; charset=utf-8"), (html.FileName, html.ContentType));
        Assert.Equal("orchardcore-4abc.xlf", TranslationFileExchange.Export(TranslationFileExchange.FormatXliff, "4abc", ["fr", "de"], [job, job with { Reference = "item:4abc:de" }]).FileName);
    }

    [Fact]
    public void Unmapped_languages_do_not_block_the_export()
    {
        var settings = new SupertextSettings();
        Assert.Equal("en-IE", TranslationFileExchange.LanguageCode(() => SupertextConfiguration.TargetLanguageCode(settings, "en_IE"), "en_IE"));
        Assert.Equal("de-DE", TranslationFileExchange.LanguageCode(() => SupertextConfiguration.TargetLanguageCode(settings, "de"), "de"));
    }

    [Fact]
    public async Task Import_writes_ok_texts_through_the_segments()
    {
        var file = Assert.Single(TranslationFiles.Read(Translate(await ExportXliff())));
        Assert.Equal(("4abc", "fr-CH"), TranslationFileExchange.Locate(file));

        var item = Article();
        var counts = TranslationFileExchange.Import(file, await Collect(item), includeChanged: false);

        Assert.Equal(new ImportCounts(3, 0, 0, 0, 0), counts);
        Assert.Equal("[fr] Hello", (string)item["TitlePart"]!["Title"]!);
        Assert.Equal("[fr] <p>Body <b>text</b></p>", (string)item["HtmlBodyPart"]!["Html"]!);
    }

    [Fact]
    public async Task Source_changed_is_written_only_with_the_option()
    {
        var file = Assert.Single(TranslationFiles.Read(Translate(await ExportXliff())));

        var skipped = Article(title: "Hello again");
        Assert.Equal(new ImportCounts(2, 1, 0, 0, 0), TranslationFileExchange.Import(file, await Collect(skipped), includeChanged: false));
        Assert.Equal("Hello again", (string)skipped["TitlePart"]!["Title"]!);

        var included = Article(title: "Hello again");
        Assert.Equal(new ImportCounts(3, 0, 0, 0, 0), TranslationFileExchange.Import(file, await Collect(included), includeChanged: true));
        Assert.Equal("[fr] Hello", (string)included["TitlePart"]!["Title"]!);
    }

    [Fact]
    public async Task Untranslated_invalid_and_missing_are_not_written()
    {
        var xliff = await ExportXliff();
        // Only the title is translated; the body gets markup the source doesn't have.
        var translated = System.Text.RegularExpressions.Regex.Replace(xliff, @"(<trans-unit id=""Article/TitlePart/Title""[^>]*>\s*<source>Hello</source>)", "$1<target>Bonjour</target>");
        translated = System.Text.RegularExpressions.Regex.Replace(translated, @"(<trans-unit id=""Article/HtmlBodyPart/Html""[^>]*>\s*<source>.*?</source>)", "$1<target>Corps <script>x</script></target>");
        var file = Assert.Single(TranslationFiles.Read(translated));

        var item = Article();
        var segments = await Collect(item);
        // A segment added since the export: "missing" in the file.
        segments.Add(new PendingSegment(new HtmlDocument.Segment("New text", false), "Article/New/Text", _ => throw new InvalidOperationException("must not be written")));

        var counts = TranslationFileExchange.Import(file, segments, includeChanged: true);

        Assert.Equal(1, counts.Imported);
        Assert.Equal(1, counts.Untranslated);
        Assert.Equal(1, counts.InvalidMarkup);
        Assert.Equal(1, counts.Missing);
        Assert.Equal("Bonjour", (string)item["TitlePart"]!["Title"]!);
        Assert.Equal("A subtitle", (string)item["Article"]!["Subtitle"]!["Text"]!);
        Assert.Equal("<p>Body <b>text</b></p>", (string)item["HtmlBodyPart"]!["Html"]!);
    }

    [Fact]
    public async Task Other_systems_and_unknown_references_are_refused()
    {
        var other = new FileJob("Contao-Supertext-Translation", "page:12", "en", "fr-CH", [new FileSegment("title", "Hello")]);
        var file = Assert.Single(TranslationFiles.Read(TranslationFiles.ExportXliff([other])));
        var error = Assert.Throws<TranslationFileGlueException>(() => TranslationFileExchange.Locate(file));
        Assert.Equal((TranslationFileGlueException.OtherSystem, "Contao-Supertext-Translation"), (error.Code, error.Argument));

        var unknown = other with { Plugin = TranslationFileExchange.PluginName };
        file = Assert.Single(TranslationFiles.Read(TranslationFiles.ExportXliff([unknown])));
        Assert.Equal(TranslationFileGlueException.NotFound, Assert.Throws<TranslationFileGlueException>(() => TranslationFileExchange.Locate(file)).Code);

        Assert.Single(TranslationFiles.Read(await ExportXliff()));
    }

    [Fact]
    public void Too_large_uploads_are_refused_before_reading()
    {
        var stream = new ThrowingStream();
        var error = Assert.Throws<TranslationFileGlueException>(() => TranslationFileExchange.Read(stream, TranslationFileExchange.MaxUploadBytes + 1));
        Assert.Equal(TranslationFileGlueException.TooLarge, error.Code);
    }

    [Fact]
    public void Bad_files_are_translation_file_errors()
    {
        var error = Assert.Throws<TranslationFileException>(() => TranslationFileExchange.Read(new MemoryStream(Encoding.UTF8.GetBytes("<p>not a file</p>")), 17));
        Assert.Contains(error.Code, new[] { TranslationFileErrorCodes.InvalidFile, TranslationFileErrorCodes.MissingMetadata });
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("read");
    }
}
