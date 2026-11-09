using Supertext.OrchardCore.Translation.Api;
using Xunit;

namespace Supertext.OrchardCore.Translation.Tests;

/// <summary>Cultures → Supertext codes: the mapping, else an exact Supertext code, else the SDK default (de → de-DE), else unsupported_language.</summary>
public class LanguageMappingTests
{
    private static readonly IReadOnlyDictionary<string, string> Mapping = LanguageMapping.Parse("de = de-CH\nen=en-US\n\nbroken line\nzh-CN=zh-Hans\nDE=de-AT\n");

    [Fact]
    public void Parses_and_serializes_the_setting()
    {
        Assert.Equal(3, Mapping.Count);
        Assert.Equal("de-CH", Mapping["de"]); // first entry per culture wins
        Assert.Equal("de=de-CH\nen=en-US", LanguageMapping.Serialize([new("de", " de-CH "), new("en", "en-US"), new("fr", "  ")]));
        Assert.Empty(LanguageMapping.Parse(null));
    }

    [Theory]
    [InlineData("de", "de-CH")]        // mapped
    [InlineData("DE", "de-CH")]        // keys compared like codes
    [InlineData("zh_cn", "zh-Hans")]
    [InlineData("de-CH", "de-CH")]     // exact Supertext code
    [InlineData("fr_ch", "fr-CH")]
    [InlineData("es", "es")]           // bare for languages without regions
    [InlineData("sl", "sl")]
    [InlineData("fr", "fr-FR")]        // SDK defaults
    [InlineData("it", "it-IT")]
    [InlineData("es-ES", "es")]
    [InlineData("sl-SI", "sl")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-CN", "zh-Hans")]
    public void Target_is_the_mapping_an_exact_code_or_the_default(string culture, string expected)
        => Assert.Equal(expected, LanguageMapping.Target(culture, Mapping));

    [Theory]
    [InlineData("en-IE")]   // no obvious default: needs a mapping
    [InlineData("de-LI")]
    [InlineData("gsw")]
    [InlineData("sr-RS")]
    [InlineData("xx")]
    public void Unmapped_culture_is_an_unsupported_language(string culture)
    {
        var error = Assert.Throws<SupertextException>(() => LanguageMapping.Target(culture, new Dictionary<string, string>()));
        Assert.Equal("unsupported_language", error.Code);
        Assert.Equal("The language {0} has no Supertext language yet. Ask your administrator to map it under Settings → Supertext → Language mapping.", error.Template);
        Assert.Equal(new object[] { culture }, error.Args);
        Assert.Null(LanguageMapping.TargetOrNull(culture, new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("en-US", "en")]
    [InlineData("de", "de")]      // mapped to de-CH, sent in its source form
    [InlineData("zh-CN", "zh")]   // mapped to zh-Hans
    [InlineData("", "")]          // auto-detect
    public void Source_is_the_bare_language(string culture, string expected)
        => Assert.Equal(expected, LanguageMapping.Source(culture, Mapping));

    [Fact]
    public void Unknown_source_is_an_unsupported_language()
        => Assert.Equal("unsupported_language", Assert.Throws<SupertextException>(() => LanguageMapping.Source("xx", Mapping)).Code);

    [Theory]
    [InlineData("de-CH", "more", "more")]
    [InlineData("fr-CH", "less", "less")]
    [InlineData("en-US", "more", "default")]   // English has no formal/informal choice
    [InlineData("rm", "less", "default")]
    [InlineData("xx-YY", "more", "more")]      // unknown (mapped) code: as set
    [InlineData("de-CH", "default", "default")]
    public void Politeness_only_where_Supertext_offers_it(string target, string setting, string expected)
        => Assert.Equal(expected, LanguageMapping.PolitenessFor(target, setting));

    [Fact]
    public void Lists_the_supported_targets()
    {
        Assert.Contains(LanguageMapping.Targets, l => l.Code == "de-CH" && l.Politeness);
        Assert.DoesNotContain(LanguageMapping.Targets, l => l.Code == "de");
        Assert.Equal("de-CH", LanguageMapping.MatchTarget("de_ch"));
        Assert.Null(LanguageMapping.MatchTarget("de"));
        Assert.False(LanguageMapping.SupportsPoliteness("en-GB"));
        Assert.Equal(LanguageMapping.Targets.Count, LanguageMapping.TargetOptions.Count);
        Assert.True(LanguageMapping.IsSupportedTarget("de_ch"));
        Assert.False(LanguageMapping.IsSupportedTarget("es-ES"));
    }

    [Theory]
    [InlineData("de-CH", "de-CH")]   // exact
    [InlineData("de", "de-DE")]      // default
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("nl_NL", "nl")]
    [InlineData("en-IE", null)]      // the select shows "— choose —"
    [InlineData("gsw", null)]
    public void Suggests_the_exact_code_else_the_default(string culture, string expected)
        => Assert.Equal(expected, LanguageMapping.SuggestTarget(culture));

    [Fact]
    public void Migrates_stored_codes_without_losing_any()
    {
        var migrated = LanguageMapping.Migrate(LanguageMapping.Parse("es=es-ES\nde=de_ch\nfr=fr\nen-IE=en-GB\nxx=xx-YY"));
        Assert.Equal("es", migrated["es"]);        // not a Supertext code, obvious replacement
        Assert.Equal("de-CH", migrated["DE"]);     // Supertext's spelling
        Assert.Equal("fr-FR", migrated["fr"]);
        Assert.Equal("en-GB", migrated["en-IE"]);  // already a Supertext code
        Assert.Equal("xx-YY", migrated["xx"]);     // unknown: kept, the settings page flags it
    }
}
