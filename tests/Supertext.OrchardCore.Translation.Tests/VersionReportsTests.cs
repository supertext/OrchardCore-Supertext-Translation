using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OrchardCore.Entities;
using Supertext.OrchardCore.Translation.Api;
using Supertext.OrchardCore.Translation.PluginHealth;
using Supertext.OrchardCore.Translation.Sdk.V1;
using Supertext.OrchardCore.Translation.Services;
using Xunit;

namespace Supertext.OrchardCore.Translation.Tests;

/// <summary>The Commando Center glue: setting, site URL, ping, when reports are sent, state storage. Fake HTTP, never the live API or center.</summary>
public class VersionReportsTests
{
    private const string Key = "secret-test-key";
    private const string ReportUrl = "https://center.test/api/v1/report";

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(true, "0", false)]
    [InlineData(true, "off", false)]
    [InlineData(false, "1", true)]
    [InlineData(false, "maybe", false)]
    public void Setting_and_environment(bool setting, string environment, bool expected)
        => Assert.Equal(expected, VersionReports.Enabled(setting, environment));

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("true", true)]
    [InlineData("x", null)]
    public void Environment_override_for_the_settings_page(string environment, bool? expected)
        => Assert.Equal(expected, VersionReports.EnvironmentOverride(environment ?? string.Empty));

    [Theory]
    [InlineData("https://www.example.ch/", "http", "internal:8080", "", "https://www.example.ch")]
    [InlineData("", "https", "demo.example.ch", "/tenant", "https://demo.example.ch/tenant")]
    [InlineData("not a url", "http", "localhost:5000", null, "http://localhost:5000")]
    [InlineData(null, "ftp", "example.ch", "", "http://example.ch")]
    [InlineData(null, "https", "", "", null)]
    public void Site_url_from_the_base_url_setting_or_the_request(string baseUrl, string scheme, string host, string pathBase, string expected)
        => Assert.Equal(expected, VersionReports.SiteUrl(baseUrl, scheme, host, pathBase));

    [Fact]
    public void Ping_echoes_the_challenge_only_while_reports_are_on()
    {
        var ok = VersionReports.Ping("abcdefghijklmnop_-12", enabled: true);
        Assert.Equal(200, ok.Status);
        Assert.Equal("no-store", ok.Headers["Cache-Control"]);
        Assert.Equal("application/json", ok.Headers["Content-Type"]);
        Assert.Equal("OrchardCore-Supertext-Translation", JsonNode.Parse(ok.Body)!["plugin"]!.GetValue<string>());
        Assert.Equal("abcdefghijklmnop_-12", JsonNode.Parse(ok.Body)!["challenge"]!.GetValue<string>());

        Assert.Equal(400, VersionReports.Ping("short", enabled: true).Status);
        Assert.Equal(400, VersionReports.Ping("<script>alert(1)</script>xxxxxxxx", enabled: true).Status);
        Assert.Equal(400, VersionReports.Ping(null, enabled: true).Status);
        Assert.Equal(404, VersionReports.Ping("abcdefghijklmnop_-12", enabled: false).Status);
        Assert.Equal("https://www.example.ch/supertext/ping", VersionReports.CallbackUrl("https://www.example.ch/"));
    }

    [Fact]
    public async Task First_request_checks_the_key_and_reports_installed_once()
    {
        var http = new FakeHttp();
        var context = Context(http, enabled: true);

        var first = await VersionReports.RunAsync(ReporterAction.OnRequest, context, ReporterState.Empty, Key);

        Assert.Equal(ReportEvents.Installed, first.Event);
        Assert.True(first.Sent);
        Assert.Equal(KeyCheckStatus.Ok, first.Check!.Status);
        Assert.Equal(["GET https://api.test/v1/features", "POST " + ReportUrl], http.Requests);
        var report = JsonNode.Parse(http.Reports.Single())!;
        Assert.Equal("installed", report["event"]!.GetValue<string>());
        Assert.Equal("https://www.example.ch", report["site_url"]!.GetValue<string>());
        Assert.Equal("OrchardCore-Supertext-Translation", report["plugin"]!["name"]!.GetValue<string>());
        Assert.Equal("1.2.3", report["plugin"]!["version"]!.GetValue<string>());
        Assert.Equal("Orchard Core", report["system"]!["name"]!.GetValue<string>());
        Assert.Equal("3.0.1", report["system"]!["version"]!.GetValue<string>());
        Assert.Equal("https://www.example.ch/supertext/ping", report["callback"]!["url"]!.GetValue<string>());
        Assert.True(report["api_key_configured"]!.GetValue<bool>());
        Assert.Equal(Supertext.OrchardCore.Translation.Sdk.SdkInfo.Version, report["sdk_version"]!.GetValue<string>());
        Assert.DoesNotContain(Key, http.Reports.Single());

        // Same versions, same key: nothing to do, no request at all.
        var second = await VersionReports.RunAsync(ReporterAction.OnRequest, context, first.State, Key);
        Assert.Null(second.Event);
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal(first.State.ToJson(), second.State.ToJson());

        // A changed key is checked again; a new module version is reported as upgraded.
        var upgraded = await VersionReports.RunAsync(ReporterAction.OnRequest, context with { ModuleVersion = "1.3.0" }, second.State, Key);
        Assert.Equal(ReportEvents.Upgraded, upgraded.Event);
    }

    [Fact]
    public async Task Settings_save_reports_settings_saved_with_the_check()
    {
        var http = new FakeHttp { FeaturesStatus = HttpStatusCode.Unauthorized };
        var context = Context(http, enabled: true);
        var installed = await VersionReports.RunAsync(ReporterAction.AfterSettingsSaved, context, ReporterState.Empty, Key);
        Assert.Equal(ReportEvents.Installed, installed.Event);
        Assert.Equal(KeyCheckStatus.Invalid, installed.Check!.Status);

        var saved = await VersionReports.RunAsync(ReporterAction.AfterSettingsSaved, context, installed.State, Key);
        Assert.Equal(ReportEvents.SettingsSaved, saved.Event);
        Assert.Equal("invalid", JsonNode.Parse(http.Reports.Last())!["api_key_check"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Missing_key_is_checked_without_a_request()
    {
        var http = new FakeHttp();
        var result = await VersionReports.RunAsync(ReporterAction.AfterSettingsSaved, Context(http, enabled: false), ReporterState.Empty, "");
        Assert.Equal(KeyCheckStatus.Missing, result.Check!.Status);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Rejected_key_during_a_translation_is_reported_once()
    {
        var http = new FakeHttp();
        var context = Context(http, enabled: true);
        var installed = await VersionReports.RunAsync(ReporterAction.OnRequest, context, ReporterState.Empty, Key);

        var rejected = await VersionReports.RunAsync(ReporterAction.AuthenticationFailed, context, installed.State, Key, 403);
        Assert.Equal(ReportEvents.ApiKeyCheck, rejected.Event);
        Assert.Equal(KeyCheckStatus.Invalid, rejected.Check!.Status);
        Assert.Equal(403, rejected.Check.HttpStatus);

        var again = await VersionReports.RunAsync(ReporterAction.AuthenticationFailed, context, rejected.State, Key, 403);
        Assert.Null(again.Event);
        Assert.Equal(2, http.Reports.Count);
    }

    [Fact]
    public async Task Nothing_is_sent_with_reports_off_but_the_key_is_still_checked()
    {
        var http = new FakeHttp();
        var result = await VersionReports.RunAsync(ReporterAction.OnRequest, Context(http, enabled: false), ReporterState.Empty, Key);
        Assert.Null(result.Event);
        Assert.Equal(KeyCheckStatus.Ok, result.Check!.Status);
        Assert.Empty(http.Reports);
    }

    [Fact]
    public async Task A_failing_center_or_network_never_throws()
    {
        var http = new FakeHttp { Fail = true };
        var result = await VersionReports.RunAsync(ReporterAction.OnRequest, Context(http, enabled: true), ReporterState.Empty, Key);
        Assert.Equal(KeyCheckStatus.Unreachable, result.Check!.Status);
        Assert.False(result.Sent);
        Assert.NotNull(result.State.LastFailureAt); // no new attempt for an hour
    }

    [Fact]
    public void State_is_stored_in_the_site_document_without_the_key()
    {
        var site = new Entity();
        Assert.Same(ReporterState.Empty, SupertextReporting.LoadState(site));

        var state = new ReporterState("1.0.0|3.0.1|10.0.0", "ok", null, new KeyCheck("ok", "2026-10-14T09:00:00Z", 200, null, ApiKeyCheck.FingerprintOf(Key)));
        SupertextReporting.StoreState(site, state);

        Assert.Equal(state.ToJson(), SupertextReporting.LoadState(site).ToJson());
        Assert.DoesNotContain(Key, site.Properties.ToJsonString());

        site.Alter<Settings.SupertextReportingState>(s => s.Reporter = "{broken");
        Assert.Same(ReporterState.Empty, SupertextReporting.LoadState(site));
    }

    [Fact]
    public async Task State_stored_by_the_SDK_0_2_reporter_is_kept_no_new_installed_report()
    {
        // What the SDK 0.2 reporter stored in the site document before the switch to Plugin Health.
        var stored = "{\"reportedVersions\":\"1.2.3|3.0.1|" + Environment.Version + "\",\"reportedKeyStatus\":\"ok\",\"lastFailureAt\":null,"
            + "\"keyCheck\":{\"status\":\"ok\",\"checkedAt\":\"2026-10-14T09:00:00Z\",\"httpStatus\":200,\"errorCode\":null,\"fingerprint\":\"" + ApiKeyCheck.FingerprintOf(Key) + "\"}}";
        var site = new Entity();
        site.Alter<Settings.SupertextReportingState>(s => s.Reporter = stored);
        var state = SupertextReporting.LoadState(site);
        // Plugin Health 0.2+ adds keyChecks and reportedKeyStatuses (null with one key); everything else is kept.
        Assert.Equal(stored[..^1] + ",\"keyChecks\":null,\"reportedKeyStatuses\":null}", state.ToJson());

        var http = new FakeHttp();
        var result = await VersionReports.RunAsync(ReporterAction.OnRequest, Context(http, enabled: true), state, Key);
        Assert.Null(result.Event);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Send_configuration_sends_modules_and_the_language_mapping()
    {
        var http = new FakeHttp();
        Extension[] modules = [new("Lombiq.HelpfulExtensions", "13.0.0", true, "module"), new("TheOldTheme", null, false, "theme")];
        PluginHealth.LanguageMapping[] languages = [new("en-US", "en-US"), new("de-CH", "de-CH"), new("en-IE", null)];

        var result = await VersionReports.SendConfigurationAsync(Context(http, enabled: true), ReporterState.Empty, modules, languages);

        Assert.Equal(ReportEvents.Extensions, result.Event);
        Assert.True(result.Sent);
        Assert.Equal(["POST " + ReportUrl], http.Requests);
        var report = JsonNode.Parse(http.Reports.Single())!;
        Assert.Equal("extensions", report["event"]!.GetValue<string>());
        Assert.Equal("Lombiq.HelpfulExtensions", report["extensions"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("13.0.0", report["extensions"]![0]!["version"]!.GetValue<string>());
        Assert.False(report["extensions"]![1]!["active"]!.GetValue<bool>());
        Assert.Equal("de-CH", report["languages"]![1]!["supertext"]!.GetValue<string>());
        Assert.Null(report["languages"]![2]!["supertext"]);
    }

    [Fact]
    public async Task Send_configuration_sends_nothing_with_reports_off()
    {
        var http = new FakeHttp();
        var result = await VersionReports.SendConfigurationAsync(Context(http, enabled: false), ReporterState.Empty, [new("a.b")], [new("de-CH", "de-CH")]);

        Assert.False(result.Sent);
        Assert.Empty(http.Requests);
    }

    private static ReportingContext Context(FakeHttp http, bool enabled)
        => new(enabled, "https://www.example.ch", "1.2.3", "3.0.1", "https://api.test/v1/")
        {
            ReportUrl = ReportUrl,
            HttpClient = new HttpClient(http),
        };

    private sealed class FakeHttp : HttpMessageHandler
    {
        public HttpStatusCode FeaturesStatus { get; init; } = HttpStatusCode.OK;

        public bool Fail { get; init; }

        public List<string> Requests { get; } = [];

        public List<string> Reports { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.Method + " " + request.RequestUri);
            if (Fail)
            {
                throw new HttpRequestException("connection refused");
            }
            if (request.RequestUri!.ToString() == ReportUrl)
            {
                Reports.Add(await request.Content!.ReadAsStringAsync(ct));
                return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(FeaturesStatus) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
        }
    }
}
