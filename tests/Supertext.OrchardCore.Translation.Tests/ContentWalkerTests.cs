using System.Text.Json.Nodes;
using Supertext.OrchardCore.Translation.Api;
using Supertext.OrchardCore.Translation.Services;
using Supertext.OrchardCore.Translation.Settings;
using Xunit;

namespace Supertext.OrchardCore.Translation.Tests;

public class ContentWalkerTests
{
    private static readonly Dictionary<string, IReadOnlyList<PartSchema>> Types = new()
    {
        ["Article"] =
        [
            new("TitlePart", "TitlePart", []),
            new("Article", "Article",
            [
                new("Subtitle", "TextField", null),
                new("Color", "TextField", "Color"),
                new("Website", "LinkField", null),
                new("Image", "MediaField", null),
            ]),
            new("HtmlBodyPart", "HtmlBodyPart", []),
            new("AutoroutePart", "AutoroutePart", []),
            new("FlowPart", "FlowPart", []),
        ],
        ["Paragraph"] = [new("Paragraph", "Paragraph", [new("Content", "HtmlField", "Wysiwyg")])],
        ["Code"] = [new("Code", "Code", [new("Content", "HtmlField", "Monaco")])],
    };

    private static ContentWalker Walker(params string[] excluded)
        => new(type => Task.FromResult(Types.TryGetValue(type, out var parts) ? parts : (IReadOnlyList<PartSchema>)[]), excluded);

    private static JsonObject Article() => JsonNode.Parse("""
        {
          "ContentType": "Article",
          "TitlePart": { "Title": "Hello" },
          "Article": {
            "Subtitle": { "Text": "A subtitle" },
            "Color": { "Text": "#ff0000" },
            "Website": { "Url": "https://example.com", "Text": "Our website" },
            "Image": { "Paths": ["a.jpg", "b.jpg"], "MediaTexts": ["A cat", ""] }
          },
          "HtmlBodyPart": { "Html": "<p>Body <b>text</b></p>" },
          "AutoroutePart": { "Path": "en/hello" },
          "FlowPart": { "Widgets": [
            { "ContentType": "Paragraph", "Paragraph": { "Content": { "Html": "<p>Widget text</p>" } } },
            { "ContentType": "Code", "Code": { "Content": { "Html": "<pre>var x = 1;</pre>" } } }
          ] }
        }
        """)!.AsObject();

    [Fact]
    public async Task Finds_prose_in_parts_fields_and_widgets()
    {
        var segments = await Walker().CollectAsync(Article(), "Article");
        var texts = segments.Select(s => s.Segment.Text).ToList();
        Assert.Equal(["Hello", "A subtitle", "Our website", "A cat", "<p>Body <b>text</b></p>", "<p>Widget text</p>"], texts);
        Assert.True(segments.Single(s => s.Segment.Text.StartsWith("<p>Body")).Segment.IsHtml);
        Assert.False(segments.Single(s => s.Segment.Text == "Hello").Segment.IsHtml);
    }

    [Fact]
    public async Task Apply_writes_back_into_the_json()
    {
        var item = Article();
        var segments = await Walker().CollectAsync(item, "Article");
        foreach (var s in segments)
        {
            s.Apply("[de] " + s.Segment.Text);
        }
        Assert.Equal("[de] Hello", (string)item["TitlePart"]!["Title"]!);
        Assert.Equal("[de] A cat", (string)item["Article"]!["Image"]!["MediaTexts"]![0]!);
        Assert.Equal("[de] <p>Widget text</p>", (string)item["FlowPart"]!["Widgets"]![0]!["Paragraph"]!["Content"]!["Html"]!);
        Assert.Equal("https://example.com", (string)item["Article"]!["Website"]!["Url"]!);
        Assert.Equal("en/hello", (string)item["AutoroutePart"]!["Path"]!);
    }

    [Fact]
    public async Task Excluded_fields_and_parts_are_skipped()
    {
        var segments = await Walker("Article.Subtitle", "HtmlBodyPart", "Paragraph.Paragraph.Content").CollectAsync(Article(), "Article");
        Assert.Equal(["Hello", "Our website", "A cat"], segments.Select(s => s.Segment.Text));
    }

    [Theory]
    [InlineData("Hello world", true)]
    [InlineData("https://example.com/page", false)]
    [InlineData("info@example.com", false)]
    [InlineData("/en/about", false)]
    [InlineData("42.5 %", false)]
    [InlineData("  ", false)]
    [InlineData("#anchor", false)]
    public void Only_prose_is_translated(string text, bool expected)
        => Assert.Equal(expected, ContentWalker.ShouldTranslate(text));

    [Fact]
    public void Language_mapping_and_exclusions_are_parsed()
    {
        var settings = new SupertextSettings { LanguageMapping = "de = de-CH\nfr=fr-FR\n", ExcludedFields = "A.B, C\n D " };
        Assert.Equal("de-CH", SupertextConfiguration.TargetLanguageCode(settings, "DE"));
        Assert.Equal("it-CH", SupertextConfiguration.TargetLanguageCode(settings, "it-CH"));
        Assert.Equal("it-IT", SupertextConfiguration.TargetLanguageCode(settings, "it")); // the SDK's default target
        Assert.Throws<SupertextException>(() => SupertextConfiguration.TargetLanguageCode(settings, "en-IE")); // no obvious default
        Assert.Equal("en", SupertextConfiguration.SourceLanguageCode(settings, "en-US"));
        Assert.Equal("de", SupertextConfiguration.SourceLanguageCode(settings, "de"));
        Assert.Equal(["A.B", "C", "D"], SupertextConfiguration.ExcludedFields(settings));
    }
}
