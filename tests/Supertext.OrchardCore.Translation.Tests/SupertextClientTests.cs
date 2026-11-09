using System.Net;
using System.Text;
using Supertext.OrchardCore.Translation.Api;
using Supertext.OrchardCore.Translation.Drivers;
using Supertext.OrchardCore.Translation.Services;
using Supertext.OrchardCore.Translation.Settings;
using Supertext.OrchardCore.Translation.ViewModels;
using Xunit;

namespace Supertext.OrchardCore.Translation.Tests;

/// <summary>The wrapper around the bundled Supertext SDK, against a fake HTTP handler.</summary>
public class SupertextClientTests
{
    private static readonly SupertextConnection Connection = new("https://api.test/v1/", "Supertext-Auth-Key test-key", PollIntervalSeconds: 1, PollTimeoutSeconds: 10);

    [Fact]
    public async Task Translates_segments_in_one_document_and_keeps_liquid()
    {
        var handler = new FakeApi(html => html
            .Replace(">Hello<", ">Hallo<")
            .Replace("<p>Hi ", "<p>Hoi ")
            .Replace("<div data-st-id=\"2\">Lost</div>", string.Empty));
        var client = new SupertextClient(new HttpClient(handler));

        var result = await client.TranslateSegmentsAsync(Connection,
        [
            new("Hello", false),
            new("<p>Hi {{ User.Name }}</p>", true),
            new("Lost", false),
            new("   ", false),
        ], "de-DE", "en", "more");

        Assert.Equal("Hallo", result[0]);
        Assert.Equal("<p>Hoi {{ User.Name }}</p>", result[1]);
        Assert.False(result.ContainsKey(2)); // missing in the answer: the source is kept
        Assert.False(result.ContainsKey(3)); // blank: never sent
        Assert.Equal(1, handler.Uploads);
        Assert.Contains("name=\"target_lang\"\r\n\r\nde-DE", handler.LastUpload);
        Assert.Contains("name=\"source_lang\"\r\n\r\nen", handler.LastUpload);
        Assert.DoesNotContain("{{", handler.LastUpload);
        Assert.Equal("Supertext-Auth-Key test-key", handler.Authorization);
    }

    [Fact]
    public async Task Unsupported_language_pair_names_the_codes()
    {
        var handler = new FakeApi(_ => "", rejectWith: (HttpStatusCode.NotFound, "{\"error_code\":\"INVALID_LANGUAGE_PAIR\",\"source_lang\":\"en\",\"target_lang\":\"xx-YY\"}"));
        var client = new SupertextClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<SupertextException>(() => client.TranslateDocumentAsync(Connection, "<p>x</p>", "xx-YY", "en", "default"));

        Assert.Equal("Supertext does not support this language pair ({0} → {1}). Check the language mapping under Settings → Supertext.", error.Template);
        Assert.Equal(new object[] { "en", "xx-YY" }, error.Args);
    }

    [Fact]
    public async Task Review_cultures_go_through_the_review_service_others_directly()
    {
        var handler = new FakeApi(html => html.Replace(">Hello<", ">Hallo<"));
        var client = new SupertextClient(new HttpClient(handler));
        var connection = Connection with { ReviewUrl = "https://review.test/v1/" };

        var reviewed = await client.TranslateSegmentsAsync(connection.ForReview(true), [new("Hello", false)], "de-CH", "en", "default");

        Assert.Equal("Hallo", reviewed[0]);
        Assert.Equal(
            ["POST /v1/translate/ai/file", "GET /v1/translate/ai/file/f1/status", "GET /v1/translate/ai/file/f1/translation", "DELETE /v1/translate/ai/file/f1"],
            handler.Requests.Select(r => r.Method + " " + r.Path).Distinct());   // upload, status (polled), download, delete
        Assert.All(handler.Requests, r => Assert.Equal("review.test", r.Host));
        Assert.Equal("Supertext-Auth-Key test-key", handler.Authorization);
        Assert.Contains("OrchardCore-Supertext-Translation/" + ModuleVersion.Current, handler.UserAgent);
        Assert.DoesNotContain("0.0.0", handler.UserAgent);

        handler.Requests.Clear();
        await client.TranslateSegmentsAsync(connection.ForReview(false), [new("Hello", false)], "fr-CH", "en", "default");
        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, r => Assert.Equal("api.test", r.Host));
    }

    [Fact]
    public void Review_is_off_without_a_review_url_or_with_the_api_url()
    {
        Assert.Same(Connection, Connection.ForReview(true));
        var same = Connection with { ReviewUrl = "https://api.test/v1" };
        Assert.Same(same, same.ForReview(true));
        var review = Connection with { ReviewUrl = SupertextClient.ReviewUrl };
        Assert.Same(review, review.ForReview(false));
        Assert.Equal(SupertextClient.ReviewUrl, review.ForReview(true).Endpoint);
        Assert.Equal("test-key", SupertextClient.NormalizeApiKey(review.ForReview(true).ApiKey));
        Assert.Equal(Connection.PollTimeoutSeconds, review.ForReview(true).PollTimeoutSeconds);
    }

    [Fact]
    public void Review_url_defaults_to_Supertexts_review_service_and_empty_turns_it_off()
    {
        Assert.Equal("https://localizationreview-production.up.railway.app/v1/", SupertextClient.ReviewUrl);
        Assert.Equal(SupertextClient.ReviewUrl, SupertextConfiguration.ResolveReviewUrl(null, null));
        Assert.Equal(string.Empty, SupertextConfiguration.ResolveReviewUrl("", "https://other.test/v1/"));   // SUPERTEXT_REVIEW_URL= (empty)
        Assert.Equal("https://other.test/v1/", SupertextConfiguration.ResolveReviewUrl(null, " https://other.test/v1/ "));
    }

    [Fact]
    public void Review_setting_per_culture()
    {
        var settings = new SupertextSettings { ReviewCultures = ["de-CH"] };
        Assert.True(SupertextConfiguration.ReviewFor(settings, "DE-ch"));
        Assert.False(SupertextConfiguration.ReviewFor(settings, "fr-CH"));
        Assert.False(SupertextConfiguration.ReviewFor(new SupertextSettings(), "de-CH"));   // older installs: off

        var merged = SupertextSettingsDisplayDriver.MergeReview(["de-CH", "es"],
        [
            new LanguageMappingRow { Culture = "de-CH", Review = false },
            new LanguageMappingRow { Culture = "fr-CH", Review = true },
        ]);
        Assert.Equal(["es", "fr-CH"], merged);   // es: a culture the site no longer has keeps its value
    }

    private sealed class FakeApi(Func<string, string> translate, (HttpStatusCode Status, string Body)? rejectWith = null) : HttpMessageHandler
    {
        private readonly Dictionary<string, string> files = [];

        public int Uploads { get; private set; }

        public string LastUpload { get; private set; } = string.Empty;

        public string Authorization { get; private set; }

        public string UserAgent { get; private set; }

        public List<(string Method, string Host, string Path)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.Host, request.RequestUri.AbsolutePath));
            UserAgent = request.Headers.UserAgent.ToString();
            Authorization = request.Headers.Authorization?.ToString() ?? string.Join(",", request.Headers.GetValues("Authorization"));
            var path = request.RequestUri!.AbsolutePath["/v1/".Length..];
            if (request.Method == HttpMethod.Post && path == "translate/ai/file")
            {
                if (rejectWith is { } reject)
                {
                    return Reply(reject.Status, reject.Body);
                }
                Uploads++;
                LastUpload = await request.Content!.ReadAsStringAsync(ct);
                var start = LastUpload.IndexOf("<!DOCTYPE", StringComparison.Ordinal);
                var end = LastUpload.LastIndexOf("</html>", StringComparison.Ordinal) + "</html>".Length;
                files["f1"] = translate(LastUpload[start..end]);
                return Reply(HttpStatusCode.OK, "{\"file_id\":\"f1\"}");
            }
            return path switch
            {
                "translate/ai/file/f1/status" => Reply(HttpStatusCode.OK, "{\"status\":\"done\"}"),
                "translate/ai/file/f1/translation" => Reply(HttpStatusCode.OK, files["f1"]),
                _ => Reply(HttpStatusCode.OK, "{}"),
            };
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8) };
    }
}
